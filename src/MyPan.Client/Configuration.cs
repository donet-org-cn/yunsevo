using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyPan.Contracts;

namespace MyPan.Client;

public sealed record ClientConfig(string ServerUrl, string ProtectedApiKey, string SyncRoot, bool FilesOnDemand = true,
    bool StartAtLogin = false, string RemoteDirectory = "", string AccountId = "")
{
    public void Validate()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("服务器地址须为 HTTPS，本机测试允许 http://localhost。");
        if (!Path.IsPathFullyQualified(SyncRoot) || Path.GetPathRoot(SyncRoot) == Path.GetFullPath(SyncRoot))
            throw new ArgumentException("请选择专用同步文件夹，不能使用磁盘根目录。");
        if (Secret.Unprotect(ProtectedApiKey).Length < 32) throw new ArgumentException("API 密钥至少为 32 个字符。");
        ArgumentNullException.ThrowIfNull(RemoteDirectory);
        if (RemoteDirectory.Length > 0) CloudPath.Normalize(RemoteDirectory);
        var drive = new DriveInfo(Path.GetPathRoot(SyncRoot)!);
        if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS")
            throw new ArgumentException("同步目录须位于本机 NTFS 固定磁盘。");
    }

    public static ClientConfig Default => new("http://localhost:5080", "", Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MyPan"));

    public static ClientConfig Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize(File.ReadAllText(path), ClientJson.Default.ClientConfig) ?? Default : Default;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteText(path, JsonSerializer.Serialize(this, ClientJson.Default.ClientConfig));
    }

    public string GetStatePath(string configPath)
    {
        // Keep earlier installations working, while preserving a separate baseline for each folder binding.
        var legacy = Path.GetFullPath(configPath) + ".state.json";
        if (File.Exists(legacy))
        {
            var old = JsonSerializer.Deserialize(File.ReadAllText(legacy), ClientJson.Default.SyncState)
                ?? throw new InvalidDataException("原同步状态损坏，请先恢复状态备份。");
            if (old.ServerUrl == ServerUrl.TrimEnd('/') && old.SyncRoot.Equals(Path.GetFullPath(SyncRoot), StringComparison.OrdinalIgnoreCase) &&
                old.RemoteDirectory.Equals(RemoteDirectory, StringComparison.OrdinalIgnoreCase) && old.AccountId == AccountId) return legacy;
        }
        var binding = ServerUrl.TrimEnd('/') + "\n" + RemoteDirectory.ToUpperInvariant() + "\n" + Path.GetFullPath(SyncRoot).ToUpperInvariant() + (AccountId.Length == 0 ? "" : "\nuser:" + AccountId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)))[..24];
        return Path.GetFullPath(configPath) + ".profiles/" + hash + ".state.json";
    }
}

public sealed record SyncState(string ServerUrl, string SyncRoot, List<CloudEntry> Entries,
    string RemoteDirectory = "", Guid? RemoteDirectoryId = null, string AccountId = "")
{
    public List<Guid> LocalOnlyDeletions { get; init; } = [];
    public Guid JournalGeneration { get; init; }
}

[JsonSerializable(typeof(ClientConfig))]
[JsonSerializable(typeof(SyncState))]
internal partial class ClientJson : JsonSerializerContext;

public static partial class Secret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [LibraryImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [LibraryImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [LibraryImport("kernel32.dll")] private static partial nint LocalFree(nint memory);

    private static unsafe byte[] Transform(byte[] input, bool encrypt)
    {
        fixed (byte* bytes = input)
        {
            var blob = new Blob { Size = input.Length, Data = (nint)bytes };
            Blob output;
            var ok = encrypt ? CryptProtectData(ref blob, 0, 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref blob, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { new Span<byte>((void*)output.Data, output.Size).Clear(); LocalFree(output.Data); }
        }
    }
    public static string Protect(string text) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(text), true));
    public static string Unprotect(string text) => string.IsNullOrEmpty(text) ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(text), false));
}
