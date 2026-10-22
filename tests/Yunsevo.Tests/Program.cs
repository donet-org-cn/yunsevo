using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yunsevo.Client;
using Yunsevo.Contracts;
using static Yunsevo.Client.CloudNative;

try
{
    var workspace = Path.GetFullPath(args.FirstOrDefault(x => x.StartsWith("--workspace="))?[12..] ?? Environment.CurrentDirectory);
    var testRoot = Path.Combine(workspace, ".test-data", DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(testRoot);
    int checks = 0;
    void Assert(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
    void BadPath(string path)
    {
        try { CloudPath.Normalize(path); throw new Exception("Accepted unsafe path: " + path); }
        catch (ArgumentException) { checks++; }
    }

    Assert(Marshal.SizeOf<Registration>() == 72, "CF_SYNC_REGISTRATION ABI");
    Assert(Marshal.SizeOf<Policies>() == 24, "CF_SYNC_POLICIES ABI");
    Assert(Marshal.SizeOf<Metadata>() == 48, "CF_FS_METADATA ABI");
    Assert(Marshal.SizeOf<Placeholder>() == 88, "CF_PLACEHOLDER_CREATE_INFO ABI");
    Assert(Marshal.SizeOf<CallbackInfo>() == 152, "CF_CALLBACK_INFO ABI");
    Assert(Marshal.OffsetOf<CallbackInfo>(nameof(CallbackInfo.RequestKey)).ToInt32() == 144, "CF request key offset");
    Assert(Marshal.SizeOf<OperationInfo>() == 48, "CF_OPERATION_INFO ABI");
    Assert(Marshal.SizeOf<TransferParameters>() == 40, "CF_OPERATION_PARAMETERS TransferData size");
    Assert(Marshal.SizeOf<SyncRootInfoHeader>() == 1056 && Marshal.OffsetOf<SyncRootInfoHeader>(nameof(SyncRootInfoHeader.ProviderName)).ToInt32() == 28,
        "CF_SYNC_ROOT_STANDARD_INFO fixed header ABI");
    foreach (var path in new[] { "../escape", "/root", "a//b", "a\\b", "CON.txt", "a/NUL", "LPT9.log", "COM¹.txt", "a.", "a ", "a:b", "a\0b" }) BadPath(path);
    Assert(CloudPath.Normalize("中文目录/发票.txt") == "中文目录/发票.txt", "Unicode portable paths");
    var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    Assert(Secret.Unprotect(Secret.Protect(key)) == key, "DPAPI secret roundtrip");
    var binding = new string('A', 64);
    Assert(CloudApi.SameRootIdentity("PreviousProduct:v1:" + binding, "Yunsevo:v1:" + binding), "Brand rename preserves an unchanged server, user and folder binding");
    Assert(!CloudApi.SameRootIdentity("PreviousProduct:v1:" + binding, "Yunsevo:v1:" + new string('B', 64)) &&
        !CloudApi.SameRootIdentity("PreviousProduct:v1:short", "Yunsevo:v1:short"), "Binding migration rejects changed accounts, folders and malformed identities");

    if (args.Contains("--cleanup-tool"))
    {
        var root = Path.Combine(testRoot, "cleanup-probe");
        var config = new ClientConfig("http://127.0.0.1:9", Secret.Protect(key), root);
        using var api = new CloudApi(config);
        var placeholder = Path.Combine(root, "online-only.bin");
        var local = Path.Combine(root, "local.txt");
        string? cleanupShellId = null;
        try
        {
            using var provider = new CloudProvider(root, api, _ => { });
            await ShellIntegration.Register(root);
            cleanupShellId = Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(
                await Windows.Storage.StorageFolder.GetFolderFromPathAsync(root)).Id;
            provider.Create(new(Guid.NewGuid(), "online-only.bin", false, 1024, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N")));
            await File.WriteAllTextAsync(local, "Local file must survive Explorer entry cleanup.");
            Assert(CloudProvider.State(placeholder).Partial, "Cleanup fixture contains an online-only placeholder");
            var originalBytes = await File.ReadAllBytesAsync(local);
            var id = Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(
                await Windows.Storage.StorageFolder.GetFolderFromPathAsync(root)).Id;
            var command = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(workspace, "tools/explorer-cleanup/cleanup-explorer.ps1"),
                "-Apply", "-RemovePath", root, "-BackupDirectory", Path.Combine(testRoot, "cleanup-backup") }) command.ArgumentList.Add(value);
            using var process = Process.Start(command)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert(process.ExitCode == 0, "Registry-only cleanup succeeds: " + await output + await error);
            Assert(File.Exists(placeholder) && new FileInfo(placeholder).Length == 1024 && CloudProvider.State(placeholder).Partial,
                "Cleanup preserves online-only placeholder, length and hydration state");
            Assert((await File.ReadAllBytesAsync(local)).SequenceEqual(originalBytes), "Cleanup preserves local file bytes");
            Assert(QueryRegistration(root) is { IsRoot: true }, "Sidebar cleanup preserves the filesystem cloud registration");
            using var registered = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + id);
            Assert(registered == null, "Sidebar registry entry is removed");
            Assert(Directory.EnumerateFiles(Path.Combine(testRoot, "cleanup-backup"), "manifest.json", SearchOption.AllDirectories).Any(), "Cleanup exports recovery metadata before removal");
            Console.WriteLine($"ALL PASSED: {checks} checks. Artifacts: {testRoot}");
        }
        finally
        {
            // This is a disposable fixture; normal file-cloud teardown is appropriate here only.
            SearchIndex.RemoveOwned(root);
            CloudProvider.Unregister(root);
            if (cleanupShellId != null)
            {
                using var remaining = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + cleanupShellId);
                if (remaining != null) Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(cleanupShellId);
            }
        }
        return 0;
    }

    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    var url = $"http://127.0.0.1:{port}";
    var serverExe = args.FirstOrDefault(x => x.StartsWith("--server-exe="))?[13..];
    var start = new ProcessStartInfo(serverExe ?? "dotnet")
    { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
    if (serverExe == null) start.ArgumentList.Add(Path.Combine(workspace, "src/Yunsevo.Server/bin/Release/net10.0/Yunsevo.Server.dll"));
    start.Environment["ASPNETCORE_URLS"] = url;
    start.Environment["Yunsevo__UnattendedSetup"] = "true"; start.Environment["Yunsevo__ApiKey"] = key;
    var keyFile = Path.Combine(testRoot, "api-key.txt");
    start.Environment["Yunsevo__ApiKeyFile"] = keyFile;
    start.Environment["Yunsevo__DataPath"] = Path.Combine(testRoot, "server");
    start.Environment["Yunsevo__MaxUploadBytes"] = (8 * 1024 * 1024).ToString();
    start.Environment["Logging__LogLevel__Default"] = "Warning";
    var logs = new StringBuilder();
    Process? serverProcess = null;
    void StartServer()
    {
        serverProcess = Process.Start(start)!;
        serverProcess.OutputDataReceived += (_, e) => { lock (logs) logs.AppendLine(e.Data); };
        serverProcess.ErrorDataReceived += (_, e) => { lock (logs) logs.AppendLine(e.Data); };
        serverProcess.BeginOutputReadLine(); serverProcess.BeginErrorReadLine();
    }
    void StopServer() { if (serverProcess is { HasExited: false }) { serverProcess.Kill(true); serverProcess.WaitForExit(); } serverProcess?.Dispose(); }
    using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.Authorization = new("Bearer", key);
    async Task Ready()
    {
        for (int i = 0; i < 100; i++)
        {
            if (serverProcess!.HasExited) throw new Exception("Server exited: " + logs);
            try { if ((await http.GetAsync("/health")).IsSuccessStatusCode) return; } catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Server startup: " + logs);
    }
    async Task<Catalog> List() => (await http.GetFromJsonAsync("/api/entries", ApiJson.Default.Catalog))!;
    async Task<CloudEntry> Put(string path, byte[] data, string? version = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString(path)) { Content = new ByteArrayContent(data) };
        if (version == null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); else request.Headers.IfMatch.Add(new('"' + version + '"'));
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new Exception("Upload: " + await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!;
    }
    async Task Delete(CloudEntry entry, long revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/entries/" + entry.Id);
        request.Headers.IfMatch.Add(new('"' + entry.Version + '"'));
        request.Headers.Add("X-Catalog-Revision", revision.ToString());
        using var response = await http.SendAsync(request); response.EnsureSuccessStatusCode();
    }
    string? cloudRoot = null;
    string? previousShellId = null;
    SyncEngine? engine = null;
    try
    {
        StartServer(); await Ready();
        Assert(File.ReadAllText(keyFile).Trim() == key, "Configured API key saved to program key file");
        using (var anonymous = new HttpClient { BaseAddress = new Uri(url) })
        {
            using var page = await anonymous.GetAsync("/");
            Assert(page.IsSuccessStatusCode && (await page.Content.ReadAsStringAsync()).Contains("连接你的云盘"), "Web management login is publicly reachable");
            Assert(page.Content.Headers.ContentType?.MediaType == "text/html" && page.Headers.Contains("Content-Security-Policy"), "Embedded page has HTML content type and security policy");
            foreach (var asset in new[] { "/assets/app.css", "/assets/app.js" })
            {
                using var resource = await anonymous.GetAsync(asset);
                Assert(resource.IsSuccessStatusCode && (await resource.Content.ReadAsStringAsync()).Length > 1000, "Embedded web resource: " + asset);
            }
            using var deniedKey = await anonymous.GetAsync("/api-key.txt");
            Assert(deniedKey.StatusCode == HttpStatusCode.Unauthorized, "Key file is never publicly served");
        }
        using (var deniedKey = await http.GetAsync("/api-key.txt"))
            Assert(deniedKey.StatusCode == HttpStatusCode.NotFound, "Authenticated requests cannot download the key file");
        StopServer();
        var generatedKeyFile = Path.Combine(testRoot, "generated", "api-key.txt");
        start.Environment.Remove("Yunsevo__ApiKey");
        start.Environment["Yunsevo__ApiKeyFile"] = generatedKeyFile;
        StartServer(); await Ready();
        var generatedKey = File.ReadAllText(generatedKeyFile).Trim();
        Assert(generatedKey.Length == 64 && generatedKey.All(Uri.IsHexDigit) && generatedKey != key, "First startup generates a random 256-bit API key without configuration");
        using (var generatedClient = new HttpClient { BaseAddress = new Uri(url) })
        {
            generatedClient.DefaultRequestHeaders.Authorization = new("Bearer", generatedKey);
            using var catalog = await generatedClient.GetAsync("/api/entries");
            Assert(catalog.IsSuccessStatusCode, "Generated key authenticates file API");
            StopServer(); StartServer(); await Ready();
            Assert(File.ReadAllText(generatedKeyFile).Trim() == generatedKey, "Restart reuses existing key file without rotating it");
            using var afterRestart = await generatedClient.GetAsync("/api/entries");
            Assert(afterRestart.IsSuccessStatusCode, "Persisted key authenticates after restart");
        }
        Assert(!logs.ToString().Contains(key) && !logs.ToString().Contains(generatedKey), "Startup logs do not disclose API keys");
        StopServer();
        start.Environment["Yunsevo__ApiKey"] = key;
        start.Environment["Yunsevo__ApiKeyFile"] = keyFile;
        StartServer(); await Ready();
        using (var anonymous = new HttpClient { BaseAddress = new Uri(url) })
        using (var denied = await anonymous.GetAsync("/api/entries")) Assert(denied.StatusCode == HttpStatusCode.Unauthorized, "Unauthenticated API rejected");
        using (var bad = await http.PostAsJsonAsync("/api/folders", new CreateFolder("../escape"), ApiJson.Default.CreateFolder))
            Assert(bad.StatusCode == HttpStatusCode.BadRequest, "Unsafe API path rejected");
        using (var folder = await http.PostAsJsonAsync("/api/folders", new CreateFolder("资料"), ApiJson.Default.CreateFolder)) folder.EnsureSuccessStatusCode();
        var bytes = new byte[2 * 1024 * 1024 + 123]; RandomNumberGenerator.Fill(bytes);
        var file = await Put("资料/中文.bin", bytes);
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/content/" + file.Id + "?version=" + file.Version))
        {
            request.Headers.Range = new(4096, 8191);
            using var response = await http.SendAsync(request);
            Assert(response.StatusCode == HttpStatusCode.PartialContent, "HTTP Range 206");
            Assert((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes.Skip(4096).Take(4096)), "HTTP range bytes match");
        }
        using (var stale = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString(file.Path)) { Content = new ByteArrayContent([1, 2]) })
        {
            stale.Headers.IfMatch.Add(new("\"stale\""));
            using var response = await http.SendAsync(stale);
            Assert(response.StatusCode == HttpStatusCode.PreconditionFailed, "Stale upload cannot overwrite");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=too-large.bin") { Content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]) })
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            request.Headers.ExpectContinue = true;
            using var response = await http.SendAsync(request);
            Assert(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Upload limit enforced");
            Assert(response.Headers.ConnectionClose == true, "Oversized request cannot leave an unread body on a reused HTTP/1 connection");
        }
        var trashFile = await Put("trash.txt", Encoding.UTF8.GetBytes("restore me"));
        var snapshot = await List();
        var folderEntry = snapshot.Entries.Single(x => x.IsDirectory && x.Path == "资料");
        using (var staleDelete = new HttpRequestMessage(HttpMethod.Delete, "/api/entries/" + folderEntry.Id))
        {
            staleDelete.Headers.IfMatch.Add(new('"' + folderEntry.Version + '"'));
            staleDelete.Headers.Add("X-Catalog-Revision", (snapshot.Revision - 1).ToString());
            using var response = await http.SendAsync(staleDelete);
            Assert(response.StatusCode == HttpStatusCode.PreconditionFailed, "Directory delete guards subtree revision");
        }
        await Delete(trashFile, snapshot.Revision);
        Assert(!(await List()).Entries.Any(x => x.Id == trashFile.Id), "Soft delete removes active entry");
        using (var restored = await http.PostAsync($"/api/trash/{trashFile.Id}/restore", null)) restored.EnsureSuccessStatusCode();
        Assert((await List()).Entries.Any(x => x.Id == trashFile.Id), "Recycle bin restore");
        StopServer(); StartServer(); await Ready();
        Assert(File.ReadAllText(keyFile).Trim() == key, "Explicit API key remains stable across restarts");
        Assert((await List()).Entries.Any(x => x.Id == file.Id), "Catalog persists across server restart");
        Assert((await http.GetByteArrayAsync("/api/content/" + file.Id)).SequenceEqual(bytes), "Blob persists across server restart");
        async Task<CloudEntry> Folder(string path)
        {
            using var response = await http.PostAsJsonAsync("/api/folders", new CreateFolder(path), ApiJson.Default.CreateFolder);
            response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!;
        }
        var firstEpoch = await Folder("epoch");
        await Put("epoch/old.txt", [1]);
        await Delete(firstEpoch, (await List()).Revision);
        var secondEpoch = await Folder("epoch");
        await Put("epoch/new.txt", [2]);
        await Delete(secondEpoch, (await List()).Revision);
        using (var restored = await http.PostAsync($"/api/trash/{secondEpoch.Id}/restore", null)) restored.EnsureSuccessStatusCode();
        Assert((await List()).Entries.Any(x => x.Path == "epoch/new.txt") && !(await List()).Entries.Any(x => x.Path == "epoch/old.txt"),
            "Recycle bin restores only the selected deletion batch");
        await Delete(secondEpoch, (await List()).Revision);
        await Put("empty.bin", []);

        var namedConfig = new ClientConfig(url, Secret.Protect(key), Path.Combine(testRoot, "named-a"), RemoteDirectory: "工作资料");
        using (var named = new CloudApi(namedConfig))
        using (var second = new CloudApi(namedConfig with { SyncRoot = Path.Combine(testRoot, "named-b") }))
        using (var isolated = new CloudApi(namedConfig with { RemoteDirectory = "其他资料" }))
        {
            Assert((await named.List(default)).Entries.Count == 0, "Named cloud folder starts empty and excludes files outside its scope");
            var namedRoot = (await List()).Entries.Single(x => x.Path == "工作资料");
            await named.Folder("子目录", default);
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("shared across devices"));
            var shared = await named.Upload("子目录/共享.txt", content, null, default);
            Assert(shared.Path == "子目录/共享.txt" && (await List()).Entries.Any(x => x.Id == shared.Id && x.Path == "工作资料/子目录/共享.txt"),
                "Named upload maps local relative paths to web-visible cloud directory");
            var secondCatalog = await second.List(default);
            Assert(secondCatalog.Entries.Any(x => x.Id == shared.Id && x.Path == shared.Path) && second.RemoteDirectoryId == namedRoot.Id,
                "Different client and local directory reconnect to the same stable cloud folder and file IDs");
            Assert((await isolated.List(default)).Entries.Count == 0, "Different cloud directory sees a separate file list");
            var renamed = await second.Move(shared, "子目录/改名.txt", default);
            Assert(renamed.Path == "子目录/改名.txt" && (await List()).Entries.Any(x => x.Id == shared.Id && x.Path == "工作资料/子目录/改名.txt"),
                "Scoped rename stays inside selected cloud folder");
            using (var download = await second.Download(renamed.Id, renamed.Version, 0, content.Length, default))
                Assert((await download.Content.ReadAsByteArrayAsync()).SequenceEqual(content.ToArray()), "Scoped client downloads original shared bytes by stable ID");
            await second.Delete(renamed, (await second.List(default)).Revision, default);
            Assert(!(await List()).Entries.Any(x => x.Id == shared.Id) && (await List()).Entries.Any(x => x.Id == file.Id),
                "Scoped delete removes selected file and preserves unrelated root files");
            await Delete(namedRoot, (await List()).Revision);
            bool refusedMissingRoot = false;
            try { await named.List(default); } catch (InvalidOperationException) { refusedMissingRoot = true; }
            Assert(refusedMissingRoot && !(await List()).Entries.Any(x => x.Path == "工作资料"), "Deleted bound cloud directory is not silently recreated");
            using (var restored = await http.PostAsync($"/api/trash/{namedRoot.Id}/restore", null)) restored.EnsureSuccessStatusCode();
            Assert((await named.List(default)).Entries.Any(x => x.Path == "子目录") && named.RemoteDirectoryId == namedRoot.Id,
                "Restoring cloud directory reconnects its original identity");
            var emptySubfolder = (await named.List(default)).Entries.Single();
            await named.Delete(emptySubfolder, (await named.List(default)).Revision, default);
        }
        using (var raceA = new CloudApi(namedConfig with { RemoteDirectory = "并发资料/嵌套" }))
        using (var raceB = new CloudApi(namedConfig with { RemoteDirectory = "并发资料/嵌套" }))
        {
            await Task.WhenAll(raceA.List(default), raceB.List(default));
            Assert(raceA.RemoteDirectoryId == raceB.RemoteDirectoryId && (await List()).Entries.Count(x => x.Path == "并发资料/嵌套") == 1,
                "Concurrent new clients reuse one nested cloud folder");
        }
        var oldConfigJson = JsonSerializer.Serialize(namedConfig with { RemoteDirectory = "" }, ClientJson.Default.ClientConfig);
        var restoredConfig = JsonSerializer.Deserialize(oldConfigJson.Replace(",\"RemoteDirectory\":\"\"", ""), ClientJson.Default.ClientConfig)!;
        Assert(restoredConfig.RemoteDirectory == "", "Legacy client configuration keeps root scope by default");
        var legacyConfigPath = Path.Combine(testRoot, "legacy-client.json");
        var legacyStatePath = legacyConfigPath + ".state.json";
        var legacyStateJson = JsonSerializer.Serialize(new SyncState(url, namedConfig.SyncRoot, []), ClientJson.Default.SyncState)
            .Replace(",\"RemoteDirectory\":\"\"", "").Replace(",\"RemoteDirectoryId\":null", "");
        File.WriteAllText(legacyStatePath, legacyStateJson);
        Assert((namedConfig with { RemoteDirectory = "" }).GetStatePath(legacyConfigPath) == legacyStatePath,
            "Existing installations reuse legacy state without losing tracked files");
        Assert(namedConfig.GetStatePath(Path.Combine(testRoot, "profiles.json")) !=
            (namedConfig with { SyncRoot = Path.Combine(testRoot, "named-b") }).GetStatePath(Path.Combine(testRoot, "profiles.json")),
            "Changing local directory preserves separate sync state instead of reusing its deletion baseline");

        if (!args.Contains("--skip-cloud"))
        {
            SyncEngine? firstNamed = null, secondNamed = null;
            var namedStateA = Path.Combine(testRoot, "named-a.state.json");
            var namedStateB = Path.Combine(testRoot, "named-b.state.json");
            try
            {
                firstNamed = new(namedConfig, namedStateA, Console.WriteLine);
                await firstNamed.SyncOnce();
                Assert(JsonSerializer.Deserialize(File.ReadAllText(namedStateA), ClientJson.Default.SyncState)?.RemoteDirectoryId.HasValue == true,
                    "Cloud folder binding is persisted even when it contains no files");
                await File.WriteAllTextAsync(Path.Combine(namedConfig.SyncRoot, "换客户端.txt"), "same remote folder, different local roots");
                await firstNamed.SyncOnce();
                var originalShared = (await List()).Entries.Single(x => x.Path == "工作资料/换客户端.txt");
                var configB = namedConfig with { SyncRoot = Path.Combine(testRoot, "named-b") };
                secondNamed = new(configB, namedStateB, Console.WriteLine);
                await secondNamed.SyncOnce();
                var secondLocal = Path.Combine(configB.SyncRoot, "换客户端.txt");
                Assert(File.Exists(secondLocal) && CloudProvider.State(secondLocal) is { Placeholder: true, Partial: true },
                    "Second client receives same named cloud files as on-demand placeholders");
                Assert(await File.ReadAllTextAsync(secondLocal) == "same remote folder, different local roots", "Second client's placeholder downloads shared content");
                Assert((await List()).Entries.Count(x => x.Path == "工作资料/换客户端.txt") == 1 &&
                    (await List()).Entries.Any(x => x.Id == originalShared.Id), "Replacing a client does not create a new cloud folder or duplicate files");
                await Delete((await List()).Entries.Single(x => x.Path == "工作资料"), (await List()).Revision);
                bool stopped = false;
                try { await secondNamed.SyncOnce(); } catch (InvalidOperationException) { stopped = true; }
                Assert(stopped && File.Exists(secondLocal), "Removed named root stops sync without deleting existing local files");
                var removedNamedRoot = (await http.GetFromJsonAsync("/api/trash", ApiJson.Default.Catalog))!.Entries.Single(x => x.Path == "工作资料");
                using (var restored = await http.PostAsync($"/api/trash/{removedNamedRoot.Id}/restore", null)) restored.EnsureSuccessStatusCode();
                secondNamed.Dispose(); secondNamed = new(configB, namedStateB, Console.WriteLine);
                await secondNamed.SyncOnce();
                Assert(File.Exists(secondLocal) && (await List()).Entries.Any(x => x.Id == originalShared.Id), "Named directory binding survives client restart and cloud restore");
            }
            finally
            {
                firstNamed?.Dispose(); secondNamed?.Dispose();
                foreach (var directory in new[] { namedConfig.SyncRoot, Path.Combine(testRoot, "named-b") })
                    if (Directory.Exists(directory)) CloudProvider.Unregister(directory);
            }
        }

        if (!args.Contains("--skip-cloud"))
        {
            var switchingRoot = Path.Combine(testRoot, "switching-root");
            var switchConfig = namedConfig with { SyncRoot = switchingRoot, RemoteDirectory = "切换前" };
            SyncEngine? switchingEngine = null;
            try
            {
                switchingEngine = new(switchConfig, Path.Combine(testRoot, "switch-before.state.json"), Console.WriteLine);
                await switchingEngine.SyncOnce();
                switchingEngine.Dispose(); switchingEngine = null;
                var beforeRegistration = QueryRegistration(switchingRoot);
                Assert(beforeRegistration is { IsRoot: true, ProviderName: "Yunsevo" } && beforeRegistration.Identity.StartsWith("Yunsevo:v1:"),
                    "Registration query finds this exact root and its persisted cloud binding");
                var newScopeConfig = switchConfig with { RemoteDirectory = "切换后" };
                var newScopeState = Path.Combine(testRoot, "switch-after.state.json");
                switchingEngine = new(newScopeConfig, newScopeState, Console.WriteLine);
                await switchingEngine.SyncOnce();
                var afterRegistration = QueryRegistration(switchingRoot);
                Assert(afterRegistration is { IsRoot: true } && afterRegistration.Identity != beforeRegistration!.Identity,
                    "Empty previously registered directory switches scope without ERROR_INVALID_PARAMETER");
                switchingEngine.Dispose(); switchingEngine = null;
                CloudProvider.Unregister(switchingRoot);
                switchingEngine = new(newScopeConfig, newScopeState, Console.WriteLine);
                await switchingEngine.SyncOnce();
                Assert(QueryRegistration(switchingRoot) is { ProviderName: "Yunsevo" }, "Existing sync state reconnects after Windows registration was removed");
                await File.WriteAllTextAsync(Path.Combine(switchingRoot, "保留.txt"), "do not remove during scope switch");
                await switchingEngine.SyncOnce();
                switchingEngine.Dispose(); switchingEngine = null;
                var preservedIdentity = QueryRegistration(switchingRoot)!.Identity;
                var refused = false;
                try { SyncEngine.ValidateLocalDirectory(switchConfig, Path.Combine(testRoot, "new-profile.state.json")); }
                catch (InvalidOperationException ex) { refused = ex.Message.Contains("新的空本地文件夹"); }
                Assert(refused && await File.ReadAllTextAsync(Path.Combine(switchingRoot, "保留.txt")) == "do not remove during scope switch" &&
                    QueryRegistration(switchingRoot)!.Identity == preservedIdentity, "Failed scope preflight preserves local content and previous registration");
                switchingEngine = new(newScopeConfig, newScopeState, Console.WriteLine);
                await switchingEngine.SyncOnce();
                Assert(File.Exists(Path.Combine(switchingRoot, "保留.txt")), "Previous working configuration resumes after a rejected switch");
            }
            finally
            {
                switchingEngine?.Dispose();
                if (Directory.Exists(switchingRoot)) CloudProvider.Unregister(switchingRoot);
            }

            var foreignRoot = Path.Combine(testRoot, "foreign-root");
            Directory.CreateDirectory(foreignRoot);
            unsafe void RegisterTestRoot(string rootPath, string name, string identity = "", uint flags = 4)
            {
                var identityBytes = Encoding.UTF8.GetBytes(identity);
                fixed (char* providerName = name, providerVersion = "1.0")
                fixed (byte* rootIdentity = identityBytes)
                {
                    var registration = new Registration { Size = (uint)Marshal.SizeOf<Registration>(), Name = providerName, Version = providerVersion, ProviderId = Guid.NewGuid(), RootIdentity = (nint)rootIdentity, RootIdentityLength = (uint)identityBytes.Length };
                    var policies = new Policies { Size = (uint)Marshal.SizeOf<Policies>(), Hydration = 2, Population = 3, InSync = 0x100 };
                    Check(CfRegisterSyncRoot(rootPath, registration, policies, flags));
                }
            }
            var legacyRegisteredRoot = Path.Combine(testRoot, "legacy-registered-empty");
            Directory.CreateDirectory(legacyRegisteredRoot);
            RegisterTestRoot(legacyRegisteredRoot, "Yunsevo");
            try
            {
                Assert(QueryRegistration(legacyRegisteredRoot) is { ProviderName: "Yunsevo", Identity: "" }, "Legacy Yunsevo registration is recognized without a root identity");
                using var legacyRecovery = new SyncEngine(namedConfig with { SyncRoot = legacyRegisteredRoot, RemoteDirectory = "myws" },
                    Path.Combine(testRoot, "legacy-recovery.state.json"), Console.WriteLine);
                await legacyRecovery.SyncOnce();
                Assert(QueryRegistration(legacyRegisteredRoot)?.Identity.StartsWith("Yunsevo:v1:") == true,
                    "Empty legacy root without a profile reconnects and acquires the selected binding");
            }
            finally { CloudProvider.Unregister(legacyRegisteredRoot); }
            var brandedRoot = Path.Combine(testRoot, "previous-brand");
            var brandedState = Path.Combine(testRoot, "previous-brand.state.json");
            var brandedConfig = namedConfig with { SyncRoot = brandedRoot, RemoteDirectory = "brand-migration" };
            await Folder("brand-migration");
            await Put("brand-migration/existing.txt", Encoding.UTF8.GetBytes("preserved through rename"));
            try
            {
                using (var original = new SyncEngine(brandedConfig, brandedState, Console.WriteLine)) await original.SyncOnce();
                var originalIdentity = QueryRegistration(brandedRoot)!.Identity;
                RegisterTestRoot(brandedRoot, "PreviousProduct", "PreviousProduct" + originalIdentity[originalIdentity.IndexOf(':')..], 5);
                using (var upgraded = new SyncEngine(brandedConfig, brandedState, Console.WriteLine))
                {
                    await upgraded.SyncOnce();
                    Assert(QueryRegistration(brandedRoot) is { ProviderName: "Yunsevo" } migrated && migrated.Identity == originalIdentity &&
                        await File.ReadAllTextAsync(Path.Combine(brandedRoot, "existing.txt")) == "preserved through rename",
                        "Brand upgrade reconnects existing placeholders and state without changing their cloud binding or bytes");
                }
            }
            finally { CloudProvider.Unregister(brandedRoot); }
            RegisterTestRoot(foreignRoot, "AnotherProvider");
            try
            {
                bool refusedForeign = false;
                try { using var rejected = new SyncEngine(namedConfig with { SyncRoot = foreignRoot }, Path.Combine(testRoot, "foreign.state.json"), Console.WriteLine); }
                catch (InvalidOperationException ex) { refusedForeign = ex.Message.Contains("其他云盘"); }
                Assert(refusedForeign && QueryRegistration(foreignRoot)?.ProviderName == "AnotherProvider", "Registration recovery never overwrites a different cloud provider");
                var nestedRoot = Path.Combine(foreignRoot, "nested"); Directory.CreateDirectory(nestedRoot);
                bool refusedNested = false;
                try { using var rejected = new SyncEngine(namedConfig with { SyncRoot = nestedRoot }, Path.Combine(testRoot, "nested.state.json"), Console.WriteLine); }
                catch (InvalidOperationException) { refusedNested = true; }
                Assert(refusedNested && QueryRegistration(nestedRoot) is { IsRoot: false }, "Registration query rejects a child directory inside an existing sync root");
            }
            finally { CloudProvider.Unregister(foreignRoot); }
        }

        if (!args.Contains("--skip-cloud"))
        {
            cloudRoot = Path.Combine(testRoot, "sync");
            var config = new ClientConfig(url, Secret.Protect(key), cloudRoot);
            var state = Path.Combine(testRoot, "state.json");
            engine = new(config, state, Console.WriteLine);
            if (args.Contains("--shell"))
            {
                var previousId = "PreviousFixture!" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + "!" + Guid.NewGuid().ToString("N");
                previousShellId = previousId;
                var previousFolder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(cloudRoot);
                Windows.Storage.Provider.StorageProviderSyncRootManager.Register(new()
                {
                    Id = previousId, Path = previousFolder,
                    DisplayNameResource = "Previous fixture", IconResource = @"%SystemRoot%\system32\imageres.dll,-1040",
                    HydrationPolicy = Windows.Storage.Provider.StorageProviderHydrationPolicy.Full,
                    PopulationPolicy = Windows.Storage.Provider.StorageProviderPopulationPolicy.AlwaysFull,
                    InSyncPolicy = Windows.Storage.Provider.StorageProviderInSyncPolicy.FileLastWriteTime,
                    HardlinkPolicy = Windows.Storage.Provider.StorageProviderHardlinkPolicy.None,
                    AllowPinning = true,
                    Version = "1.0.0", ProviderId = new("64cb5459-3b19-429c-8d1c-700c5f2f7298")
                });
                await ShellIntegration.Register(cloudRoot, Console.WriteLine);
                var retainedRoot = Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(previousFolder);
                Assert(retainedRoot.Id == previousId && retainedRoot.DisplayNameResource == ProductBrand.Product, "Brand rename preserves the existing sync-root identity and updates its displayed name");
                Assert(SearchIndex.IsIncluded(cloudRoot), "Sync root outside user profile is included in Windows Search");
            }
            await engine.SyncOnce();
            var localFile = Path.Combine(cloudRoot, "资料", "中文.bin");
            Assert(File.Exists(localFile), "Remote files appear as real Explorer placeholders");
            Assert(CloudProvider.State(localFile) is { Placeholder: true, Partial: true }, "Initial placeholder has no downloaded content");
            Assert((await File.ReadAllBytesAsync(Path.Combine(cloudRoot, "empty.bin"))).Length == 0, "Zero-byte placeholder opens");
            Assert((await File.ReadAllBytesAsync(localFile)).SequenceEqual(bytes), "Opening placeholder hydrates multi-chunk bytes");
            Assert(!CloudProvider.State(localFile).Partial, "Hydration marks file locally available");
            await engine.SetOffline(file.Path, false);
            Assert(CloudProvider.State(localFile).Partial, "Free space dehydrates file");
            await engine.SetOffline(file.Path, true);
            Assert(!CloudProvider.State(localFile).Partial, "Keep offline hydrates and pins file");

            if (args.Contains("--shell"))
            {
                async Task<uint> Availability(string path, uint expected)
                {
                    uint state = 0;
                    for (var attempt = 0; attempt < 50; attempt++)
                    {
                        var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                        var properties = await storageFile.Properties.RetrievePropertiesAsync(["System.StorageProviderState"]);
                        state = Convert.ToUInt32(properties["System.StorageProviderState"], System.Globalization.CultureInfo.InvariantCulture);
                        if (state == expected) return state;
                        await Task.Delay(100); // Search notifications update the Shell cache asynchronously.
                    }
                    Console.WriteLine($"Shell availability still {state}, expected {expected}");
                    var diagnosticFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    var diagnosticProperties = await diagnosticFile.Properties.RetrievePropertiesAsync(["System.SyncTransferStatus"]);
                    Console.WriteLine($"Transfer status: {diagnosticProperties["System.SyncTransferStatus"]}; attrs: 0x{(uint)File.GetAttributes(path):X}; last property result: 0x{ShellTransfer.LastResult:X8}");
                    return state;
                }
                var pinnedState = await Availability(localFile, 3);
                var progressPath = Path.Combine(cloudRoot, "shell-progress.txt");
                await File.WriteAllTextAsync(progressPath, "progress");
                var progressWritten = ShellTransfer.Set(progressPath, 4, 1024, bytes.Length);
                Assert(progressWritten, $"Explorer accepts volatile transfer progress (0x{ShellTransfer.LastResult:X8})");
                var transferringFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(progressPath);
                var transferringProperties = await transferringFile.Properties.RetrievePropertiesAsync(["System.SyncTransferStatus"]);
                var activeTransfer = Convert.ToUInt32(transferringProperties["System.SyncTransferStatus"], System.Globalization.CultureInfo.InvariantCulture);
                Assert((activeTransfer & 4) != 0, "Explorer reports active transfer status");
                _ = ShellTransfer.Set(progressPath, 0, bytes.Length, bytes.Length);
                File.Delete(progressPath);
                await engine.SetOffline(file.Path, false);
                var cloudState = await Availability(localFile, 1);
                Assert(pinnedState == 3 && cloudState == 1, "Explorer availability property distinguishes pinned and cloud-only files");
                await engine.SetOffline(file.Path, true);
            }

            var readOnlyPath = Path.Combine(cloudRoot, "read-only.img");
            var readOnlyBytes = Encoding.UTF8.GetBytes("read-only firmware content");
            await File.WriteAllBytesAsync(readOnlyPath, readOnlyBytes);
            File.SetAttributes(readOnlyPath, File.GetAttributes(readOnlyPath) | FileAttributes.ReadOnly);
            using (var pendingSource = OpenUploadSource(readOnlyPath))
                engine.Provider.PrepareUpload(readOnlyPath, pendingSource.SafeFileHandle.DangerousGetHandle(), false);
            Assert(CloudProvider.State(readOnlyPath) is { Placeholder: true, InSync: false }, "Local file exposes pending sync state before upload");
            if (args.Contains("--shell"))
            {
                uint transferStatus = 0;
                bool pendingStatusVisible = false;
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    var pendingFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(readOnlyPath);
                    var pendingProperties = await pendingFile.Properties.RetrievePropertiesAsync(["System.SyncTransferStatus", "System.StorageProviderUIStatus"]);
                    transferStatus = Convert.ToUInt32(pendingProperties["System.SyncTransferStatus"], System.Globalization.CultureInfo.InvariantCulture);
                    pendingStatusVisible = (transferStatus & 0x201) != 0 && pendingProperties["System.StorageProviderUIStatus"] is byte[] { Length: > 0 };
                    if (pendingStatusVisible) break;
                    await Task.Delay(100); // Shell/Search can return the pre-conversion cache immediately after CfConvertToPlaceholder.
                }
                // Windows exposes STS_INCOMPLETE from the Cloud Files state even when a read-only file's volatile store denies writes.
                Assert(pendingStatusVisible, $"Explorer reports pending sync state for read-only files (0x{transferStatus:X})");
            }
            await engine.SyncOnce();
            var readOnlyEntry = (await List()).Entries.Single(x => x.Path == "read-only.img");
            Assert((await http.GetByteArrayAsync("/api/content/" + readOnlyEntry.Id)).SequenceEqual(readOnlyBytes), "Read-only firmware uploads exact bytes");
            Assert((File.GetAttributes(readOnlyPath) & FileAttributes.ReadOnly) != 0, "Uploading preserves read-only attribute");
            Assert(CloudProvider.State(readOnlyPath).InSync, "Read-only file becomes a synced cloud placeholder");

            var busyPath = Path.Combine(cloudRoot, "0-busy.txt");
            await File.WriteAllTextAsync(busyPath, "busy content");
            await using (var busy = new FileStream(busyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await File.WriteAllTextAsync(Path.Combine(cloudRoot, "z-healthy.txt"), "healthy content");
                await Put("资料/remote-healthy.txt", Encoding.UTF8.GetBytes("remote healthy"));
                await engine.SyncOnce();
                var partialCatalog = await List();
                Assert(partialCatalog.Entries.Any(x => x.Path == "z-healthy.txt"), "Locked file does not block other uploads");
                Assert(!partialCatalog.Entries.Any(x => x.Path == "0-busy.txt"), "Locked file remains pending instead of being marked synced");
                Assert(File.Exists(Path.Combine(cloudRoot, "资料", "remote-healthy.txt")), "Locked file does not block remote placeholders");
            }
            await engine.SyncOnce();
            Assert((await List()).Entries.Any(x => x.Path == "0-busy.txt"), "Pending file retries after its lock is released");

            // An unsupported name must not make the next scan omit its accessible siblings and delete them remotely.
            var invalidName = @"\\?\" + Path.Combine(cloudRoot, "资料", "invalid.");
            await File.WriteAllTextAsync(invalidName, "unsupported Windows cloud name");
            try
            {
                await File.WriteAllTextAsync(Path.Combine(cloudRoot, "资料", "healthy-sibling.txt"), "healthy sibling");
                await engine.SyncOnce();
                var isolated = await List();
                Assert(isolated.Entries.Any(x => x.Id == file.Id) && File.Exists(localFile), "Scan errors never delete accessible tracked siblings");
                Assert(isolated.Entries.Any(x => x.Path == "资料/healthy-sibling.txt"), "Unsupported sibling name does not block valid uploads");
            }
            finally { File.Delete(invalidName); }
            await engine.SyncOnce();

        var renameProtectedFolder = await Folder("rename-protected");
        var renameProtectedFile = await Put("rename-protected/child.txt", Encoding.UTF8.GetBytes("keep during failed scan"));
        await engine.SyncOnce();
        Directory.Move(Path.Combine(cloudRoot, "rename-protected"), Path.Combine(cloudRoot, "rename-pending"));
        var invalidMovedChild = @"\\?\" + Path.Combine(cloudRoot, "rename-pending", "invalid.");
        await File.WriteAllTextAsync(invalidMovedChild, "unsupported child");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cloudRoot, "healthy-during-rename.txt"), "healthy");
            await engine.SyncOnce();
            var blockedRename = await List();
            Assert(blockedRename.Entries.Any(x => x.Id == renameProtectedFolder.Id) && blockedRename.Entries.Any(x => x.Id == renameProtectedFile.Id),
                "Pending directory rename with scan errors never deletes its cloud subtree");
            Assert(blockedRename.Entries.Any(x => x.Path == "healthy-during-rename.txt"), "Pending directory rename does not block unrelated uploads");
        }
        finally { File.Delete(invalidMovedChild); }
        await engine.SyncOnce();
        Assert((await List()).Entries.Any(x => x.Id == renameProtectedFile.Id && x.Path == "rename-pending/child.txt"), "Directory rename resumes after scan error is resolved");

            var localText = Path.Combine(cloudRoot, "new.txt");
            await File.WriteAllTextAsync(localText, "local upload");
            await engine.SyncOnce();
            var localEntry = (await List()).Entries.Single(x => x.Path == "new.txt");
            Assert(Encoding.UTF8.GetString(await http.GetByteArrayAsync("/api/content/" + localEntry.Id)) == "local upload", "New local file uploads");
            Assert(CloudProvider.State(localText).InSync, "Uploaded file becomes in-sync placeholder");
            await File.WriteAllTextAsync(localText, "local edit");
            await engine.SyncOnce();
            Assert(Encoding.UTF8.GetString(await http.GetByteArrayAsync("/api/content/" + localEntry.Id)) == "local edit", "Local edits upload");

            Directory.CreateDirectory(Path.Combine(cloudRoot, "本地目录"));
            await File.WriteAllTextAsync(Path.Combine(cloudRoot, "本地目录", "child.txt"), "child");
            await engine.SyncOnce();
            Assert((await List()).Entries.Any(x => x.Path == "本地目录/child.txt"), "Nested local folder uploads");
            Directory.Move(Path.Combine(cloudRoot, "本地目录"), Path.Combine(cloudRoot, "已改名"));
            await engine.SyncOnce();
            Assert((await List()).Entries.Any(x => x.Path == "已改名/child.txt"), "Explorer directory rename preserves subtree");

            using (var request = new HttpRequestMessage(HttpMethod.Patch, "/api/entries/" + localEntry.Id)
            { Content = JsonContent.Create(new MoveEntry("remote-renamed.txt", (await List()).Entries.Single(x => x.Id == localEntry.Id).Version), ApiJson.Default.MoveEntry) })
            using (var response = await http.SendAsync(request)) response.EnsureSuccessStatusCode();
            await engine.SyncOnce();
            Assert(File.Exists(Path.Combine(cloudRoot, "remote-renamed.txt")) && !File.Exists(localText), "Remote rename updates local path");
            localText = Path.Combine(cloudRoot, "remote-renamed.txt");

            var updated = await Put(file.Path, Encoding.UTF8.GetBytes("cloud update"), file.Version);
            await engine.SyncOnce();
            Assert(!CloudProvider.State(localFile).Partial, "Remote update preserves keep-offline pin");
            Assert(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(localFile)) == "cloud update", "Updated placeholder hydrates current version");
            await engine.SetOffline(file.Path, false);
            await Put(file.Path, Encoding.UTF8.GetBytes("cloud update 2"), updated.Version);
            await engine.SyncOnce();
            Assert(CloudProvider.State(localFile).Partial, "Remote update invalidates unpinned cached bytes");
            Assert(await File.ReadAllTextAsync(localFile) == "cloud update 2", "Unpinned update downloads current bytes");
            await File.WriteAllTextAsync(localText, "both-local");
            localEntry = (await List()).Entries.Single(x => x.Id == localEntry.Id);
            await Put(localEntry.Path, Encoding.UTF8.GetBytes("both-remote"), localEntry.Version);
            await engine.SyncOnce();
            Assert((await List()).Entries.Any(x => x.Path.Contains("冲突")), "Concurrent edits preserve conflict copy");
            Assert(await File.ReadAllTextAsync(localText) == "both-remote", "Concurrent edit original refreshes from cloud");
            Assert(await File.ReadAllTextAsync(Directory.EnumerateFiles(cloudRoot, "*冲突*").Single()) == "both-local", "Conflict copy retains local bytes");

            var unsyncedFolder = await Folder("edited-delete");
            await Put("edited-delete/edited.txt", Encoding.UTF8.GetBytes("initial"));
            await engine.SyncOnce();
            var editedPath = Path.Combine(cloudRoot, "edited-delete", "edited.txt");
            await File.WriteAllTextAsync(editedPath, "retain-local-edit");
            await Delete(unsyncedFolder, (await List()).Revision);
            await engine.SyncOnce();
            var retained = (await List()).Entries.Single(x => x.Path == "edited-delete/edited.txt");
            Assert(Encoding.UTF8.GetString(await http.GetByteArrayAsync("/api/content/" + retained.Id)) == "retain-local-edit",
                "Remote folder deletion retains and reuploads unsynced local edits");

            // Simulate Explorer's pin changes without using the settings actions.
            using (var handle = new Oplock(localFile)) Check(CfSetPinState(handle.Win32, 1, 0, 0));
            await engine.SyncOnce();
            Assert(!CloudProvider.State(localFile).Partial, "Explorer pin request hydrates content");
            using (var handle = new Oplock(localFile)) Check(CfSetPinState(handle.Win32, 2, 0, 0));
            await engine.SyncOnce();
            Assert(CloudProvider.State(localFile).Partial, "Explorer unpin transition releases cached content");

            File.Delete(Path.Combine(cloudRoot, "trash.txt"));
            await engine.SyncOnce();
            Assert(!(await List()).Entries.Any(x => x.Id == trashFile.Id), "Explorer deletion moves cloud entry to recycle bin");
            var remoteDelete = (await List()).Entries.Single(x => x.Path == "已改名");
            await Delete(remoteDelete, (await List()).Revision);
            await engine.SyncOnce();
            Assert(!Directory.Exists(Path.Combine(cloudRoot, "已改名")), "Cloud folder deletion removes clean local subtree");
            engine.Dispose(); engine = new(config, state, Console.WriteLine);
            await engine.SyncOnce();
            Assert(File.Exists(localFile), "Client restart retains sync state and reconnects");
            if (args.Contains("--shell"))
            {
                await ShellIntegration.Register(cloudRoot);
                Assert(Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(
                    await Windows.Storage.StorageFolder.GetFolderFromPathAsync(cloudRoot)).DisplayNameResource == ProductBrand.Product, "Shell registration roundtrip");
                engine.Dispose(); engine = null;
                ShellIntegration.Unregister(cloudRoot);
                cloudRoot = null;
            }
        }

        if (!args.Contains("--skip-cloud"))
        {
            var confirmRoot = Path.Combine(testRoot, "confirm-deletion"); var confirmState = Path.Combine(testRoot, "confirm-deletion.state.json");
            using var loginResponse = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", key), ApiJson.Default.LoginRequest); loginResponse.EnsureSuccessStatusCode();
            var account = (await loginResponse.Content.ReadFromJsonAsync(ApiJson.Default.LoginResult))!;
            var confirmConfig = new ClientConfig(url, Secret.Protect(account.AccessToken), confirmRoot, RemoteDirectory: "删除确认", AccountId: account.User.Id.ToString("N"));
            await Folder("删除确认"); var retained = await Put("删除确认/local-only.txt", Encoding.UTF8.GetBytes("keep cloud")); var removed = await Put("删除确认/cloud-delete.txt", Encoding.UTF8.GetBytes("confirm cloud"));
            try
            {
                var onlineDeleted = await Put("删除确认/online.txt", Encoding.UTF8.GetBytes("online delete")); var onlinePrompts = 0;
                using (var first = new SyncEngine(confirmConfig, confirmState, Console.WriteLine, (request, ct) => { onlinePrompts++; return Task.FromResult(Yunsevo.Sync.DeletionChoice.Defer); }))
                {
                    await first.SyncOnce(); File.Delete(Path.Combine(confirmRoot, "online.txt")); await first.SyncOnce();
                    Assert(onlinePrompts == 0 && !(await List()).Entries.Any(x => x.Id == onlineDeleted.Id), "Windows on-demand online deletion propagates without a confirmation prompt");
                }
                File.Delete(Path.Combine(confirmRoot, "local-only.txt")); var count = 0; var startup = false;
                using (var second = new SyncEngine(confirmConfig, confirmState, Console.WriteLine, (request, ct) => { count++; startup = request.DetectedAtStartup; return Task.FromResult(Yunsevo.Sync.DeletionChoice.LocalOnly); }))
                { await second.SyncOnce(); await second.SyncOnce(); }
                Assert(count == 1 && startup && (await List()).Entries.Any(x => x.Id == retained.Id) && !File.Exists(Path.Combine(confirmRoot, "local-only.txt")), "Windows account login and offline local-only deletion preserve the cloud file without recreating its placeholder");
                File.Delete(Path.Combine(confirmRoot, "cloud-delete.txt"));
                using (var third = new SyncEngine(confirmConfig, confirmState, Console.WriteLine, (request, ct) => Task.FromResult(Yunsevo.Sync.DeletionChoice.DeleteCloud))) await third.SyncOnce();
                Assert(!File.Exists(Path.Combine(confirmRoot, "local-only.txt")) && !(await List()).Entries.Any(x => x.Id == removed.Id), "Windows deletion decisions persist separately from the confirmed cloud-delete operation");
            }
            finally { CloudProvider.Unregister(confirmRoot); }
        }
        var nativeClient = args.FirstOrDefault(x => x.StartsWith("--client-exe="))?[13..];
        if (nativeClient != null)
        {
            // Earlier checks intentionally run with an 8 MiB request limit. Restore
            // a larger test limit before exercising Native AOT chunked uploads.
            StopServer(); start.Environment["Yunsevo__MaxUploadBytes"] = (64 * 1024 * 1024).ToString(); StartServer(); await Ready();
            async Task<int> RunNative(params string[] arguments)
            {
                var nativeStart = new ProcessStartInfo(nativeClient)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                nativeStart.Environment["YUNSEVO_API_KEY"] = key;
                foreach (var argument in arguments) nativeStart.ArgumentList.Add(argument);
                using var native = Process.Start(nativeStart)!;
                var stdout = native.StandardOutput.ReadToEndAsync(); var stderr = native.StandardError.ReadToEndAsync();
                await native.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (native.ExitCode != 0) Console.Error.WriteLine(await stdout + await stderr);
                return native.ExitCode;
            }
            var nativeConfig = Path.Combine(testRoot, "native-client.json");
            var nativeRoot = Path.Combine(testRoot, "native-sync");
            Assert(await RunNative("--init", "--config", nativeConfig, "--server", url, "--root", nativeRoot, "--remote-dir", "AOT共享") == 0, "AOT client initializes DPAPI config with named cloud directory");
            Assert(await RunNative("--ui-smoke", "--config", nativeConfig) == 0, "AOT settings input, log notifications and button dispatch");
            await Folder("AOT共享");
            var nativeFile = await Put("AOT共享/native.bin", bytes);
            var nativeStart = new ProcessStartInfo(nativeClient)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in new[] { "--headless", "--config", nativeConfig }) nativeStart.ArgumentList.Add(argument);
            using var nativeProcess = Process.Start(nativeStart)!;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeLogs = new StringBuilder();
            nativeProcess.OutputDataReceived += (_, e) => { lock (nativeLogs) nativeLogs.AppendLine(e.Data); if (e.Data?.Contains("同步完成") == true) ready.TrySetResult(); };
            nativeProcess.ErrorDataReceived += (_, e) => { lock (nativeLogs) nativeLogs.AppendLine(e.Data); };
            nativeProcess.BeginOutputReadLine(); nativeProcess.BeginErrorReadLine();
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var nativePath = Path.Combine(nativeRoot, "native.bin");
                Assert(!Directory.Exists(Path.Combine(nativeRoot, "资料")), "AOT client only syncs the configured cloud directory");
                // Windows hides cloud reparse tags from unrelated processes. RECALL_ON_DATA_ACCESS remains visible.
                Assert(((uint)File.GetAttributes(nativePath) & 0x400000) != 0 && new FileInfo(nativePath).Length == bytes.Length,
                    "AOT client creates unhydrated placeholders");
                Assert((await File.ReadAllBytesAsync(nativePath)).SequenceEqual(bytes), "AOT unmanaged callback downloads exact file bytes");
                var nativeReadOnlyPath = Path.Combine(nativeRoot, "native-readonly.img");
                var firmware = Encoding.UTF8.GetBytes("AOT read-only firmware upload");
                await File.WriteAllBytesAsync(nativeReadOnlyPath, firmware);
                File.SetAttributes(nativeReadOnlyPath, File.GetAttributes(nativeReadOnlyPath) | FileAttributes.ReadOnly);
                CloudEntry? nativeReadOnlyEntry = null;
                for (var attempt = 0; attempt < 100 && nativeReadOnlyEntry == null; attempt++)
                {
                    nativeReadOnlyEntry = (await List()).Entries.Find(x => x.Path == "AOT共享/native-readonly.img");
                    if (nativeReadOnlyEntry == null) await Task.Delay(100);
                }
                Assert(nativeReadOnlyEntry != null, "AOT client uploads read-only firmware");
                Assert((await http.GetByteArrayAsync("/api/content/" + nativeReadOnlyEntry!.Id)).SequenceEqual(firmware) &&
                    (File.GetAttributes(nativeReadOnlyPath) & FileAttributes.ReadOnly) != 0, "AOT upload preserves read-only content and attribute");
                for (var index = 0; index < 130; index++) await File.WriteAllTextAsync(Path.Combine(nativeRoot, "batch-" + index + ".txt"), "batch-" + index);
                var bulkWatch = System.Diagnostics.Stopwatch.StartNew();
                while (bulkWatch.Elapsed < TimeSpan.FromSeconds(60) && (await List()).Entries.Count(x => x.Path.StartsWith("AOT共享/batch-", StringComparison.Ordinal)) < 130) await Task.Delay(150);
                Assert((await List()).Entries.Count(x => x.Path.StartsWith("AOT共享/batch-", StringComparison.Ordinal)) == 130, "Native AOT Explorer client uploads 130 files through bounded batches");
                var largeBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16 * 1024 * 1024 + 321);
                await File.WriteAllBytesAsync(Path.Combine(nativeRoot, "native-large.bin"), largeBytes);
                CloudEntry? nativeLarge = null; var largeWatch = System.Diagnostics.Stopwatch.StartNew();
                while (largeWatch.Elapsed < TimeSpan.FromSeconds(60) && nativeLarge == null)
                { nativeLarge = (await List()).Entries.Find(x => x.Path == "AOT共享/native-large.bin"); if (nativeLarge == null) await Task.Delay(150); }
                Assert(nativeLarge != null && nativeLarge.Size == largeBytes.Length, "Native AOT Explorer client commits a multi-chunk large upload");
                Assert((await http.GetByteArrayAsync("/api/content/" + nativeLarge!.Id)).SequenceEqual(largeBytes), "Native AOT large upload preserves exact bytes through the Cloud Files source handle");
            }
            catch { Console.Error.WriteLine(nativeLogs); throw; }
            finally
            {
                if (!nativeProcess.HasExited) { nativeProcess.Kill(true); await nativeProcess.WaitForExitAsync(); }
                Assert(await RunNative("--unregister", "--config", nativeConfig) == 0, "AOT client unregisters Explorer after shutdown");
            }
        }
        Console.WriteLine($"ALL PASSED: {checks} checks. Artifacts: {testRoot}");
    }
    catch { Console.Error.WriteLine(logs); throw; }
    finally
    {
        // Keep the exact fixture identity even if registration/assertions failed,
        // or engine disposal removed CFAPI metadata needed by folder-based lookup.
        engine?.Dispose();
        if (previousShellId != null)
        {
            try { Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(previousShellId); }
            catch (Exception ex) { Console.Error.WriteLine("Fixture shell cleanup: " + ex.Message); }
        }
        if (cloudRoot != null)
        {
            try
            {
                if (args.Contains("--shell")) ShellIntegration.Unregister(cloudRoot);
                else CloudProvider.Unregister(cloudRoot);
            }
            catch (Exception ex) { Console.Error.WriteLine("Unregister: " + ex.Message); }
        }
        StopServer();
        File.WriteAllText(Path.Combine(testRoot, "server.log"), logs.ToString());
    }
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
