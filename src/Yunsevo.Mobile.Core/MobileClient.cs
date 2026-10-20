using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yunsevo.Contracts;
using Yunsevo.Sync;

namespace Yunsevo.Mobile.Core;

public sealed class MobileClient : IDisposable
{
    private readonly IAccountVault vault;
    private readonly SemaphoreSlim transfers = new(1);
    private readonly SemaphoreSlim cameraRecovery = new(1);
    private readonly Guid producer = Guid.NewGuid();
    private readonly object sessionGate = new();
    private sealed record Session(MobileAccount Account, CloudConnection Connection, string Key);
    private Session? session;
    private Session Current => Volatile.Read(ref session) ?? throw new InvalidOperationException("请先登录。");
    private bool IsCurrent(Session value) => ReferenceEquals(Volatile.Read(ref session)?.Connection, value.Connection);
    public MobileRepository Repository { get; }
    public MobileAccount? Account => Volatile.Read(ref session)?.Account;
    public CloudConnection? Connection => Volatile.Read(ref session)?.Connection;
    public event Action<TransferProgress>? Progress;
    public string AccountKey => Volatile.Read(ref session)?.Key ?? "";
    private static string Scope(MobileAccount account) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        new Uri(account.Server).AbsoluteUri.TrimEnd('/') + "\n" + account.Login.User.Id.ToString("N"))));
    public MobileOptions Options => Repository.Options(AccountKey);
    public (CloudConnection Connection, string Key) TransferSession() { var current = Current; return (current.Connection, current.Key); }
    public MobileClient(MobileRepository repository, IAccountVault vault) { Repository = repository; this.vault = vault; }
    public void LoadRemembered()
    {
        var saved = vault.Load(); if (saved == null) return;
        lock (sessionGate) session = new(saved, new(saved.Server, saved.Login.AccessToken), Scope(saved));
    }
    public async Task Resume(CancellationToken ct)
    {
        var current = Current;
        var result = await current.Connection.Resume(ct);
        lock (sessionGate)
        {
            if (!IsCurrent(current)) return;
            var saved = new MobileAccount(current.Account.Server, result);
            vault.Save(saved); session = current with { Account = saved };
        }
    }
    public async Task Login(string server, string username, string password, CancellationToken ct)
    {
        var next = new CloudConnection(server);
        try
        {
            var login = await next.Login(username, password, ct);
            var saved = new MobileAccount(next.Http.BaseAddress!.AbsoluteUri.TrimEnd('/'), login);
            Session? previous;
            lock (sessionGate)
            {
                vault.Save(saved); previous = session; session = new(saved, next, Scope(saved));
            }
            previous?.Connection.Dispose();
        }
        catch { next.Dispose(); throw; }
    }
    public async Task Logout(CancellationToken ct)
    {
        Session? current;
        lock (sessionGate)
        {
            current = session; if (current == null) return;
            // Local sign-out must complete even if the server is unreachable. Clear the vault
            // before dropping the session so a storage failure cannot pretend to sign out.
            vault.Clear(); session = null;
        }
        current.Connection.Dispose(); // Abort in-flight requests from the signed-out session.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(2));
        using var revoke = new CloudConnection(current.Account.Server, current.Account.Login.AccessToken);
        try { using var response = await revoke.Http.PostAsync("api/auth/logout", null, budget.Token); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
    }
    public Task<List<SyncFolder>> Folders(CancellationToken ct) => Folders(Current.Connection, ct);
    private static async Task<List<SyncFolder>> Folders(CloudConnection connection, CancellationToken ct)
    {
        return await connection.Http.GetFromJsonAsync("api/sync-folders", ApiJson.Default.ListSyncFolder, ct) ?? [];
    }
    public async Task<PagedResult<BrowserEntry>> Browse(string folder, int page, string query, CancellationToken ct)
    {
        var connection = Connection ?? throw new InvalidOperationException("请先登录。");
        var url = "api/browser/entries?folder=" + Uri.EscapeDataString(folder) + "&page=" + page + "&pageSize=30&query=" + Uri.EscapeDataString(query);
        using var response = await connection.Http.GetAsync(url, ct); await CloudConnection.Check(response, ct);
        return await response.Content.ReadFromJsonAsync(ApiJson.Default.PagedResultBrowserEntry, ct) ?? throw new IOException("目录结果为空。");
    }
    public async Task<PagedResult<BrowserEntry>> Album(string folder, int page, CancellationToken ct, string? after = null)
    {
        var current = Current;
        using var response = await current.Connection.Http.GetAsync("api/browser/media?folder=" + Uri.EscapeDataString(folder) + "&page=" + page + "&pageSize=24" + (after == null ? "" : "&after=" + Uri.EscapeDataString(after)), ct);
        await CloudConnection.Check(response, ct);
        return await response.Content.ReadFromJsonAsync(ApiJson.Default.PagedResultBrowserEntry, ct) ?? throw new IOException("相册响应为空。");
    }
    public async Task<MediaNeighbors> MediaNeighbors(Guid id, string folder, CancellationToken ct)
    {
        var current = Current;
        using var response = await current.Connection.Http.GetAsync("api/browser/media/" + id + "/neighbors?folder=" + Uri.EscapeDataString(folder), ct);
        await CloudConnection.Check(response, ct);
        return await response.Content.ReadFromJsonAsync(ApiJson.Default.MediaNeighbors, ct) ?? throw new IOException("相册导航响应为空。");
    }
    public async Task<CaptureEnvironment> CaptureEnvironment(EnvironmentQuery query, CancellationToken ct)
    {
        var current = Current;
        using var response = await current.Connection.Http.PostAsJsonAsync("api/mobile/environment", query, ApiJson.Default.EnvironmentQuery, ct);
        await CloudConnection.Check(response, ct);
        return await response.Content.ReadFromJsonAsync(ApiJson.Default.CaptureEnvironment, ct) ?? throw new IOException("环境查询响应为空。");
    }
    public async Task<FolderSelection> CreateFolder(FolderSelection parent, string name, CancellationToken ct)
    {
        name = CloudPath.Normalize(name.Trim());
        if (name.Contains('/')) throw new ArgumentException("请输入一层文件夹名称。");
        using var response = await Connection!.Http.PostAsJsonAsync("api/folders", new CreateFolder(parent.Path + "/" + name), ApiJson.Default.CreateFolder, ct);
        await CloudConnection.Check(response, ct);
        var entry = (await response.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry, ct))!;
        return new(entry.Id, entry.Path, parent.Category);
    }
    public async Task<MobileJob> Stage(Stream source, string originalName, FolderSelection folder, MobileUploadMetadata metadata,
        string subdirectory, CancellationToken ct, string? albumKey = null, string? albumRevision = null, long? maxBytes = null,
        string? expectedAccount = null)
    {
        var current = Current;
        if (expectedAccount != null && current.Key != expectedAccount) throw new InvalidOperationException("账号已切换，请重新选择保存目录。");
        var account = current.Key; var id = Guid.NewGuid();
        var original = MobileRepository.FileName(originalName);
        var extension = Path.GetExtension(original);
        var name = Path.GetFileNameWithoutExtension(original) + "-" + id.ToString("N")[..12] + extension;
        var localName = id.ToString("N"); var local = Path.Combine(Repository.Home, "outbox", localName);
        var temporary = local + ".partial"; long length = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072]; int count;
                while ((count = await source.ReadAsync(buffer, ct)) != 0)
                {
                    length = checked(length + count);
                    if (maxBytes.HasValue && length > maxBytes.Value) throw new IOException("文件超过设备可用空间或允许的大小。");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                output.Flush(true);
            }
            if (AccountKey != account) throw new InvalidOperationException("账号已切换，请重新选择保存目录。");
            metadata = metadata with { CaptureId = metadata.CaptureId ?? id };
            var job = new MobileJob(id, account, localName, name, length, Convert.ToHexString(hash.GetHashAndReset()), folder,
                string.IsNullOrEmpty(subdirectory) ? "" : CloudPath.Normalize(subdirectory), metadata, DateTimeOffset.UtcNow,
                AlbumKey: albumKey, AlbumRevision: albumRevision);
            File.Move(temporary, local);
            try { Repository.Add(job); } catch { File.Delete(local); throw; }
            return job;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public string PrepareCameraPhoto(string name, FolderSelection folder, MobileUploadMetadata metadata)
    {
        var current = Current;
        if (metadata.Kind != "camera" || !metadata.CaptureId.HasValue || metadata.CaptureId.Value == Guid.Empty)
            throw new InvalidOperationException("需要登录身份与有效拍摄编号。");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".jpg" or ".mp4")) throw new ArgumentException("工程拍摄仅支持 JPEG 和 MP4。");
        var captures = Path.Combine(Repository.Home, "captures"); Directory.CreateDirectory(captures);
        var stem = Path.Combine(captures, metadata.CaptureId!.Value.ToString("N"));
        AtomicFile.WriteText(stem + ".json", JsonSerializer.Serialize(new CameraDraft(metadata.CaptureId.Value, current.Key,
            MobileRepository.FileName(name), folder, metadata, producer, Extension: extension), MobileJson.Default.CameraDraft));
        return stem + extension;
    }
    public async Task CompleteCameraPhoto(Guid id, CancellationToken ct)
    {
        var manifest = Path.Combine(Repository.Home, "captures", id.ToString("N") + ".json");
        await cameraRecovery.WaitAsync(ct);
        try
        {
            if (File.Exists(manifest))
            {
                var draft = JsonSerializer.Deserialize(File.ReadAllBytes(manifest), MobileJson.Default.CameraDraft)!;
                AtomicFile.WriteText(manifest, JsonSerializer.Serialize(draft with { Ready = true }, MobileJson.Default.CameraDraft));
            }
        }
        finally { cameraRecovery.Release(); }
        await RecoverCapturedPhotos(ct);
    }
    public async Task<int> RecoverCapturedPhotos(CancellationToken ct)
    {
        await cameraRecovery.WaitAsync(ct);
        try
        {
            var directory = Path.Combine(Repository.Home, "captures"); if (!Directory.Exists(directory) || Account == null) return 0;
            var recovered = 0;
            foreach (var manifest in Directory.EnumerateFiles(directory, "*.json"))
            {
                var draft = JsonSerializer.Deserialize(File.ReadAllBytes(manifest), MobileJson.Default.CameraDraft) ?? throw new IOException("拍摄恢复记录无效。");
                if (draft.Account != AccountKey) continue;
                if (!draft.Ready && draft.Producer == producer) continue;
                var stem = Path.Combine(directory, draft.Id.ToString("N"));
                if (draft.Extension is not (".jpg" or ".mp4")) throw new IOException("拍摄恢复记录的媒体格式无效。");
                var photo = stem + draft.Extension; var owned = Path.Combine(Repository.Home, "outbox", draft.Id.ToString("N"));
                if (Repository.Jobs(draft.Account).Any(x => x.Id == draft.Id)) { File.Delete(manifest); if (File.Exists(photo)) File.Delete(photo); continue; }
                var source = File.Exists(owned) ? owned : photo; if (!File.Exists(source)) continue;
                await using var input = File.OpenRead(source); var size = input.Length;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)); input.Close();
                if (AccountKey != draft.Account) break;
                if (source != owned) File.Move(source, owned);
                var name = Path.GetFileNameWithoutExtension(draft.Name) + "-" + draft.Id.ToString("N")[..12] + Path.GetExtension(draft.Name);
                Repository.Add(new(draft.Id, draft.Account, draft.Id.ToString("N"), name, size, hash, draft.Folder, "", draft.Metadata,
                    draft.Metadata.CapturedUtc ?? DateTimeOffset.UtcNow));
                File.Delete(manifest); recovered++;
            }
            return recovered;
        }
        finally { cameraRecovery.Release(); }
    }
    public async Task<int> ScanAlbum(IAlbumSource source, string deviceName, CancellationToken ct)
    {
        var current = Volatile.Read(ref session); if (current == null) return 0;
        var options = Repository.Options(current.Key); if (!options.AutoBackup || options.BackupFolder == null) return 0;
        var folder = (await Folders(current.Connection, ct)).Find(x => x.Id == options.BackupFolder.Id && (x.Access & 2) != 0)
            ?? throw new InvalidOperationException("相册备份目录已失效或没有写入权限，请重新选择。");
        var selected = new FolderSelection(folder.Id, folder.Path, folder.Category);
        var remaining = 32 - Repository.Jobs(current.Key).Count(x => x.State is not ("completed" or "cancelled"));
        var added = 0; if (remaining <= 0) return 0;
        await foreach (var asset in source.Enumerate(ct))
        {
            if (!IsCurrent(current)) break;
            if (!options.IncludeVideos && asset.MimeType.StartsWith("video/", StringComparison.Ordinal)) continue;
            if (Repository.HasAlbum(current.Key, asset.Key, asset.Revision, selected.Id)) continue;
            var date = asset.TakenUtc ?? DateTimeOffset.UtcNow;
            var subdir = MobileRepository.FileName(deviceName) + "/" + date.ToString("yyyy/MM");
            var metadata = new MobileUploadMetadata("album", asset.TakenUtc, DeviceName: deviceName);
            await using var input = await source.Open(asset, ct);
            var staged = await Stage(input, asset.Name, selected, metadata, subdir, ct, asset.Key, asset.Revision, expectedAccount: current.Key);
            if (asset.Size > 0 && staged.Length != asset.Size)
            {
                Repository.Update(staged with { State = "cancelled", Error = "相册文件仍在写入，将在下一次扫描时重试。" });
                File.Delete(Repository.LocalPath(staged)); continue;
            }
            if (++added >= remaining) break;
        }
        return added;
    }
    public async Task<int> Pump(bool wifiUnavailable, bool charging, CancellationToken ct)
    {
        if (!await transfers.WaitAsync(0, ct)) return 0;
        try
        {
            var current = Current; var connection = current.Connection;
            var account = current.Key;
            var options = Repository.Options(account);
            if (options.PauseUploads || options.WifiOnly && wifiUnavailable || options.ChargingOnly && !charging) return 0;
            var folders = await Folders(connection, ct); var uploaded = 0;
            foreach (var original in Repository.Jobs(account).OrderBy(x => x.CreatedUtc))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsCurrent(current) || Repository.Options(account).PauseUploads) break;
                if (original.State is "completed" or "cancelled" || original.RetryUtc > DateTimeOffset.UtcNow) continue;
                var job = original;
                try
                {
                    var folder = folders.Find(x => x.Id == job.Folder.Id && (x.Access & 2) != 0)
                        ?? throw new InvalidOperationException("保存目录已失效或没有写入权限。");
                    var parent = folder.Path;
                    foreach (var segment in job.Subdirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        parent += "/" + segment;
                        var known = await Lookup(connection, parent, ct);
                        if (known == null)
                        {
                            using var create = await connection.Http.PostAsJsonAsync("api/folders", new CreateFolder(parent), ApiJson.Default.CreateFolder, ct);
                            if (create.StatusCode != HttpStatusCode.Conflict) await CloudConnection.Check(create, ct);
                            known = await Lookup(connection, parent, ct);
                        }
                        if (known?.IsDirectory != true || (known.Access & 2) == 0) throw new InvalidOperationException("备份子目录不存在或没有写入权限。");
                    }
                    var remote = parent + "/" + job.Name;
                    var existing = await Lookup(connection, remote, ct);
                    CloudEntry entry;
                    if (existing != null)
                    {
                        if (existing.Size != job.Length || existing.Origin?.CaptureId != job.Metadata.CaptureId ||
                            existing.Origin?.UploadedBy.UserId != current.Account.Login.User.Id || existing.Origin?.Kind != job.Metadata.Kind ||
                            !string.Equals(existing.Origin?.ContentSha256, job.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("云端同名文件内容不同，已保留本地待上传文件。");
                        entry = existing;
                    }
                    else
                    {
                        job = job with { State = "uploading", Error = null };
                        Repository.Update(job);
                        await using var input = File.OpenRead(Repository.LocalPath(job));
                        entry = await FileTransfers.Upload(connection, remote, input, null, account,
                            Path.Combine(Repository.Home, "transfers", account), ct,
                            (bytes, total) => Progress?.Invoke(new(job.Id, bytes, total)), job.Sha256, job.Metadata);
                    }
                    Repository.Update(job with { State = "completed", Completed = entry, Error = null, RetryUtc = null });
                    FileTransfers.Acknowledge(connection, remote, entry, account, Path.Combine(Repository.Home, "transfers", account));
                    File.Delete(Repository.LocalPath(job)); uploaded++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { Repository.Update(job with { State = "pending" }); throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
                {
                    var attempts = job.Attempts + 1;
                    var error = ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } ? "登录已失效，请重新登录原账号。" : ex.Message;
                    Repository.Update(job with { State = "failed", Error = error, Attempts = attempts,
                        RetryUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(1800, 15 * Math.Pow(2, Math.Min(attempts, 7)))) });
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }) break;
                }
            }
            return uploaded;
        }
        finally { transfers.Release(); }
    }
    private static async Task<CloudEntry?> Lookup(CloudConnection connection, string remote, CancellationToken ct)
    {
        using var response = await connection.Http.GetAsync("api/browser/entry?path=" + Uri.EscapeDataString(remote), ct);
        await CloudConnection.Check(response, ct);
        return (await response.Content.ReadFromJsonAsync(ApiJson.Default.EntryLookup, ct))?.Entry;
    }
    public void Dispose() { Connection?.Dispose(); transfers.Dispose(); cameraRecovery.Dispose(); }
}
