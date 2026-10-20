using System.Text;
using System.Text.Json;
using Yunsevo.Contracts;

namespace Yunsevo.Mobile.Core;

public sealed class MobileRepository
{
    private readonly object gate = new();
    private MobileState state;
    public string Home { get; }
    public event Action? Changed;
    private string StatePath => Path.Combine(Home, "mobile-state.json");
    public MobileRepository(string home)
    {
        Home = Path.GetFullPath(home); Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Path.Combine(Home, "outbox"));
        state = File.Exists(StatePath) ? JsonSerializer.Deserialize(File.ReadAllBytes(StatePath), MobileJson.Default.MobileState)
            ?? throw new InvalidDataException("手机同步状态为空，请保留应用数据。")
            : new(Guid.NewGuid().ToString("N"), [], [], []);
        if (state.Options == null || state.Jobs == null || state.AlbumReceipts == null)
            throw new InvalidDataException("手机同步状态损坏，请保留应用数据。");
    }
    public string DeviceId { get { lock (gate) return state.DeviceId; } }
    public MobileOptions Options(string account) { lock (gate) return state.Options.GetValueOrDefault(account) ?? new(); }
    public void SetOptions(string account, MobileOptions options) => Edit(s => s with { Options = new(s.Options) { [account] = options } });
    public List<MobileJob> Jobs(string account) { lock (gate) return state.Jobs.Where(x => x.Account == account).OrderByDescending(x => x.CreatedUtc).ToList(); }
    public string LocalPath(MobileJob job)
    {
        if (!Guid.TryParseExact(job.LocalFile, "N", out _)) throw new InvalidDataException("待上传文件标识无效。");
        return Path.Combine(Home, "outbox", job.LocalFile);
    }
    public bool HasAlbum(string account, string key, string revision, Guid target)
    {
        var scoped = account + "/" + target.ToString("N") + "/" + key;
        lock (gate) return state.AlbumReceipts.GetValueOrDefault(scoped) == revision ||
            state.Jobs.Any(x => x.Account == account && x.Folder.Id == target && x.AlbumKey == key && x.AlbumRevision == revision && x.State != "cancelled");
    }
    public void Add(MobileJob job) => Edit(s => s with { Jobs = [.. s.Jobs, job] });
    public void Update(MobileJob job) => Edit(s =>
    {
        var receipts = new Dictionary<string, string>(s.AlbumReceipts);
        if (job.State == "completed" && job.AlbumKey != null)
            receipts[job.Account + "/" + job.Folder.Id.ToString("N") + "/" + job.AlbumKey] = job.AlbumRevision!;
        var jobs = s.Jobs.Select(x => x.Id == job.Id ? job : x).ToList();
        var remove = jobs.Where(x => x.State is "completed" or "cancelled").OrderByDescending(x => x.CreatedUtc).Skip(200).Select(x => x.Id).ToHashSet();
        return s with { Jobs = jobs.Where(x => !remove.Contains(x.Id)).ToList(), AlbumReceipts = receipts };
    });
    public void Retry(string account)
    {
        Edit(s => s with { Jobs = s.Jobs.Select(x => x.Account == account && x.State == "failed"
            ? x with { State = "pending", RetryUtc = null, Error = null, Attempts = 0 } : x).ToList() });
    }
    private void Edit(Func<MobileState, MobileState> operation)
    {
        lock (gate)
        {
            var next = operation(state);
            AtomicFile.WriteText(StatePath, JsonSerializer.Serialize(next, MobileJson.Default.MobileState));
            state = next;
        }
        Changed?.Invoke();
    }
    public static string FileName(string value)
    {
        value = value.Replace('\\', '/').Split('/')[^1];
        var safe = new string(value.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "文件";
        if (safe.Length > 150) safe = safe[..135] + Path.GetExtension(safe)[..Math.Min(Path.GetExtension(safe).Length, 12)];
        try { return CloudPath.Normalize(safe); } catch (ArgumentException) { return "_" + safe; }
    }
    public static string Watermark(string project, string photographer, DateTimeOffset captured, MobileOptions options, string? location)
        => string.Join("\n", new[] { "沄序 · 工程记录", "工程：" + project,
            string.IsNullOrWhiteSpace(options.Place) ? null : "部位：" + options.Place,
            "拍摄人：" + photographer, "时间：" + captured.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
            string.IsNullOrWhiteSpace(location) ? null : "位置：" + location,
            string.IsNullOrWhiteSpace(options.Note) ? null : "备注：" + options.Note }.Where(x => x != null));
}
