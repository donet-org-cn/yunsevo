using System.Text.Json;
using System.Text.Json.Serialization;
using Yunsevo.Contracts;

namespace Yunsevo.Server;

public sealed record TrashedEntry(CloudEntry Entry, Guid Batch, DeletionInfo? Deleted = null);
public sealed record StoreState(long Revision, List<CloudEntry> Entries, List<TrashedEntry> Trash)
{
    public List<StoredUser> Users { get; init; } = [];
    public List<GroupInfo> Groups { get; init; } = [];
    public List<FolderPolicy> Policies { get; init; } = [];
    public List<StoredVersion> History { get; init; } = [];
    public List<StoredSession> Sessions { get; init; } = [];
    public Guid JournalGeneration { get; init; }
}
[JsonSerializable(typeof(ContentCommit))]
[JsonSerializable(typeof(StoreState))]
internal partial class StoreJson : JsonSerializerContext;
internal sealed record ContentCommit(long Revision, CloudEntry Entry, List<StoredVersion> History);

public sealed class StoreException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed partial class CloudStore : IDisposable
{
    private readonly string root;
    private readonly FileStream processLock;
    private readonly SemaphoreSlim gate = new(1);
    private StoreState state;
    private readonly StateJournal<ContentCommit> contentJournal;
    public CloudStore(string root, string bootstrapKey, string administrator = "admin", string? administratorPassword = null)
    {
        this.root = System.IO.Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
        processLock = new(System.IO.Path.Combine(this.root, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Directory.CreateDirectory(System.IO.Path.Combine(this.root, "blobs"));
            var file = System.IO.Path.Combine(this.root, "catalog.json");
            if (!File.Exists(file) && File.Exists(System.IO.Path.Combine(this.root, "catalog.journal"))) throw new InvalidDataException("目录快照丢失，已保留增量记录，请恢复备份。");
            state = File.Exists(file) ? LoadState(file) : new(0, [], []);
            contentJournal = new(System.IO.Path.Combine(this.root, "catalog.journal"), state.JournalGeneration, StoreJson.Default.ContentCommit);
            foreach (var commit in contentJournal.Replay())
            {
                if (commit.Revision != state.Revision + 1) throw new InvalidDataException("目录增量记录顺序损坏，已停止启动。");
                state = state with { Revision = commit.Revision, Entries = state.Entries.Where(x => x.Id != commit.Entry.Id).Append(commit.Entry).ToList(), History = state.History.Where(x => x.EntryId != commit.Entry.Id).Concat(commit.History).ToList() };
            }
            InitializeSecurity(administratorPassword ?? bootstrapKey, administrator);
            EnsurePersonalHomes();
        }
        catch
        {
            processLock.Dispose();
            throw;
        }
    }

    private static StoreState LoadState(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(file));
        var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object ||
            !json.TryGetProperty("Revision", out var revision) || !revision.TryGetInt64(out var number) || number < 0)
            throw new InvalidDataException("目录数据库缺少有效的 Revision，已停止启动以保护现有数据。");

        string[] metadata = ["Users", "Groups", "Policies", "History", "Sessions"];
        // V1 has none of the security/versioning fields. A partially damaged V2 catalog
        // must not be mistaken for V1 and have its administrator or permissions reset.
        var legacy = !metadata.Any(name => json.TryGetProperty(name, out _));
        foreach (var name in legacy ? new[] { "Entries", "Trash" } : ["Entries", "Trash", .. metadata])
            if (!json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"目录数据库字段 {name} 缺失或格式无效，已停止启动以保护现有数据。");

