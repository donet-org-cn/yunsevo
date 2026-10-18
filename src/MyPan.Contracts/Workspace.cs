namespace MyPan.Contracts;

public static class ProductBrand
{
    public const string Name = "沄序";
    public const string English = "Yunsevo";
    public const string Product = "沄序文件云";
    public const string Desktop = "沄序 Drive";
    public const string Tagline = "文件有序，协作从容";
}
public sealed record DeletionOrigin(string? DeviceName, string Source, string? RemoteAddress);
public sealed record DeletionInfo(DateTimeOffset Time, Guid UserId, string Username, string DisplayName,
    string? DeviceName, string Source, string? RemoteAddress);
public sealed record BrowserEntry(CloudEntry Entry, DeletionInfo? Deleted);
public sealed record EntryLookup(CloudEntry? Entry);
public sealed record PagedResult<T>(List<T> Items, int Total, int Page, int PageSize,
    long Revision = 0, int FolderAccess = 0, long TotalFiles = 0, long TotalBytes = 0);
public sealed record ResetPassword(string? Password = null);
public sealed record PasswordResult(string Username, string Password);
public sealed record ChangePassword(string CurrentPassword, string NewPassword);
public sealed record BackupFile(string Path, long Size, string Sha256);
public sealed record BackupManifest(int FormatVersion, string Product, DateTimeOffset CreatedUtc, long Revision, List<BackupFile> Files);
