using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyPan.Contracts;

namespace MyPan.Server;

public sealed partial class CloudStore
{
    private readonly SemaphoreSlim backupGate = new(1);
    public async Task<FileStream> CreateBackup(RequestActor actor, string apiKey, Dictionary<string, byte[]> protocolFiles, AuditLog audit, CancellationToken ct)
    {
        Admin(actor);
        if (!await backupGate.WaitAsync(0, ct)) throw new StoreException(409, "已有迁移备份正在生成，请稍后重试。");
        FileStream? output = null;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            output = new FileStream(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yunsevo-backup-" + Guid.NewGuid().ToString("N") + ".zip"), options);
            await gate.WaitAsync(ct);
            try
            {
                Admin(actor); var snapshot = state; var files = new List<BackupFile>();
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
                {
                    async Task Add(string path, Stream input, long length, CompressionLevel compression)
                    {
                        var entry = zip.CreateEntry(path, compression);
                        await using var destination = entry.Open(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[131072]; var remaining = length;
                        while (remaining > 0)
                        {
                            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                            if (read == 0) throw new IOException("备份源文件不完整：" + path);
                            hash.AppendData(buffer, 0, read); await destination.WriteAsync(buffer.AsMemory(0, read), ct); remaining -= read;
                        }
                        files.Add(new(path, length, Convert.ToHexString(hash.GetHashAndReset())));
                    }
                    async Task AddBytes(string path, byte[] content)
                    { using var input = new MemoryStream(content, false); await Add(path, input, content.Length, CompressionLevel.Fastest); }
                    await AddBytes("data/catalog.json", JsonSerializer.SerializeToUtf8Bytes(snapshot with { Sessions = [] }, StoreJson.Default.StoreState));
                    foreach (var blob in Referenced(snapshot).Order(StringComparer.Ordinal))
                    {
                        if (blob.Contains('/') || blob.Contains('\\')) throw new InvalidDataException("文件版本标识无效，无法生成备份。");
                        await using var input = new FileStream(System.IO.Path.Combine(root, "blobs", blob), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await Add("data/blobs/" + blob, input, input.Length, CompressionLevel.NoCompression);
                    }
                    foreach (var file in protocolFiles) await AddBytes("data/" + file.Key, file.Value);
                    var logs = audit.CaptureBackupFiles();
                    try { foreach (var log in logs) await Add("data/audit/" + log.Name, log.Stream, log.Size, CompressionLevel.Fastest); }
                    finally { foreach (var log in logs) log.Stream.Dispose(); }
                    await AddBytes("api-key.txt", Encoding.UTF8.GetBytes(apiKey + "\n"));
                    await AddBytes("RESTORE.txt", Encoding.UTF8.GetBytes("沄序文件云迁移备份\n请在新机器安装相同或更新的服务端版本。\n使用随项目 scripts/restore-backup.ps1 校验并恢复到一个新目录。\n将 MyPan__DataPath 指向恢复后的 data，将 MyPan__ApiKeyFile 指向恢复后的 api-key.txt。\nTLS 证书、监听地址和环境变量需在新机器单独配置。\n备份含账户哈希与服务密钥，请妥善保存。恢复后所有设备需重新登录。\n"));
                    var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Fastest);
                    await using var manifestStream = manifest.Open();
                    await JsonSerializer.SerializeAsync(manifestStream, new BackupManifest(1, ProductBrand.Product, DateTimeOffset.UtcNow, snapshot.Revision, files), ApiJson.Default.BackupManifest, ct);
                }
            }
            finally { gate.Release(); }
            await output.FlushAsync(ct); output.Position = 0; return output;
        }
        catch { if (output != null) await output.DisposeAsync(); throw; }
        finally { backupGate.Release(); }
    }
}
