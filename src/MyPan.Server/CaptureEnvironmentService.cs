using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyPan.Contracts;

namespace MyPan.Server;

internal sealed record StoredEnvironment(EnvironmentSettings Settings, string? EncryptedKey);
[JsonSerializable(typeof(StoredEnvironment))]
internal partial class EnvironmentStorageJson : JsonSerializerContext;

// Provider credentials stay on the server. All callers share bounded caches and upstream throttling.
public sealed partial class CaptureEnvironmentService : IDisposable
{
    private readonly CloudStore store;
    private readonly string settingsFile, keyFile;
    private readonly object sync = new();
    private readonly HttpClient http;
    private StoredEnvironment current;
    private int revision;
    private sealed record JsonCache(byte[] Bytes, DateTimeOffset Expires, DateTimeOffset? Modified);
    private readonly Dictionary<string, JsonCache> responses = [];
    private readonly Dictionary<string, (CaptureEnvironment Value, DateTimeOffset Expires)> results = [];
    private readonly Dictionary<Guid, (DateTimeOffset Start, int Count)> callers = [];
    private readonly SemaphoreSlim upstream = new(1);
    private DateTimeOffset nextRequest;
    public CaptureEnvironmentService(CloudStore store, string dataPath)
    {
        this.store = store; settingsFile = Path.Combine(dataPath, "environment-settings.json"); keyFile = Path.Combine(dataPath, "environment.key");
        current = File.Exists(settingsFile) ? JsonSerializer.Deserialize(File.ReadAllBytes(settingsFile), EnvironmentStorageJson.Default.StoredEnvironment)
            ?? throw new InvalidDataException("环境服务配置为空。") : new(new(), null);
        http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true, MaxAutomaticRedirections = 3, ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(30) })
            { Timeout = TimeSpan.FromSeconds(8) };
    }
    public EnvironmentSettings Settings(RequestActor actor)
    {
        store.RequireAdmin(actor);
        lock (sync) return current.Settings with { HasAmapKey = current.EncryptedKey != null, AmapKey = null, ClearAmapKey = false };
    }
    public EnvironmentSettings Save(RequestActor actor, EnvironmentSettings request)
    {
        store.RequireAdmin(actor);
        if (request.Contact == null || request.PhotonServer == null || request.AddressProvider is not ("photon" or "amap" or "disabled") || request.WeatherProvider is not ("met-no" or "amap" or "disabled") ||
            request.CacheMinutes is < 1 or > 60 || request.Contact.Length > 200 || request.Contact.Any(char.IsControl))
            throw new StoreException(400, "环境服务配置无效。");
        static Uri SecureUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new StoreException(400, "服务地址须使用 HTTPS，本机测试允许 HTTP。");
            return uri;
        }
        _ = SecureUrl(request.PhotonServer); _ = SecureUrl(request.Contact);
        if (request.AmapKey != null && (request.AmapKey.Length is < 16 or > 128 || request.AmapKey.Any(c => !char.IsAsciiLetterOrDigit(c))))
            throw new StoreException(400, "高德 Web 服务 Key 格式无效。");
        lock (sync)
        {
            var encrypted = request.ClearAmapKey ? null : request.AmapKey == null ? current.EncryptedKey : Encrypt(request.AmapKey);
            current = new(request with { AmapKey = null, ClearAmapKey = false, HasAmapKey = false }, encrypted);
            AtomicFile.WriteText(settingsFile, JsonSerializer.Serialize(current, EnvironmentStorageJson.Default.StoredEnvironment));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(settingsFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            revision++; responses.Clear(); results.Clear(); return Settings(actor);
        }
    }
    public Dictionary<string, byte[]> BackupFiles(RequestActor actor)
    {
        store.RequireAdmin(actor);
        lock (sync) return new[] { settingsFile, keyFile }.Where(File.Exists).ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
    }
    private byte[] Key()
    {
        if (!File.Exists(keyFile))
        {
            using var file = new FileStream(keyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(RandomNumberGenerator.GetBytes(32)); file.Flush(true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return File.ReadAllBytes(keyFile);
    }
    private string Encrypt(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var clear = Encoding.UTF8.GetBytes(value); var cipher = new byte[clear.Length]; var tag = new byte[16];
        using var aes = new AesGcm(Key(), 16); aes.Encrypt(nonce, clear, cipher, tag); return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }
    private string? Decrypt(string? value)
    {
        if (value == null) return null;
        var bytes = Convert.FromBase64String(value); var clear = new byte[bytes.Length - 28];
        using var aes = new AesGcm(Key(), 16); aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), clear); return Encoding.UTF8.GetString(clear);
    }
    public async Task<CaptureEnvironment> Resolve(RequestActor actor, EnvironmentQuery query, CancellationToken ct)
    {
        if (!double.IsFinite(query.Latitude) || !double.IsFinite(query.Longitude) || query.Latitude is < -90 or > 90 || query.Longitude is < -180 or > 180)
            throw new StoreException(400, "定位坐标无效。");
        EnvironmentSettings settings; string? amapKey; string cacheKey;
        lock (sync)
        {
            settings = current.Settings;
            if (!settings.Enabled) return new(null, null, null, DateTimeOffset.UtcNow, AddressError: "服务器未开启地址服务。", WeatherError: "服务器未开启天气服务。");
            cacheKey = revision + "/" + Coordinate(query.Latitude, 4) + "/" + Coordinate(query.Longitude, 4) + "/" + query.Address + "/" + query.Weather;
            if (results.TryGetValue(cacheKey, out var found) && found.Expires > DateTimeOffset.UtcNow) return found.Value;
            var limit = callers.GetValueOrDefault(actor.Id);
            if (limit.Start < DateTimeOffset.UtcNow.AddMinutes(-1)) limit = (DateTimeOffset.UtcNow, 0);
            if (limit.Count >= 12) throw new StoreException(429, "位置查询过于频繁，请稍后重试。");
            callers[actor.Id] = (limit.Start, limit.Count + 1); amapKey = Decrypt(current.EncryptedKey);
        }
        string? address = null, source = null, attribution = null, addressError = null, weatherError = null, city = null;
        if (query.Address && settings.AddressProvider == "disabled") addressError = "服务端已关闭地址查询。";
        if (query.Weather && settings.WeatherProvider == "disabled") weatherError = "服务端已关闭天气查询。";
        WeatherReading? weather = null;
        if ((query.Address && settings.AddressProvider == "amap") || (query.Weather && settings.WeatherProvider == "amap"))
        {
            try
            {
                if (amapKey == null) throw new IOException("请在服务端配置高德 Web 服务 Key。");
                var gps = Coordinate(query.Longitude, 6) + "," + Coordinate(query.Latitude, 6);
                using var conversion = await Fetch("https://restapi.amap.com/v3/assistant/coordinate/convert?coordsys=gps&locations=" + Uri.EscapeDataString(gps) + "&key=" + amapKey, settings, TimeSpan.FromHours(1), ct);
                AmapOk(conversion.RootElement);
                var location = conversion.RootElement.GetProperty("locations").GetString()!;
                using var geocode = await Fetch("https://restapi.amap.com/v3/geocode/regeo?extensions=base&location=" + Uri.EscapeDataString(location) + "&key=" + amapKey, settings, TimeSpan.FromMinutes(30), ct);
                AmapOk(geocode.RootElement);
                var detail = geocode.RootElement.GetProperty("regeocode"); city = Text(detail.GetProperty("addressComponent"), "adcode");
                if (query.Address && settings.AddressProvider == "amap")
                { address = Text(detail, "formatted_address"); source = "高德地图"; attribution = "地址数据：高德地图"; }
            }
            catch (Exception ex) when (ProviderError(ex, ct))
            { addressError = query.Address ? Message(ex) : null; weatherError = query.Weather && settings.WeatherProvider == "amap" ? Message(ex) : null; }
        }
        if (query.Address && settings.AddressProvider == "photon")
        {
            try
            {
                using var data = await Fetch(settings.PhotonServer.TrimEnd('/') + "/reverse?lat=" + Coordinate(query.Latitude, 4) + "&lon=" + Coordinate(query.Longitude, 4) + "&limit=1", settings, TimeSpan.FromMinutes(30), ct);
                var feature = data.RootElement.GetProperty("features").EnumerateArray().FirstOrDefault();
                if (feature.ValueKind == JsonValueKind.Undefined) throw new IOException("没有找到附近地址。");
                var properties = feature.GetProperty("properties");
                address = string.Join(" ", new[] { "country", "state", "city", "district", "locality", "street", "housenumber", "name" }.Select(x => Text(properties, x)).Where(x => x != null).Distinct());
                source = "Photon · 附近地址"; attribution = "© OpenStreetMap contributors · ODbL";
            }
            catch (Exception ex) when (ProviderError(ex, ct)) { addressError = Message(ex); }
        }
        if (query.Weather && settings.WeatherProvider != "disabled" && weatherError == null)
        {
            try
            {
                if (settings.WeatherProvider == "amap")
                {
                    if (city == null || amapKey == null) throw new IOException("无法确定天气行政区。");
                    using var data = await Fetch("https://restapi.amap.com/v3/weather/weatherInfo?extensions=base&city=" + Uri.EscapeDataString(city) + "&key=" + amapKey, settings, TimeSpan.FromMinutes(15), ct);
                    AmapOk(data.RootElement); var live = data.RootElement.GetProperty("lives").EnumerateArray().FirstOrDefault();
                    if (live.ValueKind == JsonValueKind.Undefined) throw new IOException("当前区域没有天气数据。");
                    var date = DateTime.SpecifyKind(DateTime.ParseExact(Text(live, "reporttime")!, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
                    weather = new(Text(live, "weather") ?? "天气", Number(live, "temperature_float", "temperature"), new DateTimeOffset(date, TimeSpan.FromHours(8)).ToUniversalTime(),
                        "高德天气", "https://lbs.amap.com/api/webservice/guide/api/weatherinfo", "天气数据：高德地图", Humidity: Number(live, "humidity_float", "humidity"));
                }
                else weather = await MetWeather(query, settings, ct);
                if (weather.DataUtc < DateTimeOffset.UtcNow.AddHours(-3) || weather.DataUtc > DateTimeOffset.UtcNow.AddHours(2))
                { weather = null; weatherError = "天气数据已过期，请稍后刷新。"; }
            }
            catch (Exception ex) when (ProviderError(ex, ct)) { weatherError = Message(ex); }
        }
        var value = new CaptureEnvironment(Trim(address, 240), source, weather, DateTimeOffset.UtcNow, attribution, addressError, weatherError);
        lock (sync)
        {
            if (results.Count >= 512) results.Remove(results.Keys.First());
            results[cacheKey] = (value, DateTimeOffset.UtcNow.AddMinutes(weatherError != null || addressError != null ? 1 : settings.CacheMinutes));
        }
        return value;
    }
    private async Task<JsonDocument> Fetch(string url, EnvironmentSettings settings, TimeSpan ttl, CancellationToken ct)
    {
        await upstream.WaitAsync(ct);
        try
        {
            JsonCache? saved;
            lock (sync) responses.TryGetValue(url, out saved);
            if (saved?.Expires > DateTimeOffset.UtcNow) return JsonDocument.Parse(saved.Bytes);
            var wait = nextRequest - DateTimeOffset.UtcNow; if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            nextRequest = DateTimeOffset.UtcNow.AddSeconds(1);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Yunsevo/1.1 (+" + settings.Contact + ")");
            if (saved?.Modified != null) request.Headers.IfModifiedSince = saved.Modified;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var expires = response.Content.Headers.Expires ?? DateTimeOffset.UtcNow.Add(ttl);
            if (expires < DateTimeOffset.UtcNow.AddSeconds(30)) expires = DateTimeOffset.UtcNow.AddSeconds(30);
            if (response.StatusCode == HttpStatusCode.NotModified && saved != null)
            { lock (sync) responses[url] = saved with { Expires = expires }; return JsonDocument.Parse(saved.Bytes); }
            if (!response.IsSuccessStatusCode) throw new IOException("服务暂不可用（HTTP " + (int)response.StatusCode + "）。");
            await using var stream = await response.Content.ReadAsStreamAsync(ct); using var memory = new MemoryStream(); var buffer = new byte[16384]; int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
            { if (memory.Length + read > 1024 * 1024) throw new IOException("服务响应过大。"); memory.Write(buffer, 0, read); }
            var bytes = memory.ToArray(); var document = JsonDocument.Parse(bytes);
            lock (sync)
            {
                if (responses.Count >= 512) responses.Remove(responses.Keys.First());
                responses[url] = new(bytes, expires, response.Content.Headers.LastModified);
            }
            return document;
        }
        finally { upstream.Release(); }
    }
    private static bool ProviderError(Exception ex, CancellationToken ct) => ex is HttpRequestException or IOException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException ||
        ex is OperationCanceledException && !ct.IsCancellationRequested;
    private static string Message(Exception ex) => ex is IOException ? Trim(ex.Message, 160)! : "查询暂不可用，请检查服务端配置或稍后重试。";
    private static string Coordinate(double value, int places) => Math.Round(value, places).ToString("F" + places, CultureInfo.InvariantCulture);
    private static string? Text(JsonElement element, string field) => element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double Number(JsonElement element, string first, string? fallback = null) =>
        double.Parse(Text(element, first) ?? (fallback == null ? null : Text(element, fallback)) ?? throw new IOException("服务未返回有效数值。"), CultureInfo.InvariantCulture);
    private static string? Trim(string? value, int maximum) => value == null ? null : value[..Math.Min(value.Length, maximum)];
    private static void AmapOk(JsonElement element)
    { if (Text(element, "status") != "1") throw new IOException("高德服务返回：" + Trim(Text(element, "info") ?? "未知错误", 80)); }
    private async Task<WeatherReading> MetWeather(EnvironmentQuery query, EnvironmentSettings settings, CancellationToken ct)
    {
        using var data = await Fetch("https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=" + Coordinate(query.Latitude, 2) + "&lon=" + Coordinate(query.Longitude, 2), settings, TimeSpan.FromMinutes(15), ct);
        var point = data.RootElement.GetProperty("properties").GetProperty("timeseries").EnumerateArray()
            .OrderBy(x => Math.Abs((DateTimeOffset.Parse(Text(x, "time")!, CultureInfo.InvariantCulture) - DateTimeOffset.UtcNow).TotalMinutes)).First();
        var instant = point.GetProperty("data"); var details = instant.GetProperty("instant").GetProperty("details");
        var period = instant.TryGetProperty("next_1_hours", out var shortTerm) ? shortTerm : instant.GetProperty("next_6_hours");
        var symbol = Text(period.GetProperty("summary"), "symbol_code") ?? "";
        var description = symbol.Contains("thunder") ? "雷雨" : symbol.Contains("snow") ? "降雪" : symbol.Contains("rain") ? "降雨" :
            symbol.Contains("fog") ? "雾" : symbol.StartsWith("clearsky", StringComparison.Ordinal) ? "晴" : symbol.StartsWith("fair", StringComparison.Ordinal) ? "晴间多云" : "多云";
        return new(description, details.GetProperty("air_temperature").GetDouble(), DateTimeOffset.Parse(Text(point, "time")!, CultureInfo.InvariantCulture),
            "MET Norway", "https://api.met.no/", "Weather data: MET Norway · CC BY 4.0", true,
            details.GetProperty("relative_humidity").GetDouble(), details.GetProperty("wind_speed").GetDouble());
    }
    public void Dispose() { http.Dispose(); upstream.Dispose(); }
}
