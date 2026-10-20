using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yunsevo.Contracts;
using Yunsevo.Sync;

var workspace = Environment.CurrentDirectory;
var executable = args.First(x => x.StartsWith("--server="))[9..];
var label = args.FirstOrDefault(x => x.StartsWith("--label="))?[8..] ?? "current";
var headless = args.FirstOrDefault(x => x.StartsWith("--headless="))?[11..];
var largeMiB = int.Parse(args.FirstOrDefault(x => x.StartsWith("--large-mib="))?[12..] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
var count = int.Parse(args.FirstOrDefault(x => x.StartsWith("--files="))?[8..] ?? "300", System.Globalization.CultureInfo.InvariantCulture);
var directory = Path.Combine(workspace, ".test-data", "performance-" + label + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); Directory.CreateDirectory(directory);
using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var url = "http://127.0.0.1:" + port; var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
start.Environment["ASPNETCORE_URLS"] = url; start.Environment["Yunsevo__UnattendedSetup"] = "true"; start.Environment["Yunsevo__ApiKey"] = key; start.Environment["Yunsevo__ApiKeyFile"] = Path.Combine(directory, "key.txt"); start.Environment["Yunsevo__DataPath"] = Path.Combine(directory, "data"); start.Environment["Logging__LogLevel__Default"] = "Warning";
using var process = Process.Start(start)!; process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { }; process.BeginOutputReadLine(); process.BeginErrorReadLine();
try
{
    using var http = new HttpClient { BaseAddress = new(url), Timeout = TimeSpan.FromMinutes(5) }; http.DefaultRequestHeaders.Authorization = new("Bearer", key);
    var ready = false;
    for (var attempt = 0; attempt < 150; attempt++) { try { using var response = await http.GetAsync("health"); if (response.IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { } await Task.Delay(100); }
    if (!ready) throw new TimeoutException("Isolated benchmark server startup.");
    var me = (await http.GetFromJsonAsync("api/me", ApiJson.Default.UserInfo))!;
    var remote = "个人/admin/bench";
    using (var created = await http.PostAsJsonAsync("api/folders", new CreateFolder(remote), ApiJson.Default.CreateFolder)) created.EnsureSuccessStatusCode();
    var source = Path.Combine(directory, "source");
    using var connection = new CloudConnection(url, key);
    using var engine = headless == null ? new PortableSync(connection, url, me.Id, remote, source, Path.Combine(directory, "upload-state.json"), _ => { }) : null;
    async Task NativeCycle(string local, string configName)
    {
        var config = Path.Combine(directory, configName);
        if (!File.Exists(config)) await File.WriteAllTextAsync(config, new System.Text.Json.Nodes.JsonObject { ["server"] = url, ["username"] = "admin", ["remoteDirectory"] = remote, ["syncRoot"] = local }.ToJsonString());
        var childStart = new ProcessStartInfo(headless!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--config", config, "--once" }) childStart.ArgumentList.Add(argument);
        childStart.Environment["YUNSEVO_PASSWORD"] = key;
        using var child = Process.Start(childStart)!; var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        if (child.ExitCode != 0) throw new Exception("Native benchmark client failed: " + await error);
        await output;
    }
    async Task UploadCycle() { if (engine != null) await engine.SyncOnce(default); else await NativeCycle(source, "upload.json"); }
    await UploadCycle();
    var bytes = RandomNumberGenerator.GetBytes(1024);
    for (var i = 0; i < count; i++) await File.WriteAllBytesAsync(Path.Combine(source, "file-" + i.ToString("D5") + ".bin"), bytes);
    var watch = Stopwatch.StartNew(); var passes = 0;
    for (; passes < count + 5; passes++)
    {
        await UploadCycle();
        var catalog = (await http.GetFromJsonAsync("api/entries", ApiJson.Default.Catalog))!;
        if (catalog.Entries.Count(x => x.Path.StartsWith(remote + "/file-", StringComparison.Ordinal)) == count) break;
    }
    var uploadMs = watch.Elapsed.TotalMilliseconds;
    using var targetConnection = new CloudConnection(url, key);
    var target = Path.Combine(directory, "target");
    using var download = headless == null ? new PortableSync(targetConnection, url, me.Id, remote, target, Path.Combine(directory, "download-state.json"), _ => { }) : null;
    watch.Restart(); if (download != null) await download.SyncOnce(default); else await NativeCycle(target, "download.json"); var downloadMs = watch.Elapsed.TotalMilliseconds;
    if (Directory.GetFiles(target).Length != count || !(await File.ReadAllBytesAsync(Path.Combine(target, "file-00000.bin"))).SequenceEqual(bytes)) throw new Exception("Benchmark data mismatch.");
    watch.Restart(); for (var i = 0; i < 5; i++) await UploadCycle(); var idleMs = watch.Elapsed.TotalMilliseconds / 5;
    double largeUploadMs = 0, largeDownloadMs = 0;
    if (largeMiB > 0)
    {
        var sourceLarge = Path.Combine(source, "large.bin");
        await using (var output = new FileStream(sourceLarge, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
        {
            var block = RandomNumberGenerator.GetBytes(1024 * 1024);
            for (var index = 0; index < largeMiB; index++) await output.WriteAsync(block);
        }
        watch.Restart(); await UploadCycle(); largeUploadMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); if (download != null) await download.SyncOnce(default); else await NativeCycle(target, "download.json"); largeDownloadMs = watch.Elapsed.TotalMilliseconds;
        await using var original = File.OpenRead(sourceLarge); await using var received = File.OpenRead(Path.Combine(target, "large.bin"));
        var originalHash = await SHA256.HashDataAsync(original); var receivedHash = await SHA256.HashDataAsync(received);
        if (!originalHash.SequenceEqual(receivedHash)) throw new Exception("Large benchmark file hash mismatch.");
    }
    process.Refresh();
    var result = new Benchmark(label, count, 1024, uploadMs, downloadMs, idleMs, passes + 1, process.PeakWorkingSet64, largeMiB, largeUploadMs, largeDownloadMs, headless == null ? "In-process portable engine" : "Native headless --once including startup/login");
    await File.WriteAllTextAsync(Path.Combine(directory, "benchmark.json"), JsonSerializer.Serialize(result, BenchmarkJson.Default.Benchmark));
    Console.WriteLine(JsonSerializer.Serialize(result, BenchmarkJson.Default.Benchmark)); Console.WriteLine(directory);
}
finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }

internal sealed record Benchmark(string Label, int Files, int BytesPerFile, double UploadMilliseconds, double DownloadMilliseconds, double IdleMilliseconds, int UploadPasses, long ServerPeakWorkingSetBytes, int LargeMiB, double LargeUploadMilliseconds, double LargeDownloadMilliseconds, string Mode);
[JsonSerializable(typeof(Benchmark))]
internal partial class BenchmarkJson : JsonSerializerContext;
