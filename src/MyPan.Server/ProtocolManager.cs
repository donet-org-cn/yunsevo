using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyPan.Contracts;

namespace MyPan.Server;

public sealed class ProtocolManager(CloudStore store, AuditLog audit, string dataPath, long maxUpload, string? gatewayPath = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly string settingsPath = Path.Combine(dataPath, "protocols.json");
    private readonly string credentialPath = Path.Combine(dataPath, "smb-credentials.json");
    private readonly string encryptionPath = Path.Combine(dataPath, "smb-key.bin");
    private ProtocolSettings settings = new();
    private FtpService? ftp;
    private Process? smb;
    private string? error, baseUrl;
    public string GatewayKey { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public bool GatewayAuthorized(string value) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(value)), SHA256.HashData(Encoding.UTF8.GetBytes(GatewayKey)));
    public bool WebDav => settings.WebDav;
    public ProtocolStatus Status => new(settings, ftp != null, smb is { HasExited: false }, error);
    public async Task Initialize(string url)
    {
        baseUrl = url;
        if (File.Exists(settingsPath)) settings = JsonSerializer.Deserialize(File.ReadAllText(settingsPath), ApiJson.Default.ProtocolSettings) ?? new();
        try { await Start(settings); } catch (Exception ex) { await Stop(); error = ex.Message; }
    }
    public async Task<ProtocolStatus> Save(RequestActor actor, ProtocolSettings proposed)
    {
        store.RequireAdmin(actor);
        if (!IPAddress.TryParse(proposed.ListenAddress, out _) || proposed.FtpPort is < 1 or > 65535 || proposed.SmbPort is < 1 or > 65535 || proposed.FtpPort == proposed.SmbPort)
            throw new StoreException(400, "监听 IP 或端口无效，FTP 和 SMB 端口不能相同。");
        await gate.WaitAsync();
        try
        {
            store.RequireAdmin(actor); var old = settings; await Stop();
            try { await Start(proposed); settings = proposed; AtomicFile.WriteText(settingsPath, JsonSerializer.Serialize(settings, ApiJson.Default.ProtocolSettings)); error = null; }
            catch (Exception ex) { await Stop(); settings = old; try { await Start(old); } catch (Exception rollback) { error = rollback.Message; } throw new StoreException(409, "协议启动失败，配置已回退：" + ex.Message); }
            return Status;
        }
        finally { gate.Release(); }
    }
    private async Task Start(ProtocolSettings config)
    {
        if (config.Ftp) { ftp = new(store, audit, IPAddress.Parse(config.ListenAddress), config.FtpPort, maxUpload); ftp.Start(); }
        if (!config.Smb) return;
        var executable = gatewayPath ?? Path.Combine(AppContext.BaseDirectory, "smb", "MyPan.SmbGateway" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(executable)) throw new IOException("缺少 smb/MyPan.SmbGateway；请发布完整服务端包后启用 SMB。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--server", baseUrl!, "--listen", config.ListenAddress, "--port", config.SmbPort.ToString(), "--staging", Path.Combine(dataPath, "smb-staging") }) start.ArgumentList.Add(arg);
        start.Environment["MYPAN_GATEWAY_KEY"] = GatewayKey;
        start.Environment["MYPAN_GATEWAY_PARENT"] = Environment.ProcessId.ToString();
        smb = Process.Start(start) ?? throw new IOException("无法启动 SMB 网关。");
        // Child stops automatically when parent disappears; no external/system SMB process is modified.
        var ready = await smb.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (ready != "MYPAN_SMB_READY") throw new IOException("SMB 网关启动失败：" + ready);
        _ = Drain(smb.StandardOutput); _ = Drain(smb.StandardError);
    }
    private static async Task Drain(StreamReader reader) { try { while (await reader.ReadLineAsync() != null) { } } catch (IOException) { } }
    private async Task Stop()
    {
        if (ftp != null) { await ftp.DisposeAsync(); ftp = null; }
        if (smb != null) { if (!smb.HasExited) { smb.Kill(true); await smb.WaitForExitAsync(); } smb.Dispose(); smb = null; }
    }
    public async ValueTask DisposeAsync() { await Stop(); gate.Dispose(); }
    private byte[] EncryptionKey()
    {
        if (!File.Exists(encryptionPath))
        {
            using var stream = new FileStream(encryptionPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(RandomNumberGenerator.GetBytes(32)); stream.Flush(true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(encryptionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return File.ReadAllBytes(encryptionPath);
    }
    public SmbCredential ResetSmbPassword(RequestActor actor, Guid id)
    {
        store.RequireAdmin(actor);
        var user = store.Users(actor).Find(x => x.Id == id && !x.Disabled) ?? throw new StoreException(404, "用户不存在或已停用。");
        lock (credentialPath)
        {
            var passwords = LoadCredentials(); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var nonce = RandomNumberGenerator.GetBytes(12); var bytes = Encoding.UTF8.GetBytes(password); var encrypted = new byte[bytes.Length]; var tag = new byte[16];
            using var aes = new AesGcm(EncryptionKey(), 16); aes.Encrypt(nonce, bytes, encrypted, tag);
            passwords.RemoveAll(x => x.Username == user.Username); passwords.Add(new(user.Username, Convert.ToBase64String([.. nonce, .. tag, .. encrypted])));
            AtomicFile.WriteText(credentialPath, JsonSerializer.Serialize(passwords, ApiJson.Default.ListSmbCredential));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(credentialPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new(user.Username, password);
        }
    }
    private List<SmbCredential> LoadCredentials() => File.Exists(credentialPath) ? JsonSerializer.Deserialize(File.ReadAllText(credentialPath), ApiJson.Default.ListSmbCredential) ?? [] : [];
    public async Task<Dictionary<string, byte[]>> CaptureBackupFiles(RequestActor actor, CancellationToken ct)
    {
        store.RequireAdmin(actor); await gate.WaitAsync(ct);
        try
        {
            lock (credentialPath)
            {
                var files = new Dictionary<string, byte[]>();
                foreach (var path in new[] { settingsPath, credentialPath, encryptionPath })
                    if (File.Exists(path)) files.Add(Path.GetFileName(path), File.ReadAllBytes(path));
                return files;
            }
        }
        finally { gate.Release(); }
    }
    public SmbCredential GetSmbPassword(string username)
    {
        var actor = store.ProtocolActor(username);
        lock (credentialPath)
        {
            var stored = LoadCredentials().Find(x => x.Username == actor.Username) ?? throw new StoreException(404, "未启用此用户的 SMB 密码。");
            var bytes = Convert.FromBase64String(stored.Password); var clear = new byte[bytes.Length - 28];
            using var aes = new AesGcm(EncryptionKey(), 16); aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), clear);
            return new(actor.Username, Encoding.UTF8.GetString(clear));
        }
    }
}
