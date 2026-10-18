using MyPan.Contracts;

namespace MyPan.Server;

public sealed partial class CloudStore
{
    public List<SyncFolder> SyncFolders(RequestActor actor)
    {
        var snapshot = Volatile.Read(ref state);
        var permissions = new LivePermissions(snapshot, actor);
        // Use one immutable snapshot for both visibility and labels. Do not disclose
        // ACL principals or private folders that the caller cannot see.
        var visible = snapshot.Entries.Where(x => x.IsDirectory)
            .Select(x => (Folder: x, Access: permissions.Access(x.Path))).Where(x => (x.Access & 1) != 0).ToArray();
        var result = new Dictionary<Guid, SyncFolder>();
        foreach (var (folder, access) in visible)
        {
            var rule = permissions.Rule(folder.Path);
            var category = HomeOwner(snapshot, folder.Path) != null ? "private" : rule?.Visibility switch { "private" => "private", "public" => "public", _ => "assigned" };
            result[folder.Id] = new(folder.Id, folder.Path, category, access);
            for (var parent = CloudPath.Parent(folder.Path); parent != null; parent = CloudPath.Parent(parent))
            {
                if (!actor.WebAdmin && parent.Equals("个人", StringComparison.OrdinalIgnoreCase)) continue;
                if (permissions.Folders.TryGetValue(parent, out var ancestor)) result.TryAdd(ancestor.Id, new(ancestor.Id, ancestor.Path, "container", 8));
            }
        }
        return result.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
    private static List<CloudEntry> PermissionEntries(StoreState snapshot, bool trash, Guid? batch = null) => !trash ? snapshot.Entries : batch.HasValue
        ? snapshot.Trash.Where(x => x.Batch == batch).Select(x => x.Entry).Concat(snapshot.Entries).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList()
        : snapshot.Entries.Concat(snapshot.Trash.Select(x => x.Entry)).DistinctBy(x => x.Id).ToList();
    private static int Access(StoreState snapshot, string path, RequestActor actor, bool trash = false, Guid? entryId = null)
    {
        var user = snapshot.Users.Find(x => x.Id == actor.Id && !x.Disabled);
        if (user == null) return 0;
        if (user.IsAdmin && actor.WebAdmin) return 7;
        var home = HomeOwner(snapshot, path);
        if (home != null && home.Id != actor.Id) return 0;
        var batch = trash ? snapshot.Trash.Find(x => x.Entry.Id == entryId)?.Batch : null;
        var folders = PermissionEntries(snapshot, trash, batch);
        var rule = snapshot.Policies.Select(policy => (Policy: policy, Folder: folders.Find(x => x.Id == policy.FolderId)))
            .Where(x => x.Folder != null && (path.Equals(x.Folder.Path, StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(path, x.Folder.Path)))
            .OrderByDescending(x => x.Folder!.Path.Length).Select(x => x.Policy).FirstOrDefault();
        if (rule == null) return 0;
        if (rule.OwnerId == actor.Id) return 7;
        if (rule.Visibility == "private") return 0;
        var rights = rule.Visibility == "public" ? rule.PublicAccess : 0;
        foreach (var grant in rule.Grants)
            if (grant.Kind == "user" && grant.Id == actor.Id || grant.Kind == "group" && snapshot.Groups.Any(x => x.Id == grant.Id && x.Members.Contains(actor.Id))) rights |= grant.Access;
        return rights;
    }
    private static void Require(StoreState snapshot, string path, RequestActor actor, int access, bool trash = false, Guid? entryId = null)
    {
        if ((Access(snapshot, path, actor, trash, entryId) & access) != access) throw new StoreException(403, "你没有此文件夹或文件的操作权限。");
    }
    private static int Retention(StoreState snapshot, string path, Guid? entryId = null)
    {
        var batch = snapshot.Entries.Any(x => x.Id == entryId) ? null : snapshot.Trash.Find(x => x.Entry.Id == entryId)?.Batch;
        var folders = PermissionEntries(snapshot, batch.HasValue, batch);
        return snapshot.Policies.Where(x => x.KeepVersions.HasValue).Select(policy => (Policy: policy, Folder: folders.Find(x => x.Id == policy.FolderId)))
            .Where(x => x.Folder != null && (path.Equals(x.Folder.Path, StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(path, x.Folder.Path)))
            .OrderByDescending(x => x.Folder!.Path.Length).Select(x => x.Policy.KeepVersions!.Value).FirstOrDefault(10);
    }
    private static StoreState Retain(StoreState snapshot)
    {
        var files = snapshot.Entries.Concat(snapshot.Trash.Select(x => x.Entry)).Where(x => !x.IsDirectory).DistinctBy(x => x.Id).ToDictionary(x => x.Id);
        var versions = snapshot.History.Where(x => files.ContainsKey(x.EntryId)).GroupBy(x => x.EntryId)
            .SelectMany(group => group.OrderByDescending(x => x.ModifiedUtc).Take(Math.Max(0, Retention(snapshot, files[group.Key].Path, group.Key) - 1))).ToList();
        return snapshot with { History = versions };
    }
    private static long Used(StoreState snapshot, string folder)
    {
        var files = snapshot.Entries.Concat(snapshot.Trash.Select(x => x.Entry))
            .Where(x => !x.IsDirectory && CloudPath.IsUnder(x.Path, folder)).DistinctBy(x => x.Id).ToDictionary(x => x.Id);
        return checked(files.Values.Sum(x => x.Size) + snapshot.History.Where(x => files.ContainsKey(x.EntryId)).Sum(x => x.Size));
    }
    private static void CheckQuotas(StoreState proposed, StoreState? previous = null)
    {
        var folders = PermissionEntries(proposed, true);
        foreach (var policy in proposed.Policies.Where(x => x.QuotaBytes.HasValue))
        {
            var folder = folders.Find(x => x.Id == policy.FolderId);
            if (folder == null) continue;
            var used = Used(proposed, folder.Path);
            // An overfull legacy folder may delete/purge/shrink, but cannot grow further.
            var oldFolder = previous == null ? null : PermissionEntries(previous, true).Find(x => x.Id == folder.Id);
            var oldUsage = oldFolder == null ? 0 : Used(previous!, oldFolder.Path);
            if (used > policy.QuotaBytes!.Value && used > oldUsage) throw new StoreException(507, "文件夹容量不足：" + folder.Path + "（历史版本和回收站也占用配额）。");
        }
    }
    private static HashSet<string> Referenced(StoreState snapshot) => snapshot.Entries.Concat(snapshot.Trash.Select(x => x.Entry))
        .Where(x => !x.IsDirectory).Select(x => x.Id.ToString("N") + "_" + x.Version)
        .Concat(snapshot.History.Select(x => x.EntryId.ToString("N") + "_" + x.Version)).ToHashSet(StringComparer.Ordinal);
    private void Prune(StoreState before, StoreState after)
    {
        foreach (var name in Referenced(before).Except(Referenced(after)))
        {
            try { File.Delete(System.IO.Path.Combine(root, "blobs", name)); }
            catch (IOException) { /* An in-flight download may still hold it; startup cleanup can reclaim it later. */ }
            catch (UnauthorizedAccessException) { /* Keep an orphan rather than fail a committed transaction. */ }
        }
    }
    public FolderPolicyInfo Policy(RequestActor actor, Guid id)
    {
        var snapshot = state;
        var folder = snapshot.Entries.Find(x => x.Id == id && x.IsDirectory) ?? throw new StoreException(404, "文件夹不存在。");
        Require(snapshot, folder.Path, actor, 1);
        var own = snapshot.Policies.Find(x => x.FolderId == id);
        var inherited = snapshot.Policies.Select(x => (Policy: x, Folder: snapshot.Entries.Find(e => e.Id == x.FolderId)))
            .Where(x => x.Folder != null && CloudPath.IsUnder(folder.Path, x.Folder.Path)).OrderByDescending(x => x.Folder!.Path.Length).Select(x => x.Policy).FirstOrDefault();
        var fallback = inherited == null ? new FolderPolicy(id, BootstrapId, "restricted", 0, []) : inherited with { FolderId = id, QuotaBytes = null, KeepVersions = null };
        return new(own ?? fallback, Used(snapshot, folder.Path), Retention(snapshot, folder.Path));
    }
    public async Task<FolderPolicyInfo> SavePolicy(RequestActor actor, Guid id, FolderPolicy request)
    {
        if (request.FolderId != id || request.Visibility is not ("private" or "public" or "restricted") || request.PublicAccess is not (0 or 1 or 3) ||
            request.QuotaBytes is <= 0 || request.KeepVersions is < 1 or > 1000 || request.Grants.Count > 500 ||
            request.Grants.Any(x => x.Kind is not ("user" or "group") || x.Access is not (1 or 3 or 7))) throw new StoreException(400, "文件夹权限、配额或版本保留参数无效。");
        await gate.WaitAsync();
        try
        {
            var folder = state.Entries.Find(x => x.Id == id && x.IsDirectory) ?? throw new StoreException(404, "文件夹不存在。");
            Require(state, folder.Path, actor, 4);
            var home = HomeOwner(state, folder.Path);
            if (home != null && folder.Path.Equals(HomePath(home), StringComparison.OrdinalIgnoreCase) &&
                (request.OwnerId != home.Id || request.Visibility != "private" || request.PublicAccess != 0 || request.Grants.Count != 0))
                throw new StoreException(409, "默认个人文件夹始终属于本人且保持私有，请使用公共或指定授权目录进行共享。");
            var old = state.Policies.Find(x => x.FolderId == id);
            if (!(actor.WebAdmin && state.Users.Any(x => x.Id == actor.Id && x.IsAdmin && !x.Disabled)) &&
                (request.QuotaBytes != old?.QuotaBytes || request.KeepVersions != old?.KeepVersions)) throw new StoreException(403, "容量和版本保留策略仅限管理员调整。");
            if (!state.Users.Any(x => x.Id == request.OwnerId && !x.Disabled)) throw new StoreException(400, "所有者不存在或已停用。");
            if (!(actor.WebAdmin && state.Users.Any(x => x.Id == actor.Id && x.IsAdmin)) && request.OwnerId != (old?.OwnerId ?? actor.Id)) throw new StoreException(403, "仅网页管理员可以转移所有者。");
            if (request.Grants.Any(g => g.Kind == "user" ? !state.Users.Any(x => x.Id == g.Id) : !state.Groups.Any(x => x.Id == g.Id))) throw new StoreException(400, "授权用户或用户组不存在。");
            var proposed = Retain(state with { Policies = state.Policies.Where(x => x.FolderId != id).Append(request).ToList() });
            var before = state;
            // Reducing a quota below current usage is allowed; subsequent growth is blocked.
            await CommitState(proposed);
            Prune(before, state);
            return new(request, Used(state, folder.Path), Retention(state, folder.Path));
        }
        finally { gate.Release(); }
    }
    public async Task InheritPolicy(RequestActor actor, Guid id)
    {
        await gate.WaitAsync();
        try
        {
            var folder = state.Entries.Find(x => x.Id == id && x.IsDirectory) ?? throw new StoreException(404, "文件夹不存在。");
            Require(state, folder.Path, actor, 4);
            var home = HomeOwner(state, folder.Path);
            if (home != null && folder.Path.Equals(HomePath(home), StringComparison.OrdinalIgnoreCase))
                throw new StoreException(409, "默认个人文件夹必须保持独立的私有权限，不能恢复继承。");
            var old = state.Policies.Find(x => x.FolderId == id);
            if (!(actor.WebAdmin && state.Users.Any(x => x.Id == actor.Id && x.IsAdmin && !x.Disabled)) && (old?.QuotaBytes != null || old?.KeepVersions != null))
                throw new StoreException(403, "此目录有管理员设置的容量或版本策略，不能移除。");
            var before = state; await CommitState(Retain(state with { Policies = state.Policies.Where(x => x.FolderId != id).ToList() })); Prune(before, state);
        }
        finally { gate.Release(); }
    }
    public List<CloudVersion> Versions(RequestActor actor, Guid id)
    {
        var snapshot = state;
        var file = snapshot.Entries.Find(x => x.Id == id && !x.IsDirectory) ?? throw new StoreException(404, "文件不存在。");
        Require(snapshot, file.Path, actor, 1);
        return new[] { new CloudVersion(file.Version, file.Size, file.ModifiedUtc, true) }
            .Concat(snapshot.History.Where(x => x.EntryId == id).Select(x => new CloudVersion(x.Version, x.Size, x.ModifiedUtc, false)))
            .OrderByDescending(x => x.Current).ThenByDescending(x => x.ModifiedUtc).ToList();
    }
    public async Task<(FileStream Stream, CloudEntry Entry)> DownloadVersion(RequestActor actor, Guid id, string version, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
        var snapshot = state;
        var file = snapshot.Entries.Find(x => x.Id == id && !x.IsDirectory) ?? throw new StoreException(404, "文件不存在。");
        Require(snapshot, file.Path, actor, 1);
        if (file.Version == version) return (OpenBlob(id, version), file);
        var old = snapshot.History.Find(x => x.EntryId == id && x.Version == version) ?? throw new StoreException(404, "版本不存在或已超过保留期限。");
        return (OpenBlob(id, version), file with { Version = old.Version, Size = old.Size, ModifiedUtc = old.ModifiedUtc });
        }
        finally { gate.Release(); }
    }
    public async Task<CloudEntry> RestoreVersion(RequestActor actor, Guid id, string version, string expected)
    {
        await gate.WaitAsync();
        try
        {
            var current = state.Entries.Find(x => x.Id == id && !x.IsDirectory) ?? throw new StoreException(404, "文件不存在。");
            Require(state, current.Path, actor, 2); CheckVersion(current, expected, false);
            if (version == current.Version) return current;
            var old = state.History.Find(x => x.EntryId == id && x.Version == version) ?? throw new StoreException(404, "版本不存在。");
            var restored = current with { Version = Guid.NewGuid().ToString("N"), ModifiedUtc = DateTimeOffset.UtcNow, Size = old.Size };
            var proposed = Retain(state with { Entries = state.Entries.Select(x => x.Id == id ? restored : x).ToList(),
                History = [.. state.History, new(id, current.Version, current.Size, current.ModifiedUtc)] });
            CheckQuotas(proposed, state);
            File.Copy(Blob(id, old.Version), Blob(id, restored.Version), false);
            var before = state; await CommitState(proposed); Prune(before, state);
            return restored;
        }
        finally { gate.Release(); }
    }
    public async Task Purge(RequestActor actor, Guid id)
    {
        await gate.WaitAsync();
        try
        {
            var current = state.Trash.Find(x => x.Entry.Id == id) ?? throw new StoreException(404, "回收站项目不存在。");
            var removed = state.Trash.Where(x => x.Batch == current.Batch && (x.Entry.Id == id || CloudPath.IsUnder(x.Entry.Path, current.Entry.Path))).ToList();
            foreach (var item in removed) Require(state, item.Entry.Path, actor, 2, true, item.Entry.Id);
            var ids = removed.Select(x => x.Entry.Id).ToHashSet();
            var proposed = state with { Trash = state.Trash.Except(removed).ToList(), History = state.History.Where(x => !ids.Contains(x.EntryId)).ToList(),
                Policies = state.Policies.Where(x => !ids.Contains(x.FolderId)).ToList() };
            var before = state; await CommitState(proposed); Prune(before, state);
        }
        finally { gate.Release(); }
    }
}
