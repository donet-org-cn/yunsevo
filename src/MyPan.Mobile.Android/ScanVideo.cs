using Android.Graphics;
using Android.Media;
using System.Globalization;

namespace MyPan.Mobile.Android;

// Bounded, time-distributed keyframe selection. Image differences are not a parallax detector;
// the SfM stage still has to validate camera translation and geometry before publishing a model.
internal static class ScanVideo
{
    private sealed record Candidate(long Time, double Sharpness, double[] Luma);
    public static string Extract(Guid id, string source, Action<string> progress, CancellationToken token)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(27)) throw new IOException("视频选帧需要 Android 8.1 或更新系统；当前设备可使用引导拍照。");
        ScanStore.EnsureCollecting(id);
        var home = ScanStore.Home(id);
        if (Path.GetDirectoryName(Path.GetFullPath(source)) != Path.GetFullPath(home) || !File.Exists(source)) throw new IOException("扫描视频不存在。");
        if (new FileInfo(source).Length > 512L * 1024 * 1024) throw new IOException("视频不能超过 512 MB。");
        var prefix = "frame-" + Path.GetFileNameWithoutExtension(source) + "-";
        if (ScanStore.Files(id).Any(x => Path.GetFileName(x).StartsWith(prefix, StringComparison.Ordinal))) throw new IOException("此视频已选取过照片，请新建扫描后重新选择，避免重复帧影响重建。");
        var room = 72 - ScanStore.Files(id).Length;
        if (room < 6) throw new IOException("剩余照片名额不足 6 张，请新建扫描。");
        using var retriever = new MediaMetadataRetriever();
        retriever.SetDataSource(source);
        if (!long.TryParse(retriever.ExtractMetadata(MetadataKey.Duration), out var duration) || duration < 3000 || duration > 180_000)
            throw new IOException("请选择 3 秒至 3 分钟的连续环绕视频，避免快切、变焦和不同物体混在一起。");
        var count = Math.Min(240, Math.Max(12, (int)(duration / 350)));
        var buckets = Math.Min(Math.Min(room, 48), Math.Max(6, count / 3));
        var best = new Candidate?[buckets];
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var time = (long)((i + .5) * duration * 1000 / count); // Android frame timestamps are microseconds.
            using var bitmap = retriever.GetScaledFrameAtTime(time, Option.Closest, 256, 256);
            if (bitmap == null) continue;
            var candidate = Measure(bitmap, time);
            if (candidate == null) continue;
            var bucket = Math.Min(buckets - 1, i * buckets / count);
            if (best[bucket] == null || candidate.Sharpness > best[bucket]!.Sharpness) best[bucket] = candidate;
            progress("检查画面 " + (i + 1) + "/" + count + " · 已覆盖 " + best.Count(x => x != null) + " 个时间段");
        }
        var chosen = new List<Candidate>();
        foreach (var candidate in best)
        {
            if (candidate == null || chosen.Count > 0 && Difference(chosen[^1].Luma, candidate.Luma) < 4) continue;
            chosen.Add(candidate);
        }
        if (chosen.Count < 6) throw new IOException("只找到 " + chosen.Count + " 张清晰且有变化的画面，未添加照片。请增加光线，慢走绕物体一圈，避免停留、抖动或只转动手机；源视频已保留。");
        var staging = Path.Combine(home, "frames-" + Guid.NewGuid().ToString("N") + ".partial");
        Directory.CreateDirectory(staging);
        try
        {
            for (var i = 0; i < chosen.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var candidate = chosen[i];
                using var bitmap = retriever.GetScaledFrameAtTime(candidate.Time, Option.Closest, 3840, 3840) ?? throw new IOException("无法解码所选视频帧。");
                var path = Path.Combine(staging, prefix + candidate.Time.ToString("D12", CultureInfo.InvariantCulture) + ".jpg");
                using (var output = File.Create(path)) if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 95, output)) throw new IOException("视频帧保存失败。");
                using (var exif = new ExifInterface(path))
                {
                    exif.SetAttribute("Orientation", "1");
                    exif.SetAttribute("ImageDescription", "Video keyframe; source=" + Path.GetFileName(source) + "; timestampUs=" + candidate.Time.ToString(CultureInfo.InvariantCulture));
                    exif.SaveAttributes();
                }
                progress("保存清晰画面 " + (i + 1) + "/" + chosen.Count);
            }
            token.ThrowIfCancellationRequested();
            ScanStore.AddVideoFrames(id, Directory.GetFiles(staging), token);
            return "已从视频筛选 " + chosen.Count + " 张照片，过滤模糊／低纹理和近乎重复的画面；源视频已保留。尚未验证三维视差，请生成模型检查。";
        }
        finally { Directory.Delete(staging, true); }
    }
    private static Candidate? Measure(Bitmap bitmap, long time)
    {
        using var thumb = Bitmap.CreateScaledBitmap(bitmap, 96, 72, true)!;
        var pixels = new int[96 * 72]; thumb.GetPixels(pixels, 0, 96, 0, 0, 96, 72);
        var luma = new double[pixels.Length]; double sum = 0;
        for (var i = 0; i < pixels.Length; i++)
        { var c = pixels[i]; var v = .299 * ((c >> 16) & 255) + .587 * ((c >> 8) & 255) + .114 * (c & 255); luma[i] = v; sum += v; }
        var mean = sum / luma.Length; var contrast = Math.Sqrt(luma.Sum(x => (x - mean) * (x - mean)) / luma.Length);
        double lap = 0, lap2 = 0; var n = 0;
        for (var y = 1; y < 71; y++) for (var x = 1; x < 95; x++)
        {
            var i = y * 96 + x; var v = 4 * luma[i] - luma[i - 1] - luma[i + 1] - luma[i - 96] - luma[i + 96]; lap += v; lap2 += v * v; n++;
        }
        var sharpness = lap2 / n - Math.Pow(lap / n, 2);
        if (mean is < 6 or > 250 || contrast < 10 || sharpness < 18) return null;
        for (var i = 0; i < luma.Length; i++) luma[i] -= mean;
        return new(time, sharpness, luma);
    }
    private static double Difference(double[] a, double[] b)
    { double sum = 0; for (var i = 0; i < a.Length; i++) { var d = a[i] - b[i]; sum += d * d; } return Math.Sqrt(sum / a.Length); }
}
