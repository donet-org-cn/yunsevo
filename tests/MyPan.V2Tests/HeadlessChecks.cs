using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MyPan.Contracts;

internal static class HeadlessChecks
{
    public static async Task Run(string executable, string root, string url, string password, HttpClient alice, Action<bool, string> assert)
    {
        var directory = Path.Combine(root, "native-headless"); Directory.CreateDirectory(directory); var local = Path.Combine(directory, "files"); var config = Path.Combine(directory, "client.json");
        await File.WriteAllTextAsync(config, new JsonObject { ["server"] = url, ["username"] = "alice", ["remoteDirectory"] = "个人/alice", ["syncRoot"] = local }.ToJsonString());
        async Task<string> Run(params string[] args)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--config"); start.ArgumentList.Add(config); foreach (var argument in args) start.ArgumentList.Add(argument); start.Environment["MYPAN_PASSWORD"] = password;
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            if (process.ExitCode != 0) throw new Exception("Headless failed: " + await output + await error); return await output;
        }
        await Run("--once"); assert(File.Exists(Path.Combine(local, "secret.txt")), "Native AOT headless client logs in and downloads account-scoped files without a GUI");
        File.Delete(Path.Combine(local, "secret.txt")); await Run("--once");
        var pending = await Run("--list-deletions");
        assert(pending.Contains("secret.txt") && !pending.Contains(".mypan-download-readable.txt") && !File.Exists(Path.Combine(local, "secret.txt")), "Headless offline deletion waits in an inspectable confirmation queue");
        await Run("--resolve-delete", "secret.txt", "--decision", "local"); await Run("--once"); await Run("--once");
        assert(!File.Exists(Path.Combine(local, "secret.txt")) && (await alice.GetFromJsonAsync("/api/entries", ApiJson.Default.Catalog))!.Entries.Any(x => x.Path == "个人/alice/secret.txt"), "Headless local-only deletion survives restart and preserves cloud content");
        await File.WriteAllTextAsync(Path.Combine(local, "headless.txt"), "headless upload"); await Run("--once"); File.Delete(Path.Combine(local, "headless.txt")); await Run("--once");
        await Run("--resolve-delete", "headless.txt", "--decision", "cloud"); await Run("--once");
        assert((await alice.GetFromJsonAsync("/api/trash", ApiJson.Default.Catalog))!.Entries.Any(x => x.Path == "个人/alice/headless.txt"), "Headless explicit cloud-delete decision moves the file into the recycle bin");
    }
}
