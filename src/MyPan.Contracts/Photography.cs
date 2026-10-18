namespace MyPan.Contracts;

// Identity is filled by the server from the authenticated account, never supplied by a phone.
public sealed record FileIdentity(Guid UserId, string Username, string DisplayName);
public sealed record MobileUploadMetadata(string Kind, DateTimeOffset? CapturedUtc = null,
    Guid? ProjectFolderId = null, string? Project = null, string? Place = null, string? Note = null,
    string? Watermark = null, double? Latitude = null, double? Longitude = null, string? Location = null,
    string? DeviceName = null, Guid? CaptureId = null, CaptureEnvironment? Environment = null);
public sealed record FileProvenance(string Kind, FileIdentity UploadedBy, DateTimeOffset UploadedUtc,
    FileIdentity? Photographer = null, DateTimeOffset? CapturedUtc = null,
    Guid? ProjectFolderId = null, string? Project = null, string? Place = null, string? Note = null,
    string? Watermark = null, double? Latitude = null, double? Longitude = null, string? Location = null,
    string? DeviceName = null, Guid? CaptureId = null, string? ContentSha256 = null, CaptureEnvironment? Environment = null);
