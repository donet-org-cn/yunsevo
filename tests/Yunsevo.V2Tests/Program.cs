using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yunsevo.Contracts;
using Yunsevo.Sync;

var workspace = Path.GetFullPath(args.FirstOrDefault(x => x.StartsWith("--workspace="))?[12..] ?? Environment.CurrentDirectory);
var root = Path.Combine(workspace, ".test-data", "v2-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
int checks = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var tls = args.Contains("--tls");
var url = (tls ? "https" : "http") + "://127.0.0.1:" + port;
var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var serverExe = args.FirstOrDefault(x => x.StartsWith("--server-exe="))?[13..];
var start = new ProcessStartInfo(serverExe ?? "dotnet") { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
if (serverExe == null) start.ArgumentList.Add(Path.Combine(workspace, "src/Yunsevo.Server/bin/Release/net10.0/Yunsevo.Server.dll"));
start.Environment["ASPNETCORE_URLS"] = url;
start.Environment["Yunsevo__UnattendedSetup"] = "true"; start.Environment["Yunsevo__ApiKey"] = key; start.Environment["Yunsevo__ApiKeyFile"] = Path.Combine(root, "api-key.txt");
start.Environment["Yunsevo__DataPath"] = Path.Combine(root, "data"); start.Environment["Logging__LogLevel__Default"] = "Warning";
start.Environment["Yunsevo__SmbGatewayPath"] = args.FirstOrDefault(x => x.StartsWith("--gateway-exe="))?[14..] ?? Path.Combine(workspace, "src/Yunsevo.SmbGateway/bin/Release/net10.0/Yunsevo.SmbGateway" + (OperatingSystem.IsWindows() ? ".exe" : ""));
string? certificateHash = null;
if (args.Contains("--second-server-smoke"))
{
    using var second = await SecondServer.Start(start, root, key);
    Console.WriteLine("PASS: Second isolated server starts with independent installation settings. Artifacts: " + root);
    return;
}
if (tls)
{
    using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
    using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    certificateHash = cert.GetCertHashString(HashAlgorithmName.SHA256); var pass = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)); var certPath = Path.Combine(root, "server.pfx");
    File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pfx, pass)); start.Environment["Yunsevo__CertificatePath"] = certPath; start.Environment["Yunsevo__CertificatePassword"] = pass; start.Environment["Yunsevo__HttpsPort"] = port.ToString();
}
var logs = new StringBuilder(); Process? process = null;
void Start() { process = Process.Start(start)!; process.OutputDataReceived += (_, e) => { lock (logs) logs.AppendLine(e.Data); }; process.ErrorDataReceived += (_, e) => { lock (logs) logs.AppendLine(e.Data); }; process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
void Stop() { if (process is { HasExited: false }) { process.Kill(true); process.WaitForExit(); } process?.Dispose(); }
HttpClient Client(string? token = null)
{
    var handler = new HttpClientHandler();
    if (certificateHash != null) handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString(HashAlgorithmName.SHA256) == certificateHash;
    var client = new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(20) };
    if (tls) { client.DefaultRequestVersion = HttpVersion.Version20; client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact; }
    if (token != null) client.DefaultRequestHeaders.Authorization = new("Bearer", token); return client;
}
using var admin = Client(key);
async Task Ready() { for (var i = 0; i < 150; i++) { if (process!.HasExited) throw new Exception("Server exited: " + logs); try { using var r = await admin.GetAsync("/health"); if (r.IsSuccessStatusCode) return; } catch (HttpRequestException) { } await Task.Delay(100); } throw new TimeoutException("Server startup"); }
async Task<LoginResult> Login(string name, string password) { using var c = Client(); using var r = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, password), ApiJson.Default.LoginRequest); r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync(ApiJson.Default.LoginResult))!; }
async Task<Catalog> List(HttpClient c) => (await c.GetFromJsonAsync("/api/entries", ApiJson.Default.Catalog))!;
async Task<CloudEntry> Folder(string path) { using var r = await admin.PostAsJsonAsync("/api/folders", new CreateFolder(path), ApiJson.Default.CreateFolder); r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!; }
async Task<CloudEntry> Put(HttpClient client, string path, byte[] bytes, string? version = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString(path)) { Content = new ByteArrayContent(bytes) };
    if (version == null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); else request.Headers.IfMatch.Add(new('"' + version + '"'));
    using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!;
}
async Task Policy(CloudEntry folder, Guid owner, string visibility, List<AccessGrant> grants, long? quota = null, int? keep = null, int publicAccess = 3)
{ using var r = await admin.PutAsJsonAsync("/api/folders/" + folder.Id + "/policy", new FolderPolicy(folder.Id, owner, visibility, publicAccess, grants, quota, keep), ApiJson.Default.FolderPolicy); r.EnsureSuccessStatusCode(); }
async Task Delete(CloudEntry entry)
{ using var r = new HttpRequestMessage(HttpMethod.Delete, "/api/entries/" + entry.Id); r.Headers.IfMatch.Add(new('"' + entry.Version + '"')); r.Headers.Add("X-Catalog-Revision", (await List(admin)).Revision.ToString()); using var response = await admin.SendAsync(r); response.EnsureSuccessStatusCode(); }
try
{
    if (!tls) ClientWorkflowChecks.Run(root, url, Assert);
    Start(); await Ready();
    using (var anonymous = Client()) { using var denied = await anonymous.GetAsync("/api/admin/users"); Assert(denied.StatusCode == HttpStatusCode.Unauthorized, "Administration requires authentication"); }
    var adminLogin = await Login("admin", key); Assert(adminLogin.User.IsAdmin, "Bootstrap administrator can log in with generated initial password");
    if (tls)
    {
        using var response = await admin.GetAsync("/api/me"); Assert(response.Version.Major == 2, "HTTPS negotiates actual HTTP/2");
        using var quic = Client(key); quic.DefaultRequestVersion = HttpVersion.Version30;
        try { using var response3 = await quic.GetAsync("/api/me"); Assert(response3.Version.Major == 3, "HTTPS negotiates actual HTTP/3 over QUIC"); }
        catch (HttpRequestException ex) { Console.WriteLine("HTTP/3 unavailable on this host: " + ex.HttpRequestError + " · " + ex.InnerException?.Message); }
        using var fallback = Client(key); fallback.DefaultRequestVersion = HttpVersion.Version30; fallback.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        using var fallbackResponse = await fallback.GetAsync("/api/me"); Assert(fallbackResponse.IsSuccessStatusCode, "HTTP/3 preference can fall back to an available HTTPS protocol");
    }
    var password = "User-password-" + Guid.NewGuid().ToString("N");
    async Task<UserInfo> User(string name) { using var r = await admin.PostAsJsonAsync("/api/admin/users", new SaveUser(name, name, password), ApiJson.Default.SaveUser); r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync(ApiJson.Default.UserInfo))!; }
    var alice = await User("alice"); var bob = await User("bob");
    var aliceLogin = await Login("alice", password); var bobLogin = await Login("bob", password);
    using var a = Client(aliceLogin.AccessToken); using var b = Client(bobLogin.AccessToken);
    Assert((await List(a)).Entries.Any(x => x.Path == "个人/alice") && !(await List(a)).Entries.Any(x => x.Path == "个人/bob"), "Private home folders are isolated in directory listings");
    var syncFolders = (await a.GetFromJsonAsync("/api/sync-folders", ApiJson.Default.ListSyncFolder))!;
    Assert(syncFolders.Any(x => x.Path == "个人/alice" && x.Category == "private" && (x.Access & 1) != 0) && syncFolders.Any(x => x.Path == "公共" && x.Category == "public"), "Folder picker labels private and public roots from authoritative permissions");
    Assert(!syncFolders.Any(x => x.Path == "个人/bob") && syncFolders.Any(x => x.Path == "个人" && x.Category == "container" && x.Access == 8), "Folder picker hides other users' private roots and marks structural ancestors as nonselectable");
    using (var forbidden = await a.GetAsync("/api/admin/users")) Assert(forbidden.StatusCode == HttpStatusCode.Forbidden, "Regular users cannot access administrator APIs");
    var secret = await Put(a, "个人/alice/secret.txt", Encoding.UTF8.GetBytes("<script>alert('preview')</script>"));
    var rememberedAdminToken = tls ? null : await DeviceLoginChecks.Run(admin, url, key, secret.Id, Assert);
    if (!tls) await TransferChecks.Run(admin, a, b, url, aliceLogin.AccessToken, alice.Id, root, async () => { Stop(); Start(); await Ready(); }, Assert);
    foreach (var path in new[] { "/api/content/" + secret.Id, "/api/preview/" + secret.Id, "/api/entries/" + secret.Id + "/versions" })
    { using var denied = await b.GetAsync(path); Assert(denied.StatusCode == HttpStatusCode.Forbidden, "ID guessing cannot bypass permission: " + path.Split('/')[2]); }
    using (var preview = await a.GetAsync("/api/preview/" + secret.Id)) Assert(preview.Content.Headers.ContentType?.MediaType == "text/plain", "Text previews are served as inert plain text");
    var project = await Folder("项目");
    GroupInfo team;
    using (var r = await admin.PostAsJsonAsync("/api/admin/groups", new SaveGroup("研发组", [alice.Id]), ApiJson.Default.SaveGroup)) { r.EnsureSuccessStatusCode(); team = (await r.Content.ReadFromJsonAsync(ApiJson.Default.GroupInfo))!; }
    await Policy(project, adminLogin.User.Id, "restricted", [new("group", team.Id, 3)], 16, 3);
    var inherited = await Folder("项目/继承目录");
    syncFolders = (await a.GetFromJsonAsync("/api/sync-folders", ApiJson.Default.ListSyncFolder))!;
    Assert(syncFolders.Any(x => x.Id == project.Id && x.Category == "assigned" && x.Access == 3) && syncFolders.Any(x => x.Id == inherited.Id && x.Category == "assigned"), "Group-assigned folder types and access apply to all inherited subfolders");
    Assert(!(await b.GetFromJsonAsync("/api/sync-folders", ApiJson.Default.ListSyncFolder))!.Any(x => x.Id == project.Id), "A folder cannot appear in the picker before its user or group is granted access");
    var first = await Put(a, "项目/version.txt", new byte[8]); var second = await Put(a, first.Path, new byte[6], first.Version);
    Assert(!(await List(b)).Entries.Any(x => x.Path == "项目/version.txt"), "Restricted group files are hidden from nonmembers");
    using (var r = await admin.PutAsJsonAsync("/api/admin/groups/" + team.Id, new SaveGroup(team.Name, [alice.Id, bob.Id]), ApiJson.Default.SaveGroup)) r.EnsureSuccessStatusCode();
    Assert((await List(b)).Entries.Any(x => x.Id == first.Id), "Group membership changes apply to existing login sessions");
    using (var request = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString(first.Path)) { Content = new ByteArrayContent(new byte[4]) })
    {
        request.Headers.IfMatch.Add(new('"' + second.Version + '"')); using var response = await a.SendAsync(request);
        Assert(response.StatusCode == HttpStatusCode.InsufficientStorage && (await List(a)).Entries.Single(x => x.Id == first.Id).Version == second.Version,
            "History counts toward folder quota and rejected upload leaves current version unchanged");
    }
    await Policy(project, adminLogin.User.Id, "restricted", [new("group", team.Id, 3)], 16, 2);
    var third = await Put(a, first.Path, new byte[4], second.Version);
    var versions = (await a.GetFromJsonAsync("/api/entries/" + first.Id + "/versions", ApiJson.Default.ListCloudVersion))!;
    Assert(versions.Count == 2 && versions.Any(x => x.Version == second.Version) && !versions.Any(x => x.Version == first.Version), "Retention keeps latest N total versions including current");
    using (var removed = await a.GetAsync("/api/entries/" + first.Id + "/versions/" + first.Version)) Assert(removed.StatusCode == HttpStatusCode.NotFound, "Pruned versions are not downloadable");
    await Policy(project, adminLogin.User.Id, "restricted", [new("group", team.Id, 3)], 1024, 3);
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/entries/" + first.Id + "/versions/" + second.Version + "/restore"))
    { request.Headers.IfMatch.Add(new('"' + third.Version + '"')); using var restored = await a.SendAsync(request); restored.EnsureSuccessStatusCode(); }
    Assert((await a.GetByteArrayAsync("/api/content/" + first.Id)).Length == 6, "Restoring a retained version creates current content with original bytes");
    var current = (await List(a)).Entries.Single(x => x.Id == first.Id); await Delete(current);
    await Policy(project, adminLogin.User.Id, "restricted", [new("group", team.Id, 3)], 10, 1);
    using (var request = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString("项目/new.txt")) { Content = new ByteArrayContent(new byte[8]) })
    { request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); using var response = await a.SendAsync(request); Assert(response.StatusCode == HttpStatusCode.InsufficientStorage, "Recycle bin content continues to consume folder quota"); }
    using (var purge = await a.DeleteAsync("/api/trash/" + current.Id)) purge.EnsureSuccessStatusCode();
    var afterPurge = await Put(a, "项目/after-purge.txt", new byte[8]); Assert(afterPurge.Size == 8, "Permanent deletion releases folder quota");
    await Delete(afterPurge); using (var purge = await a.DeleteAsync("/api/trash/" + afterPurge.Id)) purge.EnsureSuccessStatusCode();
    async Task<HttpStatusCode> Race(string name)
    { using var request = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString("项目/" + name)) { Content = new ByteArrayContent(new byte[6]) }; request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); using var response = await a.SendAsync(request); return response.StatusCode; }
    var races = await Task.WhenAll(Race("a.bin"), Race("b.bin"));
    Assert(races.Count(x => x == HttpStatusCode.OK) == 1 && races.Count(x => x == HttpStatusCode.InsufficientStorage) == 1, "Concurrent uploads cannot race past the quota gate");
    await Policy(project, adminLogin.User.Id, "restricted", [new("group", team.Id, 1)], 1024, 3);
    using (var denied = await b.PostAsJsonAsync("/api/folders", new CreateFolder("项目/readonly"), ApiJson.Default.CreateFolder)) Assert(denied.StatusCode == HttpStatusCode.Forbidden, "Read-only grants reject folder creation");
    using (var r = await admin.PutAsJsonAsync("/api/admin/groups/" + team.Id, new SaveGroup(team.Name, [alice.Id]), ApiJson.Default.SaveGroup)) r.EnsureSuccessStatusCode();
    Assert(!(await List(b)).Entries.Any(x => CloudPath.IsUnder(x.Path, "项目")), "Removing group membership revokes existing session access immediately");
    using (var events = await a.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead))
    {
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync()); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!(await reader.ReadLineAsync(deadline.Token))!.StartsWith("data:", StringComparison.Ordinal)) { }
        var pending = Task.Run(async () => { while (await reader.ReadLineAsync(deadline.Token) is { } line) if (line.StartsWith("data:", StringComparison.Ordinal)) return true; return false; });
        var watch = Stopwatch.StartNew(); await Put(a, "个人/alice/realtime.txt", [1]); Assert(await pending && watch.Elapsed < TimeSpan.FromSeconds(2), "Server pushes a committed change without waiting for the polling interval");
    }
    if (!tls)
    {
        using var connection = new CloudConnection(url, aliceLogin.AccessToken);
        var localRoot = Path.Combine(root, "portable"); var statePath = Path.Combine(root, "portable-state.json");
        using var portable = new PortableSync(connection, url, alice.Id, "个人/alice", localRoot, statePath, Console.WriteLine);
        await portable.SyncOnce(default); Assert(File.Exists(Path.Combine(localRoot, "secret.txt")), "Portable engine downloads authorized named-folder content");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "portable-upload.txt"), "portable AOT sync"); await portable.SyncOnce(default);
        Assert((await List(a)).Entries.Any(x => x.Path == "个人/alice/portable-upload.txt"), "Portable engine uploads local changes into the same cloud directory");
        var onlineEntry = await Put(a, "个人/alice/online-delete.txt", [2]);
        var onlineRoot = Path.Combine(root, "online-delete");
        var prompts = 0;
        Task<DeletionChoice> UnexpectedPrompt(DeletionRequest request, CancellationToken ct) { prompts++; return Task.FromResult(DeletionChoice.Defer); }
        using (var online = new PortableSync(connection, url, alice.Id, "个人/alice", onlineRoot, Path.Combine(root, "online-state.json"), Console.WriteLine, UnexpectedPrompt))
        {
            await online.SyncOnce(default);
            File.Delete(Path.Combine(onlineRoot, "online-delete.txt")); await online.SyncOnce(default);
            Assert(prompts == 0 && !(await List(a)).Entries.Any(x => x.Id == onlineEntry.Id), "Online local deletion goes directly to the cloud recycle bin without a prompt");
            var offlineEntry = await Put(a, "个人/alice/offline-runtime.txt", [3]); await online.SyncOnce(default);
            Stop();
            try { await online.SyncOnce(default); } catch (HttpRequestException) { }
            File.Delete(Path.Combine(onlineRoot, "offline-runtime.txt")); await Task.Delay(150);
            Start(); await Ready(); await online.SyncOnce(default); await online.SyncOnce(default);
            Assert(prompts >= 2 && (await List(a)).Entries.Any(x => x.Id == offlineEntry.Id), "A runtime disconnection requires confirmation after reconnect and Defer cannot later auto-delete");
        }
        var similarName = await Put(a, "个人/alice/.yunsevo-download-readable.txt", [4]); await portable.SyncOnce(default); await portable.SyncOnce(default);
        Assert(File.Exists(Path.Combine(localRoot, ".yunsevo-download-readable.txt")) && (await List(a)).Entries.Any(x=>x.Id==similarName.Id), "A real file resembling a temporary download name is never mistaken for a local deletion");
        using var runCancel = new CancellationTokenSource(); var live = portable.Run(runCancel.Token);
        await Put(a, "个人/alice/push.txt", Encoding.UTF8.GetBytes("instant"));
        var watch = Stopwatch.StartNew(); while (!File.Exists(Path.Combine(localRoot, "push.txt")) && watch.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(50);
        Assert(File.Exists(Path.Combine(localRoot, "push.txt")), "Portable engine reacts to server events while keeping the session open");
        runCancel.Cancel(); await live;
        File.Delete(Path.Combine(localRoot, "push.txt"));
        var confirmations = 0; var startupDetected = false;
        Task<DeletionChoice> LocalOnly(DeletionRequest request, CancellationToken ct) { confirmations++; startupDetected = request.DetectedAtStartup; return Task.FromResult(DeletionChoice.LocalOnly); }
        using (var restarted = new PortableSync(connection, url, alice.Id, "个人/alice", localRoot, statePath, Console.WriteLine, LocalOnly))
        { await restarted.SyncOnce(default); await restarted.SyncOnce(default); }
        Assert(confirmations == 1 && startupDetected && !File.Exists(Path.Combine(localRoot, "push.txt")) && (await List(a)).Entries.Any(x => x.Path == "个人/alice/push.txt"), "Offline deletion requires confirmation; local-only choice persists without deleting cloud data or re-downloading");
        File.Delete(Path.Combine(localRoot, "portable-upload.txt"));
        using (var restarted = new PortableSync(connection, url, alice.Id, "个人/alice", localRoot, statePath, Console.WriteLine, (request, ct) => Task.FromResult(DeletionChoice.DeleteCloud))) await restarted.SyncOnce(default);
        Assert(!(await List(a)).Entries.Any(x => x.Path == "个人/alice/portable-upload.txt") && (await a.GetFromJsonAsync("/api/trash", ApiJson.Default.Catalog))!.Entries.Any(x => x.Path == "个人/alice/portable-upload.txt"), "Confirmed offline deletion moves cloud content into the recoverable recycle bin");
        var readonlyFolder = await Folder("个人/alice/readonly"); await Put(admin, "个人/alice/readonly/item.txt", Encoding.UTF8.GetBytes("read only"));
        await Policy(readonlyFolder, adminLogin.User.Id, "restricted", [new("user", alice.Id, 1)]);
        using (var restarted = new PortableSync(connection, url, alice.Id, "个人/alice", localRoot, statePath, Console.WriteLine, LocalOnly))
        {
            await restarted.SyncOnce(default); await File.WriteAllTextAsync(Path.Combine(localRoot, "readonly", "blocked.txt"), "preserve locally");
            await File.WriteAllTextAsync(Path.Combine(localRoot, "healthy.txt"), "healthy upload"); await restarted.SyncOnce(default);
            Assert((await List(a)).Entries.Any(x => x.Path == "个人/alice/healthy.txt") && File.Exists(Path.Combine(localRoot, "readonly", "blocked.txt")), "A denied local upload cannot block healthy files or discard local bytes");
            await File.WriteAllTextAsync(Path.Combine(localRoot, "secret.txt"), "local conflict"); var currentSecret = (await List(a)).Entries.Single(x => x.Id == secret.Id);
            await Put(a, currentSecret.Path, Encoding.UTF8.GetBytes("remote conflict"), currentSecret.Version); await restarted.SyncOnce(default);
            Assert(await File.ReadAllTextAsync(Path.Combine(localRoot, "secret.txt")) == "remote conflict" && Directory.EnumerateFiles(localRoot, "secret*").Any(x => File.ReadAllText(x) == "local conflict"), "Concurrent local and remote edits preserve both versions in separate files");
        }
    }
    var desktopExe = args.FirstOrDefault(x => x.StartsWith("--desktop-exe="))?[14..];
    if (desktopExe != null && !tls)
    {
        for (var mode = 0; mode < (OperatingSystem.IsWindows() ? 2 : 1); mode++)
        {
        var nativeRoot = Path.Combine(root, mode == 0 ? "native-desktop" : "native-desktop-on-demand");
        var nativeStart = new ProcessStartInfo(desktopExe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "--probe", "--server", url, "--root", nativeRoot, "--remote-dir", "个人/alice" }) nativeStart.ArgumentList.Add(arg);
        if (mode == 1) { nativeStart.ArgumentList.Add("--on-demand"); nativeStart.ArgumentList.Add("--verify-file"); nativeStart.ArgumentList.Add("secret.txt"); }
        nativeStart.Environment["YUNSEVO_USERNAME"] = "alice"; nativeStart.Environment["YUNSEVO_PASSWORD"] = password;
        using var native = Process.Start(nativeStart)!; var stdout = native.StandardOutput.ReadToEndAsync(); var stderr = native.StandardError.ReadToEndAsync();
        try { await native.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!native.HasExited) { native.Kill(true); await native.WaitForExitAsync(); } }
        if (native.ExitCode != 0) Console.Error.WriteLine(await stdout + await stderr);
        Assert(native.ExitCode == 0 && File.Exists(Path.Combine(nativeRoot, "secret.txt")), mode == 0 ? "Published Avalonia Native AOT client logs in and downloads scoped account files" : "Published Avalonia Native AOT account client registers Explorer placeholders and hydrates real cloud bytes");
        }
    }
    var catalogText = await File.ReadAllTextAsync(Path.Combine(root, "data", "catalog.json"));
    var movable = await Folder("个人/alice/move-quota"); await Policy(movable, alice.Id, "private", [], 8, 1);
    var deletedUnderMoved = await Put(a, "个人/alice/move-quota/deleted.bin", new byte[6]); await Delete(deletedUnderMoved);
    using (var move = await a.PatchAsJsonAsync("/api/entries/" + movable.Id, new MoveEntry("个人/alice/move-renamed", movable.Version), ApiJson.Default.MoveEntry)) move.EnsureSuccessStatusCode();
    using (var upload = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString("个人/alice/move-renamed/growth.bin")) { Content = new ByteArrayContent(new byte[3]) })
    { upload.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); using var result = await a.SendAsync(upload); Assert(result.StatusCode == HttpStatusCode.InsufficientStorage, "Renaming a quota folder cannot detach its recycle-bin usage or bypass the size limit"); }
    using (var restored = await a.PostAsync("/api/trash/" + deletedUnderMoved.Id + "/restore", null)) restored.EnsureSuccessStatusCode();
    Assert((await List(a)).Entries.Any(x=>x.Id==deletedUnderMoved.Id && x.Path=="个人/alice/move-renamed/deleted.bin"), "A deleted file restores under its folder's new name with the same immutable ID");
    var oldPrivate = await Folder("公共/reused"); await Policy(oldPrivate, bob.Id, "private", []); var oldPrivateFile = await Put(b, "公共/reused/private.txt", [3]); await Delete(oldPrivate);
    var reused = await Folder("公共/reused"); await Policy(reused, adminLogin.User.Id, "public", []);
    using (var move = await a.PatchAsJsonAsync("/api/entries/"+reused.Id,new MoveEntry("公共/reused-new",reused.Version),ApiJson.Default.MoveEntry)) move.EnsureSuccessStatusCode();
    Assert((await b.GetFromJsonAsync("/api/trash",ApiJson.Default.Catalog))!.Entries.Any(x=>x.Id==oldPrivateFile.Id && x.Path=="公共/reused/private.txt") && !(await a.GetFromJsonAsync("/api/trash",ApiJson.Default.Catalog))!.Entries.Any(x=>x.Id==oldPrivateFile.Id), "Reusing and renaming a public path cannot move or expose an older private deletion batch");
    var headlessExe = args.FirstOrDefault(x => x.StartsWith("--headless-exe="))?[15..];
    if (headlessExe != null && !tls) await HeadlessChecks.Run(headlessExe, root, url, password, a, Assert);
    if (desktopExe != null && !tls)
    {
        var privateUi = await Folder("个人/alice/ui-workflow"); var publicUi = await Folder("公共/ui-workflow");
        await Put(a, privateUi.Path + "/probe.txt", Encoding.UTF8.GetBytes("UI-private")); await Put(a, publicUi.Path + "/probe.txt", Encoding.UTF8.GetBytes("UI-public"));
        var guiHome = Path.Combine(root, "desktop-workflow");
        using var secondServer = await SecondServer.Start(start, root, key);
        var guiStart = new ProcessStartInfo(desktopExe) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }; guiStart.ArgumentList.Add("--workflow-probe");
        guiStart.Environment["YUNSEVO_DESKTOP_HOME"] = guiHome; guiStart.Environment["YUNSEVO_PROBE_SERVER"] = url;
        guiStart.Environment["YUNSEVO_USERNAME"] = "alice"; guiStart.Environment["YUNSEVO_PASSWORD"] = password;
        guiStart.Environment["YUNSEVO_PROBE_PRIVATE"] = privateUi.Path; guiStart.Environment["YUNSEVO_PROBE_PUBLIC"] = publicUi.Path;
        guiStart.Environment["YUNSEVO_PROBE_SECOND_SERVER"] = secondServer.Url; guiStart.Environment["YUNSEVO_PROBE_SECOND_PASSWORD"] = key;
        using var gui = Process.Start(guiStart)!;
        try { await gui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        finally { if (!gui.HasExited) { gui.Kill(true); await gui.WaitForExitAsync(); } }
        if (File.Exists(Path.Combine(guiHome, "workflow-error.txt"))) Console.Error.WriteLine(await File.ReadAllTextAsync(Path.Combine(guiHome, "workflow-error.txt")));
        Assert(gui.ExitCode == 0 && File.Exists(Path.Combine(guiHome, "workflow-verification.json")), "Published Native AOT GUI completes login, both folder-selection wizards, concurrent modes, online delete, per-task pause and relogin");
        var restoreStart = new ProcessStartInfo(desktopExe) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }; restoreStart.ArgumentList.Add("--restore-probe");
        restoreStart.Environment["YUNSEVO_DESKTOP_HOME"] = guiHome; restoreStart.Environment.Remove("YUNSEVO_PASSWORD"); restoreStart.Environment.Remove("YUNSEVO_PROBE_SECOND_PASSWORD");
        using var restoredGui = Process.Start(restoreStart)!;
        try { await restoredGui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!restoredGui.HasExited) { restoredGui.Kill(true); await restoredGui.WaitForExitAsync(); } }
        if (File.Exists(Path.Combine(guiHome, "restore-error.txt"))) Console.Error.WriteLine(await File.ReadAllTextAsync(Path.Combine(guiHome, "restore-error.txt")));
        Assert(restoredGui.ExitCode == 0 && File.Exists(Path.Combine(guiHome, "restore-verification.json")), "A second Native AOT GUI process restores two server logins and three saved tasks without receiving any password");
    }
    await ProtocolChecks.Run(admin, a, b, url, password, alice.Id, bob.Id, tls, Assert);
    if (!tls) await CommercialChecks.Run(admin, a, url, aliceLogin.AccessToken, alice.Id, Assert);
    if (!tls) await MigrationFeaturesChecks.Run(admin, a, b, root, workspace, start, alice.Id, aliceLogin.AccessToken, password, Assert);
    Assert(!catalogText.Contains(password) && !catalogText.Contains(aliceLogin.AccessToken), "Persisted accounts and sessions contain hashes, not plaintext passwords or session tokens");
    Stop(); Start(); await Ready(); Assert((await List(a)).Entries.Any(x => x.Id == secret.Id), "Accounts, sessions and file permissions persist across restart");
    if (rememberedAdminToken != null)
    {
        using var restoredDevice = new CloudConnection(url, rememberedAdminToken); var resumed = await restoredDevice.Resume(default);
        using var denied = await restoredDevice.Http.GetAsync("/api/content/" + secret.Id);
        Assert(resumed.User.IsAdmin && denied.StatusCode == HttpStatusCode.Forbidden, "Remembered login survives server restart with its device permission boundary intact");
    }
    using (var disabled = await admin.PutAsJsonAsync("/api/admin/users/" + bob.Id, new SaveUser(bob.Username, bob.DisplayName, null, false, true), ApiJson.Default.SaveUser)) disabled.EnsureSuccessStatusCode();
    using (var denied = await b.GetAsync("/api/entries")) Assert(denied.StatusCode == HttpStatusCode.Unauthorized, "Disabling a user invalidates their existing session");
    using (var logout = await a.PostAsync("/api/auth/logout", null)) logout.EnsureSuccessStatusCode();
    using (var denied = await a.GetAsync("/api/me")) Assert(denied.StatusCode == HttpStatusCode.Unauthorized, "Logout invalidates the access token");
    File.WriteAllText(Path.Combine(root, "verification.json"), System.Text.Json.JsonSerializer.Serialize(new ChangeNotice(checks), ApiJson.Default.ChangeNotice));
    Console.WriteLine($"ALL PASSED: {checks} v2 checks. Artifacts: {root}");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine(logs); Environment.ExitCode = 1; }
finally { Stop(); File.WriteAllText(Path.Combine(root, "server.log"), logs.ToString()); }
