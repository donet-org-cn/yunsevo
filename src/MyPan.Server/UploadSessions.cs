using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyPan.Contracts;

namespace MyPan.Server;

internal sealed record PendingUpload(Guid Id, Guid Owner, StartUpload Request, long Offset, DateTimeOffset ExpiresUtc, string Version, CloudEntry? Completed = null, long ProgressCounter = 0);
[JsonSerializable(typeof(PendingUpload))]
internal partial class UploadJson : JsonSerializerContext;

internal sealed class UploadSessions
{
    internal const int ChunkBytes = 8 * 1024 * 1024;
    private readonly CloudStore store;
    private readonly string directory;
    private readonly long limit, pendingLimit;
    private readonly SemaphoreSlim createGate = new(1), transfers = new(8);
    private readonly SemaphoreSlim[] stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private DateTimeOffset lastCleanup;
    internal UploadSessions(CloudStore store, string root, long limit, long pendingLimit)
    {
        this.store = store; this.limit = limit; this.pendingLimit = pendingLimit;
        directory = System.IO.Path.Combine(root, "transfers"); Directory.CreateDirectory(directory);
    }
    private string Metadata(Guid id) => System.IO.Path.Combine(directory, id.ToString("N") + ".json");
    private string Part(Guid id) => System.IO.Path.Combine(directory, id.ToString("N") + ".part");
    private string Progress(Guid id) => System.IO.Path.Combine(directory, id.ToString("N") + ".progress");
    private PendingUpload ReadProgress(PendingUpload upload)
    {
        try { return ReadProgressCore(upload); } catch (FileNotFoundException) { return upload; }
    }
    private PendingUpload ReadProgressCore(PendingUpload upload)
    {
        if (upload.Completed != null || !File.Exists(Progress(upload.Id))) return upload;
        using var progressFile = new FileStream(Progress(upload.Id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[(int)Math.Min(128, progressFile.Length)]; progressFile.ReadExactly(bytes);
        var available = File.Exists(Part(upload.Id)) ? new FileInfo(Part(upload.Id)).Length : 0;
        for (var index = 0; index < 2 && bytes.Length >= (index + 1) * 64; index++)
        {
            var slot = bytes.AsSpan(index * 64, 64);
            if (!SHA256.HashData(slot[..24]).AsSpan().SequenceEqual(slot.Slice(24, 32))) continue;
            var counter = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slot);
            var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slot[8..]);
            var expires = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(slot[16..]);
            if (counter > upload.ProgressCounter && offset >= 0 && offset <= upload.Request.Length && offset <= available)
                upload = upload with { ProgressCounter = counter, Offset = offset, ExpiresUtc = DateTimeOffset.FromUnixTimeMilliseconds(expires) };
        }
        return upload;
    }
    private void SaveProgress(PendingUpload upload)
    {
        Span<byte> slot = stackalloc byte[64]; slot.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(slot, upload.ProgressCounter);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(slot[8..], upload.Offset);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(slot[16..], upload.ExpiresUtc.ToUnixTimeMilliseconds());
        SHA256.HashData(slot[..24], slot.Slice(24, 32));
        using var output = new FileStream(Progress(upload.Id), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (output.Length < 128) output.SetLength(128);
        output.Position = (upload.ProgressCounter & 1) * 64; output.Write(slot); output.Flush(true);
    }
    private SemaphoreSlim Stripe(Guid id) => stripes[(id.GetHashCode() & int.MaxValue) % stripes.Length];
    private void Save(PendingUpload upload) => AtomicFile.WriteText(Metadata(upload.Id), JsonSerializer.Serialize(upload, UploadJson.Default.PendingUpload));
    private PendingUpload Load(Guid id, RequestActor actor)
    {
        if (!File.Exists(Metadata(id))) throw new StoreException(404, "上传会话不存在，请重新开始。");
        var upload = JsonSerializer.Deserialize(File.ReadAllBytes(Metadata(id)), UploadJson.Default.PendingUpload) ?? throw new IOException("上传记录损坏。");
        if (upload.Owner != actor.Id) throw new StoreException(404, "上传会话不存在。");
        upload = ReadProgress(upload);
        if (upload.ExpiresUtc <= DateTimeOffset.UtcNow) throw new StoreException(410, "上传会话已过期。");
        return upload;
    }
    private PendingUpload Resolve(PendingUpload upload, RequestActor actor)
    {
        var committed = store.CompletedUpload(actor, upload.Request.Path, upload.Version);
        if (upload.Completed != null) return upload;
        // Recover a successful commit even if the process died before its receipt was written.
        if (committed != null) { upload = upload with { Completed = committed, Offset = upload.Request.Length }; Save(upload); }
        else store.ValidateUpload(actor, upload.Request.Path, upload.Request.Length, upload.Request.Version, upload.Request.CreateOnly, limit, checkQuota: false);
        return upload;
    }
    private static UploadStatus Public(PendingUpload upload) => new(upload.Id, upload.Request.Length, upload.Offset, ChunkBytes, upload.ExpiresUtc, upload.Completed);
    internal async Task<UploadStatus> Begin(RequestActor actor, StartUpload request, CancellationToken ct)
    {
        request = request with { Path = CloudPath.Normalize(request.Path) };
        if (request.Sha256.Length != 64 || request.Sha256.Any(x => !char.IsAsciiHexDigit(x))) throw new StoreException(400, "需要有效的 SHA-256 校验值。");
        store.ValidateUpload(actor, request.Path, request.Length, request.Version, request.CreateOnly, limit);
        store.ValidatePhoto(actor, request.Path, request.Metadata);
        await createGate.WaitAsync(ct);
        try
        {
            Cleanup();
            var active = Records().Where(x => x.Completed == null && x.ExpiresUtc > DateTimeOffset.UtcNow).ToList();
            if (active.Count >= 128 || active.Count(x => x.Owner == actor.Id) >= 8) throw new StoreException(429, "进行中的上传较多，请等待部分文件完成后重试。");
            if (request.Length > pendingLimit || active.Sum(x => x.Request.Length) > pendingLimit - request.Length) throw new StoreException(507, "待上传文件的预留空间已达上限，请完成或取消其他上传。");
            var upload = new PendingUpload(Guid.NewGuid(), actor.Id, request, 0, DateTimeOffset.UtcNow.AddHours(24), Guid.NewGuid().ToString("N"));
            using (File.Create(Part(upload.Id))) { }
            try { Save(upload); } catch { File.Delete(Part(upload.Id)); throw; }
            return Public(upload);
        }
        finally { createGate.Release(); }
    }
    internal async Task<UploadStatus> Status(RequestActor actor, Guid id, CancellationToken ct)
    {
        var stripe = Stripe(id); await stripe.WaitAsync(ct);
        try { return Public(Resolve(Load(id, actor), actor)); } finally { stripe.Release(); }
    }
    internal async Task<UploadStatus> Chunk(RequestActor actor, Guid id, string range, string sha256, Stream source, long? contentLength, CancellationToken ct)
    {
        if (!ContentRangeHeaderValue.TryParse(range, out var parsed) || parsed.Unit != "bytes" || !parsed.HasRange || !parsed.HasLength ||
            sha256.Length != 64 || sha256.Any(x => !char.IsAsciiHexDigit(x))) throw new StoreException(400, "分块范围或校验值无效。");
        var length = parsed.To!.Value - parsed.From!.Value + 1;
        if (length > ChunkBytes || contentLength != length) throw new StoreException(413, "分块大小超过限制或长度不符。");
        await transfers.WaitAsync(ct); var stripe = Stripe(id);
        try
        {
            await stripe.WaitAsync(ct);
            try
            {
                var upload = Resolve(Load(id, actor), actor);
                if (upload.Completed != null) return Public(upload);
                if (parsed.From != upload.Offset || parsed.Length != upload.Request.Length || parsed.To >= upload.Request.Length) throw new StoreException(409, "上传进度已变化，请查询断点后重试。");
                await using var output = new FileStream(Part(id), FileMode.Open, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (output.Length < upload.Offset) throw new IOException("暂存文件不完整。");
                output.SetLength(upload.Offset); output.Position = upload.Offset;
                var buffer = ArrayPool<byte>.Shared.Rent(131072); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                try
                {
                    var remaining = length;
                    while (remaining > 0)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(131072, remaining)), ct);
                        if (read == 0) throw new StoreException(400, "分块上传未完成，请从原断点重试。");
                        hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct); remaining -= read;
                    }
                    if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new StoreException(400, "分块校验失败，请重新上传此块。");
                    output.Flush(true);
                    upload = upload with { Offset = checked(upload.Offset + length), ExpiresUtc = DateTimeOffset.UtcNow.AddHours(24), ProgressCounter = checked(upload.ProgressCounter + 1) };
                    SaveProgress(upload); return Public(upload);
                }
                catch { output.SetLength(parsed.From.Value); output.Flush(true); throw; }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            finally { stripe.Release(); }
        }
        finally { transfers.Release(); }
    }
    internal async Task<CloudEntry> Complete(RequestActor actor, Guid id, CancellationToken ct)
    {
        await transfers.WaitAsync(ct); var stripe = Stripe(id);
        try
        {
            await stripe.WaitAsync(ct);
            try
            {
                var upload = Resolve(Load(id, actor), actor);
                if (upload.Completed != null) return upload.Completed;
                if (upload.Offset != upload.Request.Length) throw new StoreException(409, "文件尚未上传完整。");
                await using (var input = new FileStream(Part(id), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (input.Length != upload.Offset || !Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(upload.Request.Sha256, StringComparison.OrdinalIgnoreCase)) throw new StoreException(400, "完整文件校验失败，请取消并重新上传。");
                }
                var entry = await store.CommitUpload(actor, upload.Request.Path, Part(id), upload.Request.Length, upload.Request.Version, upload.Request.CreateOnly, upload.Version, ct,
                    upload.Request.Metadata, upload.Request.Sha256.ToUpperInvariant());
                Save(upload with { Completed = entry }); return entry;
            }
            finally { stripe.Release(); }
        }
        finally { transfers.Release(); }
    }
    internal async Task Cancel(RequestActor actor, Guid id, CancellationToken ct)
    {
        var stripe = Stripe(id); await stripe.WaitAsync(ct);
        try { _ = Load(id, actor); File.Delete(Part(id)); File.Delete(Progress(id)); File.Delete(Metadata(id)); } finally { stripe.Release(); }
    }
    private void Cleanup()
    {
        if (DateTimeOffset.UtcNow - lastCleanup < TimeSpan.FromMinutes(1)) return;
        lastCleanup = DateTimeOffset.UtcNow;
        foreach (var upload in Records())
        {
            if (upload.ExpiresUtc > DateTimeOffset.UtcNow) continue;
            var stripe = Stripe(upload.Id); if (!stripe.Wait(0)) continue;
            try
            {
                if (File.Exists(Metadata(upload.Id)) && ReadProgress(JsonSerializer.Deserialize(File.ReadAllBytes(Metadata(upload.Id)), UploadJson.Default.PendingUpload)!).ExpiresUtc <= DateTimeOffset.UtcNow)
                { File.Delete(Part(upload.Id)); File.Delete(Progress(upload.Id)); File.Delete(Metadata(upload.Id)); }
            }
            finally { stripe.Release(); }
        }
        foreach (var orphan in Directory.EnumerateFiles(directory, "*.part"))
            if (!File.Exists(System.IO.Path.ChangeExtension(orphan, ".json")) && File.GetLastWriteTimeUtc(orphan) < DateTime.UtcNow.AddHours(-24)) File.Delete(orphan);
    }
    private IEnumerable<PendingUpload> Records()
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            PendingUpload? record;
            try { record = JsonSerializer.Deserialize(File.ReadAllBytes(path), UploadJson.Default.PendingUpload); }
            catch (FileNotFoundException) { continue; }
            if (record != null) yield return ReadProgress(record);
        }
    }
}
