using System.Text;
using System.Text.Json;
using MyPan.Contracts;

namespace MyPan.Server;

internal static class PhotoRequest
{
    public static MobileUploadMetadata? Read(HttpContext context)
    {
        var encoded = context.Request.Headers["X-MyPan-Photo"].ToString();
        if (encoded.Length == 0) return null;
        if (encoded.Length > 8192) throw new StoreException(400, "拍摄信息过长。");
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            return JsonSerializer.Deserialize(bytes, ApiJson.Default.MobileUploadMetadata) ?? throw new StoreException(400, "拍摄信息不能为空。");
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new StoreException(400, "拍摄信息编码无效。"); }
    }
}

public sealed partial class CloudStore
{
    internal void ValidatePhoto(RequestActor actor, string path, MobileUploadMetadata? metadata)
    {
        if (metadata == null) return;
        if (metadata.Kind is not ("camera" or "album" or "shared" or "scan" or "model")) throw new StoreException(400, "文件来源无效。");
        void Text(string? value, int maximum, bool multiline = false)
        {
            if (value != null && (value.Length > maximum || value.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t'))))
                throw new StoreException(400, "拍摄信息包含过长或无效的文字。");
        }
        Text(metadata.Project, 160); Text(metadata.Place, 200); Text(metadata.Note, 500, true);
        Text(metadata.Watermark, 1500, true); Text(metadata.Location, 240); Text(metadata.DeviceName, 128);
        if (metadata.Environment is { } environment)
        {
            Text(environment.Address, 240); Text(environment.AddressSource, 80); Text(environment.AddressAttribution, 160);
            Text(environment.AddressError, 160); Text(environment.WeatherError, 160);
            if (environment.Weather is { } weather)
            {
                Text(weather.Description, 80); Text(weather.Source, 80); Text(weather.SourceUrl, 200); Text(weather.Attribution, 160);
                if (!double.IsFinite(weather.TemperatureC) || weather.TemperatureC is < -100 or > 80 ||
                    weather.Humidity is { } humidity && (!double.IsFinite(humidity) || humidity is < 0 or > 100) ||
                    weather.WindSpeed is { } wind && (!double.IsFinite(wind) || wind is < 0 or > 150))
                    throw new StoreException(400, "天气数值无效。");
            }
        }
        if (metadata.CapturedUtc is { } captured && (captured.Year < 1900 || captured > DateTimeOffset.UtcNow.AddDays(1)))
            throw new StoreException(400, "设备上报的拍摄时间无效。");
        if (metadata.Latitude.HasValue != metadata.Longitude.HasValue ||
            metadata.Latitude is { } lat && (!double.IsFinite(lat) || lat is < -90 or > 90) ||
            metadata.Longitude is { } lon && (!double.IsFinite(lon) || lon is < -180 or > 180))
            throw new StoreException(400, "拍摄坐标无效。");
        if (metadata.Kind == "camera" && (metadata.CapturedUtc == null || !metadata.CaptureId.HasValue || metadata.CaptureId.Value == Guid.Empty))
            throw new StoreException(400, "工程照片需要拍摄时间与唯一拍摄编号。");
        if (metadata.Kind == "camera" && (metadata.ProjectFolderId == null || string.IsNullOrWhiteSpace(metadata.Watermark)))
            throw new StoreException(400, "请先选择工程目录并设置照片水印。");
        if (metadata.ProjectFolderId is { } folderId)
        {
            var snapshot = Volatile.Read(ref state);
            var folder = snapshot.Entries.Find(x => x.Id == folderId && x.IsDirectory) ?? throw new StoreException(409, "工程目录已不存在。");
            Require(snapshot, folder.Path, actor, 2);
            if (!CloudPath.IsUnder(path, folder.Path)) throw new StoreException(400, "照片必须保存到选定的工程目录。");
        }
        if (JsonSerializer.SerializeToUtf8Bytes(metadata, ApiJson.Default.MobileUploadMetadata).Length > 6144)
            throw new StoreException(400, "拍摄信息总长度超过限制。");
    }
    private FileProvenance Provenance(RequestActor actor, string path, MobileUploadMetadata? metadata, string? sha256)
    {
        ValidatePhoto(actor, path, metadata);
        var snapshot = Volatile.Read(ref state);
        var user = snapshot.Users.Find(x => x.Id == actor.Id && !x.Disabled) ?? throw new StoreException(401, "账号已失效。");
        var identity = new FileIdentity(user.Id, user.Username, user.DisplayName);
        var project = metadata?.ProjectFolderId is { } id ? snapshot.Entries.Find(x => x.Id == id)?.Path.Split('/')[^1] : metadata?.Project;
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new(metadata?.Kind ?? "file", identity, DateTimeOffset.UtcNow,
            metadata?.Kind is "camera" or "scan" ? identity : null, metadata?.CapturedUtc,
            metadata?.ProjectFolderId, project, Clean(metadata?.Place), Clean(metadata?.Note), metadata?.Watermark,
            metadata?.Latitude, metadata?.Longitude, Clean(metadata?.Location), Clean(metadata?.DeviceName), metadata?.CaptureId, sha256, metadata?.Environment);
    }
}
