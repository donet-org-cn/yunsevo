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
    Assert(new UploadNetworkState(true, true, false, false, false).AllowsWifiOnly, "Direct Wi-Fi permits Wi-Fi-only uploads");
    Assert(new UploadNetworkState(true, true, false, true, true).AllowsWifiOnly, "Metered VPN over Wi-Fi permits Wi-Fi-only uploads");
    Assert(!new UploadNetworkState(true, false, true, true, true).AllowsWifiOnly, "VPN over cellular cannot bypass Wi-Fi-only uploads");
    Assert(!new UploadNetworkState(true, false, true, false, false).AllowsWifiOnly, "Unmetered cellular is still blocked by Wi-Fi-only policy");
    Assert(!new UploadNetworkState(true, true, true, true, false).AllowsWifiOnly, "VPN with mixed Wi-Fi and cellular stays blocked");
    Assert(!new UploadNetworkState(true, false, false, true, false).AllowsWifiOnly, "Undisclosed VPN transport stays blocked");
    Assert(!new UploadNetworkState(false, true, false, false, false).AllowsWifiOnly, "Disconnected Wi-Fi cannot start uploads");
    Assert(new UploadNetworkState(true, true, false, false, true).AllowsWifiOnly, "Metered Wi-Fi obeys the selected transport policy");
    var frameClock = new VideoFrameClock();
    const long mono = 900_000_000_000L, sensor = mono + 201_634_000_000_000L;
    Assert(frameClock.Timestamp(sensor, mono) == mono, "Camera boot-time offset is mapped into the encoder monotonic clock");
    Assert(frameClock.Timestamp(sensor + 33_000_000, mono + 33_000_000) == mono + 33_000_000, "Video frame spacing preserves real sensor timing");
    Assert(frameClock.Timestamp(sensor + 33_000_000, mono + 34_000_000) > mono + 33_000_000, "Repeated camera timestamps remain strictly monotonic");
    Assert(frameClock.Timestamp(sensor + 99_000_000_000_000L, mono + 70_000_000) == mono + 70_000_000, "Discontinuous camera timestamps cannot add hours to a video");
    frameClock.Reset();
    Assert(frameClock.Timestamp(sensor, mono) == mono, "Every recording starts with a fresh timestamp mapping");
    Assert(frameClock.Timestamp(sensor + 11_000_000_000L, mono + 11_000_000_000L) - mono == 11_000_000_000L, "An eleven-second recording has an eleven-second video timeline");
    Assert(frameClock.Timestamp(sensor - 1, mono + 11_033_000_000L) == mono + 11_033_000_000L, "Backward sensor timestamps recover without reversing presentation time");
    foreach (var local in new[] { "http://192.168.1.8:5080", "http://10.77.0.5", "http://172.16.0.1", "http://172.31.255.254", "http://169.254.1.2", "http://[fd12::1]", "http://[fe80::1]", "http://[::ffff:192.168.1.8]", "https://example.com" })
    {
        using var allowed = new CloudConnection(local); Assert(allowed.Http.BaseAddress != null, "Accept supported server: " + local);
    }
    foreach (var remote in new[] { "http://8.8.8.8", "http://172.15.0.1", "http://172.32.0.1", "http://192.169.1.1", "http://example.com", "http://0.0.0.0", "http://[2001:4860:4860::8888]", "http://[::ffff:8.8.8.8]", "http://user:secret@192.168.1.8", "http://192.168.1.8?token=secret" })
    {
        var rejected = false; try { using var denied = new CloudConnection(remote); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Reject public or malformed cleartext server: " + remote.Split('@').Last().Split('?')[0]);
    }
    using (var localOnly = new CloudConnection("http://127.0.0.1"))
    {
        var rejected = false; try { using var response = await localOnly.Http.GetAsync("http://example.com"); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Absolute HTTP requests cannot bypass LAN validation or send credentials to public hosts");
    }
    await Start();
    var repository = new MobileRepository(Path.Combine(root, "phone"));
    using var mobile = new MobileClient(repository, vault);
    await mobile.Login(url, "mobileowner", password, default);
    Assert((await mobile.Connection!.Capabilities(default)).FileProvenance, "Native server advertises mobile file provenance support");
    var home = (await mobile.Folders(default)).Single(x => x.Path == "个人/mobileowner");
    var project = await mobile.CreateFolder(new(home.Id, home.Path, home.Category), "示范工程", default);
    var metadata = new MobileUploadMetadata("camera", DateTimeOffset.UtcNow, project.Id, "伪造目录名",
        "一号楼", "<script>photo-note</script>", "工程：示范工程\n拍摄人：mobileowner\n时间：现场记录", DeviceName: "QA-Android", CaptureId: Guid.NewGuid(),
        Environment: new("测试工地", "Fixture", new("多云", 23, DateTimeOffset.UtcNow, "Fixture", "https://example.com", "Fixture data", Forecast: true), DateTimeOffset.UtcNow));
    var photoBytes = Encoding.UTF8.GetBytes("watermarked photo fixture");
    var photo = await mobile.Stage(new MemoryStream(photoBytes), "现场照片.jpg", project, metadata, "", default);
    Assert(File.Exists(repository.LocalPath(photo)), "Camera output is staged durably before any network upload");
    Assert(await mobile.Pump(true, true, default) == 0, "Wi-Fi-only policy prevents uploads without Wi-Fi");
    Assert(await mobile.Pump(false, true, default) == 1, "Camera queue uploads when network policy permits");
    var uploaded = repository.Jobs(mobile.AccountKey).Single(x => x.Id == photo.Id).Completed!;
    Assert(uploaded.Origin?.Photographer?.UserId == mobile.Account!.Login.User.Id &&
        uploaded.Origin.UploadedBy.Username == "mobileowner", "Photographer and uploader come from the authenticated account");
    Assert(uploaded.Origin!.Project == "示范工程" && uploaded.Origin.ProjectFolderId == project.Id,
        "Project name and routing are resolved from the authorized server folder");
    Assert(uploaded.Origin.ContentSha256 == Convert.ToHexString(SHA256.HashData(photoBytes)), "Provenance is bound to the committed content hash");
    Assert(uploaded.Origin.Watermark == metadata.Watermark && uploaded.Origin.DeviceName == "QA-Android", "Watermark text and device source survive upload");
    Assert(uploaded.Origin.Environment?.Weather?.Forecast == true && uploaded.Origin.Environment.Address == "测试工地", "Address and timestamped forecast provenance survive upload");
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
    var albumPage = await client.Album(project.Path, 1, default);
    Assert(albumPage.Items.Any(x => x.Entry.Id == largeEntry.Id) && !albumPage.Items.Any(x => x.Entry.Id == uploaded.Id), "Cloud album includes videos and omits recycled photographs");
    var smallPage = await client.Connection.Http.GetFromJsonAsync("api/browser/media?page=1&pageSize=1", ApiJson.Default.PagedResultBrowserEntry);
    Assert(smallPage!.Items.Count == 1 && smallPage.Total > 1, "Cloud media pagination limits results without losing total count");
    Assert(smallPage.NextCursor != null, "Media page provides an opaque continuation cursor");
    var nextPage = await client.Connection.Http.GetFromJsonAsync("api/browser/media?pageSize=1&after=" + Uri.EscapeDataString(smallPage.NextCursor!), ApiJson.Default.PagedResultBrowserEntry);
    Assert(nextPage!.Items.Count == 1 && nextPage.Items[0].Entry.Id != smallPage.Items[0].Entry.Id && nextPage.Total == smallPage.Total, "Cursor continuation advances without repeating the previous media item");
    using (var invalidCursor = await client.Connection.Http.GetAsync("api/browser/media?after=not-a-cursor"))
        Assert(invalidCursor.StatusCode == HttpStatusCode.BadRequest, "Malformed media cursors are rejected");
    var pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAgAAAAEACAIAAABK8lkwAAADwElEQVR4nO3XQQ2DABBFQWhqgVOdVQQSOFQJpzqrj6ogm/BmBGz+7WXXbT8XAHoe0wMAmCEAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQ9bzu9O99XHece3t9P9MT4P58AABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAECUAAFECABAlAABRAgAQJQAAUQIAELVu+zm9AYABPgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAIgSAIAoAQCIEgCAKAEAiBIAgCgBAFia/mxqB0evgfeBAAAAAElFTkSuQmCC");
    var thumbnailJob = await client.Stage(new MemoryStream(pngBytes), "thumbnail.png", project, new("shared"), "", default);
    await client.Pump(false,true,default);
    var thumbnailEntry = client.Repository.Jobs(client.AccountKey).Single(x=>x.Id==thumbnailJob.Id).Completed!;
    byte[] thumbnail;
    using (var response = await client.Connection.Http.GetAsync("api/media/" + thumbnailEntry.Id + "/thumbnail?version=" + thumbnailEntry.Version))
    { await CloudConnection.Check(response,default); thumbnail = await response.Content.ReadAsByteArrayAsync(); }
    var dimensions = JpegDimensions(thumbnail);
    Assert(dimensions == (256,128) && thumbnail.Length < 256*1024, "Native AOT server creates a real JPEG thumbnail bounded to 256 pixels");
    using (var response = await client.Connection.Http.GetAsync("api/media/"+thumbnailEntry.Id+"/thumbnail"))
        Assert((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(thumbnail), "Repeated thumbnail requests use the same versioned cached JPEG");
    using (var response = await client.Connection.Http.GetAsync("api/media/"+thumbnailEntry.Id+"/thumbnail?version=stale"))
        Assert(response.StatusCode==HttpStatusCode.Conflict, "Stale thumbnail versions are rejected");
    var neighborhood = await client.MediaNeighbors(thumbnailEntry.Id,project.Path,default);
    Assert(neighborhood.Current.Id==thumbnailEntry.Id && (neighborhood.Next!=null || neighborhood.Previous!=null), "Media navigation returns authorized adjacent items in the album");
    var continued = await client.Connection.Http.GetFromJsonAsync("api/browser/media?pageSize=1&after="+Uri.EscapeDataString(smallPage.NextCursor!),ApiJson.Default.PagedResultBrowserEntry);
    Assert(continued!.Items[0].Entry.Id==nextPage.Items[0].Entry.Id, "Adding a newer photo does not shift an existing continuation cursor");
    using (var forbidden = await client.Connection.Http.GetAsync("api/admin/environment-settings"))
        Assert(forbidden.StatusCode == HttpStatusCode.Forbidden, "An administrator using a device session cannot read web administration settings");
    var draftId = Guid.NewGuid();
    using (var recorder = new MobileClient(new MobileRepository(Path.Combine(root, "video-recovery")), vault))
    {
        recorder.LoadRemembered();
        var target = recorder.PrepareCameraPhoto("capture.mp4", project, metadata with { CaptureId = draftId });
        await File.WriteAllBytesAsync(target, "complete MP4 fixture"u8.ToArray());
    }
    using (var recovered = new MobileClient(new MobileRepository(Path.Combine(root, "video-recovery")), vault))
    {
        recovered.LoadRemembered(); await recovered.RecoverCapturedPhotos(default); await recovered.Pump(false, true, default);
        Assert(recovered.Repository.Jobs(recovered.AccountKey).Single().Completed?.Path.EndsWith(".mp4") == true,
            "A completed video draft survives a process restart and retains its MP4 extension");
    }
    var preference = client.Options with { SaveCameraToGallery = false, WatermarkStyle = "横幅记录", WatermarkBackground = false, WatermarkTextColor = "#FFFF00", IncludeWeather = true };
    client.Repository.SetOptions(client.AccountKey, preference);
    Assert(new MobileRepository(client.Repository.Home).Options(client.AccountKey) == preference, "Cloud-only saving and watermark appearance persist per account");
    var originalScope = client.AccountKey;
    var awaiting = await client.Stage(new MemoryStream("private pending"u8.ToArray()), "pending.txt", project, new("shared"), "", default);
    using (var web = new HttpClient { BaseAddress = new Uri(url) })
    {
        using var loginResponse = await web.PostAsJsonAsync("api/auth/login", new LoginRequest("mobileowner", password), ApiJson.Default.LoginRequest);
        await CloudConnection.Check(loginResponse, default);
        var webSession = (await loginResponse.Content.ReadFromJsonAsync(ApiJson.Default.LoginResult))!;
        web.DefaultRequestHeaders.Authorization = new("Bearer", webSession.AccessToken);
        var fakeKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using (var saved = await web.PutAsJsonAsync("api/admin/environment-settings", new EnvironmentSettings(Enabled: false, AddressProvider: "amap", WeatherProvider: "amap", AmapKey: fakeKey), ApiJson.Default.EnvironmentSettings))
        {
            await CloudConnection.Check(saved, default);
            var value = (await saved.Content.ReadFromJsonAsync(ApiJson.Default.EnvironmentSettings))!;
            Assert(value.HasAmapKey && value.AmapKey == null && !File.ReadAllText(Path.Combine(root,"server","environment-settings.json")).Contains(fakeKey), "Provider key is encrypted on disk and redacted in administrator responses");
        }
        using (var saved = await web.PutAsJsonAsync("api/admin/environment-settings", new EnvironmentSettings(Enabled: false), ApiJson.Default.EnvironmentSettings))
        { await CloudConnection.Check(saved, default); Assert((await saved.Content.ReadFromJsonAsync(ApiJson.Default.EnvironmentSettings))!.HasAmapKey, "Saving settings with an empty key preserves the encrypted credential"); }
        var unavailable = await client.CaptureEnvironment(new(39.9,116.4), default);
        Assert(unavailable.Weather == null && unavailable.Address == null && unavailable.WeatherError != null, "Disabled environment services report unavailable without inventing weather or an address");
        using (var invalid = await web.PostAsJsonAsync("api/mobile/environment", new EnvironmentQuery(91,116), ApiJson.Default.EnvironmentQuery))
            Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "Environment lookup rejects out-of-range coordinates");
        using (var backupResponse = await web.GetAsync("api/admin/backup"))
        {
            await CloudConnection.Check(backupResponse, default);
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await backupResponse.Content.ReadAsByteArrayAsync()));
            Assert(zip.GetEntry("data/environment-settings.json") != null && zip.GetEntry("data/environment.key") != null, "Migration backup includes encrypted provider settings and their decryption key");
        }
        using (var saved = await web.PutAsJsonAsync("api/admin/environment-settings", new EnvironmentSettings(Enabled: false, ClearAmapKey: true), ApiJson.Default.EnvironmentSettings))
        { await CloudConnection.Check(saved, default); Assert(!(await saved.Content.ReadFromJsonAsync(ApiJson.Default.EnvironmentSettings))!.HasAmapKey, "Administrator can explicitly remove a provider credential"); }
        using var response = await web.PostAsJsonAsync("api/admin/users", new SaveUser("seconduser", "Second user", password), ApiJson.Default.SaveUser);
        await CloudConnection.Check(response, default);
    }
    var switchingAlbum = new FixtureAlbum { BeforeEnumerate = () => client.Login(url, "seconduser", password, default) };
    Assert(await client.ScanAlbum(switchingAlbum, "QA-Android", default) == 0 && client.Repository.Jobs(client.AccountKey).Count == 0,
        "An album scan stops when the account changes while media is being enumerated");
    Assert(!(await client.Album("", 1, default)).Items.Any(x => x.Entry.Path.StartsWith(project.Path + "/")), "Cloud album does not expose another account's private engineering media");
    using (var deniedThumb = await client.Connection!.Http.GetAsync("api/media/"+thumbnailEntry.Id+"/thumbnail"))
        Assert(deniedThumb.StatusCode==HttpStatusCode.Forbidden, "A cached thumbnail remains protected after switching to an unauthorized account");
    using (var deniedNavigation = await client.Connection.Http.GetAsync("api/browser/media/"+thumbnailEntry.Id+"/neighbors"))
        Assert(deniedNavigation.StatusCode==HttpStatusCode.NotFound, "Media navigation hides another account's private collection");
    Assert(await client.Pump(false, true, default) == 0 && client.Repository.Jobs(originalScope).Single(x => x.Id == awaiting.Id).State == "pending",
        "Signing into another account cannot upload the previous account's private queue");
    var staleRejected = false;
    try { await client.Stage(new MemoryStream("stale selection"u8.ToArray()), "stale.txt", project, new("shared"), "", default, expectedAccount: originalScope); }
    catch (InvalidOperationException) { staleRejected = true; }
    Assert(staleRejected && client.Repository.Jobs(client.AccountKey).Count == 0, "A stale folder selection cannot stage data under a different account");
    await client.Login(url, "mobileowner", password, default);
    Assert(await client.Pump(false, true, default) == 1 && client.Repository.Jobs(originalScope).Single(x => x.Id == awaiting.Id).State == "completed",
        "Returning to the original account resumes its preserved upload queue");
    var onlineToken = client.Account!.Login.AccessToken;
    await client.Logout(default);
    using (var revoked = new HttpClient())
    {
        revoked.DefaultRequestHeaders.Authorization = new("Bearer", onlineToken);
        using var denied = await revoked.GetAsync(url + "/api/me");
        Assert(denied.StatusCode == HttpStatusCode.Unauthorized, "Online mobile logout revokes the server token");
    }
    await client.Login(url, "mobileowner", password, default);
    var preserved = await client.Stage(new MemoryStream("offline logout queue"u8.ToArray()), "offline-preserved.txt", project, new("shared"), "", default);
    await Stop();
    var logout = client.Logout(default);
    Assert(client.Account == null && vault.Load() == null, "Offline logout clears the active session and remembered vault immediately");
    await logout.WaitAsync(TimeSpan.FromSeconds(4));
    using (var signedOut = new MobileClient(new MobileRepository(repository.Home), vault))
    { signedOut.LoadRemembered(); Assert(signedOut.Account == null, "Restart after offline logout stays signed out"); }
    Assert(client.Repository.Jobs(originalScope).Any(x => x.Id == preserved.Id && File.Exists(client.Repository.LocalPath(x))), "Offline logout preserves queued local files for the original account");
    await File.WriteAllTextAsync(Path.Combine(root, "verification.json"), new JsonObject { ["checksPassed"] = checks, ["nativeServer"] = executable, ["fixture"] = root }.ToJsonString());
    Console.WriteLine("ALL PASSED: " + checks + " mobile integration checks. " + root);
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
finally { await Stop(); await File.WriteAllTextAsync(Path.Combine(root, "server.log"), serverLog.Replace(password, "[redacted]")); }

static (int Width,int Height) JpegDimensions(byte[] bytes)
{
    for (var index=2;index+8<bytes.Length;)
    {
        if(bytes[index++]!=0xff)break;
        var marker=bytes[index++];if(marker is 0xd8 or 0xd9)continue;
        var length=(bytes[index]<<8)|bytes[index+1];if(length<2||index+length>bytes.Length)break;
        if(marker is 0xc0 or 0xc1 or 0xc2)return((bytes[index+5]<<8)|bytes[index+6],(bytes[index+3]<<8)|bytes[index+4]);
        index+=length;
    }
    throw new InvalidDataException("Thumbnail is not a complete JPEG image.");
}

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
