using System.Security.Cryptography;
using System.Text;
using Yunsevo.Contracts;

namespace Yunsevo.Server;

internal static class ServerCredentials
{
    public static (string Key, string FilePath) Load(IConfiguration configuration)
    {
        var path = Path.GetFullPath(configuration["Yunsevo:ApiKeyFile"] ?? Path.Combine(AppContext.BaseDirectory, "api-key.txt"));
        var configured = configuration["Yunsevo:ApiKey"];
        if (configured is not null)
        {
            Validate(configured);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path).Trim() != configured)
                AtomicFile.WriteText(path, configured + Environment.NewLine);
            ProtectUnix(path);
            return (configured, path);
        }
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var stream = new FileStream(path, options);
                var bytes = Encoding.UTF8.GetBytes(generated + Environment.NewLine);
                stream.Write(bytes);
                stream.Flush(true);
            }
            catch (IOException) when (File.Exists(path)) { /* Another startup created the key first. */ }
        }
        var key = File.ReadAllText(path).Trim();
        Validate(key);
        ProtectUnix(path);
        return (key, path);
    }

    private static void Validate(string key)
    {
        if (key.Length < 32 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
            throw new InvalidOperationException("API 密钥必须至少 32 个字符，且不能包含空白或控制字符。请检查 api-key.txt 或 Yunsevo__ApiKey 配置；已有密钥不会自动替换。");
    }

    private static void ProtectUnix(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
