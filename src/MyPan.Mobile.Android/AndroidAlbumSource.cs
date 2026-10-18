using Android;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using System.Runtime.CompilerServices;
using MyPan.Mobile.Core;

namespace MyPan.Mobile.Android;

internal sealed class AndroidAlbumSource(Context context) : IAlbumSource
{
    public static bool HasPermission(Context context) => OperatingSystem.IsAndroidVersionAtLeast(33)
        ? context.CheckSelfPermission(Manifest.Permission.ReadMediaImages) == Permission.Granted ||
          context.CheckSelfPermission(Manifest.Permission.ReadMediaVideo) == Permission.Granted ||
          OperatingSystem.IsAndroidVersionAtLeast(34) && context.CheckSelfPermission("android.permission.READ_MEDIA_VISUAL_USER_SELECTED") == Permission.Granted
        : context.CheckSelfPermission(Manifest.Permission.ReadExternalStorage) == Permission.Granted;
    public async IAsyncEnumerable<AlbumAsset> Enumerate([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        foreach (var (uri, video) in new[] { (MediaStore.Images.Media.ExternalContentUri!, false), (MediaStore.Video.Media.ExternalContentUri!, true) })
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33) && context.CheckSelfPermission(video ? Manifest.Permission.ReadMediaVideo : Manifest.Permission.ReadMediaImages) != Permission.Granted &&
                !(OperatingSystem.IsAndroidVersionAtLeast(34) && context.CheckSelfPermission("android.permission.READ_MEDIA_VISUAL_USER_SELECTED") == Permission.Granted)) continue;
            string[] columns = ["_id", "_display_name", "_size", "date_modified", "datetaken", "mime_type", "bucket_display_name"];
            columns = [.. columns, OperatingSystem.IsAndroidVersionAtLeast(29) ? "relative_path" : "_data"];
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) columns = [.. columns, "generation_modified"];
            using var cursor = context.ContentResolver!.Query(uri, columns, OperatingSystem.IsAndroidVersionAtLeast(29) ? "is_pending=0" : null, null, "date_added DESC");
            if (cursor == null) continue;
            while (cursor.MoveToNext())
            {
                ct.ThrowIfCancellationRequested();
                var id = cursor.GetLong(0); var name = cursor.GetString(1) ?? ("photo-" + id + (video ? ".mp4" : ".jpg"));
                var size = cursor.GetLong(2); var modified = cursor.GetLong(3); var taken = cursor.GetLong(4);
                var mime = cursor.GetString(5) ?? (video ? "video/mp4" : "image/jpeg");
                var mediaPath = cursor.GetString(7) ?? "";
                if (new[] { "Pictures/Yunsevo/", "Movies/Yunsevo/" }.Any(folder => mediaPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || mediaPath.Contains("/" + folder, StringComparison.OrdinalIgnoreCase))) continue;
                var key = ContentUris.WithAppendedId(uri, id)!.ToString();
                var generation = OperatingSystem.IsAndroidVersionAtLeast(30) ? ":" + cursor.GetLong(8) : "";
                yield return new(key!, size + ":" + modified + generation, name, size, cursor.GetString(6) ?? "相册",
                    taken > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(taken) : DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, modified)), mime);
            }
        }
    }
    public Task<Stream> Open(AlbumAsset asset, CancellationToken ct)
    {
        if (asset.Size > new StatFs(context.FilesDir!.AbsolutePath).AvailableBytes - 64L * 1024 * 1024)
            throw new IOException("手机空间不足以暂存待备份的照片或视频，请先释放空间。");
        return Task.FromResult(context.ContentResolver!.OpenInputStream(global::Android.Net.Uri.Parse(asset.Key)!) ?? throw new IOException("照片已删除或未授权访问。"));
    }
}
