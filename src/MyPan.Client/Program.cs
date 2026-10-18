using System.Security.Cryptography;
using System.Text;
using MyPan.Client;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string? Value(string name) => Array.IndexOf(args, name) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        var configPath = Path.GetFullPath(Value("--config") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPan", "client.json"));
        if (args.Contains("--help"))
        {
            Console.WriteLine("MyPan.Client [--config <file>] [--headless | --background] [--init --server <url> --root <folder> --remote-dir <云端目录>] [--unregister]");
            Console.WriteLine("--init 从 MYPAN_API_KEY 环境变量读取密钥，使用 Windows DPAPI 保存。--unregister 停止后解除 Explorer 注册，保留本地文件。");
            return 0;
        }
        try
        {
            using var mutex = new Mutex(true, "Local\\MyPan-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configPath.ToUpperInvariant())))[..24], out var first);
            if (!first) throw new InvalidOperationException("该配置的 MyPan 客户端已经运行。");
            if (args.Contains("--init"))
            {
                var key = Environment.GetEnvironmentVariable("MYPAN_API_KEY") ?? throw new ArgumentException("请设置 MYPAN_API_KEY 环境变量。");
                var config = new ClientConfig(Value("--server") ?? "http://localhost:5080", Secret.Protect(key),
                    Value("--root") ?? ClientConfig.Default.SyncRoot, RemoteDirectory: Value("--remote-dir") ?? "");
                config.Validate(); config.Save(configPath); Console.WriteLine("配置已保存：" + configPath); return 0;
            }
            if (args.Contains("--unregister"))
            {
                var config = ClientConfig.Load(configPath);
                ShellIntegration.SetStartup(configPath, false);
                ShellIntegration.Unregister(config.SyncRoot);
                Console.WriteLine("已解除注册，本地文件和云端数据均保留。仅云端占位文件将无法打开，重新连接可恢复。");
                return 0;
            }
            if (args.Contains("--headless"))
            {
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                var config = ClientConfig.Load(configPath);
                using var engine = new SyncEngine(config, config.GetStatePath(configPath), Console.WriteLine);
                ShellIntegration.Register(config.SyncRoot, Console.WriteLine).GetAwaiter().GetResult();
                engine.Run(cancellation.Token).GetAwaiter().GetResult(); return 0;
            }
            if (!args.Contains("--ui-smoke")) Win32.FreeConsole();
            return new SettingsWindow(configPath, args.Contains("--background"), args.Contains("--ui-smoke")).Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (!args.Contains("--headless") && !args.Contains("--init") && !args.Contains("--unregister") && !args.Contains("--ui-smoke"))
                Win32.MessageBox(0, ex.Message, "MyPan 云盘", 0x10);
            return 1;
        }
    }
}
