using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyPan.Contracts;
using MyPan.Sync;

internal static class TransferChecks
{
    internal static async Task Run(HttpClient admin, HttpClient alice, HttpClient bob, string url, string token, Guid user, string root, Func<Task> restart, Action<bool, string> assert)
    {
        async Task<CloudEntry> Folder(string path)
        { using var result = await admin.PostAsJsonAsync("api/folders", new CreateFolder(path), ApiJson.Default.CreateFolder); result.EnsureSuccessStatusCode(); return (await result.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!; }
        async Task<UploadStatus> Begin(string path, byte[] bytes, string? version = null, string? hash = null)
        {
            using var result = await alice.PostAsJsonAsync("api/uploads", new StartUpload(path, bytes.Length, version, version == null, hash ?? Convert.ToHexString(SHA256.HashData(bytes))), ApiJson.Default.StartUpload);
            result.EnsureSuccessStatusCode(); return (await result.Content.ReadFromJsonAsync(ApiJson.Default.UploadStatus))!;
        }
        async Task<HttpResponseMessage> Chunk(UploadStatus upload, byte[] bytes, long offset, int count, string? hash = null, HttpClient? client = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "api/uploads/" + upload.Id) { Content = new ByteArrayContent(bytes, (int)offset, count) };
            request.Content.Headers.ContentRange = new(offset, offset + count - 1, bytes.Length);
            request.Headers.Add("X-Chunk-SHA256", hash ?? Convert.ToHexString(SHA256.HashData(bytes.AsSpan((int)offset, count))));
            return await (client ?? alice).SendAsync(request);
        }
        using (var scoped = await alice.GetAsync("api/entries?scope=" + Uri.EscapeDataString("个人/alice")))
        {
            var catalog = (await scoped.Content.ReadFromJsonAsync(ApiJson.Default.Catalog))!;
            assert(!catalog.Entries.Any(x => x.Path.StartsWith("公共", StringComparison.Ordinal) || x.Path.StartsWith("个人/bob", StringComparison.Ordinal)), "Scoped catalogs exclude unrelated shared and private roots");
            using var condition = new HttpRequestMessage(HttpMethod.Get, "api/entries?scope=" + Uri.EscapeDataString("个人/alice")); condition.Headers.IfNoneMatch.Add(scoped.Headers.ETag!);
            using var unchanged = await alice.SendAsync(condition);
            assert(unchanged.StatusCode == HttpStatusCode.NotModified && (await unchanged.Content.ReadAsByteArrayAsync()).Length == 0, "Unchanged catalog returns a body-free 304 response");
            using var another = new HttpRequestMessage(HttpMethod.Get, condition.RequestUri); another.Headers.IfNoneMatch.Add(scoped.Headers.ETag!);
            using var other = await bob.SendAsync(another);
            assert(other.StatusCode == HttpStatusCode.OK && !(await other.Content.ReadAsStringAsync()).Contains("secret.txt", StringComparison.Ordinal), "Catalog ETags cannot carry one account's private cache into another account");
        }
        var folder = await Folder("个人/alice/transfer-checks");
        var bytes = RandomNumberGenerator.GetBytes(16 * 1024 * 1024 + 777);
        var upload = await Begin(folder.Path + "/large.bin", bytes);
        using (var denied = await bob.GetAsync("api/uploads/" + upload.Id)) assert(denied.StatusCode == HttpStatusCode.NotFound, "Another user cannot inspect an upload session");
        using (var denied = await Chunk(upload, bytes, 0, upload.ChunkBytes, client: bob)) assert(denied.StatusCode == HttpStatusCode.NotFound, "Another user cannot append to a guessed upload session");
        using (var corrupt = await Chunk(upload, bytes, 0, upload.ChunkBytes, new string('0', 64))) assert(corrupt.StatusCode == HttpStatusCode.BadRequest, "A corrupt chunk is rejected before its offset is committed");
        var progress = (await alice.GetFromJsonAsync("api/uploads/" + upload.Id, ApiJson.Default.UploadStatus))!;
        assert(progress.Offset == 0, "Rejected chunk leaves the durable resume position unchanged");
        using (var first = await Chunk(upload, bytes, 0, upload.ChunkBytes)) first.EnsureSuccessStatusCode();
        using (var duplicate = await Chunk(upload, bytes, 0, upload.ChunkBytes)) assert(duplicate.StatusCode == HttpStatusCode.Conflict, "Replayed chunk cannot append duplicate bytes");
        using (var premature = await alice.PostAsync("api/uploads/" + upload.Id + "/complete", null)) assert(premature.StatusCode == HttpStatusCode.Conflict, "Incomplete uploads cannot become cloud files");
        assert(!(await alice.GetFromJsonAsync("api/entries", ApiJson.Default.Catalog))!.Entries.Any(x => x.Path == folder.Path + "/large.bin"), "Partial upload stays invisible to the catalog and other sync clients");
        await restart();
        progress = (await alice.GetFromJsonAsync("api/uploads/" + upload.Id, ApiJson.Default.UploadStatus))!;
        assert(progress.Offset == upload.ChunkBytes, "Server restart preserves the exact confirmed upload offset");
        for (long offset = progress.Offset; offset < bytes.Length; offset += upload.ChunkBytes)
        { using var sent = await Chunk(upload, bytes, offset, (int)Math.Min(upload.ChunkBytes, bytes.Length - offset)); sent.EnsureSuccessStatusCode(); }
        var progressFile = Path.Combine(root, "data", "transfers", upload.Id.ToString("N") + ".progress");
        using (var interrupted = new FileStream(progressFile, FileMode.Open, FileAccess.Write, FileShare.None)) { interrupted.Position = 64; interrupted.WriteByte(0xFF); interrupted.Flush(true); }
        var rolledBack = (await alice.GetFromJsonAsync("api/uploads/" + upload.Id, ApiJson.Default.UploadStatus))!;
        assert(rolledBack.Offset == 16L * 1024 * 1024, "A torn progress slot falls back to its independently checksummed preceding checkpoint");
        using (var resent = await Chunk(upload, bytes, rolledBack.Offset, (int)(bytes.Length - rolledBack.Offset))) resent.EnsureSuccessStatusCode();
        CloudEntry complete;
        using (var committed = await alice.PostAsync("api/uploads/" + upload.Id + "/complete", null)) { committed.EnsureSuccessStatusCode(); complete = (await committed.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!; }
        using (var again = await alice.PostAsync("api/uploads/" + upload.Id + "/complete", null)) { again.EnsureSuccessStatusCode(); assert((await again.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!.Version == complete.Version, "Repeated commit returns the same durable version instead of creating another version"); }
        assert((await alice.GetByteArrayAsync("api/content/" + complete.Id)).SequenceEqual(bytes), "Resumed multi-chunk upload preserves all original bytes");
        var bad = await Begin(folder.Path + "/bad.bin", [1, 2, 3], hash: new string('0', 64));
        using (var sent = await Chunk(bad, [1, 2, 3], 0, 3)) sent.EnsureSuccessStatusCode();
        using (var rejected = await alice.PostAsync("api/uploads/" + bad.Id + "/complete", null)) assert(rejected.StatusCode == HttpStatusCode.BadRequest, "Full-file checksum rejects mixed or corrupted content even when a chunk checksum matches");
        using (var canceled = await alice.DeleteAsync("api/uploads/" + bad.Id)) canceled.EnsureSuccessStatusCode();
        using (var gone = await alice.GetAsync("api/uploads/" + bad.Id)) assert(gone.StatusCode == HttpStatusCode.NotFound, "Cancel deletes resumable metadata and staged bytes");

        var cache = Path.Combine(root, "client-resume-cache"); var source = Path.Combine(root, "resume-source.bin"); await File.WriteAllBytesAsync(source, bytes);
        using (var connection = new CloudConnection(url, token))
        using (var canceled = new CancellationTokenSource())
        {
            try
            {
                await using var input = File.OpenRead(source);
                await FileTransfers.Upload(connection, folder.Path + "/client-resume.bin", input, null, user.ToString("N"), cache, canceled.Token, (position, _) => { if (position >= 8388608) canceled.Cancel(); });
                throw new Exception("Expected client upload cancellation.");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        }
        var checkpointPath = Directory.GetFiles(cache, "*.json").Single();
        using var checkpoint = JsonDocument.Parse(await File.ReadAllBytesAsync(checkpointPath)); var savedId = checkpoint.RootElement.GetProperty("Id").GetGuid();
        var saved = (await alice.GetFromJsonAsync("api/uploads/" + savedId, ApiJson.Default.UploadStatus))!;
        assert(saved.Offset == 8388608, "Client cancellation keeps the confirmed chunk and its local resume checkpoint");
        using (var connection = new CloudConnection(url, token))
        {
            await using var input = File.OpenRead(source);
            var resumed = await FileTransfers.Upload(connection, folder.Path + "/client-resume.bin", input, null, user.ToString("N"), cache, default);
            FileTransfers.Acknowledge(connection, folder.Path + "/client-resume.bin", resumed, user.ToString("N"), cache);
            var receipt = (await alice.GetFromJsonAsync("api/uploads/" + savedId, ApiJson.Default.UploadStatus))!;
            assert(receipt.Completed?.Id == resumed.Id && !File.Exists(checkpointPath), "A fresh client connection resumes the same session and removes its checkpoint after commit");
        }
        var downloadFolder = await Folder("个人/alice/resume-download");
        using (var put = new HttpRequestMessage(HttpMethod.Put, "api/content?path=" + Uri.EscapeDataString(downloadFolder.Path + "/file.bin")) { Content = new ByteArrayContent(bytes) })
        {
            put.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); using var sent = await alice.SendAsync(put); sent.EnsureSuccessStatusCode(); complete = (await sent.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!;
        }
        var destination = Path.Combine(root, "resume-download-local"); var state = Path.Combine(root, "resume-download-state.json");
        var stagedDirectory = Path.Combine(root, ".yunsevo-transfers-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state + destination)))[..24]); Directory.CreateDirectory(stagedDirectory);
        var partial = Path.Combine(stagedDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(complete.Id + "\n" + complete.Version))) + ".part");
        await File.WriteAllBytesAsync(partial, bytes[..8388608]);
        using (var connection = new CloudConnection(url, token))
        using (var sync = new PortableSync(connection, url, user, downloadFolder.Path, destination, state, _ => { })) await sync.SyncOnce(default);
        assert((await File.ReadAllBytesAsync(Path.Combine(destination, "file.bin"))).SequenceEqual(bytes) && !File.Exists(partial), "A partial full-copy download resumes by immutable file version and publishes identical bytes atomically");

        var receiptFolder = await Folder("个人/alice/lost-upload-ack"); var receiptLocal = Path.Combine(root, "lost-upload-ack"); var receiptState = Path.Combine(root, "lost-upload-ack.json");
        using (var connection = new CloudConnection(url, token))
        using (var sync = new PortableSync(connection, url, user, receiptFolder.Path, receiptLocal, receiptState, _ => { }))
        {
            await sync.SyncOnce(default); var localFile = Path.Combine(receiptLocal, "file.bin"); await File.WriteAllBytesAsync(localFile, bytes);
            await using (var input = File.OpenRead(localFile)) complete = await FileTransfers.Upload(connection, receiptFolder.Path + "/file.bin", input, null, user.ToString("N"), receiptState + ".transfers", default);
            await sync.SyncOnce(default);
            assert(Directory.GetFiles(receiptLocal).Length == 1 && !(await alice.GetFromJsonAsync("api/entries", ApiJson.Default.Catalog))!.Entries.Any(x => x.Path.StartsWith(receiptFolder.Path + "/", StringComparison.Ordinal) && x.Id != complete.Id), "A completed upload whose baseline was not saved recovers its receipt without a false conflict or duplicate file");
        }
        var quota = await Folder("个人/alice/chunk-quota");
        using (var policy = await admin.PutAsJsonAsync("api/folders/" + quota.Id + "/policy", new FolderPolicy(quota.Id, user, "private", 0, [], 10, 1), ApiJson.Default.FolderPolicy)) policy.EnsureSuccessStatusCode();
        var six = new byte[6]; var quotaA = await Begin(quota.Path + "/a.bin", six); var quotaB = await Begin(quota.Path + "/b.bin", six);
        using (var sent = await Chunk(quotaA, six, 0, 6)) sent.EnsureSuccessStatusCode();
        using (var sent = await Chunk(quotaB, six, 0, 6)) sent.EnsureSuccessStatusCode();
        using (var committed = await alice.PostAsync("api/uploads/" + quotaA.Id + "/complete", null)) committed.EnsureSuccessStatusCode();
        using (var rejected = await alice.PostAsync("api/uploads/" + quotaB.Id + "/complete", null)) assert(rejected.StatusCode == HttpStatusCode.InsufficientStorage, "Concurrent chunk uploads cannot commit beyond the folder quota");
        using (var canceled = await alice.DeleteAsync("api/uploads/" + quotaB.Id)) canceled.EnsureSuccessStatusCode();
        var stale = await Begin(folder.Path + "/client-resume.bin", bytes, (await alice.GetFromJsonAsync("api/entries", ApiJson.Default.Catalog))!.Entries.Single(x => x.Path == folder.Path + "/client-resume.bin").Version);
        var existing = (await alice.GetFromJsonAsync("api/entries", ApiJson.Default.Catalog))!.Entries.Single(x => x.Path == folder.Path + "/client-resume.bin");
        using (var overwrite = new HttpRequestMessage(HttpMethod.Put, "api/content?path=" + Uri.EscapeDataString(existing.Path)) { Content = new ByteArrayContent([9]) })
        { overwrite.Headers.IfMatch.Add(new('"' + existing.Version + '"')); using var sent = await alice.SendAsync(overwrite); sent.EnsureSuccessStatusCode(); }
        using (var rejected = await alice.PostAsync("api/uploads/" + stale.Id + "/complete", null)) assert(rejected.StatusCode == HttpStatusCode.PreconditionFailed, "A cloud version changed during chunk upload cannot be overwritten by a stale commit");
        using (var canceled = await alice.DeleteAsync("api/uploads/" + stale.Id)) canceled.EnsureSuccessStatusCode();
        var bulkFolder = await Folder("个人/alice/bulk-files"); var bulkLocal = Path.Combine(root, "bulk-local"); var bulkState = Path.Combine(root, "bulk-state.json");
        using (var connection = new CloudConnection(url, token))
        using (var sync = new PortableSync(connection, url, user, bulkFolder.Path, bulkLocal, bulkState, _ => { }))
        {
            await sync.SyncOnce(default); for (var index = 0; index < 130; index++) await File.WriteAllTextAsync(Path.Combine(bulkLocal, index + ".txt"), "bulk-" + index);
            await sync.SyncOnce(default);
            assert((await alice.GetFromJsonAsync("api/entries?scope=" + Uri.EscapeDataString(bulkFolder.Path), ApiJson.Default.Catalog))!.Entries.Count(x => !x.IsDirectory) == 130, "One sync cycle uploads more than 100 independent files through bounded batches");
            var before = File.GetLastWriteTimeUtc(bulkState); await sync.SyncOnce(default);
            assert(File.GetLastWriteTimeUtc(bulkState) == before, "An unchanged sync cycle does not rewrite its complete local state file");
        }

        var generation = Guid.NewGuid(); var journalPath = Path.Combine(root, "journal-crash-test");
        var journal = new StateJournal<CloudEntry>(journalPath, generation, ApiJson.Default.CloudEntry); journal.Append(complete);
        await File.AppendAllTextAsync(journalPath, "interrupted-frame");
        var recovered = new StateJournal<CloudEntry>(journalPath, generation, ApiJson.Default.CloudEntry);
        assert(recovered.Replay().Single() == complete, "Journal replay preserves a completed file and discards an interrupted tail");
        recovered.Append(complete with { Version = "next" });
        assert(new StateJournal<CloudEntry>(journalPath, generation, ApiJson.Default.CloudEntry).Replay().Count() == 2, "Recovery truncates the tail before subsequent durable appends");
        assert(!new StateJournal<CloudEntry>(journalPath, Guid.NewGuid(), ApiJson.Default.CloudEntry).Replay().Any(), "A newer snapshot generation cannot be reverted by an older journal");
    }
}
