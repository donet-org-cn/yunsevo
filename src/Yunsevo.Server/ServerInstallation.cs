using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Yunsevo.Contracts;

namespace Yunsevo.Server;

internal sealed record InstallationSettings(int Version, string DataPath, bool Complete);
internal sealed record InstallationRequest(string Username, string Password, string ConfirmPassword, string DataPath);
internal sealed record InstallationStatus(bool Required);
internal sealed record InstallationDefaults(string DataPath);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(InstallationSettings))]
[JsonSerializable(typeof(InstallationRequest))]
[JsonSerializable(typeof(InstallationStatus))]
[JsonSerializable(typeof(InstallationDefaults))]
internal partial class InstallationJson : JsonSerializerContext;

internal static class ServerInstallation
{
    private static string SettingsPath(IConfiguration configuration) => Path.GetFullPath(configuration["Yunsevo:InstallationFile"] ??
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configuration["Yunsevo:ApiKeyFile"] ?? Path.Combine(AppContext.BaseDirectory, "api-key.txt")))!, "server-settings.json"));

    private static InstallationSettings? Read(IConfiguration configuration)
    {
        var path = SettingsPath(configuration);
        if (!File.Exists(path)) return null;
        var settings = JsonSerializer.Deserialize(File.ReadAllBytes(path), InstallationJson.Default.InstallationSettings);
        if (settings is not { Version: 1 } || string.IsNullOrWhiteSpace(settings.DataPath) || !Path.IsPathFullyQualified(settings.DataPath))
            throw new InvalidDataException("安装配置损坏，请保留 server-settings.json 并恢复正确的存储路径。");
        return settings;
    }

    public static string DataPath(IConfiguration configuration) => Path.GetFullPath(Read(configuration)?.DataPath ?? configuration["Yunsevo:DataPath"] ??
        configuration["Yunsevo:DefaultDataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data"));

    public static void ConfigureListener(WebApplicationBuilder builder)
    {
        if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://0.0.0.0:5080");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long?>("Yunsevo:MaxUploadBytes") ?? 20L * 1024 * 1024 * 1024;
            var certificatePath = builder.Configuration["Yunsevo:CertificatePath"];
            if (string.IsNullOrEmpty(certificatePath)) return;
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, builder.Configuration["Yunsevo:CertificatePassword"]);
            void Https(ListenOptions endpoint)
            {
                endpoint.Protocols = builder.Configuration.GetValue("Yunsevo:EnableHttp3", true) ? HttpProtocols.Http1AndHttp2AndHttp3 : HttpProtocols.Http1AndHttp2;
                endpoint.UseHttps(certificate);
            }
            var port = builder.Configuration.GetValue("Yunsevo:HttpsPort", 5443);
            var address = builder.Configuration["Yunsevo:ListenAddress"];
            if (string.IsNullOrWhiteSpace(address)) options.ListenAnyIP(port, Https); else options.Listen(IPAddress.Parse(address), port, Https);
        });
    }

    private static void Save(IConfiguration configuration, string path, bool complete)
    {
        var file = SettingsPath(configuration);
        AtomicFile.WriteText(file, JsonSerializer.Serialize(new InstallationSettings(1, path, complete), InstallationJson.Default.InstallationSettings));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void ValidateAccount(string username, string password, string confirmation)
    {
        if (username.Length is < 3 or > 64 || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new StoreException(400, "管理员用户名须为 3–64 位英文字母、数字、点、横线或下划线。");
        if (password.Length is < 12 or > 256) throw new StoreException(400, "密码长度须为 12–256 个字符。");
        if (password != confirmation) throw new StoreException(400, "两次输入的密码不一致。");
    }

    private static string ValidateDirectory(string input, IConfiguration configuration)
    {
        if (!Path.IsPathFullyQualified(input)) throw new StoreException(400, "请输入服务端磁盘上的绝对路径。");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        if (path == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!)) throw new StoreException(400, "请选择专用文件夹，不能使用磁盘根目录。");
        for (var folder = new DirectoryInfo(path); folder != null; folder = folder.Parent)
            if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new StoreException(400, "存储目录不能位于符号链接或目录联接内。");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            var pending = Read(configuration);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var retry = pending is { Complete: false } && Path.TrimEndingDirectorySeparator(pending.DataPath).Equals(path, comparison) &&
                Directory.EnumerateFileSystemEntries(path).All(item => !File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint) &&
                    (Path.GetFileName(item) == "store.lock" && File.Exists(item) || Path.GetFileName(item) == "blobs" && Directory.Exists(item) && !Directory.EnumerateFileSystemEntries(item).Any()));
            if (!retry) throw new StoreException(409, "存储目录必须为空，已有文件已保留。迁移数据请使用备份恢复流程。");
        }
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, ".yunsevo-write-test-" + Guid.NewGuid().ToString("N"));
        try { using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.WriteByte(0); stream.Flush(true); }
        finally { File.Delete(probe); }
        return path;
    }

    public static async Task<bool> EnsureInstalled(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var configuration = builder.Configuration;
        var settings = Read(configuration);
        var path = DataPath(configuration);
        if (File.Exists(Path.Combine(path, "catalog.json")))
        {
            // Upgrades keep their existing account database. No administrator is recreated.
            if (settings == null || !settings.Complete) Save(configuration, path, true);
            return true;
        }
        if (settings is { Complete: true }) throw new InvalidDataException("安装已完成，但存储目录缺少 catalog.json。请检查磁盘挂载或恢复数据；不会重新初始化或重置管理员。");
        if (File.Exists(Path.Combine(path, "catalog.journal"))) throw new InvalidDataException("目录快照丢失，已保留增量日志。请恢复备份。");
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath(configuration))!);
        using var setupLock = new FileStream(SettingsPath(configuration) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var (key, _) = ServerCredentials.Load(configuration);
        if (configuration.GetValue("Yunsevo:UnattendedSetup", false))
        {
            var username = configuration["Yunsevo:AdminUsername"] ?? "admin";
            var password = configuration["Yunsevo:AdminPassword"] ?? configuration["Yunsevo:ApiKey"] ??
                throw new InvalidOperationException("无人值守初始化须提供 Yunsevo__AdminPassword 或显式 Yunsevo__ApiKey。");
            ValidateAccount(username, password, password);
            path = ValidateDirectory(path, configuration);
            Save(configuration, path, false);
            using (var store = new CloudStore(path, key, username, password)) { }
            Save(configuration, path, true);
            return true;
        }
        ConfigureListener(builder);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16384);
        var codeFile = Path.Combine(Path.GetDirectoryName(SettingsPath(configuration))!, "setup-code.txt");
        var codeConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Yunsevo:ApiKeyFile"] = codeFile }).Build();
        var (code, _) = ServerCredentials.Load(codeConfiguration);
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        await using var app = builder.Build();
        var gate = new SemaphoreSlim(1);
        var complete = false;
        var attempts = new Queue<DateTimeOffset>();
        bool Authorized(HttpContext context)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}") return false;
            return CryptographicOperations.FixedTimeEquals(codeHash, SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Yunsevo-Setup-Code"].ToString())));
        }
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            try
            {
                if (context.Request.Path.StartsWithSegments("/api/setup") && context.Request.Path != "/api/setup/status")
                {
                    lock (attempts)
                    {
                        while (attempts.TryPeek(out var time) && time < DateTimeOffset.UtcNow.AddMinutes(-1)) attempts.Dequeue();
                        if (attempts.Count >= 12) throw new StoreException(429, "尝试过于频繁，请一分钟后重试。");
                        if (!Authorized(context)) { attempts.Enqueue(DateTimeOffset.UtcNow); throw new StoreException(403, "安装码不正确，请读取服务端程序目录中的 setup-code.txt。"); }
                    }
                }
                if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/setup")) throw new StoreException(503, "服务端尚未初始化，请先在网页中完成安装。");
                await next(context);
            }
            catch (StoreException ex) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new ApiError(ex.Message), ApiJson.Default.ApiError); }
            catch (JsonException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new ApiError("安装信息格式不正确，请完整填写表单。"), ApiJson.Default.ApiError); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new ApiError("无法完成安装，请检查存储路径是否为空、磁盘是否可写以及程序目录权限。"), ApiJson.Default.ApiError); }
        });
        WebInterface.Map(app, installation: true);
        app.MapGet("/health", () => Results.Text("Yunsevo setup required"));
        app.MapGet("/api/setup/status", () => Results.Json(new InstallationStatus(!complete), InstallationJson.Default.InstallationStatus));
        app.MapGet("/api/setup/defaults", () => Results.Json(new InstallationDefaults(path), InstallationJson.Default.InstallationDefaults));
        app.MapPost("/api/setup/complete", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(InstallationJson.Default.InstallationRequest, context.RequestAborted) ?? throw new StoreException(400, "安装信息为空。");
            await gate.WaitAsync(context.RequestAborted);
            try
            {
                if (complete) throw new StoreException(409, "安装已完成，请进入登录页。");
                var username = request.Username.Trim();
                ValidateAccount(username, request.Password, request.ConfirmPassword);
                var chosen = ValidateDirectory(request.DataPath, configuration);
                Save(configuration, chosen, false);
                using (var store = new CloudStore(chosen, key, username, request.Password)) { }
                Save(configuration, chosen, true);
                complete = true;
                try { File.Delete(codeFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                // Stop only after the successful response reaches the browser; the same process then starts the full server.
                context.Response.OnCompleted(() =>
                {
                    _ = Task.Run(async () => { await Task.Delay(500); app.Lifetime.StopApplication(); });
                    return Task.CompletedTask;
                });
                return Results.Json(new InstallationStatus(false), InstallationJson.Default.InstallationStatus);
            }
            finally { gate.Release(); }
        });
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            Console.WriteLine("首次安装：请在浏览器中打开服务端地址，设置管理员与存储目录。");
            Console.WriteLine("安装码文件：" + codeFile);
        });
        await app.RunAsync();
        gate.Dispose();
        return complete;
    }
}
