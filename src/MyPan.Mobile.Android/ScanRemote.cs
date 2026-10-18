using System.Net.Http.Json;
using MyPan.Contracts;
using MyPan.Sync;
namespace MyPan.Mobile.Android;
internal static class ScanRemote
{
    private static readonly SemaphoreSlim Gate = new(1);
    public static async Task Pump(CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct)) return;
        try { foreach (var scan in ScanStore.List().Where(x => x.State is "uploading" or "remote")) await PollCore(scan, ct); }
        finally { Gate.Release(); }
    }
    public static async Task Poll(ScanSession scan, CancellationToken ct)
    { await Gate.WaitAsync(ct); try { await PollCore(scan, ct); } finally { Gate.Release(); } }
    private static async Task<List<ReconstructionJob>> RemoteJobs(CancellationToken ct)
    { using var r = await AppServices.Client.Connection!.Http.GetAsync("api/reconstruction/jobs", ct); await CloudConnection.Check(r, ct); return await r.Content.ReadFromJsonAsync(ApiJson.Default.ListReconstructionJob, ct) ?? []; }
    private static async Task PollCore(ScanSession scan, CancellationToken ct)
    {
        if (scan.State == "uploading")
        {
            var jobs = AppServices.Repository.Jobs(scan.Account); var sources = scan.UploadJobs?.Select(id => jobs.FirstOrDefault(x => x.Id == id)).ToList();
            if (sources == null || sources.Any(x => x?.Completed == null)) return;
            var videos = scan.SourceVideoJobs?.Select(id => jobs.FirstOrDefault(x => x.Id == id)).ToList();
            if (videos?.Any(x => x?.Completed == null) == true) return;
            var request = new StartReconstruction(scan.Folder.Id, scan.Name, sources.Select(x => new ReconstructionPhoto(x!.Completed!.Id, x.Completed.Version)).ToList(), Quality: scan.Quality, Subject: scan.Subject, ClientRequestId: scan.RemoteRequestId ?? scan.Id);
            var onlyVideoFrames = videos?.Count == 1 && scan.Inputs?.All(name => name.StartsWith("frame-" + Path.GetFileNameWithoutExtension(videos[0]!.Completed!.Path) + "-", StringComparison.Ordinal)) == true;
            if (scan.Quality > 0 && scan.Subject == "object")
            {
                using var capsResponse = await AppServices.Client.Connection!.Http.GetAsync("api/reconstruction/capabilities", ct); await CloudConnection.Check(capsResponse, ct);
                var caps = await capsResponse.Content.ReadFromJsonAsync(ApiJson.Default.ReconstructionCapabilities, ct);
                if (scan.Quality == 2 && caps?.HighQuality != true) throw new IOException("服务端未配置轮廓外形引擎，请使用手机本机生成或联系管理员。");
                if (caps?.HighQuality == true)
                    request = request with { Quality = scan.Quality == 2 ? 3 : 2, Backend = "cpu" };
                if (onlyVideoFrames && caps?.HighQualityVideo == true)
                {
                    var video = videos![0]!.Completed!;
                    request = request with { Photos = [], Video = new(video.Id, video.Version), Quality = scan.Quality == 2 ? 3 : 2, Backend = "cpu" };
                }
            }
            using var r = await AppServices.Client.Connection!.Http.PostAsJsonAsync("api/reconstruction/jobs", request, ApiJson.Default.StartReconstruction, ct); await CloudConnection.Check(r, ct);
            var job = await r.Content.ReadFromJsonAsync(ApiJson.Default.ReconstructionJob, ct) ?? throw new IOException("服务端任务响应为空。"); ScanStore.Edit(scan.Id, x => x with { State = "remote", RemoteJob = job.Id, Message = job.Message });
        }
        else if (scan.State == "remote")
        {
            var job = (await RemoteJobs(ct)).FirstOrDefault(x => x.Id == scan.RemoteJob); if (job == null) return;
            ScanStore.Edit(scan.Id, x => x with { State = job.State == "completed" ? "ready" : job.State is "failed" or "cancelled" ? "failed" : "remote", Progress = job.Progress, Message = job.Message });
        }
    }
}
