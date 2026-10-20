namespace Yunsevo.Contracts;

public sealed record LoginRequest(string Username, string Password);
public sealed record UserInfo(Guid Id, string Username, string DisplayName, bool IsAdmin, bool Disabled);
public sealed record LoginResult(string AccessToken, DateTimeOffset ExpiresUtc, UserInfo User);
public sealed record SaveUser(string Username, string DisplayName, string? Password, bool IsAdmin = false, bool Disabled = false);
public sealed record GroupInfo(Guid Id, string Name, List<Guid> Members);
public sealed record SaveGroup(string Name, List<Guid> Members);
public sealed record AccessGrant(string Kind, Guid Id, int Access);
// Access: Read=1, Read+Write=3, Read+Write+Manage=7. Traverse-only ancestor=8 in snapshots.
public sealed record FolderPolicy(Guid FolderId, Guid OwnerId, string Visibility, int PublicAccess,
    List<AccessGrant> Grants, long? QuotaBytes = null, int? KeepVersions = null);
public sealed record FolderPolicyInfo(FolderPolicy Policy, long UsedBytes, int EffectiveKeepVersions);
// Category: private, public, assigned, or container (traverse-only, not selectable).
public sealed record SyncFolder(Guid Id, string Path, string Category, int Access);
public sealed record CloudVersion(string Version, long Size, DateTimeOffset ModifiedUtc, bool Current);
public sealed record ChangeNotice(long Revision);
