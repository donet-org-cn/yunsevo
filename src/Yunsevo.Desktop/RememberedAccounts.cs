using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yunsevo.Contracts;
using Yunsevo.Sync;

namespace Yunsevo.Desktop;

internal sealed record RememberedAccount(Guid Id, string Server, string Username, Guid UserId, string Token);
[JsonSerializable(typeof(List<RememberedAccount>))]
internal partial class AccountJson : JsonSerializerContext;

// Passwords are never stored. Windows uses per-user DPAPI; Unix uses authenticated
// encryption with a separate owner-only key and a private credential directory.
internal static class RememberedAccounts
{
    internal static string FilePath(string home) => Path.Combine(home, "credentials", "accounts.bin");
    internal static List<RememberedAccount> Load(string home)
    {
        var file = FilePath(home);
        if (!File.Exists(file)) return [];
        var encrypted = File.ReadAllText(file);
        var json = Unprotect(home, encrypted);
        var records = JsonSerializer.Deserialize(json, AccountJson.Default.ListRememberedAccount) ?? throw new InvalidDataException("登录记录为空。");
        if (records.Any(x => x.Id == Guid.Empty || x.UserId == Guid.Empty || string.IsNullOrEmpty(x.Token)) || records.Select(x => x.Id).Distinct().Count() != records.Count)
            throw new InvalidDataException("登录记录损坏。");
        return records;
    }
    internal static void Save(string home, IEnumerable<RememberedAccount> accounts)
    {
        var file = FilePath(home); var directory = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var payload = Protect(home, JsonSerializer.Serialize(accounts.ToList(), AccountJson.Default.ListRememberedAccount));
        AtomicFile.WriteText(file, payload);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static byte[] Key(string home)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = Path.Combine(home, "credentials", "key.bin");
        if (!File.Exists(path))
        {
            using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            stream.Write(RandomNumberGenerator.GetBytes(32)); stream.Flush(true);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != 32) throw new InvalidDataException("登录加密密钥损坏。");
        return bytes;
    }
    private static string Protect(string home, string value)
    {
#if WINDOWS
        return Yunsevo.Client.Secret.Protect(value);
#else
        var key = Key(home);
        try
        {
            var plain = Encoding.UTF8.GetBytes(value); var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
            using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag); CryptographicOperations.ZeroMemory(plain);
            return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
#endif
    }
    private static string Unprotect(string home, string value)
    {
#if WINDOWS
        return Yunsevo.Client.Secret.Unprotect(value);
#else
        var key = Key(home);
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length < 28) throw new InvalidDataException("登录记录损坏。");
            var plain = new byte[bytes.Length - 28]; using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
            try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
#endif
    }
}

internal sealed class DesktopAccount(RememberedAccount saved)
{
    internal RememberedAccount Saved { get; set; } = saved;
    internal CloudConnection? Connection { get; set; }
    internal LoginResult? Session { get; set; }
    internal string Status { get; set; } = "正在自动连接…";
    internal bool NeedsLogin { get; set; }
    internal DateTimeOffset LastChecked { get; set; }
    internal bool Collapsed { get; set; }
    public override string ToString() => new Uri(Saved.Server).Authority + " · " + Saved.Username;
}
