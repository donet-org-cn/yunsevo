using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Net;
using Yunsevo.Contracts;
using Yunsevo.Sync;
using static Yunsevo.Client.CloudNative;

namespace Yunsevo.Client;

public sealed class SyncEngine : IDisposable
{
    private readonly ClientConfig config;
    private readonly string statePath;
    private readonly CloudApi api;
    private readonly CloudProvider provider;
    private readonly Action<string> log;
    private readonly ConfirmDeletion? confirmDeletion;
    private readonly HashSet<Guid> localOnly;
    private bool firstPass = true;
    private readonly DeletionSafety deletions;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<Guid, CloudEntry> tracked;
    private readonly StateJournal<CloudEntry> journal;
    private readonly Dictionary<Guid, uint> observedPin = [];
    private bool stateDirty;
    private Guid? savedRemoteDirectoryId;
    private string? lastSummary;
    private readonly Dictionary<string, string> reportedIssues = new(StringComparer.OrdinalIgnoreCase);
    private sealed record LocalItem(string Path, bool Directory, bool Placeholder, bool Clean, Guid? Id);
    private sealed class SyncPass
    {
        public Dictionary<string, string> Issues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Blocked(string path) => Issues.Keys.Any(blocked => blocked.Length == 0 ||
            path.Equals(blocked, StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(path, blocked));
        public bool Protected(string path) => Blocked(path) || Issues.Keys.Any(blocked => CloudPath.IsUnder(blocked, path));
        public void Fail(string path, Exception exception) => Issues[path] = exception.Message;
    }
    private static bool IsFileError(Exception exception) => exception is IOException or UnauthorizedAccessException or Win32Exception or COMException or ArgumentException ||
        exception is HttpRequestException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.Forbidden or HttpStatusCode.InsufficientStorage or HttpStatusCode.RequestEntityTooLarge };
    public CloudProvider Provider => provider;
    public event Action<bool, bool>? ConnectionChanged { add => api.ConnectionChanged += value; remove => api.ConnectionChanged -= value; }

    public static void ValidateLocalDirectory(ClientConfig config, string statePath) => _ = ReadState(config, statePath);

