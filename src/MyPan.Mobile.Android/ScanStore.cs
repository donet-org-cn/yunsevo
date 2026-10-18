using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyPan.Mobile.Core;
using MyPan.Contracts;
using MyPan.Sync;

namespace MyPan.Mobile.Android;

internal sealed record ScanSession(Guid Id, string Account, string Name, FolderSelection Folder, DateTimeOffset CreatedUtc,
    string Subject = "object", string State = "collecting", int Progress = 0, string Message = "添加原片或开始引导拍摄", string Backend = "auto",
    Guid? RemoteJob = null, List<Guid>? UploadJobs = null, Guid? ModelUpload = null, Guid? RemoteRequestId = null, List<string>? Inputs = null, int Quality = 1, List<Guid>? SourceVideoJobs = null, Guid? DerivedFrom = null);
internal sealed record ScanVideoCalibration(double Focal35mm);
[JsonSerializable(typeof(ScanSession))]
[JsonSerializable(typeof(ScanVideoCalibration))]
internal partial class ScanJson : JsonSerializerContext;
internal static class ScanStore
{
    public const int MaxPhotos = 120;
    private static readonly object Gate = new();
    private static string Root => Path.Combine(AppServices.Repository.Home, "scans");
    public static string Home(Guid id) => Path.Combine(Root, id.ToString("N"));
    public static string Photos(Guid id) => Path.Combine(Home(id), "photos");
    public static string Model(Guid id) => Path.Combine(Home(id), "model.glb");
    public static ScanSession Get(Guid id)
    {
        lock (Gate)
        {
            var value = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Home(id), "session.json")), ScanJson.Default.ScanSession) ?? throw new IOException("扫描记录无效。");
            if (value.Account != AppServices.Client.AccountKey) throw new InvalidOperationException("请使用创建此扫描的账号。");
            return value;
        }
    }
    public static List<ScanSession> List()
    {
        Directory.CreateDirectory(Root); var result = new List<ScanSession>();
        foreach (var path in Directory.EnumerateDirectories(Root)) if (Guid.TryParse(Path.GetFileName(path), out var id)) try { result.Add(Get(id)); } catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
        return result.OrderByDescending(x => x.CreatedUtc).ToList();
    }
    public static void Save(ScanSession value)
    {
        lock (Gate) { Directory.CreateDirectory(Photos(value.Id)); AtomicFile.WriteText(Path.Combine(Home(value.Id), "session.json"), JsonSerializer.Serialize(value, ScanJson.Default.ScanSession)); }
    }
    public static ScanSession Edit(Guid id, Func<ScanSession, ScanSession> change)
    { lock (Gate) { var value = change(Get(id)); Save(value); return value; } }
    public static void Delete(Guid id)
    {
        lock (Gate) { var value = Get(id); if (value.State is "running" or "remote" or "staging" or "uploading") throw new InvalidOperationException("请先结束生成或上传任务。"); Directory.Delete(Home(id), true); }
    }
    public static void RecoverPreviousModel(Guid id)
    {
        lock (Gate)
        {
            Get(id); if (File.Exists(Model(id))) return;
            var previous = Directory.GetFiles(Home(id), "model-before-*.glb").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
            if (previous != null) File.Copy(previous, Model(id));
        }
    }
    public static string[] Files(Guid id) => Directory.GetFiles(Photos(id)).Where(x => Path.GetExtension(x) is ".jpg" or ".png").OrderBy(x => x, StringComparer.Ordinal).ToArray();
    // Re-select into a separate revision. Never mix new frames with the old set or
    // invalidate a usable local / uploaded model if decoding is cancelled or fails.
    public static (ScanSession Session, string Video) CreateVideoRevision(Guid id, string source, int quality, CancellationToken token)
    {
        lock (Gate)
        {
            var previous = Get(id);
            if (previous.State is "running" or "remote" or "staging" or "uploading") throw new InvalidOperationException("请先结束当前任务，再重新选帧。");
            if (quality is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(quality));
            source = Path.GetFullPath(source);
            if (Path.GetDirectoryName(source) != Path.GetFullPath(Home(id)) || !Path.GetFileName(source).StartsWith("video-", StringComparison.Ordinal) || Path.GetExtension(source) != ".mp4" || !File.Exists(source)) throw new IOException("扫描原视频不存在。");
            if (new FileInfo(source).Length > 512L * 1024 * 1024) throw new IOException("视频不能超过 512 MB。");
            var revision = new ScanSession(Guid.NewGuid(), previous.Account, previous.Name, previous.Folder, DateTimeOffset.UtcNow,
                previous.Subject, Message: "原视频已复制，准备重新提取关键照片；旧扫描和旧模型已保留。", Quality: quality, DerivedFrom: id);
            var home = Home(revision.Id); Directory.CreateDirectory(home);
            var target = Path.Combine(home, Path.GetFileName(source));
            try
            {
                void Copy(string from, string to, long limit)
                {
                    using var input = File.OpenRead(from); using var output = File.Create(to);
                    var buffer = new byte[128 * 1024]; long total = 0; int count;
                    while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); total += count; if (total > limit) throw new IOException("源文件超过限制。"); output.Write(buffer, 0, count); }
                }
                Copy(source, target, 512L * 1024 * 1024);
                if (File.Exists(source + ".calibration.json")) Copy(source + ".calibration.json", target + ".calibration.json", 64 * 1024);
                token.ThrowIfCancellationRequested();
                if (AppServices.Client.AccountKey != previous.Account) throw new InvalidOperationException("账号已切换，请重新打开扫描。");
                Save(revision); return (revision, target);
            }
            catch { Directory.Delete(home, true); throw; } // home is a new GUID directory owned by this operation.
        }
    }
    public static void AddVideoFrames(Guid id, string[] frames, CancellationToken token)
    {
        lock (Gate)
        {
            EnsureCollecting(id); var existing = Files(id);
            if (existing.Length + frames.Length > MaxPhotos || existing.Concat(frames).Sum(x => new FileInfo(x).Length) > 512L * 1024 * 1024) throw new IOException("选取后超过扫描的 120 张／512 MB 上限。");
            var moved = new List<string>();
            try
            {
                foreach (var file in frames)
                {
                    token.ThrowIfCancellationRequested(); var target = Path.Combine(Photos(id), Path.GetFileName(file));
                    File.Move(file, target); moved.Add(target);
                }
            }
            catch { foreach (var file in moved) File.Delete(file); throw; }
        }
    }
    public static void EnsureCollecting(Guid id)
    {
        var session = Get(id); if (session.State is "running" or "remote" or "staging" || session.RemoteJob.HasValue || session.Inputs != null) throw new InvalidOperationException("重建期间不能修改原片，请新建扫描。");
        var files = Files(id); if (files.Length >= MaxPhotos || files.Sum(x => new FileInfo(x).Length) >= 512L * 1024 * 1024) throw new IOException("每次扫描最多 120 张照片、合计 512 MB。");
        if (File.Exists(Model(id))) throw new InvalidOperationException("此扫描已生成模型。补拍请新建扫描，以保留原来的模型。");
    }
}
internal static partial class ReconstructionNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Progress(nint user, int percent, nint text);
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)] private static extern nint yv_capabilities();
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)] private static extern nint yv_last_error();
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)] private static extern int yv_reconstruct_v2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string input, [MarshalAs(UnmanagedType.LPUTF8Str)] string output,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string backend, int quality, [MarshalAs(UnmanagedType.LPUTF8Str)] string subject, Progress progress, nint user);
    public static ReconstructionCapabilities Capabilities()
    {
        try { return JsonSerializer.Deserialize(Marshal.PtrToStringUTF8(yv_capabilities())!, ApiJson.Default.ReconstructionCapabilities)!; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return new(false, false, Error: "当前安装包未包含本机重建引擎。"); }
    }
    public static void Generate(Guid id, string backend, Progress progress)
    { var scan = ScanStore.Get(id); var rc = yv_reconstruct_v2(ScanStore.Photos(id), ScanStore.Model(id), backend, scan.Quality, scan.Subject, progress, 0); GC.KeepAlive(progress); if (rc != 0) throw new IOException(ReconstructionMessages.Error(Marshal.PtrToStringUTF8(yv_last_error()) ?? "重建未完成。")); }
}
