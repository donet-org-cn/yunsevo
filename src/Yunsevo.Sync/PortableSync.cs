using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yunsevo.Contracts;

namespace Yunsevo.Sync;

public sealed record LocalBaseline(CloudEntry Entry, long Length, long WriteTicks, string Hash);
public sealed record PortableState(string Server, Guid UserId, string Remote, string Root, Guid? RemoteId, List<LocalBaseline> Entries)
{
    public List<Guid> LocalOnlyDeletions { get; init; } = [];
    public Guid JournalGeneration { get; init; }
}
[JsonSerializable(typeof(PortableState))]
[JsonSerializable(typeof(LocalBaseline))]
internal partial class PortableJson : JsonSerializerContext;

// Full local copies for macOS/Linux. Windows uses Cloud Files through the Windows engine instead.
public sealed class PortableSync : IDisposable
{
    private readonly CloudConnection connection;
    private readonly string server, remote, root, statePath;
    private readonly Guid userId;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<Guid, LocalBaseline> known;
    private readonly StateJournal<LocalBaseline> journal;
    private Guid? remoteId;
    private readonly ConfirmDeletion? confirmDeletion;
    private readonly HashSet<Guid> localOnly;
    private bool firstPass = true;
    private bool localOnlyDirty;
    private readonly DeletionSafety deletions;
    private string? lastSummary;
    private readonly Dictionary<string, string> issues = new(StringComparer.OrdinalIgnoreCase);
    public PortableSync(CloudConnection connection, string server, Guid userId, string remote, string root, string statePath, Action<string> log, ConfirmDeletion? confirmDeletion = null, Guid? expectedRemoteId = null)
    {
        this.connection = connection; this.server = server.TrimEnd('/'); this.userId = userId;
        this.confirmDeletion = confirmDeletion;
        this.remote = remote.Length == 0 ? "" : CloudPath.Normalize(remote);
        this.root = Path.GetFullPath(root); this.statePath = statePath; this.log = log;
        if (Path.GetPathRoot(this.root) == this.root) throw new ArgumentException("不能使用磁盘根目录同步。");
        var old = File.Exists(statePath) ? JsonSerializer.Deserialize(File.ReadAllText(statePath), PortableJson.Default.PortableState) ?? throw new InvalidDataException("同步状态损坏。") : null;
        if (old != null && (old.Server != this.server || old.UserId != userId || old.Remote != this.remote || old.Root != this.root)) throw new InvalidOperationException("同步状态属于其他账户或目录。");
        if (old == null && Directory.Exists(this.root) && Directory.EnumerateFileSystemEntries(this.root).Any()) throw new InvalidOperationException("首次同步请选择空的本地目录，已有文件已保留。");
        for (var parent = new DirectoryInfo(this.root); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("同步目录不能位于符号链接内。");
        Directory.CreateDirectory(this.root); known = (old?.Entries ?? []).ToDictionary(x => x.Entry.Id); remoteId = old?.RemoteId ?? expectedRemoteId;
        journal = new(statePath + ".journal", old?.JournalGeneration ?? Guid.Empty, PortableJson.Default.LocalBaseline);
        foreach (var checkpoint in journal.Replay())
        {
            foreach (var replaced in known.Values.Where(x => x.Entry.Id != checkpoint.Entry.Id && x.Entry.Path.Equals(checkpoint.Entry.Path, StringComparison.OrdinalIgnoreCase)).ToArray()) known.Remove(replaced.Entry.Id);
            known[checkpoint.Entry.Id] = checkpoint;
        }
        localOnly = [.. old?.LocalOnlyDeletions ?? []];
        deletions = new(this.root);
        connection.ConnectionChanged += deletions.ConnectionChanged;
    }
    private string Full(string relative)
    {
        CloudPath.Normalize(relative);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("路径超出同步目录。");
        return full;
    }
    private string Remote(string path) => remote.Length == 0 ? path : remote + "/" + path;
    private async Task<Catalog> Catalog(CancellationToken ct)
    {
        var catalog = await connection.Catalog(remote, ct);
        if (remote.Length == 0) return catalog;
        var folder = catalog.Entries.Find(x => x.Path.Equals(remote, StringComparison.OrdinalIgnoreCase));
        if (remoteId.HasValue && folder?.Id != remoteId) throw new InvalidOperationException("已绑定的云端目录被删除、重命名或无权访问，同步已停止，本地文件已保留。");
        if (folder == null)
        {
            var prefix = "";
            foreach (var part in remote.Split('/'))
            {
                prefix = prefix.Length == 0 ? part : prefix + "/" + part;
                if (catalog.Entries.Any(x => x.IsDirectory && x.Path.Equals(prefix, StringComparison.OrdinalIgnoreCase))) continue;
                using var response = await connection.Http.PostAsJsonAsync("api/folders", new CreateFolder(prefix), ApiJson.Default.CreateFolder, ct);
                if (response.StatusCode != HttpStatusCode.Conflict) await CloudConnection.Check(response, ct);
                catalog = await connection.Catalog(remote, ct);
            }
            folder = catalog.Entries.Find(x => x.Path.Equals(remote, StringComparison.OrdinalIgnoreCase));
        }
        if (folder is not { IsDirectory: true }) throw new InvalidOperationException("所选云端目录不存在或不是文件夹。");
        if (remoteId != folder.Id) { remoteId = folder.Id; Save(); }
        return new(catalog.Revision, catalog.Entries.Where(x => CloudPath.IsUnder(x.Path, remote)).Select(x => x with { Path = x.Path[(remote.Length + 1)..] }).ToList());
    }
    private void Save()
    {
        var generation = Guid.NewGuid();
        AtomicFile.WriteText(statePath, JsonSerializer.Serialize(new PortableState(server, userId, remote, root, remoteId, [.. known.Values]) { LocalOnlyDeletions = [.. localOnly], JournalGeneration = generation }, PortableJson.Default.PortableState));
        journal.Reset(generation);
        localOnlyDirty = false;
    }
    private static async Task<string> Hash(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    private async Task Remember(CloudEntry entry, CancellationToken ct)
    {
        if (known.TryGetValue(entry.Id, out var unchanged) && unchanged.Entry == entry && entry.IsDirectory) return;
        var file = new FileInfo(Full(entry.Path));
        Remember(new LocalBaseline(entry, entry.IsDirectory ? 0 : file.Length, entry.IsDirectory ? 0 : file.LastWriteTimeUtc.Ticks,
            entry.IsDirectory ? "" : await Hash(file.FullName, ct)));
    }
    private void Remember(LocalBaseline baseline)
    {
        foreach (var old in known.Values.Where(x => x.Entry.Id != baseline.Entry.Id && x.Entry.Path.Equals(baseline.Entry.Path, StringComparison.OrdinalIgnoreCase)).ToArray()) known.Remove(old.Entry.Id);
        journal.Append(baseline); known[baseline.Entry.Id] = baseline;
        FileTransfers.Acknowledge(connection, Remote(baseline.Entry.Path), baseline.Entry, userId.ToString("N"), statePath + ".transfers");
        if (journal.Count >= 32) Save();
    }
    private static async Task<bool> Changed(string path, LocalBaseline baseline, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (file.Length == baseline.Length && file.LastWriteTimeUtc.Ticks == baseline.WriteTicks) return false;
        return await Hash(path, ct) != baseline.Hash;
    }
    private async Task<LocalBaseline> Upload(string relative, string? version, CancellationToken ct)
    {
        await using var stream = new FileStream(Full(relative), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var length = stream.Length; var writeTicks = File.GetLastWriteTimeUtc(Full(relative)).Ticks;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); stream.Position = 0;
        var uploaded = await FileTransfers.Upload(connection, Remote(relative), stream, version, userId.ToString("N"), statePath + ".transfers", ct, sha256: hash);
        return new(uploaded with { Path = relative }, length, writeTicks, hash);
    }
    private async Task<(LocalBaseline Baseline, string Temp)> StageDownload(CloudEntry entry, CancellationToken ct)
    {
        var path = Full(entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cache = Path.Combine(Path.GetDirectoryName(root)!, ".yunsevo-transfers-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(statePath + root)))[..24]);
        var resumable = true;
        try { Directory.CreateDirectory(cache); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cache = Path.GetDirectoryName(path)!; resumable = false; }
        if (!SameVolume(cache, path)) { cache = Path.GetDirectoryName(path)!; resumable = false; }
        var identity = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.Id + "\n" + entry.Version)));
        var temp = Path.Combine(cache, resumable ? identity + ".part" : ".yunsevo-download-" + Guid.NewGuid().ToString("N"));
        await TransferLimits.Downloads.WaitAsync(ct);
        try
        {
            await using var output = new FileStream(temp, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (output.Length > entry.Size) output.SetLength(0);
            var offset = output.Length;
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/content/" + entry.Id + "?version=" + entry.Version);
            if (offset > 0 && offset < entry.Size) request.Headers.Range = new(offset, entry.Size - 1);
            using var response = await connection.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await CloudConnection.Check(response, ct);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.To != entry.Size - 1) throw new InvalidDataException("下载范围不匹配。");
            }
            else { offset = 0; output.SetLength(0); }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(131072);
            try
            {
                output.Position = 0; var remaining = offset;
                while (remaining > 0)
                {
                    var read = await output.ReadAsync(buffer.AsMemory(0, (int)Math.Min(131072, remaining)), ct);
                    if (read == 0) throw new IOException("下载断点文件不完整。");
                    hash.AppendData(buffer, 0, read); remaining -= read;
                }
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(131072, Math.Max(1, entry.Size - output.Position + 1))), ct)) > 0)
                {
                    if (output.Position > entry.Size - count) throw new InvalidDataException("下载内容超出预期大小。");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct); hash.AppendData(buffer, 0, count);
                }
                output.Flush(true);
                if (output.Length != entry.Size) throw new InvalidDataException("下载内容长度不匹配。");
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
            await output.DisposeAsync();
            File.SetLastWriteTimeUtc(temp, entry.ModifiedUtc.UtcDateTime);
            return (new(entry, entry.Size, File.GetLastWriteTimeUtc(temp).Ticks, Convert.ToHexString(hash.GetHashAndReset())), temp);
        }
        catch { if (!resumable && File.Exists(temp)) File.Delete(temp); throw; }
        finally { TransferLimits.Downloads.Release(); }
    }
    private async Task PublishDownload(LocalBaseline baseline, string temp, CancellationToken ct)
    {
        var entry = baseline.Entry; var path = Full(entry.Path);
        if (File.Exists(path) && (!known.TryGetValue(entry.Id, out var old) || await Changed(path, old, ct))) File.Move(path, Conflict(entry.Path));
        File.Move(temp, path, true);
        Remember(baseline); // No cancellable await between the atomic move and its durable checkpoint.
    }
    private static bool SameVolume(string staging, string target)
    {
        if (OperatingSystem.IsWindows()) return string.Equals(Path.GetPathRoot(staging), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        try
        {
            var mounts = DriveInfo.GetDrives().Select(x => Path.TrimEndingDirectorySeparator(x.Name)).OrderByDescending(x => x.Length).ToArray();
            string? Volume(string path) => mounts.FirstOrDefault(mount => path.Equals(mount, StringComparison.Ordinal) || path.StartsWith(Path.EndsInDirectorySeparator(mount) ? mount : mount + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            var source = Volume(staging); return source != null && source == Volume(target);
        }
        catch (IOException) { return false; }
    }
    private string Conflict(string relative) => Full((CloudPath.Parent(relative) is { } p ? p + "/" : "") + Path.GetFileNameWithoutExtension(relative)[..Math.Min(100, Path.GetFileNameWithoutExtension(relative).Length)] + " (冲突 " + Guid.NewGuid().ToString("N")[..8] + ")" + Path.GetExtension(relative));
    private Dictionary<string, bool> Scan()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        void Visit(string path)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                var attrs = File.GetAttributes(child);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持同步符号链接：" + child);
                var relative = CloudPath.Normalize(Path.GetRelativePath(root, child).Replace(Path.DirectorySeparatorChar, '/'));
                var directory = (attrs & FileAttributes.Directory) != 0; result.Add(relative, directory); if (directory) Visit(child);
            }
        }
        Visit(root); return result; // A partial scan aborts the pass; absent files never become accidental cloud deletes.
    }
    public async Task SyncOnce(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool Blocked(string path) => blocked.Any(x => x.Equals(path, StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(path, x));
            void Fail(string path, Exception ex) { blocked.Add(path); if (!issues.TryGetValue(path, out var previous) || previous != ex.Message) log("项目待处理（其他文件继续同步）：" + path + " · " + ex.Message); issues[path] = ex.Message; }
            for (var pass = 0; pass < 100; pass++)
            {
                var catalog = await Catalog(ct); var disk = Scan(); var cloud = catalog.Entries.ToDictionary(x => x.Id); bool mutated = false;
                deletions.Present(disk.Keys);
                if (firstPass) foreach (var missing in known.Values.Where(x => !disk.ContainsKey(x.Entry.Path)))
                    _ = deletions.Request(missing.Entry.Path, missing.Entry.IsDirectory, true);
                foreach (var pair in known.Values.OrderBy(x => x.Entry.Path.Length).ToArray())
                {
                    if (Blocked(pair.Entry.Path)) continue;
                    try
                    {
                    if (!cloud.TryGetValue(pair.Entry.Id, out var current)) continue;
                    if (disk.ContainsKey(current.Path) && localOnly.Remove(current.Id)) localOnlyDirty = true;
                    if (localOnly.Contains(current.Id)) continue;
                    if (current.Path != pair.Entry.Path && disk.ContainsKey(pair.Entry.Path))
                    {
                        var target = Full(current.Path); if (File.Exists(target) || Directory.Exists(target)) throw new IOException("远端重命名与本地文件冲突：" + current.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (current.IsDirectory) Directory.Move(Full(pair.Entry.Path), target); else File.Move(Full(pair.Entry.Path), target);
                        foreach (var child in known.Values.Where(x => CloudPath.IsUnder(x.Entry.Path, pair.Entry.Path)).ToArray())
                            known[child.Entry.Id] = child with { Entry = child.Entry with { Path = current.Path + child.Entry.Path[pair.Entry.Path.Length..] } };
                        known[current.Id] = pair with { Entry = pair.Entry with { Path = current.Path } }; Save(); mutated = true; break;
                    }
                    if (disk.ContainsKey(current.Path)) continue;
                    if (current.Version != pair.Entry.Version || catalog.Entries.Any(x => CloudPath.IsUnder(x.Path, current.Path) && (!known.TryGetValue(x.Id, out var child) || child.Entry.Version != x.Version))) continue;
                    if ((current.Access & 2) == 0) continue;
                    var request = deletions.Request(current.Path, current.IsDirectory, firstPass);
                    if (confirmDeletion != null && !request.DetectedWhileOnline)
                    {
                        var choice = await confirmDeletion(request, ct);
                        if (choice == DeletionChoice.Defer) { blocked.Add(current.Path); continue; }
                        if (choice == DeletionChoice.LocalOnly)
                        {
                            foreach (var item in cloud.Values.Where(x => x.Id == current.Id || CloudPath.IsUnder(x.Path, current.Path))) localOnly.Add(item.Id);
                            deletions.Forget(current.Path); Save(); log("仅保留本地删除，云端文件保留：" + current.Path); continue;
                        }
                    }
                    using var deletion = new HttpRequestMessage(HttpMethod.Delete, "api/entries/" + current.Id);
                    deletion.Headers.IfMatch.Add(new('"' + current.Version + '"')); deletion.Headers.Add("X-Catalog-Revision", catalog.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    using var deleted = await connection.Http.SendAsync(deletion, ct); await CloudConnection.Check(deleted, ct);
                    foreach (var item in known.Values.Where(x => x.Entry.Id == current.Id || CloudPath.IsUnder(x.Entry.Path, current.Path)).ToArray()) known.Remove(item.Entry.Id);
                    deletions.Forget(current.Path); log("已移入云端回收站：" + current.Path);
                    Save(); mutated = true; break;
                    }
                    catch (Exception ex) when (ItemError(ex)) { Fail(pair.Entry.Path, ex); }
                }
                if (mutated) continue;
                var paths = catalog.Entries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
                var previousByPath = known.Values.ToDictionary(x => x.Entry.Path, StringComparer.OrdinalIgnoreCase);
                var uploads = new List<(string Path, string? Version)>();
                foreach (var local in disk.OrderBy(x => x.Key.Length))
                {
                    if (Blocked(local.Key)) continue;
                    try
                    {
                    paths.TryGetValue(local.Key, out var entry);
                    previousByPath.TryGetValue(local.Key, out var previous);
                    if (entry == null && previous != null)
                    {
                        if (!local.Value && await Changed(Full(local.Key), previous, ct))
                        { File.Move(Full(local.Key), Conflict(local.Key)); known.Remove(previous.Entry.Id); Save(); mutated = true; break; }
                        if (local.Value)
                        {
                            var dirty = false;
                            foreach (var child in disk.Where(x => !x.Value && CloudPath.IsUnder(x.Key, local.Key)))
                            {
                                var baseline = known.Values.FirstOrDefault(x => x.Entry.Path.Equals(child.Key, StringComparison.OrdinalIgnoreCase));
                                if (baseline == null || await Changed(Full(child.Key), baseline, ct)) { dirty = true; break; }
                            }
                            if (dirty)
                            {
                                using var response = await connection.Http.PostAsJsonAsync("api/folders", new CreateFolder(Remote(local.Key)), ApiJson.Default.CreateFolder, ct);
                                await CloudConnection.Check(response, ct);
                                var recreated = (await response.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry, ct))! with { Path = local.Key };
                                await Remember(recreated, ct); mutated = true; break;
                            }
                        }
                        if (local.Value && Directory.EnumerateFileSystemEntries(Full(local.Key)).Any()) continue;
                        if (local.Value) Directory.Delete(Full(local.Key)); else File.Delete(Full(local.Key)); known.Remove(previous.Entry.Id); Save(); mutated = true; break;
                    }
                    if (local.Value)
                    {
                        if (entry == null)
                        {
                            using var response = await connection.Http.PostAsJsonAsync("api/folders", new CreateFolder(Remote(local.Key)), ApiJson.Default.CreateFolder, ct); await CloudConnection.Check(response, ct);
                            entry = (await response.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry, ct))! with { Path = local.Key }; await Remember(entry, ct); mutated = true; break;
                        }
                        if (!entry.IsDirectory) throw new IOException("文件和目录同名冲突。");
                        await Remember(entry, ct); continue;
                    }
                    if (entry?.IsDirectory == true) throw new IOException("文件和目录同名冲突。");
                    var changed = previous == null || await Changed(Full(local.Key), previous, ct);
                    if (!changed) continue;
                    if (entry != null && (previous == null || previous.Entry.Version != entry.Version))
                    {
                        await using var recovering = new FileStream(Full(local.Key), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                        if (await FileTransfers.RecoverCompleted(connection, Remote(local.Key), recovering, entry, userId.ToString("N"), statePath + ".transfers", ct))
                        { await Remember(entry, ct); log("已恢复上传记录：" + local.Key); continue; }
                        await recovering.DisposeAsync(); File.Move(Full(local.Key), Conflict(local.Key)); mutated = true; break;
                    }
                    if (entry != null && (entry.Access & 2) == 0) { Fail(local.Key, new UnauthorizedAccessException("只读文件的本地修改已保留，未上传。")); continue; }
                    uploads.Add((local.Key, entry?.Version));
                    if (uploads.Count >= TransferLimits.BatchSize) break;
                    }
                    catch (Exception ex) when (ItemError(ex)) { Fail(local.Key, ex); }
                }
                if (uploads.Count > 0)
                {
                    var results = new (string Path, LocalBaseline? Baseline, Exception? Error)[uploads.Count];
                    await Parallel.ForEachAsync(Enumerable.Range(0, uploads.Count), new ParallelOptions { MaxDegreeOfParallelism = TransferLimits.Concurrency }, async (index, _) =>
                    {
                        var plan = uploads[index];
                        try { results[index] = (plan.Path, await Upload(plan.Path, plan.Version, ct), null); }
                        catch (Exception ex) { results[index] = (plan.Path, null, ex); }
                    });
                    var completed = results.Where(x => x.Baseline != null).Select(x => x.Baseline!).ToArray();
                    journal.AppendRange(completed);
                    foreach (var baseline in completed)
                    {
                        foreach (var replaced in known.Values.Where(x => x.Entry.Id != baseline.Entry.Id && x.Entry.Path.Equals(baseline.Entry.Path, StringComparison.OrdinalIgnoreCase)).ToArray()) known.Remove(replaced.Entry.Id);
                        known[baseline.Entry.Id] = baseline;
                        FileTransfers.Acknowledge(connection, Remote(baseline.Entry.Path), baseline.Entry, userId.ToString("N"), statePath + ".transfers");
                        log("已上传：" + baseline.Entry.Path); mutated = true;
                    }
                    if (journal.Count >= 32) Save();
                    foreach (var result in results.Where(x => x.Error != null))
                        if (ItemError(result.Error!)) Fail(result.Path, result.Error!); else System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Error!).Throw();
                }
                if (mutated) continue;
                var downloads = new List<CloudEntry>();
                foreach (var entry in catalog.Entries.OrderBy(x => x.Path.Length))
                {
                    if (Blocked(entry.Path) || localOnly.Contains(entry.Id)) continue;
                    try
                    {
                    if (entry.IsDirectory) { Directory.CreateDirectory(Full(entry.Path)); await Remember(entry, ct); continue; }
                    if (!known.TryGetValue(entry.Id, out var old) || old.Entry.Version != entry.Version || !File.Exists(Full(entry.Path)))
                    { downloads.Add(entry); }
                    }
                    catch (Exception ex) when (ItemError(ex)) { Fail(entry.Path, ex); }
                }
                foreach (var batch in downloads.Chunk(TransferLimits.BatchSize))
                {
                    var results = new (CloudEntry Entry, LocalBaseline? Baseline, string? Temp, Exception? Error)[batch.Length];
                    await Parallel.ForEachAsync(Enumerable.Range(0, batch.Length), new ParallelOptions { MaxDegreeOfParallelism = TransferLimits.Concurrency }, async (index, _) =>
                    {
                        try { var staged = await StageDownload(batch[index], ct); results[index] = (batch[index], staged.Baseline, staged.Temp, null); }
                        catch (Exception ex) { results[index] = (batch[index], null, null, ex); }
                    });
                    foreach (var item in results.Where(x => x.Baseline != null))
                    {
                        try { await PublishDownload(item.Baseline!, item.Temp!, ct); log("已下载：" + item.Entry.Path); }
                        catch (Exception ex) when (ItemError(ex)) { Fail(item.Entry.Path, ex); }
                    }
                    foreach (var item in results.Where(x => x.Error != null))
                        if (ItemError(item.Error!)) Fail(item.Entry.Path, item.Error!); else System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(item.Error!).Throw();
                }
                firstPass = false;
                deletions.CompletePass();
                if (localOnly.RemoveWhere(x => !cloud.ContainsKey(x)) > 0) localOnlyDirty = true;
                if (journal.Count > 0 || localOnlyDirty) Save();
                foreach (var resolved in issues.Keys.Where(x => !blocked.Contains(x)).ToArray()) issues.Remove(resolved);
                var summary = (blocked.Count == 0 ? "同步完成" : "同步部分完成 · " + blocked.Count + " 个项目待处理") + " · " + catalog.Entries.Count + " 个项目 · " + connection.Protocol;
                if (lastSummary != summary) { log(summary); lastSummary = summary; } return;
            }
        }
        catch { deletions.ConnectionChanged(false, false); throw; }
        finally { gate.Release(); }
    }
    private static bool ItemError(Exception ex) => ex is IOException or UnauthorizedAccessException || ex is HttpRequestException
        { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.InsufficientStorage or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed };
    public async Task Run(CancellationToken ct)
    {
        using var signals = new SyncSignals(root); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remoteEvents = connection.ObserveChanges(signals.Notify, linked.Token);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await SyncOnce(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception ex) { log("同步待重试：" + ex.Message); }
                try { await signals.Wait(ct); } catch (OperationCanceledException) { break; }
            }
        }
        finally { linked.Cancel(); await remoteEvents; }
    }
    public void Dispose() { connection.ConnectionChanged -= deletions.ConnectionChanged; deletions.Dispose(); gate.Dispose(); }
}
