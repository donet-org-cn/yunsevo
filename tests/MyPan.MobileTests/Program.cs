using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPan.Contracts;
using MyPan.Mobile.Core;
using MyPan.Sync;

var executable = args.FirstOrDefault(x => x.StartsWith("--server-exe="))?[13..]
    ?? Path.GetFullPath("artifacts/mobile-update/server/MyPan.Server.exe");
var root = Path.GetFullPath(".test-data/mobile-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var url = "http://127.0.0.1:" + port;
var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
Process? server = null; string serverLog = ""; var checks = 0;
void Assert(bool result, string description) { if (!result) throw new Exception(description); Console.WriteLine("PASS: " + description); checks++; }
async Task Start()
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("MyPan__", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
    start.Environment["ASPNETCORE_URLS"] = url;
    start.Environment["MyPan__DataPath"] = Path.Combine(root, "server");
    start.Environment["MyPan__ApiKeyFile"] = Path.Combine(root, "key.txt");
    start.Environment["MyPan__InstallationFile"] = Path.Combine(root, "settings.json");
    start.Environment["MyPan__UnattendedSetup"] = "true";
    start.Environment["MyPan__AdminUsername"] = "mobileowner"; start.Environment["MyPan__AdminPassword"] = password;
    start.Environment["Logging__LogLevel__Default"] = "Warning";
    server = Process.Start(start)!;
    server.OutputDataReceived += (_, e) => serverLog += e.Data + "\n"; server.BeginOutputReadLine();
    server.ErrorDataReceived += (_, e) => serverLog += e.Data + "\n"; server.BeginErrorReadLine();
    using var probe = new HttpClient();
    for (var i = 0; i < 100; i++)
    {
        if (server.HasExited) throw new Exception("Fixture server exited.");
        try { if ((await probe.GetAsync(url + "/health")).IsSuccessStatusCode) return; } catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    throw new TimeoutException("Fixture startup timed out.");
}
async Task Stop()
{
    if (server is { HasExited: false }) { server.Kill(true); await server.WaitForExitAsync(); }
    server?.Dispose(); server = null;
}
var vault = new MemoryVault();
try
{
    await Start();
    var repository = new MobileRepository(Path.Combine(root, "phone"));
    using var mobile = new MobileClient(repository, vault);
    await mobile.Login(url, "mobileowner", password, default);
    Assert((await mobile.Connection!.Capabilities(default)).FileProvenance, "Native server advertises mobile file provenance support");
    var home = (await mobile.Folders(default)).Single(x => x.Path == "个人/mobileowner");
    var project = await mobile.CreateFolder(new(home.Id, home.Path, home.Category), "示范工程", default);
    var metadata = new MobileUploadMetadata("camera", DateTimeOffset.UtcNow, project.Id, "伪造目录名",
        "一号楼", "<script>photo-note</script>", "工程：示范工程\n拍摄人：mobileowner\n时间：现场记录", DeviceName: "QA-Android", CaptureId: Guid.NewGuid());
    var photoBytes = Encoding.UTF8.GetBytes("watermarked photo fixture");
    var photo = await mobile.Stage(new MemoryStream(photoBytes), "现场照片.jpg", project, metadata, "", default);
    Assert(File.Exists(repository.LocalPath(photo)), "Camera output is staged durably before any network upload");
    Assert(await mobile.Pump(true, true, default) == 0, "Wi-Fi-only policy prevents metered uploads");
    Assert(await mobile.Pump(false, true, default) == 1, "Camera queue uploads when network policy permits");
    var uploaded = repository.Jobs(mobile.AccountKey).Single(x => x.Id == photo.Id).Completed!;
    Assert(uploaded.Origin?.Photographer?.UserId == mobile.Account!.Login.User.Id &&
        uploaded.Origin.UploadedBy.Username == "mobileowner", "Photographer and uploader come from the authenticated account");
    Assert(uploaded.Origin!.Project == "示范工程" && uploaded.Origin.ProjectFolderId == project.Id,
        "Project name and routing are resolved from the authorized server folder");
    Assert(uploaded.Origin.ContentSha256 == Convert.ToHexString(SHA256.HashData(photoBytes)), "Provenance is bound to the committed content hash");
    Assert(uploaded.Origin.Watermark == metadata.Watermark && uploaded.Origin.DeviceName == "QA-Android", "Watermark text and device source survive upload");
    Assert(!File.Exists(repository.LocalPath(photo)), "Private upload cache is released only after durable queue acknowledgement");
    var stateJson = File.ReadAllText(Path.Combine(repository.Home, "mobile-state.json"));
    Assert(!stateJson.Contains(mobile.Account.Login.AccessToken) && !stateJson.Contains(password), "Queue and preferences contain no login tokens or passwords");
    repository.Update(photo with { State = "pending" }); // Simulate losing the local acknowledgement after a committed HTTP response.
    using (var restarted = new MobileClient(new MobileRepository(repository.Home), vault))
    {
        restarted.LoadRemembered(); await restarted.Resume(default);
        Assert(await restarted.Pump(false, true, default) == 1, "Restart recovers a committed upload by capture ID and hash without overwriting");
        Assert(restarted.Repository.Jobs(restarted.AccountKey).Single(x => x.Id == photo.Id).Completed!.Version == uploaded.Version,
            "Lost-acknowledgement recovery preserves the original cloud version");
    }
    // Re-open the repository so later writes include the simulated restart's acknowledgement.
    using var client = new MobileClient(new MobileRepository(repository.Home), vault); client.LoadRemembered(); await client.Resume(default);
    var captureHome = Path.Combine(root, "camera-recovery");
    var recoveredId = Guid.NewGuid();
    using (var cameraClient = new MobileClient(new MobileRepository(captureHome), vault))
    {
        cameraClient.LoadRemembered();
        var prepared = cameraClient.PrepareCameraPhoto("interrupted.jpg", project, metadata with { CaptureId = recoveredId });
        await File.WriteAllBytesAsync(prepared, photoBytes);
        Assert(cameraClient.Repository.Jobs(cameraClient.AccountKey).Count == 0, "An unfinished camera draft is not uploaded while its producer is still processing");
        Assert(await cameraClient.RecoverCapturedPhotos(default) == 0, "Background recovery does not steal a camera file during gallery export");
    }
    using (var cameraClient = new MobileClient(new MobileRepository(captureHome), vault))
    {
        cameraClient.LoadRemembered();
        Assert(await cameraClient.RecoverCapturedPhotos(default) == 1, "Process restart adopts an atomically completed camera photo from its durable draft");
        Assert(await cameraClient.Pump(false, true, default) == 1, "Recovered camera draft uploads with its original project and photographer");
        Assert(await cameraClient.RecoverCapturedPhotos(default) == 0, "Camera recovery is idempotent after the durable queue entry is written");
    }
    var spoofJson = JsonNode.Parse(JsonSerializer.Serialize(metadata with { CaptureId = Guid.NewGuid() }, ApiJson.Default.MobileUploadMetadata))!;
    spoofJson["photographer"] = JsonNode.Parse("""{"userId":"00000000-0000-0000-0000-000000000001","username":"someone-else","displayName":"伪造身份"}""");
    using (var spoof = new HttpRequestMessage(HttpMethod.Put, "api/content?path=" + Uri.EscapeDataString(project.Path + "/identity.jpg")) { Content = new ByteArrayContent(photoBytes) })
    {
        spoof.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); spoof.Headers.Add("X-MyPan-Photo", Convert.ToBase64String(Encoding.UTF8.GetBytes(spoofJson.ToJsonString())));
        using var result = await client.Connection!.Http.SendAsync(spoof); await CloudConnection.Check(result, default);
        var entry = (await result.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!;
        Assert(entry.Origin?.Photographer?.Username == "mobileowner", "Client-supplied photographer fields cannot spoof another user");
    }
    using (var invalid = new HttpRequestMessage(HttpMethod.Put, "api/content?path=" + Uri.EscapeDataString(project.Path + "/invalid.jpg")) { Content = new ByteArrayContent(photoBytes) })
    {
        invalid.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        invalid.Headers.Add("X-MyPan-Photo", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(metadata with { Latitude = 100, Longitude = 1 }, ApiJson.Default.MobileUploadMetadata)));
        using var result = await client.Connection!.Http.SendAsync(invalid);
        Assert(result.StatusCode == HttpStatusCode.BadRequest, "Invalid coordinates are rejected before content commit");
    }
    using (var invalid = new HttpRequestMessage(HttpMethod.Put, "api/content?path=" + Uri.EscapeDataString(home.Path + "/wrong-project.jpg")) { Content = new ByteArrayContent(photoBytes) })
    {
        invalid.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        invalid.Headers.Add("X-MyPan-Photo", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(metadata, ApiJson.Default.MobileUploadMetadata)));
        using var result = await client.Connection!.Http.SendAsync(invalid);
        Assert(result.StatusCode == HttpStatusCode.BadRequest, "A photo cannot claim a project while being written outside that project");
    }
    var shared = await client.Stage(new MemoryStream("shared document"u8.ToArray()), "../../document.txt", project, new("shared", DeviceName: "QA-Android"), "", default);
    Assert(!shared.Name.Contains('/') && !shared.Name.Contains('\\'), "Shared file names cannot escape the selected cloud directory");
    await client.Pump(false, true, default);
    var sharedEntry = client.Repository.Jobs(client.AccountKey).Single(x => x.Id == shared.Id).Completed!;
    Assert(sharedEntry.Origin?.Kind == "shared" && sharedEntry.Origin.Photographer == null, "Shared documents have an uploader without falsely claiming a photographer");
    var album = new FixtureAlbum();
    client.Repository.SetOptions(client.AccountKey, client.Options with { AutoBackup = true, IncludeVideos = false, BackupFolder = project });
    Assert(await client.ScanAlbum(album, "QA-Android", default) == 1, "Album scan queues only authorized photos when video backup is off");
    await client.Pump(false, true, default);
    Assert(await client.ScanAlbum(album, "QA-Android", default) == 0, "Album receipts prevent duplicate backup across repeated scans");
    album.Revision = "changed";
    Assert(await client.ScanAlbum(album, "QA-Android", default) == 1, "An edited media asset is backed up as new content");
    await client.Pump(false, true, default);
    using (var overwrite = new HttpRequestMessage(HttpMethod.Put, "api/content?path=" + Uri.EscapeDataString(uploaded.Path)) { Content = new StringContent("desktop replacement") })
    {
        overwrite.Headers.IfMatch.Add(new('"' + uploaded.Version + '"'));
        using var result = await client.Connection!.Http.SendAsync(overwrite); await CloudConnection.Check(result, default);
        var replacement = (await result.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!;
        Assert(replacement.Origin?.Photographer == null, "Replacing photo bytes does not keep stale camera attribution");
        using var restored = new HttpRequestMessage(HttpMethod.Post, "api/entries/" + uploaded.Id + "/versions/" + uploaded.Version + "/restore");
        restored.Headers.IfMatch.Add(new('"' + replacement.Version + '"'));
        using var restore = await client.Connection.Http.SendAsync(restored); await CloudConnection.Check(restore, default);
        uploaded = (await restore.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!;
        Assert(uploaded.Origin?.Photographer?.Username == "mobileowner" && uploaded.Origin.Watermark == metadata.Watermark,
            "Restoring an older version restores its own photograph provenance");
    }
    // Large mobile files reuse the existing durable chunk protocol with metadata.
    var largeBytes = new byte[17 * 1024 * 1024 + 11]; RandomNumberGenerator.Fill(largeBytes);
    var large = await client.Stage(new MemoryStream(largeBytes), "large-video.mp4", project, new("album", DateTimeOffset.UtcNow, DeviceName: "QA-Android"), "", default);
    await client.Pump(false, true, default);
    var largeEntry = client.Repository.Jobs(client.AccountKey).Single(x => x.Id == large.Id).Completed!;
    Assert(largeEntry.Origin?.ContentSha256 == Convert.ToHexString(SHA256.HashData(largeBytes)), "Chunked upload commits the same content hash and mobile provenance");
    using (var response = await client.Connection!.Http.GetAsync("api/content/" + largeEntry.Id))
        Assert(SHA256.HashData(await response.Content.ReadAsByteArrayAsync()).SequenceEqual(SHA256.HashData(largeBytes)), "Large mobile upload bytes round-trip exactly");
    await Stop(); await Start(); await client.Resume(default);
    var files = await client.Connection!.Http.GetFromJsonAsync("api/entries", ApiJson.Default.Catalog);
    Assert(files!.Entries.Single(x => x.Id == uploaded.Id).Origin?.Photographer?.Username == "mobileowner", "Photographer survives a native server restart and journal replay");
    using (var remove = new HttpRequestMessage(HttpMethod.Delete, "api/entries/" + uploaded.Id))
    {
        remove.Headers.IfMatch.Add(new('"' + uploaded.Version + '"')); remove.Headers.Add("X-Catalog-Revision", files.Revision.ToString());
        using var result = await client.Connection.Http.SendAsync(remove); await CloudConnection.Check(result, default);
    }
    var trash = await client.Connection.Http.GetFromJsonAsync("api/trash", ApiJson.Default.Catalog);
    Assert(trash!.Entries.Single(x => x.Id == uploaded.Id).Origin?.Photographer?.Username == "mobileowner", "Recycle-bin photos preserve who captured them");
    var originalScope = client.AccountKey;
    var awaiting = await client.Stage(new MemoryStream("private pending"u8.ToArray()), "pending.txt", project, new("shared"), "", default);
    using (var web = new HttpClient { BaseAddress = new Uri(url) })
    {
        using var loginResponse = await web.PostAsJsonAsync("api/auth/login", new LoginRequest("mobileowner", password), ApiJson.Default.LoginRequest);
        await CloudConnection.Check(loginResponse, default);
        var webSession = (await loginResponse.Content.ReadFromJsonAsync(ApiJson.Default.LoginResult))!;
        web.DefaultRequestHeaders.Authorization = new("Bearer", webSession.AccessToken);
        using var response = await web.PostAsJsonAsync("api/admin/users", new SaveUser("seconduser", "Second user", password), ApiJson.Default.SaveUser);
        await CloudConnection.Check(response, default);
    }
    var switchingAlbum = new FixtureAlbum { BeforeEnumerate = () => client.Login(url, "seconduser", password, default) };
    Assert(await client.ScanAlbum(switchingAlbum, "QA-Android", default) == 0 && client.Repository.Jobs(client.AccountKey).Count == 0,
        "An album scan stops when the account changes while media is being enumerated");
    Assert(await client.Pump(false, true, default) == 0 && client.Repository.Jobs(originalScope).Single(x => x.Id == awaiting.Id).State == "pending",
        "Signing into another account cannot upload the previous account's private queue");
    var staleRejected = false;
    try { await client.Stage(new MemoryStream("stale selection"u8.ToArray()), "stale.txt", project, new("shared"), "", default, expectedAccount: originalScope); }
    catch (InvalidOperationException) { staleRejected = true; }
    Assert(staleRejected && client.Repository.Jobs(client.AccountKey).Count == 0, "A stale folder selection cannot stage data under a different account");
    await client.Login(url, "mobileowner", password, default);
    Assert(await client.Pump(false, true, default) == 1 && client.Repository.Jobs(originalScope).Single(x => x.Id == awaiting.Id).State == "completed",
        "Returning to the original account resumes its preserved upload queue");
    await File.WriteAllTextAsync(Path.Combine(root, "verification.json"), new JsonObject { ["checksPassed"] = checks, ["nativeServer"] = executable, ["fixture"] = root }.ToJsonString());
    Console.WriteLine("ALL PASSED: " + checks + " mobile integration checks. " + root);
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
finally { await Stop(); await File.WriteAllTextAsync(Path.Combine(root, "server.log"), serverLog.Replace(password, "[redacted]")); }

sealed class MemoryVault : IAccountVault
{
    private MobileAccount? saved;
    public MobileAccount? Load() => saved;
    public void Save(MobileAccount account) => saved = account;
    public void Clear() => saved = null;
}
sealed class FixtureAlbum : IAlbumSource
{
    public string Revision = "original";
    public Func<Task>? BeforeEnumerate;
    public async IAsyncEnumerable<AlbumAsset> Enumerate([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        if (BeforeEnumerate != null) await BeforeEnumerate();
        yield return new("photo:1", Revision, "album.jpg", 5, "Camera", DateTimeOffset.UtcNow, "image/jpeg");
        yield return new("video:1", "1", "album.mp4", 5, "Camera", DateTimeOffset.UtcNow, "video/mp4");
    }
    public Task<Stream> Open(AlbumAsset asset, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream("album"u8.ToArray()));
}
