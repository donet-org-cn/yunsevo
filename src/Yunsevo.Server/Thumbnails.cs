using System.Security.Cryptography;
using System.Text;
using Yunsevo.Contracts;
using SkiaSharp;

namespace Yunsevo.Server;

public sealed partial class CloudStore
{
    private readonly SemaphoreSlim thumbnailWorkers = new(2);
    private readonly SemaphoreSlim[] thumbnailLocks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();
    private int thumbnailWrites;
    public async Task<string> Thumbnail(RequestActor actor, Guid id, string? version, CancellationToken ct)
    {
        // Always authorize against the current file, including cache hits and conditional requests.
        var snapshot = Volatile.Read(ref state);
        var entry = snapshot.Entries.Find(x => x.Id == id && !x.IsDirectory) ?? throw new StoreException(404, "文件不存在。");
        Require(snapshot, entry.Path, actor, 1);
        if (version != null && version != entry.Version) throw new StoreException(409, "文件版本已变化，请刷新相册。");
        var extension = System.IO.Path.GetExtension(entry.Path).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".avif" or ".heic" or ".heif"))
            throw new StoreException(415, "此格式暂不提供缩略图。");
        if (entry.Size > 128L * 1024 * 1024) throw new StoreException(415, "此图片过大，请打开原图查看。");
        var directory = System.IO.Path.Combine(root, "thumbnails"); Directory.CreateDirectory(directory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("thumb-v1/" + id + "/" + entry.Version))) + ".jpg";
        var file = System.IO.Path.Combine(directory, name);
        var serial = thumbnailLocks[(StringComparer.Ordinal.GetHashCode(name) & int.MaxValue) % thumbnailLocks.Length];
        await serial.WaitAsync(ct);
        try
        {
            if (File.Exists(file)) return file;
            await thumbnailWorkers.WaitAsync(ct);
            try
            {
                var download = await Download(actor, id, entry.Version, ct);
                await using var source = download.Stream;
                await Task.Run(() => GenerateThumbnail(source, file, ct), ct);
                if (Interlocked.Increment(ref thumbnailWrites) % 64 == 0) TrimThumbnails(directory);
            }
            finally { thumbnailWorkers.Release(); }
            return file;
        }
        finally { serial.Release(); }
    }
    private static void GenerateThumbnail(Stream source, string destination, CancellationToken ct)
    {
        using var codec = SKCodec.Create(source) ?? throw new StoreException(415, "此图片格式暂不支持缩略图。");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 120_000_000)
            throw new StoreException(415, "此图片尺寸不适合生成缩略图。");
        var sampled = codec.GetScaledDimensions(Math.Min(1, 512f / Math.Max(info.Width, info.Height)));
        if ((long)sampled.Width * sampled.Height > 16_000_000) throw new StoreException(415, "此格式需要更多内存，请直接打开图片。");
        using var decoded = new SKBitmap(new SKImageInfo(sampled.Width, sampled.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result != SKCodecResult.Success) throw new StoreException(415, "图片内容不完整或无法解码。");
        ct.ThrowIfCancellationRequested();
        var swapped = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var naturalWidth = swapped ? sampled.Height : sampled.Width; var naturalHeight = swapped ? sampled.Width : sampled.Height;
        var scale = Math.Min(1, 256f / Math.Max(naturalWidth, naturalHeight));
        var width = Math.Max(1, (int)Math.Round(naturalWidth * scale)); var height = Math.Max(1, (int)Math.Round(naturalHeight * scale));
        using var output = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.White); canvas.Scale(scale);
            switch (codec.EncodedOrigin)
            {
                case SKEncodedOrigin.TopRight: canvas.Translate(sampled.Width, 0); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.BottomRight: canvas.Translate(sampled.Width, sampled.Height); canvas.RotateDegrees(180); break;
                case SKEncodedOrigin.BottomLeft: canvas.Translate(0, sampled.Height); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.RightTop: canvas.Translate(sampled.Height, 0); canvas.RotateDegrees(90); break;
                case SKEncodedOrigin.RightBottom: canvas.Translate(sampled.Height, sampled.Width); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.LeftBottom: canvas.Translate(0, sampled.Width); canvas.RotateDegrees(270); break;
            }
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(decoded, 0, 0, paint);
        }
        using var image = SKImage.FromBitmap(output); using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 70);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary)) { encoded.SaveTo(stream); stream.Flush(true); }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void TrimThumbnails(string directory)
    {
        try
        {
            long bytes = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.jpg").OrderByDescending(x => x.LastWriteTimeUtc))
            { bytes += file.Length; if (bytes > 512L * 1024 * 1024 && file.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-10)) file.Delete(); }
        }
        catch (IOException) { /* Thumbnails are disposable; an active reader can keep its cached file. */ }
    }
}
