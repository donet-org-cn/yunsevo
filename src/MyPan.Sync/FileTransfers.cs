using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyPan.Contracts;

namespace MyPan.Sync;

public static class TransferLimits
{
    public static int Concurrency { get; } = int.TryParse(Environment.GetEnvironmentVariable("MYPAN_TRANSFER_CONCURRENCY"), out var value) ? Math.Clamp(value, 1, 8) : 4;
    internal static readonly SemaphoreSlim Uploads = new(Concurrency), Downloads = new(Concurrency);
    public const int BatchSize = 64;
}
internal sealed record UploadCheckpoint(Guid Id, string Remote = "", string Account = "", string Sha256 = "", string Server = "", string? CompletedVersion = null);
[JsonSerializable(typeof(UploadCheckpoint))]
internal partial class TransferJson : JsonSerializerContext;

public static class FileTransfers
{
    public const long ResumableThreshold = 16L * 1024 * 1024;
    public static async Task<bool> RecoverCompleted(CloudConnection connection, string remote, Stream source, CloudEntry entry, string account, string cacheDirectory, CancellationToken ct)
    {
        if (!Directory.Exists(cacheDirectory)) return false;
        foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.json"))
        {
            var saved = JsonSerializer.Deserialize(File.ReadAllBytes(path), TransferJson.Default.UploadCheckpoint);
            if (saved == null || saved.Remote != remote || saved.Account != account || saved.Server != connection.Http.BaseAddress!.ToString()) continue;
            if (saved.CompletedVersion != entry.Version)
            {
                var status = await Status(connection, saved.Id, ct);
                if (status?.Completed?.Version != entry.Version || status.Completed.Id != entry.Id) continue;
            }
            source.Position = 0;
            if (source.Length != entry.Size || !Convert.ToHexString(await SHA256.HashDataAsync(source, ct)).Equals(saved.Sha256, StringComparison.Ordinal)) return false;
            AtomicFile.WriteText(path, JsonSerializer.Serialize(saved with { CompletedVersion = entry.Version }, TransferJson.Default.UploadCheckpoint)); return true;
        }
        return false;
    }
    public static void Acknowledge(CloudConnection connection, string remote, CloudEntry entry, string account, string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.json"))
        {
            var saved = JsonSerializer.Deserialize(File.ReadAllBytes(path), TransferJson.Default.UploadCheckpoint);
            if (saved?.Remote == remote && saved.Account == account && saved.Server == connection.Http.BaseAddress!.ToString() && saved.CompletedVersion == entry.Version) File.Delete(path);
        }
    }
    public static async Task<CloudEntry> Upload(CloudConnection connection, string remote, Stream source, string? version, string account, string cacheDirectory,
        CancellationToken ct, Action<long, long>? progress = null, string? sha256 = null, MobileUploadMetadata? metadata = null)
    {
        await TransferLimits.Uploads.WaitAsync(ct);
        try
        {
            if (metadata != null && !(await connection.Capabilities(ct)).FileProvenance)
                throw new InvalidOperationException("服务端需要升级后才能记录手机拍摄和上传信息。");
            if (source.Length >= ResumableThreshold && (await connection.Capabilities(ct)).ResumableUploads)
                return await Resumable(connection, remote, source, version, account, cacheDirectory, progress, sha256, ct, metadata);
            using var request = new HttpRequestMessage(HttpMethod.Put, "api/content?path=" + Uri.EscapeDataString(remote)) { Content = new StreamContent(new BorrowedStream(source, progress)) };
            if (metadata != null) request.Headers.Add("X-MyPan-Photo", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(metadata, ApiJson.Default.MobileUploadMetadata)));
            request.Headers.ExpectContinue = source.Length > 1048576;
            if (version == null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); else request.Headers.IfMatch.Add(new('"' + version + '"'));
            using var response = await connection.Http.SendAsync(request, ct); await CloudConnection.Check(response, ct);
            return await response.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry, ct) ?? throw new IOException("上传响应为空。");
        }
        finally { TransferLimits.Uploads.Release(); }
    }
    private static async Task<CloudEntry> Resumable(CloudConnection connection, string remote, Stream source, string? version, string account,
        string cacheDirectory, Action<long, long>? progress, string? sha256, CancellationToken ct, MobileUploadMetadata? metadata)
    {
        source.Position = 0;
        sha256 ??= Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
        source.Position = 0;
        var identity = connection.Http.BaseAddress + "\n" + account + "\n" + remote + "\n" + version + "\n" + source.Length + "\n" + sha256;
        if (metadata != null) identity += "\n" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(metadata, ApiJson.Default.MobileUploadMetadata)));
        Directory.CreateDirectory(cacheDirectory);
        var checkpoint = Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
        UploadStatus? status = null;
        if (File.Exists(checkpoint))
        {
            try
            {
                var saved = JsonSerializer.Deserialize(File.ReadAllBytes(checkpoint), TransferJson.Default.UploadCheckpoint);
                if (saved != null) status = await Status(connection, saved.Id, ct);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { File.Delete(checkpoint); }
        }
        if (status == null)
        {
            using var response = await connection.Http.PostAsJsonAsync("api/uploads", new StartUpload(remote, source.Length, version, version == null, sha256, metadata), ApiJson.Default.StartUpload, ct);
            await CloudConnection.Check(response, ct);
            status = await response.Content.ReadFromJsonAsync(ApiJson.Default.UploadStatus, ct) ?? throw new IOException("上传会话为空。");
            AtomicFile.WriteText(checkpoint, JsonSerializer.Serialize(new UploadCheckpoint(status.Id, remote, account, sha256, connection.Http.BaseAddress!.ToString()), TransferJson.Default.UploadCheckpoint));
        }
        if (status.Length != source.Length || status.Offset < 0 || status.Offset > source.Length || status.ChunkBytes is < 65536 or > 16777216) throw new InvalidDataException("上传进度响应无效。");
        void Receipt(CloudEntry entry) => AtomicFile.WriteText(checkpoint, JsonSerializer.Serialize(new UploadCheckpoint(status.Id, remote, account, sha256, connection.Http.BaseAddress!.ToString(), entry.Version), TransferJson.Default.UploadCheckpoint));
        if (status.Completed != null) { Receipt(status.Completed); return status.Completed; }
        var buffer = ArrayPool<byte>.Shared.Rent(status.ChunkBytes);
        try
        {
            var failures = 0;
            while (status.Offset < source.Length)
            {
                try
                {
                    source.Position = status.Offset; var length = (int)Math.Min(status.ChunkBytes, source.Length - status.Offset);
                    await source.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
                    using var request = new HttpRequestMessage(HttpMethod.Put, "api/uploads/" + status.Id) { Content = new ByteArrayContent(buffer, 0, length) };
                    request.Content.Headers.ContentRange = new(status.Offset, status.Offset + length - 1, source.Length);
                    request.Headers.Add("X-Chunk-SHA256", Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, length))));
                    using var response = await connection.Http.SendAsync(request, ct); await CloudConnection.Check(response, ct);
                    var next = await response.Content.ReadFromJsonAsync(ApiJson.Default.UploadStatus, ct) ?? throw new IOException("分块响应为空。");
                    if (next.Id != status.Id || next.Offset != status.Offset + length) throw new InvalidDataException("分块响应的进度不匹配。");
                    status = next; progress?.Invoke(status.Offset, source.Length); failures = 0;
                }
                catch (HttpRequestException ex) when (!ct.IsCancellationRequested && ++failures <= 3 && (ex.StatusCode == null || ex.StatusCode == HttpStatusCode.Conflict || (int?)ex.StatusCode >= 500))
                {
                    await Task.Delay(250 * failures, ct);
                    status = await Status(connection, status.Id, ct) ?? throw new IOException("上传会话已过期，下次同步将重新开始。");
                }
            }
            // If this response is lost, the saved session resolves its durable commit receipt on the next pass.
            using var complete = await connection.Http.PostAsync("api/uploads/" + status.Id + "/complete", null, ct);
            await CloudConnection.Check(complete, ct);
            var entry = await complete.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry, ct) ?? throw new IOException("提交响应为空。");
            Receipt(entry); return entry;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    private static async Task<UploadStatus?> Status(CloudConnection connection, Guid id, CancellationToken ct)
    {
        using var response = await connection.Http.GetAsync("api/uploads/" + id, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        await CloudConnection.Check(response, ct);
        return await response.Content.ReadFromJsonAsync(ApiJson.Default.UploadStatus, ct);
    }
    private sealed class BorrowedStream(Stream inner, Action<long, long>? progress) : Stream
    {
        private long reported;
        private void Report(int count) { if (count > 0 && (Environment.TickCount64 - reported >= 250 || Position == Length)) { reported = Environment.TickCount64; progress?.Invoke(Position, Length); } }
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); Report(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var read = await inner.ReadAsync(buffer, ct); Report(read); return read; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
