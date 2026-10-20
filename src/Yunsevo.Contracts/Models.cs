using System.Text.Json.Serialization;

namespace Yunsevo.Contracts;

public sealed record CloudEntry(Guid Id, string Path, bool IsDirectory, long Size,
    DateTimeOffset ModifiedUtc, string Version, int Access = 7, FileProvenance? Origin = null);
public sealed record Catalog(long Revision, List<CloudEntry> Entries);
public sealed record CreateFolder(string Path);
public sealed record MoveEntry(string Path, string Version);
public sealed record ApiError(string Error);
public sealed record ServerCapabilities(long MaxUploadBytes, bool ResumableUploads = false, int UploadChunkBytes = 8388608, bool FileProvenance = false);
public sealed record StartUpload(string Path, long Length, string? Version, bool CreateOnly, string Sha256, MobileUploadMetadata? Metadata = null);
public sealed record UploadStatus(Guid Id, long Length, long Offset, int ChunkBytes, DateTimeOffset ExpiresUtc, CloudEntry? Completed = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Catalog))]
[JsonSerializable(typeof(CloudEntry))]
[JsonSerializable(typeof(CreateFolder))]
[JsonSerializable(typeof(MoveEntry))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(ServerCapabilities))]
[JsonSerializable(typeof(MobileUploadMetadata))]
[JsonSerializable(typeof(FileProvenance))]
[JsonSerializable(typeof(EnvironmentQuery))]
[JsonSerializable(typeof(CaptureEnvironment))]
[JsonSerializable(typeof(EnvironmentSettings))]
[JsonSerializable(typeof(MediaNeighbors))]
[JsonSerializable(typeof(StartUpload))]
[JsonSerializable(typeof(UploadStatus))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResult))]
[JsonSerializable(typeof(UserInfo))]
[JsonSerializable(typeof(List<UserInfo>))]
[JsonSerializable(typeof(SaveUser))]
[JsonSerializable(typeof(GroupInfo))]
[JsonSerializable(typeof(List<GroupInfo>))]
[JsonSerializable(typeof(SaveGroup))]
[JsonSerializable(typeof(FolderPolicy))]
[JsonSerializable(typeof(FolderPolicyInfo))]
[JsonSerializable(typeof(List<SyncFolder>))]
[JsonSerializable(typeof(List<CloudVersion>))]
[JsonSerializable(typeof(ChangeNotice))]
[JsonSerializable(typeof(ProtocolSettings))]
[JsonSerializable(typeof(ProtocolStatus))]
[JsonSerializable(typeof(SmbCredential))]
[JsonSerializable(typeof(List<SmbCredential>))]
[JsonSerializable(typeof(AuditEntry))]
[JsonSerializable(typeof(List<AuditEntry>))]
[JsonSerializable(typeof(PagedResult<BrowserEntry>))]
[JsonSerializable(typeof(PagedResult<UserInfo>))]
[JsonSerializable(typeof(PagedResult<GroupInfo>))]
[JsonSerializable(typeof(PagedResult<AuditEntry>))]
[JsonSerializable(typeof(PagedResult<CloudVersion>))]
[JsonSerializable(typeof(PagedResult<SyncFolder>))]
[JsonSerializable(typeof(ResetPassword))]
[JsonSerializable(typeof(PasswordResult))]
[JsonSerializable(typeof(ChangePassword))]
[JsonSerializable(typeof(EntryLookup))]
[JsonSerializable(typeof(BackupManifest))]
public partial class ApiJson : JsonSerializerContext;

public static class CloudPath
{
    // Use the same portable namespace on Linux servers and Windows clients.
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2000 || path.StartsWith('/') || path.Contains('\\'))
            throw new ArgumentException("路径必须是非空相对路径，使用 / 分隔。");
        foreach (var part in path.Split('/'))
        {
            if (part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new ArgumentException("文件名包含 Windows 不支持的字符。");
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("不能使用 Windows 保留设备名。");
        }
        return path;
    }

    public static string? Parent(string path) => path.LastIndexOf('/') is var i && i >= 0 ? path[..i] : null;
    public static bool IsUnder(string path, string parent) => path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
}
