using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MyPan.Contracts;
using SMBLibrary;
using SMBLibrary.Client;

internal static class ProtocolChecks
{
    private sealed class TestSmb : SMB2Client, IDisposable { public bool Open(int port) => Connect(IPAddress.Loopback, SMBTransportType.DirectTCPTransport, port); public void Dispose() { try { Disconnect(); } catch (InvalidOperationException) { } } }
    private static int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    public static async Task Run(HttpClient admin, HttpClient alice, HttpClient bob, string url, string password, Guid aliceId, Guid bobId, bool tls, Action<bool, string> assert)
    {
        var config = new ProtocolSettings(true, true, !tls, "127.0.0.1", Port(), Port());
        using (var denied = await bob.PutAsJsonAsync("/api/admin/settings", config, ApiJson.Default.ProtocolSettings)) assert(denied.StatusCode == HttpStatusCode.Forbidden, "Protocol settings require an administrator");
        using (var enabled = await admin.PutAsJsonAsync("/api/admin/settings", config, ApiJson.Default.ProtocolSettings))
        { if (!enabled.IsSuccessStatusCode) throw new Exception(await enabled.Content.ReadAsStringAsync()); var result = (await enabled.Content.ReadFromJsonAsync(ApiJson.Default.ProtocolStatus))!; assert(result.FtpRunning && (tls || result.SmbRunning), tls ? "Enabled FTP service actually starts (SMB tested separately over the trusted loopback endpoint)" : "Enabled FTP and SMB services actually start"); }
        CloudEntry quotaFolder;
        using (var folder = await admin.PostAsJsonAsync("/api/folders", new CreateFolder("个人/alice/protocol-quota"), ApiJson.Default.CreateFolder)) { folder.EnsureSuccessStatusCode(); quotaFolder = (await folder.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!; }
        using (var policy = await admin.PutAsJsonAsync("/api/folders/" + quotaFolder.Id + "/policy", new FolderPolicy(quotaFolder.Id, aliceId, "private", 0, [], 5, 1), ApiJson.Default.FolderPolicy)) policy.EnsureSuccessStatusCode();
        using (var seed = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString("个人/alice/protocol-quota/item.txt")) { Content = new ByteArrayContent([1,2]) }) { seed.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); using var result = await alice.SendAsync(seed); result.EnsureSuccessStatusCode(); }
        using (var bypass = await alice.PutAsJsonAsync("/api/folders/" + quotaFolder.Id + "/policy", new FolderPolicy(quotaFolder.Id, aliceId, "private", 0, [], null, 1), ApiJson.Default.FolderPolicy)) assert(bypass.StatusCode == HttpStatusCode.Forbidden, "A folder owner cannot remove an administrator's storage quota");
        using (var bypass = await alice.DeleteAsync("/api/folders/" + quotaFolder.Id + "/policy")) assert(bypass.StatusCode == HttpStatusCode.Forbidden, "Restoring inheritance cannot bypass administrator quota and retention rules");
        using (var propfind = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/"))
        {
            propfind.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:" + password))); propfind.Headers.Add("Depth", "1");
            using var result = await alice.SendAsync(propfind); var xml = await result.Content.ReadAsStringAsync(); assert((int)result.StatusCode == 207 && xml.Contains("DAV:"), "WebDAV PROPFIND returns a real multistatus directory listing");
        }
        using (var upload = new HttpRequestMessage(HttpMethod.Put, "/dav/" + Uri.EscapeDataString("个人") + "/alice/dav.txt") { Content = new StringContent("WebDAV bytes") })
        { upload.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:" + password))); using var result = await alice.SendAsync(upload); assert(result.StatusCode == HttpStatusCode.Created, "WebDAV writes authenticated user files through the cloud store"); }
        var davEntry = (await alice.GetFromJsonAsync("/api/entries", ApiJson.Default.Catalog))!.Entries.Single(x => x.Path == "个人/alice/dav.txt");
        assert(await alice.GetStringAsync("/api/content/" + davEntry.Id) == "WebDAV bytes", "WebDAV uploads immediately appear in the sync API");
        using (var upload = new HttpRequestMessage(HttpMethod.Put, "/dav/" + Uri.EscapeDataString("个人") + "/alice/protocol-quota/item.txt") { Content = new ByteArrayContent(new byte[6]) })
        { upload.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:" + password))); using var result = await alice.SendAsync(upload); assert(result.StatusCode == HttpStatusCode.InsufficientStorage, "WebDAV enforces folder quotas on replacement writes"); }
        using (var stolen = new HttpRequestMessage(HttpMethod.Get, "/dav/" + Uri.EscapeDataString("个人") + "/alice/dav.txt"))
        { stolen.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:" + password))); using var result = await bob.SendAsync(stolen); assert(result.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, "WebDAV cannot bypass private folder authorization"); }
        using (var ftp = new TcpClient())
        {
            await ftp.ConnectAsync(IPAddress.Loopback, config.FtpPort); using var reader = new StreamReader(ftp.GetStream(), Encoding.UTF8); using var writer = new StreamWriter(ftp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            async Task<string> Command(string command) { await writer.WriteLineAsync(command); return await reader.ReadLineAsync() ?? throw new IOException("FTP closed"); }
            async Task<TcpClient> Passive()
            { var line = await Command("EPSV"); var port = int.Parse(Regex.Match(line, @"\(\|\|\|(\d+)\|\)").Groups[1].Value); var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port); return client; }
            assert((await reader.ReadLineAsync())!.StartsWith("220"), "FTP listener accepts standard control connections");
            await Command("USER alice"); assert((await Command("PASS " + password)).StartsWith("230"), "FTP authenticates a cloud account");
            await Command("CWD /个人/alice");
            using (var data = await Passive()) { assert((await Command("STOR ftp.txt")).StartsWith("150"), "FTP opens a passive upload channel"); await data.GetStream().WriteAsync(Encoding.UTF8.GetBytes("FTP bytes")); }
            assert((await reader.ReadLineAsync())!.StartsWith("226"), "FTP commits uploaded bytes before acknowledging success");
            using (var data = await Passive()) { await Command("RETR ftp.txt"); using var content = new StreamReader(data.GetStream()); assert(await content.ReadToEndAsync() == "FTP bytes", "FTP downloads the committed file"); } await reader.ReadLineAsync();
            assert((await Command("DELE ftp.txt")).StartsWith("250"), "FTP deletion uses the cloud recycle bin");
            using (var data = await Passive()) { await Command("STOR protocol-quota/item.txt"); await data.GetStream().WriteAsync(new byte[6]); }
            assert((await reader.ReadLineAsync())!.StartsWith("552"), "FTP reports quota rejection instead of acknowledging an oversized file"); await Command("QUIT");
        }
        assert((await alice.GetFromJsonAsync("/api/trash", ApiJson.Default.Catalog))!.Entries.Any(x => x.Path == "个人/alice/ftp.txt"), "FTP-deleted files remain recoverable in the browser recycle bin");
        if (!tls)
        {
            async Task<SmbCredential> Credential(Guid id) { using var r = await admin.PostAsync("/api/admin/users/" + id + "/smb-password", null); r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync(ApiJson.Default.SmbCredential))!; }
            var credential = await Credential(aliceId); var other = await Credential(bobId);
            using var smb = new TestSmb(); assert(smb.Open(config.SmbPort), "SMB negotiates on its configured independent TCP port");
            assert(smb.Login("", credential.Username, credential.Password) == NTStatus.STATUS_SUCCESS, "SMB authenticates using the user's dedicated credential");
            var share = smb.TreeConnect("mypan", out var treeStatus); assert(treeStatus == NTStatus.STATUS_SUCCESS, "SMB exposes a real mypan share");
            var status = share.CreateFile(out var handle, out _, "个人\\alice\\smb.txt", AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            assert(status == NTStatus.STATUS_SUCCESS, "SMB creates a file inside the authenticated private directory");
            assert(share.WriteFile(out var written, handle, 0, Encoding.UTF8.GetBytes("SMB bytes")) == NTStatus.STATUS_SUCCESS && written == 9, "SMB writes through a staged seekable file handle");
            assert(share.FlushFileBuffers(handle) == NTStatus.STATUS_SUCCESS && share.CloseFile(handle) == NTStatus.STATUS_SUCCESS, "SMB flush and close commit through API version and quota checks");
            var entry = (await alice.GetFromJsonAsync("/api/entries", ApiJson.Default.Catalog))!.Entries.Single(x => x.Path == "个人/alice/smb.txt");
            assert(await alice.GetStringAsync("/api/content/" + entry.Id) == "SMB bytes", "SMB uploads are visible to browser and sync clients");
            var quotaStatus = share.CreateFile(out var quotaHandle, out _, "个人\\alice\\protocol-quota\\item.txt", AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE, SMBLibrary.FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            var quotaWrite = quotaStatus == NTStatus.STATUS_SUCCESS ? share.WriteFile(out _, quotaHandle, 0, new byte[6]) : quotaStatus;
            var quotaFlush = quotaStatus == NTStatus.STATUS_SUCCESS ? share.FlushFileBuffers(quotaHandle) : quotaStatus;
            // SMBLibrary 1.5.8's client maps a non-success FLUSH response to STATUS_IO_TIMEOUT.
            // Verify the rejection, persisted API audit and original bytes rather than mistaking it for a successful save.
            assert(quotaStatus == NTStatus.STATUS_SUCCESS && quotaWrite == NTStatus.STATUS_SUCCESS && quotaFlush != NTStatus.STATUS_SUCCESS, "SMB flush reports failure when its file exceeds the cloud folder quota"); share.CloseFile(quotaHandle);
            var quotaEntry = (await alice.GetFromJsonAsync("/api/entries", ApiJson.Default.Catalog))!.Entries.Single(x=>x.Path=="个人/alice/protocol-quota/item.txt");
            assert((await alice.GetByteArrayAsync("/api/content/" + quotaEntry.Id)).SequenceEqual(new byte[]{1,2}), "Rejected protocol writes preserve the last committed cloud file bytes");
            share.Disconnect(); smb.Logoff(); smb.Disconnect();
            using var stranger = new TestSmb(); stranger.Open(config.SmbPort); stranger.Login("", other.Username, other.Password); var otherShare = stranger.TreeConnect("mypan", out _);
            var denied = otherShare.CreateFile(out var deniedHandle, out _, "个人\\alice\\smb.txt", AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            assert(denied != NTStatus.STATUS_SUCCESS, "SMB ID/path guessing cannot bypass another user's private folder");
            otherShare.Disconnect(); stranger.Logoff(); stranger.Disconnect();
        }
        var audit = (await admin.GetFromJsonAsync("/api/admin/audit", ApiJson.Default.ListAuditEntry))!;
        if (!tls) assert(audit.Any(x => x.Source.StartsWith("SMB") && x.Status == 507), "SMB quota rejection is recorded as a storage failure in the authoritative audit log");
        assert(audit.Any(x => x.User == "alice" && x.Action == "DELE" && x.Target == "个人/alice/ftp.txt") && audit.Any(x => x.Source.StartsWith("WebDAV")), "Persisted audit records identify protocol users, objects and recycle operations");
        assert(!System.Text.Json.JsonSerializer.Serialize(audit, ApiJson.Default.ListAuditEntry).Contains(password), "Audit logs do not include authentication passwords");
        using (var busy = new TcpListener(IPAddress.Loopback, 0))
        {
            busy.Start(); using var failure = await admin.PutAsJsonAsync("/api/admin/settings", config with { FtpPort = ((IPEndPoint)busy.LocalEndpoint).Port }, ApiJson.Default.ProtocolSettings);
            var status = (await admin.GetFromJsonAsync("/api/admin/settings", ApiJson.Default.ProtocolStatus))!;
            assert(failure.StatusCode == HttpStatusCode.Conflict && status.Settings == config && status.FtpRunning && (tls || status.SmbRunning), "Occupied protocol port is reported and the previous working configuration is restored");
        }
        using (var disabled = await admin.PutAsJsonAsync("/api/admin/settings", new ProtocolSettings(), ApiJson.Default.ProtocolSettings)) disabled.EnsureSuccessStatusCode();
        using (var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/")) { using var result = await admin.SendAsync(request); assert(result.StatusCode == HttpStatusCode.NotFound, "Disabling WebDAV removes its access endpoint immediately"); }
    }
}
