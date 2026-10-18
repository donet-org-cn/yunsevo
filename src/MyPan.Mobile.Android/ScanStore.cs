using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyPan.Mobile.Core;
using MyPan.Contracts;
using MyPan.Sync;

namespace MyPan.Mobile.Android;

internal sealed record ScanSession(Guid Id, string Account, string Name, FolderSelection Folder, DateTimeOffset CreatedUtc,
    string Subject = "object", string State = "collecting", int Progress = 0, string Message = "添加原片或开始引导拍摄", string Backend = "auto",
    Guid? RemoteJob = null, List<Guid>? UploadJobs = null, Guid? ModelUpload = null, Guid? RemoteRequestId = null, List<string>? Inputs = null);
[JsonSerializable(typeof(ScanSession))]
internal partial class ScanJson : JsonSerializerContext;
internal static class ScanStore
{
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
    public static string[] Files(Guid id) => Directory.GetFiles(Photos(id)).Where(x => Path.GetExtension(x) is ".jpg" or ".png").OrderBy(x => x, StringComparer.Ordinal).ToArray();
    public static void EnsureCollecting(Guid id)
    {
        var session = Get(id); if (session.State is "running" or "remote" or "staging" || session.RemoteJob.HasValue || session.Inputs != null) throw new InvalidOperationException("重建期间不能修改原片，请新建扫描。");
        var files = Files(id); if (files.Length >= 72 || files.Sum(x => new FileInfo(x).Length) >= 512L * 1024 * 1024) throw new IOException("每次扫描最多 72 张照片、合计 512 MB。");
        if (File.Exists(Model(id))) throw new InvalidOperationException("此扫描已生成模型。补拍请新建扫描，以保留原来的模型。");
    }
}
internal static partial class ReconstructionNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Progress(nint user, int percent, nint text);
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)] private static extern nint yv_capabilities();
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)] private static extern nint yv_last_error();
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)] private static extern int yv_reconstruct(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string input, [MarshalAs(UnmanagedType.LPUTF8Str)] string output,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string backend, int quality, Progress progress, nint user);
    public static ReconstructionCapabilities Capabilities()
    {
        try { return JsonSerializer.Deserialize(Marshal.PtrToStringUTF8(yv_capabilities())!, ApiJson.Default.ReconstructionCapabilities)!; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return new(false, false, Error: "当前安装包未包含本机重建引擎。"); }
    }
    public static void Generate(Guid id, string backend, Progress progress)
    { var rc = yv_reconstruct(ScanStore.Photos(id), ScanStore.Model(id), backend, 0, progress, 0); GC.KeepAlive(progress); if (rc != 0) throw new IOException(Marshal.PtrToStringUTF8(yv_last_error()) ?? "重建未完成。"); }
}