    private static SyncState? ReadState(ClientConfig config, string statePath)
    {
        config.Validate();
        var existing = File.Exists(statePath);
        var state = existing ? JsonSerializer.Deserialize(File.ReadAllText(statePath), ClientJson.Default.SyncState) : null;
        if (existing && state == null) throw new InvalidDataException("同步状态损坏，请保留目录并恢复状态备份。");
        if (state != null && (!state.SyncRoot.Equals(Path.GetFullPath(config.SyncRoot), StringComparison.OrdinalIgnoreCase) ||
            state.ServerUrl != config.ServerUrl.TrimEnd('/') || !state.RemoteDirectory.Equals(config.RemoteDirectory, StringComparison.OrdinalIgnoreCase) || state.AccountId != config.AccountId))
            throw new InvalidOperationException("服务器、云端目录或本地同步目录已变更。请使用新的配置文件和空文件夹，原目录会保留。");
        if (!existing && Directory.Exists(config.SyncRoot) && Directory.EnumerateFileSystemEntries(config.SyncRoot).Any())
            throw new InvalidOperationException($"本地目录“{config.SyncRoot}”已有文件，不能直接作为云端“/{config.RemoteDirectory}”的新同步目录。请为新云端目录选择新的空本地文件夹，或恢复原云端目录设置；原文件已保留。");
        // Do not traverse junctions, symbolic links, or another cloud provider's ancestor.
        for (var ancestor = new DirectoryInfo(config.SyncRoot); ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0 &&
                !(existing && ancestor.FullName.Equals(Path.GetFullPath(config.SyncRoot), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("同步目录及其父目录不能是链接或其他云盘目录。");
        return state;
    }

    public SyncEngine(ClientConfig config, string statePath, Action<string> log, ConfirmDeletion? confirmDeletion = null, Guid? expectedRemoteId = null)
    {
        var state = ReadState(config, statePath);
        var existing = state != null;
        this.config = config;
        this.statePath = statePath;
        this.log = log;
        this.confirmDeletion = confirmDeletion;
        localOnly = [.. state?.LocalOnlyDeletions ?? []];
        tracked = (state?.Entries ?? []).ToDictionary(x => x.Id);
        journal = new(statePath + ".journal", state?.JournalGeneration ?? Guid.Empty, ApiJson.Default.CloudEntry);
        foreach (var checkpoint in journal.Replay())
        {
            foreach (var replaced in tracked.Values.Where(x => x.Id != checkpoint.Id && x.Path.Equals(checkpoint.Path, StringComparison.OrdinalIgnoreCase)).ToArray()) tracked.Remove(replaced.Id);
            tracked[checkpoint.Id] = checkpoint;
        }
        api = new(config, state?.RemoteDirectoryId ?? expectedRemoteId);
        savedRemoteDirectoryId = state?.RemoteDirectoryId;
        try { provider = new(config.SyncRoot, api, log, existing); }
        catch { api.Dispose(); throw; }
        try { Save(); }
        catch
        {
            provider.Dispose(); api.Dispose();
            if (provider.CreatedRegistration) CloudProvider.Unregister(config.SyncRoot);
            throw;
        }
        deletions = new(config.SyncRoot);
        api.ConnectionChanged += deletions.ConnectionChanged;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        var generation = Guid.NewGuid();
        var value = new SyncState(config.ServerUrl.TrimEnd('/'), Path.GetFullPath(config.SyncRoot), [.. tracked.Values], config.RemoteDirectory, api.RemoteDirectoryId, config.AccountId) { LocalOnlyDeletions = [.. localOnly], JournalGeneration = generation };
        AtomicFile.WriteText(statePath, JsonSerializer.Serialize(value, ClientJson.Default.SyncState));
        journal.Reset(generation);
        savedRemoteDirectoryId = api.RemoteDirectoryId;
        stateDirty = false;
    }
    private void Remember(CloudEntry entry)
    {
        if (tracked.TryGetValue(entry.Id, out var same) && same == entry)
        { return; }
        foreach (var old in tracked.Values.Where(x => x.Id != entry.Id && x.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)).ToArray())
            tracked.Remove(old.Id);
        journal.Append(entry); tracked[entry.Id] = entry; stateDirty = true;
        api.Acknowledge(entry);
        if (journal.Count >= 32) Save();
    }
    private void Forget(CloudEntry entry)
    {
        foreach (var old in tracked.Values.Where(x => x.Id == entry.Id || CloudPath.IsUnder(x.Path, entry.Path)).ToArray()) tracked.Remove(old.Id);
        stateDirty = true;
        Save();
    }

    private Dictionary<string, LocalItem> Scan(SyncPass pass)
    {
        var items = new Dictionary<string, LocalItem>(StringComparer.OrdinalIgnoreCase);
        void Visit(string directory)
        {
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var relative = Path.GetRelativePath(config.SyncRoot, path).Replace('\\', '/');
                    if (pass.Blocked(relative)) continue;
                    try
                    {
                        CloudPath.Normalize(relative);
                        var (state, attributes, id) = Inspect(path);
                        var placeholder = state != uint.MaxValue && (state & 1) != 0;
                        if ((attributes & 0x400) != 0 && !placeholder) throw new IOException("不支持同步符号链接或目录联接：" + relative);
                        var isDirectory = (attributes & 0x10) != 0;
                        items.Add(relative, new(relative, isDirectory, placeholder, placeholder && (state & 8) != 0, id));
                        if (isDirectory) Visit(path);
                    }
                    catch (Exception ex) when (IsFileError(ex)) { pass.Fail(relative, ex); }
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                var relative = Path.GetRelativePath(config.SyncRoot, directory).Replace('\\', '/');
                pass.Fail(relative == "." ? "" : relative, ex);
            }
        }
        Visit(config.SyncRoot);
        return items;
    }

    public async Task SyncOnce(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            provider.SetStatus(0x10); // SYNC_INCREMENTAL
            var pass = new SyncPass();
            // Restart the snapshot after each server mutation; conditional writes catch intervening changes.
            for (var i = 0; i < 100; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!await Step(pass, ct)) { CompletePass(pass, false); return; }
            }
            CompletePass(pass, true);
        }
        catch { deletions.ConnectionChanged(false, false); provider.SetStatus(0xC0000002); throw; }
        finally { gate.Release(); }
    }

    private void CompletePass(SyncPass pass, bool more)
    {
        if (stateDirty) Save();
        firstPass = false;
        deletions.CompletePass();
        foreach (var resolved in reportedIssues.Keys.Where(path => !pass.Issues.ContainsKey(path))) SetItemStatus(resolved, 0);
        foreach (var (path, message) in pass.Issues)
        {
            SetItemStatus(path, 0x10);
            if (!reportedIssues.TryGetValue(path, out var old) || old != message)
                log($"项目待重试（其他文件继续同步）：{path} · {message}");
        }
        reportedIssues.Clear();
        foreach (var issue in pass.Issues) reportedIssues.Add(issue.Key, issue.Value);
        provider.SetStatus(pass.Issues.Count > 0 ? 0xC0000002u : more ? 0x10u : 1u);
        var summary = pass.Issues.Count > 0 ? $"同步部分完成 · {pass.Issues.Count} 个项目待重试 · {tracked.Count} 个已同步项目" :
            more ? "本批次已完成 100 项，下次继续同步。" : $"同步完成 · {tracked.Count} 个项目 · {(config.FilesOnDemand ? "按需下载" : "保留离线")}";
        if (summary != lastSummary) { log(summary); lastSummary = summary; }
    }

    private void SetItemStatus(string path, uint status)
    {
        if (path.Length == 0) return;
        try { _ = ShellTransfer.Set(provider.LocalPath(path), status); }
        catch (ArgumentException) { /* Invalid names stay in the error log, outside the Shell property store. */ }
    }

    private async Task<bool> Step(SyncPass pass, CancellationToken ct)
    {
        var catalog = await api.List(ct);
        if (savedRemoteDirectoryId != api.RemoteDirectoryId) Save();
        var remote = catalog.Entries.ToDictionary(x => x.Id);
        var paths = catalog.Entries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var disk = Scan(pass);
        deletions.Present(disk.Keys);
        if (firstPass) foreach (var missing in tracked.Values.Where(x => !disk.ContainsKey(x.Path)))
            _ = deletions.Request(missing.Path, missing.IsDirectory, true);
        foreach (var id in localOnly.Where(x => !remote.ContainsKey(x) || remote.TryGetValue(x, out var item) && disk.ContainsKey(item.Path)).ToArray()) { localOnly.Remove(id); stateDirty = true; }
        if (stateDirty) Save();
        var movedLocally = disk.Values.Where(local => local.Id.HasValue && tracked.TryGetValue(local.Id.Value, out var known) && local.Path != known.Path).ToArray();
        bool PendingMove(CloudEntry entry) => movedLocally.Any(local => local.Id == entry.Id ||
            local.Directory && tracked.TryGetValue(local.Id!.Value, out var parent) && CloudPath.IsUnder(entry.Path, parent.Path));

        // Apply remote renames before examining local deletions.
        foreach (var entry in catalog.Entries.OrderBy(x => x.Path.Length))
        {
            if (!tracked.TryGetValue(entry.Id, out var old) || old.Path == entry.Path) continue;
            if (pass.Protected(old.Path) || pass.Protected(entry.Path)) continue;
            try
            {
                if (disk.TryGetValue(old.Path, out var local) && local.Id == entry.Id)
                {
                    if (disk.ContainsKey(entry.Path) && !entry.Path.Equals(old.Path, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("移动冲突，请保留两处文件后处理：" + entry.Path);
                    var target = provider.LocalPath(entry.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (entry.IsDirectory) Directory.Move(provider.LocalPath(old.Path), target);
                    else File.Move(provider.LocalPath(old.Path), target);
                    foreach (var child in tracked.Values.Where(x => CloudPath.IsUnder(x.Path, old.Path)).ToArray())
                        tracked[child.Id] = child with { Path = entry.Path + child.Path[old.Path.Length..] };
                    // Keep old content version until the download/update below succeeds.
                    Remember(old with { Path = entry.Path });
                    return true;
                }
                if (!disk.ContainsKey(entry.Path)) throw new IOException("本地和云端同时移动了项目，请手动处理：" + old.Path);
            }
            catch (Exception ex) when (IsFileError(ex)) { pass.Fail(old.Path, ex); }
        }

        // A placeholder carries its immutable server ID through an Explorer rename.
        foreach (var local in disk.Values.OrderBy(x => x.Path.Length))
        {
            if (pass.Protected(local.Path)) continue;
            try
            {
                if (!local.Id.HasValue || !tracked.TryGetValue(local.Id.Value, out var old) ||
                    !remote.TryGetValue(old.Id, out var entry) || local.Path == entry.Path) continue;
                if (entry.Version != old.Version) throw new IOException("移动期间云端内容已变化：" + old.Path);
                var moved = await api.Move(entry, local.Path, ct);
                foreach (var child in tracked.Values.Where(x => CloudPath.IsUnder(x.Path, old.Path)).ToArray())
                    tracked[child.Id] = child with { Path = moved.Path + child.Path[old.Path.Length..] };
                Remember(moved);
                log("已同步重命名：" + moved.Path);
                return true;
            }
            catch (Exception ex) when (IsFileError(ex)) { pass.Fail(local.Path, ex); }
        }

        foreach (var old in tracked.Values.OrderBy(x => x.Path.Length).ToArray())
        {
            if (pass.Protected(old.Path)) continue;
            try
            {
                if (disk.ContainsKey(old.Path) || PendingMove(old)) continue;
                if (localOnly.Contains(old.Id)) continue;
                if (!remote.TryGetValue(old.Id, out var entry)) { Forget(old); return true; }
                // Missing locally but changed remotely: restore the current placeholder instead of deleting new data.
                var subtree = catalog.Entries.Where(x => x.Id == entry.Id || CloudPath.IsUnder(x.Path, entry.Path)).ToArray();
                if (subtree.Any(x => !tracked.TryGetValue(x.Id, out var known) || known.Version != x.Version || known.Path != x.Path))
                {
                    provider.Create(entry); Remember(entry); return true;
                }
                var request = deletions.Request(old.Path, old.IsDirectory, firstPass);
                if (confirmDeletion != null && !request.DetectedWhileOnline)
                {
                    var decision = await confirmDeletion(request, ct);
                    if (decision == DeletionChoice.Defer) { pass.Fail(old.Path, new IOException("删除操作等待确认。")); continue; }
                    if (decision == DeletionChoice.LocalOnly)
                    {
                        foreach (var item in subtree) localOnly.Add(item.Id);
                        deletions.Forget(old.Path); Save(); log("仅保留本地删除，云端文件保留：" + old.Path); continue;
                    }
                }
                await api.Delete(entry, catalog.Revision, ct);
                deletions.Forget(old.Path);
                Forget(old);
                log("已移入云端回收站：" + old.Path);
                return true;
            }
            catch (Exception ex) when (IsFileError(ex)) { pass.Fail(old.Path, ex); }
        }

        var uploadPlans = disk.Values.Where(local =>
        {
            if (local.Directory || pass.Blocked(local.Path)) return false;
            paths.TryGetValue(local.Path, out var entry);
            if (entry != null && (entry.IsDirectory || (entry.Access & 2) == 0 || local.Placeholder && local.Id == entry.Id && local.Clean || !tracked.TryGetValue(entry.Id, out var previous) || previous.Version != entry.Version)) return false;
            if (entry == null && local.Placeholder && local.Id.HasValue && (local.Clean && tracked.ContainsKey(local.Id.Value) || remote.ContainsKey(local.Id.Value))) return false;
            var parent = CloudPath.Parent(local.Path);
            return parent == null || paths.TryGetValue(parent, out var folder) && folder.IsDirectory;
        }).Take(TransferLimits.BatchSize).ToArray();
        if (uploadPlans.Length > 0)
        {
            var results = new (LocalItem Item, CloudEntry? Entry, Exception? Error)[uploadPlans.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, uploadPlans.Length), new ParallelOptions { MaxDegreeOfParallelism = TransferLimits.Concurrency }, async (index, _) =>
            {
                var item = uploadPlans[index]; paths.TryGetValue(item.Path, out var entry);
                try { results[index] = (item, await Upload(item, item.Path, entry?.Version, ct), null); }
                catch (Exception ex) { results[index] = (item, null, ex); }
            });
            var completed = results.Where(x => x.Entry != null).Select(x => x.Entry!).ToArray();
            journal.AppendRange(completed);
            foreach (var entry in completed)
            {
                foreach (var replaced in tracked.Values.Where(x => x.Id != entry.Id && x.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)).ToArray()) tracked.Remove(replaced.Id);
                tracked[entry.Id] = entry; stateDirty = true; api.Acknowledge(entry); log("已上传：" + entry.Path);
            }
            if (journal.Count >= 32) Save();
            foreach (var result in results.Where(x => x.Error != null))
                if (IsFileError(result.Error!)) pass.Fail(result.Item.Path, result.Error!); else System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Error!).Throw();
            if (results.Any(x => x.Entry != null)) return true;
        }

        foreach (var local in disk.Values.OrderBy(x => x.Path.Length))
        {
            if (pass.Blocked(local.Path)) continue;
            try
            {
                var hasRemote = paths.TryGetValue(local.Path, out var entry);
                if (hasRemote && entry!.IsDirectory != local.Directory) throw new IOException("文件与目录同名冲突：" + local.Path);
                if (hasRemote && local.Directory)
                {
                    if (!local.Placeholder) provider.AdoptDirectory(entry!);
                    else if (local.Id != entry!.Id) provider.Update(entry, false);
                    Remember(entry!); continue;
                }
                if (hasRemote && local.Placeholder && local.Id == entry!.Id && local.Clean) continue;

                if (!hasRemote && local.Placeholder && (local.Clean || local.Directory) && local.Id.HasValue && tracked.ContainsKey(local.Id.Value))
                {
                    if (remote.ContainsKey(local.Id.Value)) continue; // A pending rename still has a cloud counterpart.
                    if (pass.Protected(local.Path)) continue;
                    // Cloud-side deletion. Remove only clean descendants, and retain any edited local bytes.
                    var dirtyChildren = local.Directory && disk.Values.Any(x => CloudPath.IsUnder(x.Path, local.Path) && (!x.Placeholder || (!x.Directory && !x.Clean)));
                    if (!dirtyChildren)
                    {
                        if (local.Directory) Directory.Delete(provider.LocalPath(local.Path), true);
                        else File.Delete(provider.LocalPath(local.Path));
                        Forget(tracked[local.Id.Value]);
                        return true;
                    }
                }
                if (local.Directory)
                {
                    var folder = await api.Folder(local.Path, ct);
                    if (local.Placeholder) provider.Update(folder, false); else provider.AdoptDirectory(folder);
                    Remember(folder);
                    return true;
                }
                if (hasRemote && (!tracked.TryGetValue(entry!.Id, out var prior) || prior.Version != entry.Version))
                {
                    await using (var recovering = OpenUploadSource(provider.LocalPath(local.Path)))
                    {
                        if (await api.RecoverUpload(local.Path, recovering, entry, ct))
                        { provider.MarkUploaded(entry, recovering.SafeFileHandle.DangerousGetHandle(), local.Placeholder); Remember(entry); log("已恢复上传记录：" + local.Path); return true; }
                    }
                    // Both sides changed: upload the local bytes under a unique name before updating the original.
                    var conflictPath = ConflictPath(local.Path);
                    var conflictLocal = provider.LocalPath(conflictPath);
                    await using (var source = OpenUploadSource(provider.LocalPath(local.Path)))
                    {
                        var uploaded = await api.Upload(conflictPath, source, null, ct);
                        source.Position = 0;
                        await using (var copy = new FileStream(conflictLocal, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
                        {
                            await source.CopyToAsync(copy, ct);
                            copy.Flush(true);
                            provider.MarkUploaded(uploaded, copy.SafeFileHandle.DangerousGetHandle(), false);
                        }
                        Remember(uploaded);
                        provider.MarkPreserved(entry, source.SafeFileHandle.DangerousGetHandle(), local.Placeholder);
                    }
                    provider.Update(entry, true);
                    Remember(entry);
                    log("冲突副本已保留：" + conflictPath);
                    return true;
                }
                var result = await Upload(local, local.Path, entry?.Version, ct);
                Remember(result);
                log("已上传：" + local.Path);
                return true;
            }
            catch (Exception ex) when (IsFileError(ex)) { pass.Fail(local.Path, ex); }
        }

        foreach (var entry in catalog.Entries.OrderBy(x => x.Path.Length))
        {
            if (pass.Blocked(entry.Path) || PendingMove(entry) || localOnly.Contains(entry.Id)) continue;
            try
            {
                if (!disk.TryGetValue(entry.Path, out var local)) provider.Create(entry);
                else if (tracked.TryGetValue(entry.Id, out var prior) && prior.Version != entry.Version)
                    provider.Update(entry, !entry.IsDirectory);
                Remember(entry);
                var info = Inspect(provider.LocalPath(entry.Path));
                var pin = info.Attributes & 0x180000u;
                if (!entry.IsDirectory)
                {
                    if (!config.FilesOnDemand || (pin == 0x80000 && (info.State & 16) != 0)) provider.Pin(entry);
                    else if (pin == 0x100000 && observedPin.TryGetValue(entry.Id, out var previous) && previous != pin && (info.State & 8) != 0)
                        provider.Free(entry);
                }
                observedPin[entry.Id] = pin;
            }
            catch (Exception ex) when (IsFileError(ex)) { pass.Fail(entry.Path, ex); }
        }
        return false;
    }

    private async Task<CloudEntry> Upload(LocalItem item, string target, string? version, CancellationToken ct)
    {
        // Deny concurrent writes/renames until both the HTTP upload and placeholder commit complete.
        var path = provider.LocalPath(item.Path);
        await using var stream = OpenUploadSource(path);
        provider.PrepareUpload(path, stream.SafeFileHandle.DangerousGetHandle(), item.Placeholder);
        _ = ShellTransfer.Set(path, 4, 0, stream.Length);
        var entry = await api.Upload(target, stream, version, ct,
            (completed, total) => ShellTransfer.Set(path, 4, completed, total));
        if (target == item.Path) provider.MarkUploaded(entry, stream.SafeFileHandle.DangerousGetHandle(), true);
        return entry;
    }
    private static string ConflictPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var parent = CloudPath.Parent(path);
        return (parent == null ? "" : parent + "/") + name[..Math.Min(name.Length, 120)] +
            " (冲突 " + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ")" + extension;
    }

    public async Task SetOffline(string relativePath, bool offline, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var path = string.IsNullOrWhiteSpace(relativePath) ? null : CloudPath.Normalize(relativePath.Trim('/'));
            var entries = tracked.Values.Where(x => path == null || x.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                CloudPath.IsUnder(x.Path, path)).OrderBy(x => x.Path.Length).ToArray();
            if (entries.Length == 0) throw new IOException("未找到已同步的项目。");
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!offline && !entry.IsDirectory && !CloudProvider.State(provider.LocalPath(entry.Path)).InSync)
                    throw new IOException("文件尚未上传成功，不能释放空间：" + entry.Path);
                if (offline) provider.Pin(entry); else provider.Free(entry);
                observedPin[entry.Id] = offline ? 0x80000u : 0x100000u;
            }
            log(offline ? "所选项目已保留在此设备。" : "已释放所选项目的本地空间，文件仍在资源管理器可见。");
        }
        finally { gate.Release(); }
    }

    public async Task Run(CancellationToken ct)
    {
        using var signals = new SyncSignals(provider.Root);
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remoteEvents = api.ObserveChanges(signals.Notify, watching.Token);
        try
        {
        while (!ct.IsCancellationRequested)
        {
            try { await SyncOnce(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log("同步待重试：" + ex.Message); }
            try { await signals.Wait(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
        }
        finally { watching.Cancel(); await remoteEvents; }
    }
    public void Dispose() { api.ConnectionChanged -= deletions.ConnectionChanged; deletions.Dispose(); provider.Dispose(); api.Dispose(); gate.Dispose(); }
}
