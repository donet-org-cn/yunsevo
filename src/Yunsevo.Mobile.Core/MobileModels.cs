using System.Text.Json.Serialization;
using Yunsevo.Contracts;

namespace Yunsevo.Mobile.Core;

public sealed record MobileAccount(string Server, LoginResult Login);
public interface IAccountVault
{
    MobileAccount? Load();
    void Save(MobileAccount account);
    void Clear();
}
public sealed record FolderSelection(Guid Id, string Path, string Category = "assigned");
public sealed record MobileOptions(FolderSelection? BackupFolder = null, FolderSelection? Project = null,
    bool AutoBackup = false, bool WifiOnly = true, bool IncludeVideos = true, bool ChargingOnly = false,
    bool IncludeLocation = false, bool SaveCameraToGallery = true, string Place = "", string Note = "",
    bool PauseUploads = false, string WatermarkStyle = "工程卡片", bool WatermarkBackground = true,
    string WatermarkTextColor = "#FFFFFF", float WatermarkTextScale = 1, bool RecordAudio = true,
    bool IncludeAddress = true, bool IncludeWeather = false);
public sealed record MobileJob(Guid Id, string Account, string LocalFile, string Name, long Length, string Sha256,
    FolderSelection Folder, string Subdirectory, MobileUploadMetadata Metadata, DateTimeOffset CreatedUtc,
    string State = "pending", int Attempts = 0, DateTimeOffset? RetryUtc = null, string? Error = null,
    string? AlbumKey = null, string? AlbumRevision = null, CloudEntry? Completed = null);
public sealed record MobileState(string DeviceId, Dictionary<string, MobileOptions> Options,
    List<MobileJob> Jobs, Dictionary<string, string> AlbumReceipts);
public sealed record AlbumAsset(string Key, string Revision, string Name, long Size, string Album,
    DateTimeOffset? TakenUtc, string MimeType);
public interface IAlbumSource
{
    IAsyncEnumerable<AlbumAsset> Enumerate(CancellationToken ct);
    Task<Stream> Open(AlbumAsset asset, CancellationToken ct);
}
public sealed record TransferProgress(Guid JobId, long Bytes, long Total);
public sealed record CameraDraft(Guid Id, string Account, string Name, FolderSelection Folder, MobileUploadMetadata Metadata, Guid Producer, bool Ready = false, string Extension = ".jpg");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(MobileAccount))]
[JsonSerializable(typeof(MobileState))]
[JsonSerializable(typeof(MobileOptions))]
[JsonSerializable(typeof(MobileJob))]
[JsonSerializable(typeof(CameraDraft))]
public partial class MobileJson : JsonSerializerContext;
