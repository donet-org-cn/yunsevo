using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using MyPan.Contracts;

internal sealed class SecondServer(Process process, string url) : IDisposable
{
    internal string Url { get; } = url;
    internal static async Task<SecondServer> Start(ProcessStartInfo original, string root, string key)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = "http://127.0.0.1:" + port;
        var start = new ProcessStartInfo(original.FileName) { WorkingDirectory = original.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in original.ArgumentList) start.ArgumentList.Add(argument);
        foreach (var variable in original.Environment) start.Environment[variable.Key] = variable.Value;
        start.Environment["ASPNETCORE_URLS"] = url;
        start.Environment["MyPan__DataPath"] = Path.Combine(root, "second-server-data");
        start.Environment["MyPan__InstallationFile"] = Path.Combine(root, "second-server-settings.json");
        start.Environment["MyPan__ApiKeyFile"] = Path.Combine(root, "second-server-key.txt");
        var process = Process.Start(start)!;
        var result = new SecondServer(process, url);
        var logs = new StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (logs) logs.AppendLine(e.Data); }; process.ErrorDataReceived += (_, e) => { lock (logs) logs.AppendLine(e.Data); }; process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(url) }; client.DefaultRequestHeaders.Authorization = new("Bearer", key);
            var ready = false;
            for (var attempt = 0; attempt < 150; attempt++)
            {
                if (process.HasExited) { process.WaitForExit(); throw new InvalidOperationException("Second isolated server exited: " + logs); }
                try { using var health = await client.GetAsync("/health"); if (health.IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            if (!ready) throw new TimeoutException("Second isolated server did not start.");
            using var created = await client.PostAsJsonAsync("/api/admin/users", new SaveUser("secondary", "Secondary", key), ApiJson.Default.SaveUser); created.EnsureSuccessStatusCode();
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString("公共/second-server.txt")) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("second-server")) };
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    public void Dispose() { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } process.Dispose(); }
}
