using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace MyPan.Mobile.Android;

// Shared unchanged with the desktop regression runner. The model only produces
// binary foreground masks; photograph pixels are never generated or repainted.
internal static class LocalForeground
{
    internal static readonly (string Name, string Hash)[] Models = [
        ("encoder.onnx", "3827ec18dbc724715494955d132f934f896a568ff42d5ee2328aad1a0af895f7"),
        ("decoder.onnx", "cab1588bff5c6eaae85e73c012485f83affd74494e611ca062f40fbe73320116")];
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)]
    private static extern int yv_mask_input([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] float[] tensor, int length, [Out] int[] size);
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)]
    private static extern int yv_mask_output([MarshalAs(UnmanagedType.LPUTF8Str)] string path, float[] logits, int length, int width, int height);
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)] private static extern nint yv_last_error();
    private sealed record Candidate(float Score, float[] Logits, bool[] Tiny, float Area);
    private sealed record Frame(string Path, int[] Size, List<Candidate> Candidates);
    private static void Check(int result) { if (result != 0) throw new IOException(Marshal.PtrToStringUTF8(yv_last_error())); }

    internal static void Generate(string[] files, string models, string destination, Action<int, string> report, Func<bool> cancelled)
    {
        if (files.Length is < 6 or > 120) throw new IOException("轮廓重建需要 6 至 120 张原片。");
        foreach (var model in Models)
        {
            using var input = File.OpenRead(Path.Combine(models, model.Name));
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(model.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("本机轮廓模型校验失败，请重新安装完整版本。");
        }
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1, EnableMemoryPattern = false };
        using var encoder = new InferenceSession(Path.Combine(models, "encoder.onnx"), options);
        using var decoder = new InferenceSession(Path.Combine(models, "decoder.onnx"), options);
        using var run = new RunOptions();
        void Stop() { if (cancelled()) throw new OperationCanceledException("CANCELLED"); }
        var frames = new List<Frame>(); var pixels = new float[3 * 1024 * 1024];
        foreach (var file in files)
        {
            Stop(); report(frames.Count * 24 / files.Length, $"本机识别主体 {frames.Count + 1}/{files.Length}");
            int[] size = new int[2]; Check(yv_mask_input(file, pixels, pixels.Length, size));
            using var tensor = OrtValue.CreateTensorValueFromMemory(pixels, new long[] { 1, 3, 1024, 1024 });
            using var embedding = encoder.Run(run, new Dictionary<string, OrtValue> { ["pixels"] = tensor }, encoder.OutputNames);
            Stop();
            using var dimensions = OrtValue.CreateTensorValueFromMemory(new float[] { size[0], size[1] }, new long[] { 2 });
            using var masks = decoder.Run(run, new Dictionary<string, OrtValue> { ["embedding"] = embedding[0], ["size"] = dimensions }, new[] { "masks", "scores" });
            var values = masks[0].GetTensorDataAsSpan<float>(); var scores = masks[1].GetTensorDataAsSpan<float>();
            var candidates = new List<Candidate>();
            for (int c = 0; c < 4; c++)
            {
                if (!float.IsFinite(scores[c]) || scores[c] < .75f) continue;
                var logits = values.Slice(c * 256 * 256, 256 * 256); var tiny = new bool[128 * 128]; int count = 0;
                for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
                { var value = logits[Math.Min(255, y * size[1] / 512) * 256 + Math.Min(255, x * size[0] / 512)] > 0; tiny[y * 128 + x] = value; if (value) count++; }
                float area = count / (float)tiny.Length;
                if (area is > .015f and < .65f) candidates.Add(new(scores[c], logits.ToArray(), tiny, area));
            }
            if (candidates.Count == 0) throw new IOException($"第 {frames.Count + 1} 张未识别到完整主体，请检查拍摄是否居中。原片已保留。");
            frames.Add(new(file, size, candidates));
        }
        // Video keyframes retain their temporal order. Do not force continuity on
        // an unordered album import, where adjacent filenames need not be neighbors.
        var sequential = files.All(p => Path.GetFileName(p).StartsWith("frame-video-", StringComparison.Ordinal));
        var costs = new List<double[]>(); var links = new List<int[]>();
        for (int i = 0; i < frames.Count; i++)
        {
            var choices = frames[i].Candidates; var score = new double[choices.Count]; var link = new int[choices.Count];
            for (int j = 0; j < choices.Count; j++)
            {
                score[j] = -choices[j].Score; if (i == 0 || !sequential) continue;
                double best = double.PositiveInfinity;
                for (int k = 0; k < frames[i - 1].Candidates.Count; k++)
                {
                    var a = frames[i - 1].Candidates[k]; var b = choices[j]; int intersection = 0, union = 0;
                    for (int p = 0; p < a.Tiny.Length; p++) { if (a.Tiny[p] && b.Tiny[p]) intersection++; if (a.Tiny[p] || b.Tiny[p]) union++; }
                    double candidate = costs[i - 1][k] + 1.5 * (1 - intersection / (double)Math.Max(1, union)) + .25 * Math.Abs(Math.Log(b.Area / a.Area));
                    if (candidate < best) { best = candidate; link[j] = k; }
                }
                score[j] += best;
            }
            costs.Add(score); links.Add(link);
        }
        var selected = new int[frames.Count];
        for (int i = frames.Count - 1; i >= 0; i--) selected[i] = sequential && i < frames.Count - 1 ? links[i + 1][selected[i + 1]] : Array.IndexOf(costs[i], costs[i].Min());
        var areas = frames.Select((f, i) => f.Candidates[selected[i]].Area).Order().ToArray(); var median = areas[areas.Length / 2];
        if (areas[0] < median * .3 || areas[^1] > median * 3) throw new IOException("主体轮廓变化过大，可能只识别了标签或混入背景；原片已保留。");
        Directory.CreateDirectory(destination);
        for (int i = 0; i < frames.Count; i++)
        {
            Stop(); var frame = frames[i]; var logits = frame.Candidates[selected[i]].Logits;
            Check(yv_mask_output(Path.Combine(destination, Path.GetFileName(frame.Path) + ".png"), logits, logits.Length, frame.Size[0], frame.Size[1]));
        }
        report(25, "本机主体识别完成，开始计算三维外形");
    }
}
