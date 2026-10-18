using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using MyPan.Contracts;

namespace MyPan.Server;

internal sealed record ReconstructionWork(ReconstructionJob Job, StartReconstruction Request, bool WebAdmin);
[JsonSerializable(typeof(ReconstructionWork))]
internal partial class ReconstructionJson : JsonSerializerContext;

// Native photogrammetry is isolated in a bounded child process; never run native decoding in Kestrel.
internal sealed class ReconstructionJobs : IAsyncDisposable
{
    private readonly CloudStore store;
    private readonly AuditLog audit;
    private readonly string root, engine;
    private readonly string? qualityPython, qualityWorker;
    private readonly object gate = new();
    private readonly Dictionary<Guid, ReconstructionWork> jobs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> cancellation = [];
    private readonly Channel<Guid> queue = Channel.CreateBounded<Guid>(16);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private ReconstructionCapabilities? capabilities;
    private bool qualityVideoAvailable;
    public ReconstructionJobs(CloudStore store, AuditLog audit, string dataPath, string engine, string? qualityPython = null, string? qualityWorker = null)
    {
        this.store = store; this.audit = audit; this.engine = Path.GetFullPath(engine); root = Path.Combine(dataPath, "reconstruction"); Directory.CreateDirectory(root);
        this.qualityPython = string.IsNullOrWhiteSpace(qualityPython) ? null : Path.GetFullPath(qualityPython);
        this.qualityWorker = string.IsNullOrWhiteSpace(qualityWorker) ? null : Path.GetFullPath(qualityWorker);
        foreach (var path in Directory.EnumerateFiles(root, "job.json", SearchOption.AllDirectories))
        {
            try
            {
                var work = JsonSerializer.Deserialize(File.ReadAllText(path), ReconstructionJson.Default.ReconstructionWork);
                if (work == null) continue;
                work = work with { Job = work.Job with { Quality = work.Request.Quality } };
                if (work.Job.State is "queued" or "running") work = work with { Job = work.Job with { State = "failed", Message = "服务已重启，请重新提交重建。", UpdatedUtc = DateTimeOffset.UtcNow } };
                jobs[work.Job.Id] = work; Save(work);
            }
            catch (JsonException) { }
        }
        worker = Run();
    }
    private string Home(Guid id) => Path.Combine(root, id.ToString("N"));
    private void Save(ReconstructionWork work)
    {
        Directory.CreateDirectory(Home(work.Job.Id));
        var path = Path.Combine(Home(work.Job.Id), "job.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(work, ReconstructionJson.Default.ReconstructionWork)); File.Move(path + ".tmp", path, true);
    }
    public async Task<ReconstructionCapabilities> Capabilities(CancellationToken ct)
    {
        if (!File.Exists(engine)) return new(false, false, Error: "服务端尚未安装三维重建引擎，请安装 reconstruction 运行包。");
        if (capabilities != null) return capabilities;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var p = ProcessFor(); p.StartInfo.ArgumentList.Add("--capabilities"); p.Start();
        try
        {
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token); var text = await stdout; await stderr;
            if (p.ExitCode != 0) return new(false, false, Error: "三维引擎启动失败，请检查运行库。");
            var native = JsonSerializer.Deserialize(text, ApiJson.Default.ReconstructionCapabilities) ?? new(false, false, Error: "引擎返回无效状态。");
            var highQuality = await QualityAvailable(timeout.Token);
            return capabilities = native with { HighQuality = highQuality, HighQualityVideo = highQuality && qualityVideoAvailable };
        }
        finally { if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(CancellationToken.None); } }
    }
    private async Task<bool> QualityAvailable(CancellationToken ct)
    {
        if (!File.Exists(qualityPython) || !File.Exists(qualityWorker)) return false;
        using var p = ProcessFor(true); p.StartInfo.ArgumentList.Add("--capabilities");
        try
        {
            p.Start(); var stdout = p.StandardOutput.ReadToEndAsync(ct); var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct); var text = await stdout; await stderr;
            var caps = p.ExitCode == 0 ? JsonSerializer.Deserialize(text, ApiJson.Default.ReconstructionCapabilities) : null;
            qualityVideoAvailable = caps?.HighQualityVideo == true;
            return caps?.HighQuality == true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException) { return false; }
        finally { try { if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(CancellationToken.None); } } catch (InvalidOperationException) { } }
    }
    private Process ProcessFor(bool highQuality = false)
    {
        var executable = highQuality ? qualityPython! : engine;
        var p = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(highQuality ? qualityWorker! : engine)!, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 } };
        if (highQuality) p.StartInfo.ArgumentList.Add(qualityWorker!);
        // Bound compressed image allocation before OpenCV initializes its decoder limits.
        p.StartInfo.Environment["OPENCV_IO_MAX_IMAGE_PIXELS"] = "80000000";
        p.StartInfo.Environment["OPENCV_IO_MAX_IMAGE_WIDTH"] = "16000";
        p.StartInfo.Environment["OPENCV_IO_MAX_IMAGE_HEIGHT"] = "16000";
        return p;
    }
    public Dictionary<string, byte[]> BackupFiles(RequestActor actor)
    {
        store.RequireAdmin(actor);
        lock (gate) return jobs.Values.ToDictionary(x => "reconstruction/" + x.Job.Id.ToString("N") + "/job.json", x => JsonSerializer.SerializeToUtf8Bytes(x, ReconstructionJson.Default.ReconstructionWork));
    }
    public List<ReconstructionJob> List(RequestActor actor)
    { lock (gate) return jobs.Values.Where(x => x.Job.UserId == actor.Id).Select(x => x.Job).OrderByDescending(x => x.CreatedUtc).Take(100).ToList(); }
    public byte[] QualityReport(Guid id, RequestActor actor)
    {
        lock (gate)
        {
            var work = Own(id, actor); var path = Path.Combine(Home(id), "model.quality.json");
            if (work.Job.State != "completed" || !File.Exists(path) || new FileInfo(path).Length > 65536) throw new StoreException(404, "此任务没有可用的质量报告。");
            return File.ReadAllBytes(path);
        }
    }
    private ReconstructionWork Own(Guid id, RequestActor actor)
    { if (!jobs.TryGetValue(id, out var work) || work.Job.UserId != actor.Id) throw new StoreException(404, "重建任务不存在。"); return work; }
    public async Task<ReconstructionJob> Create(RequestActor actor, StartReconstruction request, CancellationToken ct)
    {
        if (request.ClientRequestId.HasValue)
        {
            lock (gate)
            {
                var prior = jobs.Values.FirstOrDefault(x => x.Job.UserId == actor.Id && x.Request.ClientRequestId == request.ClientRequestId);
                if (prior != null) return prior.Job;
            }
        }
        if (request.Photos == null || (request.Video != null ? request.Photos.Count != 0 : request.Photos.Count is < 6 or > 120 || request.Photos.Select(x => x.Id).Distinct().Count() != request.Photos.Count)) throw new StoreException(400, "请选择 6–120 张不重复的原始照片，或单独选择一个原视频。");
        if (request.Backend is not ("auto" or "cpu" or "vulkan") || request.Quality is < 0 or > 3 || request.Subject is not ("object" or "scene")) throw new StoreException(400, "重建参数无效。");
        if (request.Quality >= 2 && (request.Backend == "vulkan" || request.Subject != "object")) throw new StoreException(400, "高精度和轮廓模式目前支持 CPU 物体重建。");
        if (request.Video != null && request.Quality < 2) throw new StoreException(400, "原视频重建需要选择高精度或轮廓模式。");
        var name = CloudPath.Normalize(request.Name.Trim()); if (name.Contains('/') || name.Length > 100) throw new StoreException(400, "请输入不超过 100 字符的模型名称。");
        request = request with { Name = name };
        var caps = await Capabilities(ct); if (!caps.Cpu || request.Backend == "vulkan" && !caps.Vulkan) throw new StoreException(409, caps.Error ?? "服务端不支持指定的计算后端。");
        if (request.Quality >= 2 && !caps.HighQuality) throw new StoreException(409, "服务端尚未配置高精度重建工作进程。");
        if (request.Video != null && !caps.HighQualityVideo) throw new StoreException(409, "服务端尚未配置原视频选帧工具。");
        Validate(actor, request);
        lock (gate)
        {
            if (request.ClientRequestId.HasValue)
            {
                var prior = jobs.Values.FirstOrDefault(x => x.Job.UserId == actor.Id && x.Request.ClientRequestId == request.ClientRequestId);
                if (prior != null) return prior.Job;
            }
            if (jobs.Values.Count(x => x.Job.State is "queued" or "running") >= 16 || jobs.Values.Count(x => x.Job.UserId == actor.Id && x.Job.State is "queued" or "running") >= 2) throw new StoreException(429, "重建任务较多，请稍后再试。");
            if (jobs.Count >= 2000) throw new StoreException(409, "请先清理历史重建任务。");
            var now = DateTimeOffset.UtcNow; var job = new ReconstructionJob(Guid.NewGuid(), actor.Id, actor.Username, name, "queued", 0, "等待重建", request.Backend, request.Subject, request.Photos.Count, now, now, Quality: request.Quality);
            var work = new ReconstructionWork(job, request, actor.WebAdmin); jobs.Add(job.Id, work); Save(work);
            if (!queue.Writer.TryWrite(job.Id)) { jobs.Remove(job.Id); Directory.Delete(Home(job.Id), true); throw new StoreException(429, "重建队列已满。"); }
            return job;
        }
    }
    private string Validate(RequestActor actor, StartReconstruction request)
    {
        var entries = store.Snapshot(actor).Entries.ToDictionary(x => x.Id);
        if (!entries.TryGetValue(request.FolderId, out var folder) || !folder.IsDirectory || (folder.Access & 2) == 0) throw new StoreException(403, "模型目录不可写。");
        long total = 0;
        IEnumerable<ReconstructionPhoto> sources = request.Video is { } video ? [video] : request.Photos;
        foreach (var photo in sources)
        {
            if (!entries.TryGetValue(photo.Id, out var entry) || entry.IsDirectory || entry.Version != photo.Version || (entry.Access & 1) == 0) throw new StoreException(409, "原始照片已变更、删除或无读取权限。");
            var extension = Path.GetExtension(entry.Path).ToLowerInvariant();
            if (request.Video != null)
            { if (extension is not (".mp4" or ".mov" or ".m4v" or ".webm") || entry.Size > 512L * 1024 * 1024) throw new StoreException(400, "原视频仅支持不超过 512 MB 的 MP4/MOV/M4V/WebM。"); }
            else if (extension is not (".jpg" or ".jpeg" or ".png") || entry.Size > 40 * 1024 * 1024) throw new StoreException(400, "原始照片仅支持不超过 40 MB 的 JPEG/PNG。");
            total = checked(total + entry.Size);
        }
        if (total > 512L * 1024 * 1024) throw new StoreException(413, "每次重建原片合计不得超过 512 MB。");
        return folder.Path;
    }
    public void Cancel(Guid id, RequestActor actor, bool purge = false)
    {
        lock (gate)
        {
            var work = Own(id, actor);
            if (purge) { if (work.Job.State is "queued" or "running") throw new StoreException(409, "请先取消运行中的任务。"); if (cancellation.ContainsKey(id)) throw new StoreException(409, "任务正在结束，请稍后清理。"); Directory.Delete(Home(id), true); jobs.Remove(id); return; }
            if (work.Job.State is not ("queued" or "running")) return;
            if (cancellation.TryGetValue(id, out var stop)) stop.Cancel();
            Update(id, "cancelled", work.Job.Progress, "已取消");
        }
    }
    private void Update(Guid id, string state, int progress, string message, CloudEntry? model = null)
    {
        lock (gate)
        {
            var work = jobs[id]; if (work.Job.State == "cancelled" && state != "cancelled") return;
            work = work with { Job = work.Job with { State = state, Progress = Math.Clamp(progress, 0, 100), Message = message[..Math.Min(message.Length, 600)], UpdatedUtc = DateTimeOffset.UtcNow, Model = model } };
            jobs[id] = work; Save(work);
        }
    }
    private async Task Run()
    {
        try
        {
            await foreach (var id in queue.Reader.ReadAllAsync(stopping.Token))
            {
                ReconstructionWork work; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token); timeout.CancelAfter(TimeSpan.FromMinutes(30));
                lock (gate) { if (!jobs.TryGetValue(id, out work!)) continue; if (work.Job.State == "cancelled") continue; cancellation[id] = timeout; }
                if (work.Request.Quality >= 2) timeout.CancelAfter(TimeSpan.FromMinutes(90));
                var input = Path.Combine(Home(id), "input"); var output = Path.Combine(Home(id), "model.glb");
                try
                {
                    var actor = store.ReconstructionActor(work.Job.UserId, work.WebAdmin); var destination = Validate(actor, work.Request);
                    Directory.CreateDirectory(input); Update(id, "running", 0, "准备原始照片");
                    var sources = work.Request.Video is { } video ? new List<ReconstructionPhoto> { video } : work.Request.Photos;
                    for (var i = 0; i < sources.Count; i++)
                    {
                        var photo = sources[i]; var download = await store.Download(actor, photo.Id, photo.Version, timeout.Token); await using var source = download.Stream;
                        await using var file = File.Create(Path.Combine(input, (work.Request.Video != null ? "video" : $"view-{i:D3}") + Path.GetExtension(download.Entry.Path).ToLowerInvariant())); await source.CopyToAsync(file, timeout.Token);
                    }
                    using var p = ProcessFor(work.Request.Quality >= 2); p.StartInfo.ArgumentList.Add(input); p.StartInfo.ArgumentList.Add(output); p.StartInfo.ArgumentList.Add(work.Request.Backend); p.StartInfo.ArgumentList.Add(work.Request.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture)); p.StartInfo.ArgumentList.Add(work.Request.Subject); p.Start();
                    var lastError = "";
                    var errors = Task.Run(async () => { while (await p.StandardError.ReadLineAsync(timeout.Token) is { } line) lastError = line[..Math.Min(line.Length, 600)]; }, timeout.Token);
                    var progress = Task.Run(async () => { var last = -1; while (await p.StandardOutput.ReadLineAsync(timeout.Token) is { } line) { var parts = line.Split('\t', 2); if (parts.Length == 2 && int.TryParse(parts[0], out var n) && n != last) { last = n; Update(id, "running", n, ReconstructionMessages.Progress(parts[1])); } } }, timeout.Token);
                    try { await p.WaitForExitAsync(timeout.Token); await Task.WhenAll(errors, progress); }
                    finally { if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(CancellationToken.None); } }
                    if (p.ExitCode != 0 || !File.Exists(output)) throw new IOException("重建未完成：" + (lastError.Length == 0 ? "照片重叠或纹理不足，请补拍。" : ReconstructionMessages.Error(lastError)));
                    var qualityReport = Path.ChangeExtension(output, ".quality.json");
                    if (work.Request.Video != null && File.Exists(qualityReport) && new FileInfo(qualityReport).Length < 65536)
                    {
                        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(qualityReport, timeout.Token));
                        if (report.RootElement.TryGetProperty("inputPhotos", out var count) && count.TryGetInt32(out var n) && n is >= 6 and <= 120)
                            lock (gate) { var current = jobs[id]; work = current with { Job = current.Job with { PhotoCount = n } }; jobs[id] = work; Save(work); }
                    }
                    timeout.Token.ThrowIfCancellationRequested(); actor = store.ReconstructionActor(work.Job.UserId, work.WebAdmin); destination = Validate(actor, work.Request);
                    await using var modelStream = File.OpenRead(output);
                    var path = destination + "/" + work.Job.Name + "-" + id.ToString("N")[..8] + ".glb";
                    var metadata = new MobileUploadMetadata("model", ProjectFolderId: work.Request.FolderId, Project: destination,
                        Note: $"三维重建 · {work.Job.PhotoCount} 张原片 · {(work.Request.Quality >= 2 ? "CPU 高精度" : work.Request.Backend)} · {(work.Request.Quality == 3 ? "轮廓外形估计，未恢复内凹和未拍摄面" : work.Request.Subject)} · 任务 {id}", DeviceName: "Yunsevo server");
                    var model = await store.Upload(actor, path, modelStream, modelStream.Length, null, true, 256 * 1024 * 1024, timeout.Token, metadata);
                    // Commit and cancellation have one clear boundary; never hide a published model as a cancelled task.
                    lock (gate) { var current = jobs[id]; jobs[id] = current with { Job = current.Job with { State = "running" } }; Update(id, "completed", 100, work.Request.Quality == 3 ? "轮廓外形已生成；内凹和未拍摄面不属于实测结果。" : "模型已生成，请旋转检查表面与贴图。", model); }
                }
                catch (OperationCanceledException) { Update(id, "failed", 0, stopping.IsCancellationRequested ? "服务关闭，任务未完成。" : $"任务已取消或超过 {(work.Request.Quality >= 2 ? 90 : 30)} 分钟。请减少照片数量后重试。"); }
                catch (Exception ex) { var diagnostic = ex.ToString(); Console.Error.WriteLine("Reconstruction " + id + ": " + diagnostic[..Math.Min(diagnostic.Length, 4000)]); Update(id, "failed", 0, ex is StoreException or IOException ? ex.Message : "重建引擎异常，请检查服务端运行包。"); }
                finally
                {
                    ReconstructionJob? final; lock (gate) { cancellation.Remove(id); final = jobs.GetValueOrDefault(id)?.Job; }
                    if (final != null) audit.Write(new(DateTimeOffset.UtcNow, work.Job.Username, "Reconstruction", "GENERATE_MODEL", final.Model?.Path ?? work.Job.Name, final.State == "completed" ? 200 : final.State == "cancelled" ? 499 : 422, final.Message, "Yunsevo server"));
                    try { if (Directory.Exists(input)) Directory.Delete(input, true); if (File.Exists(output)) File.Delete(output); if (File.Exists(output + ".partial")) File.Delete(output + ".partial"); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Terminal task retains its bounded scratch directory for administrator cleanup. */ }
                    try
                    {
                        var scratch = Path.GetFullPath(Path.Combine(Home(id), "quality-work"));
                        if (Path.GetDirectoryName(scratch) == Path.GetFullPath(Home(id)) && Directory.Exists(scratch)) Directory.Delete(scratch, true);
                        var partial = Path.ChangeExtension(output, ".partial"); if (File.Exists(partial)) File.Delete(partial);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { stopping.Cancel(); queue.Writer.TryComplete(); await worker; stopping.Dispose(); }
}

public sealed partial class CloudStore
{
    internal RequestActor ReconstructionActor(Guid id, bool webAdmin) => (ActorFor(id) ?? throw new StoreException(403, "账号已停用。")) with { WebAdmin = webAdmin };
}
