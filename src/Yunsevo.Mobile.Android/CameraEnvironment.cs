using System.Globalization;
using Yunsevo.Contracts;
using Yunsevo.Mobile.Core;

namespace Yunsevo.Mobile.Android;

public sealed partial class CameraActivity
{
    private CancellationTokenSource? environmentRequest;
    private CaptureEnvironment? environmentValue;
    private DateTimeOffset environmentAttempt;
    private double environmentLatitude, environmentLongitude;
    private bool environmentLoading;
    private string? environmentError;
    private bool NeedsLocation => options != null && (options.IncludeLocation || options.IncludeAddress || options.IncludeWeather);
    private CaptureContext EnrichContext(DateTimeOffset time, double? latitude, double? longitude, string? coordinates)
    {
        CaptureEnvironment? value = null;
        if (latitude.HasValue && longitude.HasValue && (options.IncludeAddress || options.IncludeWeather))
        {
            var nearby = Math.Abs(latitude.Value - environmentLatitude) < .001 && Math.Abs(longitude.Value - environmentLongitude) < .001;
            if (nearby && environmentValue?.RetrievedUtc > time.AddMinutes(-15)) value = environmentValue;
            if (!paused && !environmentLoading && (!nearby || time - environmentAttempt > TimeSpan.FromMinutes(value == null ? 1 : 5)))
                _ = RefreshEnvironment(latitude.Value, longitude.Value);
        }
        var lines = new List<string> { MobileRepository.Watermark(Path.GetFileName(project.Path), photographer, time, options, options.IncludeLocation ? coordinates : null) };
        if (options.IncludeAddress)
            lines.Add("地址：" + (value?.Address is { Length: > 0 } address ? address + " · " + value.AddressSource : latitude == null ? "等待定位" : value?.AddressError ?? environmentError ?? "查询中"));
        if (options.IncludeWeather)
        {
            var weather = value?.Weather;
            if (weather != null && weather.DataUtc > time.AddHours(-3))
                lines.Add(FormattableString.Invariant($"天气：{weather.Description} {weather.TemperatureC:0.#}°C · {(weather.Forecast ? "预报" : "观测")} {weather.DataUtc.ToLocalTime():HH:mm} · {weather.Source}"));
            else lines.Add("天气：" + (latitude == null ? "等待定位" : value?.WeatherError ?? environmentError ?? "查询中"));
        }
        return new(time, Guid.NewGuid(), options, string.Join("\n", lines), latitude, longitude,
            value?.Address ?? (options.IncludeLocation ? coordinates : null), value);
    }
    private async Task RefreshEnvironment(double latitude, double longitude)
    {
        environmentLoading = true; environmentAttempt = DateTimeOffset.UtcNow;
        environmentLatitude = latitude; environmentLongitude = longitude; environmentValue = null; environmentError = null;
        environmentRequest?.Dispose(); environmentRequest = new(TimeSpan.FromSeconds(35));
        try
        {
            var value = await AppServices.Client.CaptureEnvironment(new(latitude, longitude, options.IncludeAddress, options.IncludeWeather), environmentRequest.Token);
            if (AppServices.Client.AccountKey == account && !paused) environmentValue = value;
        }
        catch (OperationCanceledException) { environmentError = "查询未完成"; }
        catch (Exception) { environmentError = "暂不可用，请检查服务端配置或网络"; }
        finally { environmentLoading = false; }
    }
}