        var loaded = json.Deserialize(StoreJson.Default.StoreState)
            ?? throw new InvalidDataException("目录数据库为空。");
        // Source-generated deserialization of a parameterized record supplies null for
        // absent init-only properties, even when their property initializers use [].
        if (legacy) return loaded with { Users = [], Groups = [], Policies = [], History = [], Sessions = [] };
        if (loaded.Users.Count == 0)
            throw new InvalidDataException("目录数据库的用户列表为空，已停止启动；不会重新生成管理员覆盖原账户。");
        return loaded;
    }

    public Catalog Snapshot(RequestActor actor, bool trash = false)
    {
        return Snapshot(Volatile.Read(ref state), actor, trash);
    }
    public (Catalog? Catalog, string ETag) ConditionalSnapshot(RequestActor actor, string? scope, string? expected)
    {
        scope = string.IsNullOrEmpty(scope) ? "" : CloudPath.Normalize(scope);
        var current = Volatile.Read(ref state);
        var tag = '"' + current.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + actor.Id.ToString("N") + ":" + actor.WebAdmin + ":" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope))) + '"';
        return expected == tag ? (null, tag) : (Snapshot(current, actor, false, scope), tag);
    }
    private static Catalog Snapshot(StoreState current, RequestActor actor, bool trash, string scope = "")
    {
        var source = trash ? current.Trash.Select(x => x.Entry).ToList() : current.Entries;
        if (scope.Length > 0) source = source.Where(x => x.Path.Equals(scope, StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(x.Path, scope) || CloudPath.IsUnder(scope, x.Path)).ToList();
        var permissions = trash ? null : new LivePermissions(current, actor);
        var visible = source.Select(x => (Entry: x, Rights: permissions?.Access(x.Path) ?? Access(current, x.Path, actor, true, x.Id)))
            .Where(x => (x.Rights & 1) != 0).ToDictionary(x => x.Entry.Id, x => x.Entry with { Access = x.Rights });
        var folders = source.Where(x => x.IsDirectory).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        // Include only the structural ancestors needed to reach an explicitly shared descendant.
        foreach (var entry in visible.Values.ToArray())
            for (var parent = CloudPath.Parent(entry.Path); parent != null; parent = CloudPath.Parent(parent))
            {
                if (folders.TryGetValue(parent, out var folder)) visible.TryAdd(folder.Id, folder with { Access = 8 });
            }
        return new(current.Revision, [.. visible.Values]);
    }

    private string Blob(Guid id, string version) => System.IO.Path.Combine(root, "blobs", id.ToString("N") + "_" + version);
    public string? AuditLabel(Guid id)
    {
        var snapshot = Volatile.Read(ref state);
        return snapshot.Entries.Find(x => x.Id == id)?.Path ?? snapshot.Trash.Find(x => x.Entry.Id == id)?.Entry.Path ??
            snapshot.Users.Find(x => x.Id == id)?.Username ?? snapshot.Groups.Find(x => x.Id == id)?.Name;
    }

    private FileStream OpenBlob(Guid id, string version) => new(Blob(id, version), FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async Task<(FileStream Stream, CloudEntry Entry)> Download(RequestActor actor, Guid id, string? version, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
        var snapshot = Volatile.Read(ref state);
        var entry = snapshot.Entries.Find(x => x.Id == id && !x.IsDirectory)
            ?? throw new StoreException(404, "文件不存在。");
        Require(snapshot, entry.Path, actor, 1);
        if (version != null && version != entry.Version) throw new StoreException(409, "云端版本已变化，请刷新后重试。");
        // Open while holding the mutation gate so pruning cannot remove the selected blob before it is leased.
        return (OpenBlob(entry.Id, entry.Version), entry);
        }
        finally { gate.Release(); }
    }

    public async Task<(FileStream Stream, CloudEntry Entry)> DownloadTrash(RequestActor actor, Guid id, string? version, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var snapshot = state;
            var entry = snapshot.Trash.Find(x => x.Entry.Id == id && !x.Entry.IsDirectory)?.Entry
                ?? throw new StoreException(404, "回收站文件不存在或已被永久删除。");
            Require(snapshot, entry.Path, actor, 1, true, entry.Id);
            if (version != null && version != entry.Version) throw new StoreException(409, "回收站文件版本已变化，请刷新后重试。");
            return (OpenBlob(entry.Id, entry.Version), entry);
        }
        finally { gate.Release(); }
    }

    private async Task Commit(List<CloudEntry> entries, List<TrashedEntry> trash)
    {
        await CommitState(state with { Entries = entries, Trash = trash });
    }

    private async Task CommitState(StoreState proposed)
    {
        var next = proposed with { Revision = state.Revision + 1, JournalGeneration = Guid.NewGuid() };
        await PersistSnapshot(next);
        Volatile.Write(ref state, next); SignalChange();
    }
    private async Task PersistSnapshot(StoreState next)
    {
        var temp = System.IO.Path.Combine(root, "catalog.json.tmp");
        // Flush before atomic replacement; readers only observe the committed immutable snapshot.
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, next, StoreJson.Default.StoreState);
            stream.Flush(true);
        }
        AtomicFile.Replace(temp, System.IO.Path.Combine(root, "catalog.json"));
        contentJournal.Reset(next.JournalGeneration);
    }
    private async Task CommitContent(StoreState proposed, CloudEntry entry)
    {
        var next = proposed with { Revision = state.Revision + 1 };
        contentJournal.Append(new(next.Revision, entry, next.History.Where(x => x.EntryId == entry.Id).ToList()));
        Volatile.Write(ref state, next); SignalChange();
        if (contentJournal.Count >= 128)
        {
            var compact = next with { JournalGeneration = Guid.NewGuid() };
            await PersistSnapshot(compact); Volatile.Write(ref state, compact);
        }
    }

    private static void ValidateParent(List<CloudEntry> entries, string path)
    {
        var parent = CloudPath.Parent(path);
        if (parent != null && !entries.Any(x => x.IsDirectory && x.Path.Equals(parent, StringComparison.OrdinalIgnoreCase)))
            throw new StoreException(409, "父目录不存在。");
    }

    private static void CheckVersion(CloudEntry? current, string? expected, bool createOnly)
    {
        if (createOnly)
        {
            if (current != null) throw new StoreException(412, "同名项目已存在。");
        }
        else if (current == null || string.IsNullOrEmpty(expected) || current.Version != expected)
            throw new StoreException(412, "版本冲突：云端已更新，未覆盖文件。");
    }

    public async Task<CloudEntry> Upload(RequestActor actor, string path, Stream source, long? contentLength, string? expected,
        bool createOnly, long limit, CancellationToken ct, MobileUploadMetadata? metadata = null)
    {
        path = CloudPath.Normalize(path);
        ValidateUpload(actor, path, contentLength, expected, createOnly, limit);
        ValidatePhoto(actor, path, metadata);
        using var contentHash = metadata == null ? null : System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var version = Guid.NewGuid().ToString("N");
        var temp = System.IO.Path.Combine(root, "blobs", "upload-" + version);
        try
        {
            long total = 0;
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    int count;
                    while ((count = await source.ReadAsync(buffer.AsMemory(0, 128 * 1024), ct)) > 0)
                    {
                        total = checked(total + count);
                        contentHash?.AppendData(buffer, 0, count);
                        if (total > limit) throw new StoreException(413, "超过单文件上传限制。");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    output.Flush(true);
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
            }
            if (contentLength.HasValue && total != contentLength) throw new StoreException(400, "上传长度不一致。");
            return await CommitUpload(actor, path, temp, total, expected, createOnly, version, ct, metadata,
                contentHash == null ? null : Convert.ToHexString(contentHash.GetHashAndReset()));
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal void ValidateUpload(RequestActor actor, string path, long? contentLength, string? expected, bool createOnly, long limit, bool checkQuota = true)
    {
        var snapshot = Volatile.Read(ref state);
        Require(snapshot, path, actor, 2);
        if (contentLength < 0) throw new StoreException(400, "上传长度无效。");
        if (contentLength > limit) throw new StoreException(413, "超过单文件上传限制。");
        var preliminary = snapshot.Entries.Find(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        CheckVersion(preliminary, expected, createOnly);
        ValidateParent(snapshot.Entries, path);
        if (preliminary?.IsDirectory == true) throw new StoreException(409, "目标是目录。");
        if (checkQuota && contentLength.HasValue)
        {
            var predicted = new CloudEntry(preliminary?.Id ?? Guid.NewGuid(), path, false, contentLength.Value, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
            var candidate = Retain(snapshot with { Entries = snapshot.Entries.Where(x => x.Id != predicted.Id).Append(predicted).ToList(),
                History = preliminary == null ? snapshot.History : [.. snapshot.History, new(preliminary.Id, preliminary.Version, preliminary.Size, preliminary.ModifiedUtc)] });
            CheckQuotas(candidate, snapshot);
        }
    }
    internal CloudEntry? CompletedUpload(RequestActor actor, string path, string version)
    {
        var snapshot = Volatile.Read(ref state); Require(snapshot, path, actor, 2);
        return snapshot.Entries.Find(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && x.Version == version);
    }
    internal async Task<CloudEntry> CommitUpload(RequestActor actor, string path, string temp, long total, string? expected, bool createOnly, string version, CancellationToken ct,
        MobileUploadMetadata? metadata = null, string? sha256 = null)
    {
            await gate.WaitAsync(ct);
            try
            {
                var entries = new List<CloudEntry>(state.Entries);
                var current = entries.Find(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                Require(state, path, actor, 2);
                CheckVersion(current, expected, createOnly);
                if (current?.IsDirectory == true) throw new StoreException(409, "目标是目录。");
                ValidateParent(entries, path);
                var entry = new CloudEntry(current?.Id ?? Guid.NewGuid(), path, false, total, DateTimeOffset.UtcNow, version,
                    Origin: Provenance(actor, path, metadata, sha256));
                if (current != null) entries.Remove(current);
                entries.Add(entry);
                var proposed = Retain(state with { Entries = entries, History = current == null ? state.History :
                    [.. state.History, new(current.Id, current.Version, current.Size, current.ModifiedUtc, current.Origin)] });
                CheckQuotas(proposed, state);
                File.Move(temp, Blob(entry.Id, version));
                var before = state;
                try { await CommitContent(proposed, entry); }
                catch
                {
                    if (!state.Entries.Any(x => x.Version == version) && File.Exists(Blob(entry.Id, version)) && !File.Exists(temp)) File.Move(Blob(entry.Id, version), temp);
                    throw;
                }
                Prune(before, state);
                return entry;
            }
            finally { gate.Release(); }
    }

    public async Task<CloudEntry> Folder(RequestActor actor, string path)
    {
        path = CloudPath.Normalize(path);
        await gate.WaitAsync();
        try
        {
            Require(state, CloudPath.Parent(path) ?? "", actor, 2);
            var entries = new List<CloudEntry>(state.Entries);
            if (entries.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) throw new StoreException(409, "项目已存在。");
            ValidateParent(entries, path);
            var entry = new CloudEntry(Guid.NewGuid(), path, true, 0, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
            entries.Add(entry);
            await Commit(entries, state.Trash);
            return entry;
        }
        finally { gate.Release(); }
    }

    public async Task<CloudEntry> Move(RequestActor actor, Guid id, MoveEntry request)
    {
        var path = CloudPath.Normalize(request.Path);
        await gate.WaitAsync();
        try
        {
            var current = state.Entries.Find(x => x.Id == id) ?? throw new StoreException(404, "项目不存在。");
            CheckVersion(current, request.Version, false);
            ProtectPersonalRoot(state, current.Path);
            foreach (var child in state.Entries.Where(x => x.Id == id || CloudPath.IsUnder(x.Path, current.Path))) Require(state, child.Path, actor, 2);
            Require(state, CloudPath.Parent(path) ?? "", actor, 2);
            if (CloudPath.IsUnder(path, current.Path)) throw new StoreException(409, "不能移动到自己的子目录。");
            if (state.Entries.Any(x => x.Id != id && x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new StoreException(409, "目标已存在。");
            ValidateParent(state.Entries, path);
            // Content version stays stable on a rename, allowing existing hydrated bytes to remain valid.
            var moved = current with { Path = path };
            var entries = state.Entries.Select(x => x.Id == id ? moved : CloudPath.IsUnder(x.Path, current.Path)
                ? x with { Path = path + x.Path[current.Path.Length..] } : x).ToList();
            var trash = state.Trash;
            if (current.IsDirectory)
            {
                var ancestors = state.Entries.Where(x => x.IsDirectory && (x.Id == current.Id || CloudPath.IsUnder(current.Path, x.Path))).ToDictionary(x => x.Path, x => x.Id, StringComparer.OrdinalIgnoreCase);
                // Older deletion batches may contain a different folder with the same path. They keep their own identity and permissions.
                var detachedBatches = state.Trash.Where(x => x.Entry.IsDirectory && ancestors.TryGetValue(x.Entry.Path, out var ancestor) && ancestor != x.Entry.Id).Select(x => x.Batch).ToHashSet();
                trash = state.Trash.Select(x =>
                {
                    if (!CloudPath.IsUnder(x.Entry.Path, current.Path) || detachedBatches.Contains(x.Batch)) return x;
                    Require(state, x.Entry.Path, actor, 2, true, x.Entry.Id);
                    return x with { Entry = x.Entry with { Path = path + x.Entry.Path[current.Path.Length..] } };
                }).ToList();
            }
            var proposed = Retain(state with { Entries = entries, Trash = trash });
            CheckQuotas(proposed, state);
            var before = state; await CommitState(proposed); Prune(before, state);
            return moved;
        }
        finally { gate.Release(); }
    }

    public async Task Delete(RequestActor actor, Guid id, string? version, long? revision, DeletionOrigin? origin = null)
    {
        await gate.WaitAsync();
        try
        {
            var current = state.Entries.Find(x => x.Id == id) ?? throw new StoreException(404, "项目不存在。");
            CheckVersion(current, version, false);
            ProtectPersonalRoot(state, current.Path);
            if (current.IsDirectory && revision != state.Revision)
                throw new StoreException(412, "目录内容已变化，请刷新目录版本后重试。");
            var removed = state.Entries.Where(x => x.Id == id || CloudPath.IsUnder(x.Path, current.Path)).ToList();
            foreach (var item in removed) Require(state, item.Path, actor, 2);
            var batch = Guid.NewGuid();
            var user = state.Users.Single(x => x.Id == actor.Id);
            var deleted = new DeletionInfo(DateTimeOffset.UtcNow, user.Id, user.Username, user.DisplayName, origin?.DeviceName, origin?.Source ?? "API", origin?.RemoteAddress);
            await Commit(state.Entries.Except(removed).ToList(), [.. state.Trash, .. removed.Select(x => new TrashedEntry(x, batch, deleted))]);
        }
        finally { gate.Release(); }
    }

    public async Task Restore(RequestActor actor, Guid id)
    {
        await gate.WaitAsync();
        try
        {
            var current = state.Trash.Find(x => x.Entry.Id == id) ?? throw new StoreException(404, "回收站项目不存在。");
            var restored = state.Trash.Where(x => x.Batch == current.Batch && (x.Entry.Id == id || CloudPath.IsUnder(x.Entry.Path, current.Entry.Path))).ToList();
            foreach (var item in restored) Require(state, item.Entry.Path, actor, 2, true, item.Entry.Id);
            Require(state, CloudPath.Parent(current.Entry.Path) ?? "", actor, 2);
            if (restored.Any(r => state.Entries.Any(x => x.Path.Equals(r.Entry.Path, StringComparison.OrdinalIgnoreCase))))
                throw new StoreException(409, "还原位置有同名项目。");
            ValidateParent(state.Entries, current.Entry.Path);
            var proposed = Retain(state with { Entries = [.. state.Entries, .. restored.Select(x => x.Entry)], Trash = state.Trash.Except(restored).ToList() });
            CheckQuotas(proposed, state);
            var before = state; await CommitState(proposed); Prune(before, state);
        }
        finally { gate.Release(); }
    }

    public void Dispose() { processLock.Dispose(); gate.Dispose(); }
}
