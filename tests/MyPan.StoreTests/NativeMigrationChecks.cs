using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class NativeMigrationChecks
{
    public static async Task Run(string workspace, string root, string sourceCatalog, Action<bool, string> assert)
    {
        // Copy only; never launch against or modify the source data directory.
        var sourceBytes = File.ReadAllBytes(sourceCatalog);
        using var original = JsonDocument.Parse(sourceBytes);
        var sourceBlobs = Path.Combine(Path.GetDirectoryName(sourceCatalog)!, "blobs");
        var data = Path.Combine(root, "native-data");
        var blobs = Path.Combine(data, "blobs");
        Directory.CreateDirectory(blobs);
        File.WriteAllBytes(Path.Combine(data, "catalog.json"), sourceBytes);
        foreach (var file in Directory.EnumerateFiles(sourceBlobs)) File.Copy(file, Path.Combine(blobs, Path.GetFileName(file)));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(workspace, "scripts/start-server.ps1"), "-Port", port.ToString(), "-DataPath", data })
            start.ArgumentList.Add(arg);
        start.Environment["MyPan__UnattendedSetup"] = "true"; start.Environment["MyPan__ApiKey"] = key;
        start.Environment["MyPan__ApiKeyFile"] = Path.Combine(root, "native-api-key.txt");
        start.Environment["MyPan__CertificatePath"] = "";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        string? token = null;
        string? accounts = null;
        for (var restart = 0; restart < 2; restart++)
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                var ready = false;
                for (var i = 0; i < 150; i++)
                {
                    if (process.HasExited) throw new Exception("Native launcher exited: " + await output + await errors);
                    try { using var response = await client.GetAsync("/health"); if (response.IsSuccessStatusCode) { ready = true; break; } }
                    catch (HttpRequestException) { }
                    await Task.Delay(100);
                }
                assert(ready, "Published Native AOT server starts through start-server.ps1" + (restart == 0 ? " using a legacy data copy" : " after migration"));
                using var listing = JsonDocument.Parse(await client.GetStringAsync("/api/entries"));
                var actual = listing.RootElement.GetProperty("entries").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid());
                var expected = original.RootElement.GetProperty("Entries").EnumerateArray().ToArray();
                assert(actual.Count >= expected.Length && expected.All(x => actual.TryGetValue(x.GetProperty("Id").GetGuid(), out var entry) &&
                    entry.GetProperty("path").GetString() == x.GetProperty("Path").GetString() && entry.GetProperty("version").GetString() == x.GetProperty("Version").GetString() &&
                    entry.GetProperty("size").GetInt64() == x.GetProperty("Size").GetInt64()), "Native migration retains every legacy entry ID, path, size and version");
                using var trash = JsonDocument.Parse(await client.GetStringAsync("/api/trash"));
                var actualTrash = trash.RootElement.GetProperty("entries").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
                assert(actualTrash.SetEquals(original.RootElement.GetProperty("Trash").EnumerateArray().Select(x => x.GetProperty("Entry").GetProperty("Id").GetGuid())), "Native migration retains every recycle-bin entry");
                if (restart == 0)
                {
                    foreach (var entry in expected.Where(x => !x.GetProperty("IsDirectory").GetBoolean()))
                    {
                        var id = entry.GetProperty("Id").GetGuid();
                        using var response = await client.GetAsync("/api/content/" + id, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();
                        await using var remote = await response.Content.ReadAsStreamAsync();
                        await using var local = File.OpenRead(Path.Combine(blobs, id.ToString("N") + "_" + entry.GetProperty("Version").GetString()));
                        var remoteHash = await SHA256.HashDataAsync(remote);
                        var localHash = await SHA256.HashDataAsync(local);
                        assert(remoteHash.SequenceEqual(localHash), "Native server downloads unchanged legacy blob bytes");
                    }
                    using var body = new StringContent("{\"username\":\"admin\",\"password\":\"" + key + "\"}", Encoding.UTF8, "application/json");
                    using var login = await client.PostAsync("/api/auth/login", body);
                    login.EnsureSuccessStatusCode();
                    using var result = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
                    token = result.RootElement.GetProperty("accessToken").GetString()!;
                    using var saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "catalog.json")));
                    accounts = saved.RootElement.GetProperty("Users").GetRawText();
                    var backup = Directory.GetFiles(data, "catalog.json.v1-backup-*").Single();
                    assert(File.ReadAllBytes(backup).SequenceEqual(sourceBytes), "Native migration backs up the exact original catalog");
                }
                else
                {
                    client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                    using var me = await client.GetAsync("/api/me");
                    assert(me.IsSuccessStatusCode, "Migrated login session survives native server restart");
                    using var saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "catalog.json")));
                    assert(saved.RootElement.GetProperty("Users").GetRawText() == accounts && Directory.GetFiles(data, "catalog.json.v1-backup-*").Length == 1,
                        "Native restart preserves accounts without repeating migration");
                }
            }
            finally
            {
                // Kill only the launcher and server owned by this test.
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                File.WriteAllText(Path.Combine(root, "native-launcher-" + restart + ".log"), await output + await errors);
            }
        }
        assert(File.ReadAllBytes(sourceCatalog).SequenceEqual(sourceBytes), "Source catalog is unchanged; all native checks used an isolated copy");
    }
}
