using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using MyPan.Contracts;
using MyPan.Sync;

namespace MyPan.Client;

public sealed class CloudApi : IDisposable
{
    private readonly HttpClient http;
    private readonly CloudConnection connection;
    public event Action<bool, bool>? ConnectionChanged { add => connection.ConnectionChanged += value; remove => connection.ConnectionChanged -= value; }
    private readonly string remoteDirectory;
    private readonly SemaphoreSlim directoryGate = new(1);
    private bool directoryReady;
    public Guid? RemoteDirectoryId { get; private set; }
    internal string RootIdentity { get; }
    public CloudApi(ClientConfig config, Guid? remoteDirectoryId = null)
    {
        remoteDirectory = config.RemoteDirectory.Length == 0 ? "" : CloudPath.Normalize(config.RemoteDirectory);
        RemoteDirectoryId = remoteDirectoryId;
        RootIdentity = "MyPan:v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            config.ServerUrl.TrimEnd('/') + "\n" + remoteDirectory.ToUpperInvariant() + (config.AccountId.Length == 0 ? "" : "\nuser:" + config.AccountId))));
        connection = new(config.ServerUrl, Secret.Unprotect(config.ProtectedApiKey));
        http = connection.Http;
    }
    private static Task Check(HttpResponseMessage response, CancellationToken ct) => CloudConnection.Check(response, ct);
    private Task<Catalog> ReadCatalog(CancellationToken ct) => connection.Catalog(remoteDirectory, ct);

    private string RemotePath(string path) => remoteDirectory.Length == 0 ? CloudPath.Normalize(path) : remoteDirectory + "/" + CloudPath.Normalize(path);
    private string TransferCache => Path.Combine(Environment.GetEnvironmentVariable("MYPAN_TRANSFER_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPanTransfers"), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RootIdentity))));
    public Task<bool> RecoverUpload(string path, Stream source, CloudEntry entry, CancellationToken ct) => FileTransfers.RecoverCompleted(connection, RemotePath(path), source, entry, RootIdentity, TransferCache, ct);
    public void Acknowledge(CloudEntry entry) => FileTransfers.Acknowledge(connection, RemotePath(entry.Path), entry, RootIdentity, TransferCache);
    private CloudEntry LocalEntry(CloudEntry entry)
    {
        if (remoteDirectory.Length == 0) return entry;
        if (!CloudPath.IsUnder(entry.Path, remoteDirectory)) throw new InvalidDataException("服务器返回了所选云端目录之外的项目。");
        return entry with { Path = entry.Path[(remoteDirectory.Length + 1)..] };
    }
    private async Task EnsureDirectory(CancellationToken ct)
    {
        if (remoteDirectory.Length > 0 && !directoryReady) _ = await List(ct);
    }

    public async Task<Catalog> List(CancellationToken ct)
    {
        if (remoteDirectory.Length == 0) return await ReadCatalog(ct);
        await directoryGate.WaitAsync(ct);
        try
        {
            var catalog = await ReadCatalog(ct);
            var root = catalog.Entries.Find(x => x.Path.Equals(remoteDirectory, StringComparison.OrdinalIgnoreCase));
            // A removed/replaced root must never turn into an empty snapshot and delete the client's files.
            if (RemoteDirectoryId.HasValue && (root == null || root.Id != RemoteDirectoryId.Value))
                throw new InvalidOperationException("已绑定的云端目录被删除、重命名或替换，同步已停止。请在网页恢复原目录后重试，原有本地文件已保留。");
            if (root == null)
            {
                var path = "";
                foreach (var part in remoteDirectory.Split('/'))
                {
                    path = path.Length == 0 ? part : path + "/" + part;
                    var existing = catalog.Entries.Find(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        if (!existing.IsDirectory) throw new InvalidOperationException("云端路径被同名文件占用：" + path);
                        continue;
                    }
                    using var created = await http.PostAsJsonAsync("api/folders", new CreateFolder(path), ApiJson.Default.CreateFolder, ct);
                    if (created.StatusCode != HttpStatusCode.Conflict) await Check(created, ct);
                    // Another device may have created the same folder concurrently. Reuse its stable ID.
                    catalog = await ReadCatalog(ct);
                    existing = catalog.Entries.Find(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (existing is not { IsDirectory: true }) throw new InvalidOperationException("云端目录创建失败或被同名文件占用：" + path);
                }
                root = catalog.Entries.Find(x => x.Path.Equals(remoteDirectory, StringComparison.OrdinalIgnoreCase));
            }
            if (root is not { IsDirectory: true }) throw new InvalidOperationException("云端目录名称对应的是文件，请选择文件夹。");
            RemoteDirectoryId = root.Id;
            directoryReady = true;
            return new(catalog.Revision, catalog.Entries.Where(x => CloudPath.IsUnder(x.Path, remoteDirectory)).Select(LocalEntry).ToList());
        }
        finally { directoryGate.Release(); }
    }
    public async Task<CloudEntry> Folder(string path, CancellationToken ct)
    {
        await EnsureDirectory(ct);
        using var response = await http.PostAsJsonAsync("api/folders", new CreateFolder(RemotePath(path)), ApiJson.Default.CreateFolder, ct);
        await Check(response, ct);
        return LocalEntry((await response.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry, ct))!);
    }
    public async Task<CloudEntry> Upload(string path, Stream stream, string? version, CancellationToken ct, Action<long, long>? progress = null)
    {
        await EnsureDirectory(ct);
        return LocalEntry(await FileTransfers.Upload(connection, RemotePath(path), stream, version, RootIdentity,
            TransferCache, ct, progress));
    }
    public async Task<CloudEntry> Move(CloudEntry entry, string target, CancellationToken ct)
    {
        await EnsureDirectory(ct);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/entries/{entry.Id}")
        { Content = JsonContent.Create(new MoveEntry(RemotePath(target), entry.Version), ApiJson.Default.MoveEntry) };
        using var response = await http.SendAsync(request, ct);
        await Check(response, ct);
        return LocalEntry((await response.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry, ct))!);
    }
    public async Task Delete(CloudEntry entry, long revision, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/entries/{entry.Id}");
        request.Headers.IfMatch.Add(new('"' + entry.Version + '"'));
        request.Headers.Add("X-Catalog-Revision", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(request, ct);
        await Check(response, ct);
    }
    public async Task<HttpResponseMessage> Download(Guid id, string version, long offset, long length, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/content/{id}?version={Uri.EscapeDataString(version)}");
        request.Headers.Range = new(offset, checked(offset + length - 1));
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            await Check(response, ct);
            if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset ||
                response.Content.Headers.ContentRange?.To != offset + length - 1)
                throw new InvalidDataException("服务器返回的下载范围不匹配。");
            return response;
        }
        catch { response.Dispose(); throw; }
    }
    public Task ObserveChanges(Action changed, CancellationToken ct) => connection.ObserveChanges(changed, ct);
    public void Dispose() { connection.Dispose(); directoryGate.Dispose(); }

    // HttpRequestMessage owns its content, while SyncEngine owns the locked source handle.
    private sealed class BorrowedStream(Stream inner, Action<long, long>? progress) : Stream
    {
        private long transferred, lastReport;
        private void Report(int count)
        {
            transferred += count;
            var now = Environment.TickCount64;
            if (progress != null && (now - lastReport >= 250 || transferred == Length))
            { lastReport = now; progress(transferred, Length); }
        }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = inner.Read(buffer, offset, count); Report(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var read = await inner.ReadAsync(buffer, ct); Report(read); return read; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
