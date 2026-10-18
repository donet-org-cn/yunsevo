using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyPan.Contracts;

internal static class MigrationFeaturesChecks
{
    public static async Task Run(HttpClient admin, HttpClient alice, HttpClient bob, string root, string workspace, ProcessStartInfo template, Guid aliceId, string oldToken, string password, Action<bool,string> assert)
    {
        var catalog = (await admin.GetFromJsonAsync("/api/entries", ApiJson.Default.Catalog))!;
        var home = catalog.Entries.Single(x => x.Path == "个人/alice");
        assert(catalog.Entries.Any(x => x.Path == "个人/admin"), "Bootstrap administrator has its personal folder by default");
        using (var share = await admin.PutAsJsonAsync("/api/folders/" + home.Id + "/policy", new FolderPolicy(home.Id, aliceId, "public", 3, []), ApiJson.Default.FolderPolicy))
            assert(share.StatusCode == HttpStatusCode.Conflict, "A default personal root cannot be changed to public or reassigned");
        using (var inherit = await alice.DeleteAsync("/api/folders/" + home.Id + "/policy")) assert(inherit.StatusCode == HttpStatusCode.Conflict, "A personal root cannot lose its private policy by restoring inheritance");
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/entries/" + home.Id))
        { delete.Headers.IfMatch.ParseAdd('"' + home.Version + '"'); using var response = await alice.SendAsync(delete); assert(response.StatusCode == HttpStatusCode.Conflict, "Default personal roots cannot be deleted"); }
        using (var move = await alice.PatchAsJsonAsync("/api/entries/" + home.Id, new MoveEntry("公共/renamed-home", home.Version), ApiJson.Default.MoveEntry)) assert(move.StatusCode == HttpStatusCode.Conflict, "Default personal roots cannot be moved or renamed");
        using var folderResponse = await alice.PostAsJsonAsync("/api/folders", new CreateFolder("个人/alice/backup-proof"), ApiJson.Default.CreateFolder); folderResponse.EnsureSuccessStatusCode();
        var folder = (await folderResponse.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!;
        using (var childPolicy = await admin.PutAsJsonAsync("/api/folders/" + folder.Id + "/policy", new FolderPolicy(folder.Id, aliceId, "public", 3, []), ApiJson.Default.FolderPolicy)) childPolicy.EnsureSuccessStatusCode();
        async Task<CloudEntry> Put(string path, string content, string? version = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString(path)) { Content = new StringContent(content, Encoding.UTF8) };
            if (version == null) request.Headers.IfNoneMatch.ParseAdd("*"); else request.Headers.IfMatch.ParseAdd('"' + version + '"');
            using var response = await alice.SendAsync(request); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync(ApiJson.Default.CloudEntry))!;
        }
        var first = await Put(folder.Path + "/migration.txt", "old preserved bytes");
        var current = await Put(first.Path, "new preserved bytes", first.Version);
        var removed = await Put(folder.Path + "/deleted.txt", "deleted preserved bytes");
        using (var denied = await bob.GetAsync("/api/content/" + current.Id)) assert(denied.StatusCode == HttpStatusCode.Forbidden, "A public child policy cannot disclose another user's default personal files");
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/entries/" + removed.Id))
        { delete.Headers.IfMatch.ParseAdd('"' + removed.Version + '"'); using var response = await alice.SendAsync(delete); response.EnsureSuccessStatusCode(); }
        using (var read = await alice.GetAsync("/api/trash/" + removed.Id + "/content")) assert(read.IsSuccessStatusCode && await read.Content.ReadAsStringAsync() == "deleted preserved bytes", "Recycle-bin files download without restoring them");
        using (var preview = await alice.GetAsync("/api/trash/" + removed.Id + "/preview")) assert(preview.Content.Headers.ContentType?.MediaType == "text/plain" && await preview.Content.ReadAsStringAsync() == "deleted preserved bytes", "Recycle-bin previews preserve the safe text MIME type");
        foreach (var endpoint in new[] {"content", "preview"})
        { using var denied = await bob.GetAsync("/api/trash/" + removed.Id + "/" + endpoint); assert(denied.StatusCode == HttpStatusCode.Forbidden, "Recycle-bin " + endpoint + " checks deleted-batch permissions against ID guessing"); }
        using (var range = new HttpRequestMessage(HttpMethod.Get, "/api/trash/" + removed.Id + "/content"))
        { range.Headers.Range = new(0, 6); using var response = await alice.SendAsync(range); assert(response.StatusCode == HttpStatusCode.PartialContent && await response.Content.ReadAsStringAsync() == "deleted", "Recycle-bin downloads support actual byte ranges for media"); }
        using (var stale = await alice.GetAsync("/api/trash/" + removed.Id + "/content?version=stale")) assert(stale.StatusCode == HttpStatusCode.Conflict, "Recycle-bin reads reject a stale version guard");
        using (var denied = await alice.GetAsync("/api/admin/backup")) assert(denied.StatusCode == HttpStatusCode.Forbidden, "Migration exports are restricted to administrators");
        var backupPath = Path.Combine(root, "migration-backup.zip");
        using (var response = await admin.GetAsync("/api/admin/backup", HttpCompletionOption.ResponseHeadersRead))
        { response.EnsureSuccessStatusCode(); assert(response.Content.Headers.ContentDisposition?.DispositionType == "attachment", "Migration backup is sent as a browser download"); await using var output = File.Create(backupPath); await response.Content.CopyToAsync(output); }
        using (var zip = ZipFile.OpenRead(backupPath))
        {
            using var stream = zip.GetEntry("manifest.json")!.Open(); var manifest = (await JsonSerializer.DeserializeAsync(stream, ApiJson.Default.BackupManifest))!;
            var hashesMatch = true;
            foreach (var file in manifest.Files) { using var input = zip.GetEntry(file.Path)!.Open(); hashesMatch &= Convert.ToHexString(await SHA256.HashDataAsync(input)) == file.Sha256 && zip.GetEntry(file.Path)!.Length == file.Size; }
            assert(hashesMatch && manifest.Files.Count > 5, "Every migration archive member matches its declared length and SHA-256");
            using var catStream = zip.GetEntry("data/catalog.json")!.Open(); using var document = await JsonDocument.ParseAsync(catStream);
            assert(document.RootElement.GetProperty("Sessions").GetArrayLength() == 0, "Backups retain accounts while requiring fresh logins after restoration");
            assert(zip.GetEntry("data/blobs/" + first.Id.ToString("N") + "_" + first.Version) != null && zip.GetEntry("data/blobs/" + current.Id.ToString("N") + "_" + current.Version) != null && zip.GetEntry("data/blobs/" + removed.Id.ToString("N") + "_" + removed.Version) != null, "Archive includes current content, retained versions and recycled content");
            assert(zip.GetEntry("api-key.txt") != null && zip.Entries.Any(x => x.FullName.StartsWith("data/audit/")) && !zip.Entries.Any(x => x.FullName.Contains("staging") || x.FullName.EndsWith(".tmp") || x.FullName.EndsWith("store.lock")), "Migration includes keys and audit while excluding incomplete uploads and runtime locks");
        }
        var destination = Path.Combine(root, "restored-server");
        async Task<int> Restore(string archive, string target)
        {
            var command = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] {"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(workspace,"scripts/restore-backup.ps1"),"-Archive",archive,"-Destination",target}) command.ArgumentList.Add(arg);
            using var process = Process.Start(command)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            await File.WriteAllTextAsync(target + ".restore.log", await output + await error); return process.ExitCode;
        }
        assert(await Restore(backupPath, destination) == 0 && File.Exists(Path.Combine(destination,"data/catalog.json")), "Restore script validates and publishes a complete migration into a new directory");
        assert(await Restore(backupPath, destination) != 0, "Restore refuses to overwrite existing data");
        var corrupted = Path.Combine(root,"corrupted-backup.zip"); File.Copy(backupPath, corrupted);
        using (var zip = ZipFile.Open(corrupted, ZipArchiveMode.Update)) { var previous = zip.GetEntry("RESTORE.txt")!; var length = (int)previous.Length; previous.Delete(); using var stream = zip.CreateEntry("RESTORE.txt").Open(); stream.Write(new byte[length]); }
        var invalidTarget = Path.Combine(root,"invalid-restore"); assert(await Restore(corrupted, invalidTarget) != 0 && !Directory.Exists(invalidTarget), "Corrupt backup is rejected without publishing a restored server");
        var traversal = Path.Combine(root,"traversal-backup.zip"); File.Copy(backupPath, traversal);
        using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Update)) { using var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open()); writer.Write("escape"); }
        assert(await Restore(traversal, Path.Combine(root,"traversal-restore")) != 0 && !File.Exists(Path.Combine(root,"escaped.txt")), "Restore rejects archive path traversal before extracting any files");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var child = new ProcessStartInfo(template.FileName) { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in template.ArgumentList) child.ArgumentList.Add(argument);
        foreach (var pair in template.Environment) child.Environment[pair.Key] = pair.Value;
        child.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:" + port; child.Environment["MyPan__DataPath"] = Path.Combine(destination,"data"); child.Environment["MyPan__ApiKeyFile"] = Path.Combine(destination,"api-key.txt");
        using var restored = Process.Start(child)!; var stdout = restored.StandardOutput.ReadToEndAsync(); var stderr = restored.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port) };
            for (var i=0;i<100;i++) { if (restored.HasExited) throw new Exception(await stdout + await stderr); try { using var health = await client.GetAsync("/health"); if (health.IsSuccessStatusCode) break; } catch (HttpRequestException) {} await Task.Delay(100); }
            client.DefaultRequestHeaders.Authorization = new("Bearer",oldToken);
            using (var oldSession = await client.GetAsync("/api/me")) assert(oldSession.StatusCode == HttpStatusCode.Unauthorized, "Migrated server rejects sessions issued by the original server");
            using var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("alice",password),ApiJson.Default.LoginRequest); loginResponse.EnsureSuccessStatusCode();
            var login = (await loginResponse.Content.ReadFromJsonAsync(ApiJson.Default.LoginResult))!; client.DefaultRequestHeaders.Authorization = new("Bearer",login.AccessToken);
            assert(await client.GetStringAsync("/api/content/" + current.Id) == "new preserved bytes" && await client.GetStringAsync("/api/entries/" + current.Id + "/versions/" + first.Version) == "old preserved bytes" && await client.GetStringAsync("/api/trash/" + removed.Id + "/content") == "deleted preserved bytes", "Restored server logs in with the original password and serves identical current, historical and recycled bytes");
        }
        finally { if (!restored.HasExited) { restored.Kill(true); await restored.WaitForExitAsync(); } await File.WriteAllTextAsync(Path.Combine(destination,"server.log"),await stdout+await stderr); }
    }
}
