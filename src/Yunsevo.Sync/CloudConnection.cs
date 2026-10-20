using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Yunsevo.Contracts;

namespace Yunsevo.Sync;

public sealed class CloudConnection : IDisposable
{
    public HttpClient Http { get; }
    public string Protocol { get; private set; } = "尚未连接";
    public event Action<bool, bool>? ConnectionChanged;
    private readonly SemaphoreSlim catalogGate = new(1);
    private readonly Dictionary<string, (Catalog Catalog, string? Tag)> catalogs = new(StringComparer.Ordinal);
    private ServerCapabilities? capabilities;
    public async Task<Catalog> Catalog(string scope, CancellationToken ct)
    {
        await catalogGate.WaitAsync(ct);
        try
        {
            catalogs.TryGetValue(scope, out var cached);
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/entries" + (scope.Length == 0 ? "" : "?scope=" + Uri.EscapeDataString(scope)));
            if (cached.Tag != null) request.Headers.IfNoneMatch.ParseAdd(cached.Tag);
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotModified && cached.Catalog != null) return cached.Catalog;
            await Check(response, ct);
            var result = await response.Content.ReadFromJsonAsync(ApiJson.Default.Catalog, ct) ?? throw new InvalidDataException("目录响应为空。");
            catalogs[scope] = (result, response.Headers.ETag?.ToString()); return result;
        }
        finally { catalogGate.Release(); }
    }
    public async Task<ServerCapabilities> Capabilities(CancellationToken ct) => capabilities ??= await Http.GetFromJsonAsync("api/capabilities", ApiJson.Default.ServerCapabilities, ct) ?? throw new InvalidDataException("服务器能力响应为空。");
    private sealed class ProtocolHandler(Action<Version, bool> connected, Action disconnected) : DelegatingHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(30), EnableMultipleHttp2Connections = true, EnableMultipleHttp3Connections = true, MaxConnectionsPerServer = 8
    })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not { } destination || !AllowedEndpoint(destination)) throw new ArgumentException("服务器地址须使用 HTTPS，或使用局域网 IP 的 HTTP 地址。");
            request.Version = request.RequestUri?.Scheme == "https" ? HttpVersion.Version30 : HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            try { var response = await base.SendAsync(request, ct); connected(response.Version, response.StatusCode == HttpStatusCode.Unauthorized); return response; }
            catch (HttpRequestException) { disconnected(); throw; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { disconnected(); throw; }
        }
    }
    // Cleartext is limited to literal local addresses; redirects remain disabled. Never
    // trust DNS names as private networks, since their resolved addresses can change.
    private static bool AllowedEndpoint(Uri uri)
    {
        if (uri.Scheme == "https") return true;
        if (uri.Scheme != "http") return false;
        if (uri.IsLoopback) return true;
        if (!IPAddress.TryParse(uri.IdnHost, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
            : ip.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }
    public CloudConnection(string url, string? token = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !AllowedEndpoint(uri)) throw new ArgumentException("服务器地址须使用 HTTPS；局域网可用 http://服务器内网IP:端口。");
        Http = new(new ProtocolHandler((version, unauthorized) => { Protocol = "HTTP/" + version; ConnectionChanged?.Invoke(true, unauthorized); }, () => ConnectionChanged?.Invoke(false, false))) { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(30) };
        Http.DefaultRequestHeaders.Add("X-Yunsevo-Device", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName)));
        Http.DefaultRequestHeaders.Add("X-Yunsevo-Client", "sync");
        if (token != null) Http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }
    public async Task<LoginResult> Login(string username, string password, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync("api/auth/device/login", new LoginRequest(username, password), ApiJson.Default.LoginRequest, ct);
        await Check(response, ct);
        var result = await response.Content.ReadFromJsonAsync(ApiJson.Default.LoginResult, ct) ?? throw new InvalidDataException("登录结果为空。");
        Http.DefaultRequestHeaders.Authorization = new("Bearer", result.AccessToken);
        return result;
    }
    public async Task<LoginResult> Resume(CancellationToken ct)
    {
        using var response = await Http.PostAsync("api/auth/device/resume", null, ct);
        await Check(response, ct);
        return await response.Content.ReadFromJsonAsync(ApiJson.Default.LoginResult, ct) ?? throw new InvalidDataException("登录结果为空。");
    }
    public static async Task Check(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            try { detail = JsonSerializer.Deserialize(detail, ApiJson.Default.ApiError)?.Error ?? detail; } catch (JsonException) { }
            throw new HttpRequestException($"服务端返回 {(int)response.StatusCode}: {detail}", null, response.StatusCode);
        }
    }
    public async Task ObserveChanges(Action changed, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await Http.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, ct);
                await Check(response, ct);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                while (await reader.ReadLineAsync(ct) is { } line) if (line.StartsWith("data:", StringComparison.Ordinal)) changed();
                if (!ct.IsCancellationRequested) ConnectionChanged?.Invoke(false, false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (HttpRequestException) { ConnectionChanged?.Invoke(false, false); }
            catch (IOException) { ConnectionChanged?.Invoke(false, false); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
        }
    }
    public void Dispose() => Http.Dispose();
}
