using Avalonia;

namespace Yunsevo.Desktop;

internal static class Program
{
    internal static bool Smoke;
    internal static bool WorkflowProbe;
    internal static bool RestoreProbe;
    [STAThread]
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Contains("--probe")) { try { Probe(args).GetAwaiter().GetResult(); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; } return; }
        Smoke = args.Contains("--smoke");
        WorkflowProbe = args.Contains("--workflow-probe");
        RestoreProbe = args.Contains("--restore-probe");
        if ((WorkflowProbe || RestoreProbe) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("YUNSEVO_DESKTOP_HOME")))
            throw new ArgumentException("Workflow probes require an isolated YUNSEVO_DESKTOP_HOME.");
        var home = Environment.GetEnvironmentVariable("YUNSEVO_DESKTOP_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YunsevoDesktop");
        using var instance = new Mutex(true, "YunsevoDesktop-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(home)))[..24], out var created);
        if (!created) { Console.Error.WriteLine(Yunsevo.Contracts.ProductBrand.Desktop + " 已在运行，请从系统托盘打开。"); return; }
        AppBuilder.Configure<DesktopApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
    private static async Task Probe(string[] args)
    {
        string Value(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException(name);
        using var connection = new Yunsevo.Sync.CloudConnection(Value("--server"));
        var result = await connection.Login(Environment.GetEnvironmentVariable("YUNSEVO_USERNAME") ?? "admin", Environment.GetEnvironmentVariable("YUNSEVO_PASSWORD") ?? "", default);
        var root = Path.GetFullPath(Value("--root"));
#if WINDOWS
        if (args.Contains("--on-demand"))
        {
            var config = new Yunsevo.Client.ClientConfig(Value("--server"), Yunsevo.Client.Secret.Protect(result.AccessToken), root, RemoteDirectory: Value("--remote-dir"), AccountId: result.User.Id.ToString("N"));
            var registered = false;
            try
            {
                using var engine = new Yunsevo.Client.SyncEngine(config, root + ".state.json", Console.WriteLine);
                registered = true;
                await Yunsevo.Client.ShellIntegration.Register(root, Console.WriteLine); await engine.SyncOnce();
                var first = args.Contains("--verify-file") ? Path.Combine(root, Yunsevo.Contracts.CloudPath.Normalize(Value("--verify-file")).Replace('/', Path.DirectorySeparatorChar)) : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).FirstOrDefault();
                if (first != null) { await using var input = File.OpenRead(first); await input.CopyToAsync(Stream.Null); }
                Console.WriteLine("Native AOT account login, Explorer placeholders and hydration probe passed.");
            }
            finally { if (registered) Yunsevo.Client.ShellIntegration.Unregister(root); }
            return;
        }
#endif
        using var sync = new Yunsevo.Sync.PortableSync(connection, Value("--server"), result.User.Id, Value("--remote-dir"), root, root + ".state.json", Console.WriteLine);
        await sync.SyncOnce(default);
        Console.WriteLine("Native AOT login and portable sync probe passed.");
    }
}
