using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Webkit;
using MyPan.Contracts;
using MyPan.Mobile.Core;
using MyPan.Sync;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace MyPan.Mobile.Android;

internal static class CloudMedia
{
    private static readonly SemaphoreSlim[] files = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();
    public static bool IsVideo(string path) => new[] { ".mp4", ".mov", ".m4v", ".webm", ".3gp" }.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static string Mime(string path) => MimeTypeMap.Singleton!.GetMimeTypeFromExtension(Path.GetExtension(path).TrimStart('.').ToLowerInvariant()) ?? "application/octet-stream";
    public static async Task<(CloudEntry Entry, string File)> Download(Guid id, string path, CancellationToken ct, Action<long, long>? progress = null)
    {
        var client = AppServices.Client; var (connection, scope) = client.TransferSession();
        using var lookup = await connection.Http.GetAsync("api/browser/entry?path=" + Uri.EscapeDataString(path), ct);
        await CloudConnection.Check(lookup, ct);
        var entry = (await lookup.Content.ReadFromJsonAsync(ApiJson.Default.EntryLookup, ct))?.Entry;
        if (entry?.Id != id || entry.IsDirectory) throw new IOException("文件已移动或没有访问权限，请刷新相册。");
        var extension = Path.GetExtension(entry.Path);
        var name = id.ToString("N") + "-" + scope + "-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.Version))) + extension;
        var directory = Path.Combine(AppServices.Context.FilesDir!.AbsolutePath, "previews"); Directory.CreateDirectory(directory);
        var cached = Path.Combine(directory, name);
        var gate = files[(StringComparer.Ordinal.GetHashCode(cached) & int.MaxValue) % files.Length];
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(cached) || new FileInfo(cached).Length != entry.Size)
            {
                if (entry.Size < 0 || entry.Size > new StatFs(directory).AvailableBytes - 64L * 1024 * 1024) throw new IOException("手机空间不足，请先释放空间。");
                var partial = cached + ".partial";
                try
                {
                    using var response = await connection.Http.GetAsync("api/content/" + id + "?version=" + Uri.EscapeDataString(entry.Version), HttpCompletionOption.ResponseHeadersRead, ct);
                    await CloudConnection.Check(response, ct);
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[131072]; long total = 0; int count;
                    while ((count = await input.ReadAsync(buffer, ct)) != 0)
                    {
                        total += count; if (total > entry.Size) throw new IOException("文件长度发生变化，请重试。");
                        hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct); progress?.Invoke(total, entry.Size);
                    }
                    if (total != entry.Size || entry.Origin?.ContentSha256 is { } expected && !expected.Equals(Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("文件下载不完整或校验失败，请重试。");
                    output.Close(); File.Move(partial, cached, true);
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
            if (scope != client.AccountKey) throw new IOException("账号已变更，请重新打开相册。");
            return (entry, cached);
        }
        finally { gate.Release(); }
    }
    public static async Task SaveToGallery(Context context, string file, string displayName, string mime, CancellationToken ct)
    {
        var video = mime.StartsWith("video/", StringComparison.Ordinal);
        if (!video && !mime.StartsWith("image/", StringComparison.Ordinal)) throw new IOException("此文件请使用“另存为”选择保存位置。");
        var values = new ContentValues(); values.Put(MediaStore.IMediaColumns.DisplayName, MobileRepository.FileName(displayName)); values.Put(MediaStore.IMediaColumns.MimeType, mime);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        { values.Put(MediaStore.IMediaColumns.RelativePath, (video ? "Movies" : "Pictures") + "/Yunsevo/"); values.Put(MediaStore.IMediaColumns.IsPending, 1); }
        else
        {
            var directory = Path.Combine(global::Android.OS.Environment.GetExternalStoragePublicDirectory(video ? global::Android.OS.Environment.DirectoryMovies : global::Android.OS.Environment.DirectoryPictures)!.AbsolutePath, "Yunsevo");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, Path.GetFileNameWithoutExtension(displayName) + "-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(displayName));
            values.Put(MediaStore.IMediaColumns.Data, target);
        }
        var collection = video ? MediaStore.Video.Media.ExternalContentUri! : MediaStore.Images.Media.ExternalContentUri!;
        var uri = context.ContentResolver!.Insert(collection, values) ?? throw new IOException("无法创建系统相册文件。");
        try
        {
            await using var input = File.OpenRead(file);
            await using (var output = context.ContentResolver.OpenOutputStream(uri) ?? throw new IOException("没有相册写入权限。")) await input.CopyToAsync(output, ct);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            { var ready = new ContentValues(); ready.Put(MediaStore.IMediaColumns.IsPending, 0); context.ContentResolver.Update(uri, ready, null, null); }
        }
        catch { context.ContentResolver.Delete(uri, null, null); throw; }
    }
    public static void Share(Activity activity, string file, string name, string mime)
    {
        var uri = AppFileProvider.UriFor(file, name);
        var send = new Intent(Intent.ActionSend).SetType(mime)!.PutExtra(Intent.ExtraStream, uri).AddFlags(ActivityFlags.GrantReadUriPermission);
        send.ClipData = ClipData.NewRawUri(name, uri); activity.StartActivity(Intent.CreateChooser(send, "分享文件"));
    }
    private static readonly SemaphoreSlim thumbnailDownloads = new(4);
    private static int thumbnailLoads;
    public static async Task<Bitmap?> Thumbnail(CloudEntry entry, CancellationToken ct)
    {
        if (IsVideo(entry.Path)) return null;
        var client = AppServices.Client; var (connection, scope) = client.TransferSession();
        var directory = Path.Combine(AppServices.Context.CacheDir!.AbsolutePath, "cloud-thumbnails"); Directory.CreateDirectory(directory);
        var name = scope + "-" + entry.Id.ToString("N") + "-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.Version))) + ".jpg";
        var cached = Path.Combine(directory, name);
        await thumbnailDownloads.WaitAsync(ct);
        try
        {
            if (!File.Exists(cached))
            {
                using var response = await connection.Http.GetAsync("api/media/" + entry.Id + "/thumbnail?version=" + Uri.EscapeDataString(entry.Version), HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is System.Net.HttpStatusCode.UnsupportedMediaType or System.Net.HttpStatusCode.NotFound) return null;
                await CloudConnection.Check(response, ct);
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                var temporary = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, true))
                    {
                        var buffer = new byte[16384]; var total = 0; int read;
                        while ((read = await source.ReadAsync(buffer, ct)) != 0)
                        { total += read; if (total > 256 * 1024) throw new IOException("缩略图响应过大。"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
                    }
                    File.Move(temporary, cached, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                if (Interlocked.Increment(ref thumbnailLoads) % 128 == 0) _ = Task.Run(() => TrimThumbnailCache(directory));
            }
            ct.ThrowIfCancellationRequested(); if (client.AccountKey != scope) return null;
            return await DecodeImage(cached, 256);
        }
        finally { thumbnailDownloads.Release(); }
    }
    private static void TrimThumbnailCache(string directory)
    {
        try
        {
            long size = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.jpg").OrderByDescending(x => x.LastWriteTimeUtc))
            { size += file.Length; if (size > 128L * 1024 * 1024 && file.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-5)) file.Delete(); }
        }
        catch (IOException) { }
    }
    public static async Task<Bitmap?> DecodeImage(string file, int maximum)
    {
        using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        await BitmapFactory.DecodeFileAsync(file, bounds);
        var sample = 1; while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > maximum) sample *= 2;
        using var settings = new BitmapFactory.Options { InSampleSize = sample };
        var bitmap = await BitmapFactory.DecodeFileAsync(file, settings); if (bitmap == null) return null;
        var orientation = 1;
        try { using var exif = new global::Android.Media.ExifInterface(file); orientation = exif.GetAttributeInt("Orientation", 1); }
        catch (Java.IO.IOException) { }
        if (orientation is < 2 or > 8) return bitmap;
        using var matrix = new Matrix();
        if (orientation is 3 or 4) matrix.PostRotate(180);
        else if (orientation is 5 or 6) matrix.PostRotate(90);
        else if (orientation is 7 or 8) matrix.PostRotate(270);
        if (orientation is 2 or 4 or 5 or 7) matrix.PostScale(-1, 1);
        var rotated = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true);
        if (rotated?.Handle != bitmap.Handle) bitmap.Dispose(); return rotated;
    }
}
