using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.RateLimiting;
using Yunsevo.Contracts;
using Yunsevo.Server;

Console.OutputEncoding = Encoding.UTF8;
if (!await ServerInstallation.EnsureInstalled(args)) return;
var builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJson.Default);
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
});
builder.Services.AddRateLimiter(options => options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Clamp(builder.Configuration.GetValue("Yunsevo:LoginRequestsPerMinute", 60), 5, 600), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
var (apiKey, keyFilePath) = ServerCredentials.Load(builder.Configuration);
var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + apiKey));
var legacyKey = builder.Configuration.GetValue("Yunsevo:AllowLegacyApiKey", true);
var maxUpload = builder.Configuration.GetValue<long?>("Yunsevo:MaxUploadBytes") ?? 20L * 1024 * 1024 * 1024;
ServerInstallation.ConfigureListener(builder);
var dataPath = ServerInstallation.DataPath(builder.Configuration);
using var store = new CloudStore(dataPath, apiKey);
using var captureEnvironment = new CaptureEnvironmentService(store, dataPath);
var uploads = new UploadSessions(store, dataPath, maxUpload, builder.Configuration.GetValue<long?>("Yunsevo:MaxPendingUploadBytes") ?? 40L * 1024 * 1024 * 1024);
var audit = new AuditLog(Path.Combine(dataPath, "audit"));
await using var protocols = new ProtocolManager(store, audit, dataPath, maxUpload, builder.Configuration["Yunsevo:SmbGatewayPath"]);
var app = builder.Build();
const string CookieName = "yunsevo-session";
RequestActor Actor(HttpContext context) => (RequestActor)context.Items["actor"]!;
string Token(HttpContext context) => context.Request.Headers.Authorization.ToString() is var header && header.StartsWith("Bearer ", StringComparison.Ordinal)
    ? header[7..] : context.Request.Cookies[CookieName] ?? "";
void Cookie(HttpContext context, LoginResult result) => context.Response.Cookies.Append(CookieName, result.AccessToken,
    new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", Expires = result.ExpiresUtc });
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' blob: data:; media-src 'self' blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    try
    {
        if (context.Request.Path != "/health" && context.Request.Path != "/api/auth/login" && context.Request.Path != "/api/auth/device/login" && !WebInterface.IsPublic(context.Request.Path))
        {
            var header = context.Request.Headers.Authorization.ToString();
            var isLegacy = legacyKey && CryptographicOperations.FixedTimeEquals(keyHash, SHA256.HashData(Encoding.UTF8.GetBytes(header)));
            RequestActor? actor;
            var gateway = protocols.GatewayAuthorized(context.Request.Headers["X-Yunsevo-Gateway"].ToString());
            if (context.Request.Path.StartsWithSegments("/api/internal") && !gateway) throw new StoreException(404, "接口不存在。");
            if (gateway)
            {
                actor = context.Request.Path.StartsWithSegments("/api/internal") ? new RequestActor(Guid.Empty, "smb-gateway", false) : store.ProtocolActor(context.Request.Headers["X-Yunsevo-User"].ToString());
                context.Items["source"] = "SMB";
            }
            else if (context.Request.Path.StartsWithSegments("/dav"))
            {
                if (!protocols.WebDav) throw new StoreException(404, "WebDAV 未启用。");
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"Yunsevo WebDAV\", charset=\"UTF-8\"";
                if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) throw new StoreException(401, "请使用云盘用户名和密码。");
                string basic;
                try { basic = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..])); } catch (FormatException) { throw new StoreException(401, "认证格式无效。"); }
                var separator = basic.IndexOf(':'); if (separator < 0) throw new StoreException(401, "认证格式无效。");
                actor = store.PasswordActor(basic[..separator], basic[(separator + 1)..]); context.Items["source"] = "WebDAV";
            }
            else actor = isLegacy ? store.BootstrapActor() : store.Authenticate(Token(context));
            if (actor == null) throw new StoreException(401, "请登录，或检查密钥是否正确。");
            if (!gateway && header.Length == 0 && context.Request.Method is not ("GET" or "HEAD") && context.Request.Headers["X-Yunsevo-Client"] != "web")
                throw new StoreException(403, "此请求需要客户端认证头。");
            context.Items["actor"] = actor;
            foreach (var segment in context.Request.Path.Value!.Split('/'))
                if (Guid.TryParse(segment, out var objectId)) { context.Items["auditLabel"] = store.AuditLabel(objectId); break; }
        }
        if (context.Items["actor"] != null) context.Items["origin"] = RequestOrigin.From(context);
        await next(context);
    }
    catch (StoreException ex)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = ex.Status;
        if ((ex.Status == 413 || context.Request.ContentLength > 0 && ex.Status is 401 or 403 or 507) && context.Request.Protocol.StartsWith("HTTP/1", StringComparison.Ordinal)) context.Response.Headers.Connection = "close";
        await context.Response.WriteAsJsonAsync(new ApiError(ex.Message), ApiJson.Default.ApiError);
    }
    catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == 413)
    {
        context.Response.StatusCode = 413;
        if (context.Request.Protocol.StartsWith("HTTP/1", StringComparison.Ordinal)) context.Response.Headers.Connection = "close";
        await context.Response.WriteAsJsonAsync(new ApiError("超过单文件上传限制。"), ApiJson.Default.ApiError);
    }
    catch (ArgumentException ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new ApiError(ex.Message), ApiJson.Default.ApiError); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { if (!context.Response.HasStarted) context.Response.StatusCode = 499; }
    catch { context.Response.StatusCode = 500; throw; }
    finally
    {
        if ((context.Request.Path == "/api/admin/backup" || context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/internal") && context.Request.Method is not ("GET" or "HEAD") || context.Request.Path.StartsWithSegments("/dav") && context.Request.Method is not ("GET" or "HEAD" or "OPTIONS" or "PROPFIND")))
        {
            var path = context.Request.Path.Value ?? ""; var actor = context.Items["actor"] as RequestActor;
            var source = (context.Items["source"]?.ToString() ?? "Web/API") + " " + context.Connection.RemoteIpAddress;
            var target = path + (context.Request.Query.TryGetValue("path", out var name) ? " · " + name.ToString() : "");
            var origin = context.Items["origin"] as DeletionOrigin;
            audit.Write(new(DateTimeOffset.UtcNow, actor?.Username ?? context.Items["loginUser"]?.ToString() ?? "anonymous", source, context.Request.Method, target, context.Response.StatusCode, context.Items["auditLabel"]?.ToString(), origin?.DeviceName, origin?.RemoteAddress));
        }
    }
});
app.UseRateLimiter();
WebInterface.Map(app);
WebDav.Map(app, store, protocols, maxUpload);
app.MapGet("/health", () => Results.Text("Yunsevo OK"));
app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context) =>
{
    context.Items["loginUser"] = request.Username;
    var result = await store.Login(request); Cookie(context, result); return result;
}).RequireRateLimiting("login");
app.MapPost("/api/auth/session", async (HttpContext context) => { var result = await store.CreateSession(Actor(context)); Cookie(context, result); return result; });
app.MapPost("/api/auth/device/login", async (LoginRequest request, HttpContext context) =>
{
    context.Items["loginUser"] = request.Username;
    return await store.Login(request, device: true);
}).RequireRateLimiting("login");
app.MapPost("/api/auth/device/resume", async (HttpContext context) => { var result = await store.ResumeDevice(Token(context)); return result; });
app.MapPost("/api/auth/logout", async (HttpContext context) => { await store.Logout(Token(context)); context.Response.Cookies.Delete(CookieName); return Results.NoContent(); });
app.MapGet("/api/me", (HttpContext context) => store.Me(Actor(context)));
app.MapGet("/api/admin/users", (HttpContext context) => store.Users(Actor(context)));
app.MapPost("/api/auth/password", async (ChangePassword request, HttpContext context) => { await store.ChangePassword(Actor(context), request); context.Response.Cookies.Delete(CookieName); return Results.NoContent(); }).RequireRateLimiting("login");
app.MapPost("/api/admin/users/{id:guid}/reset-password", async (Guid id, ResetPassword request, HttpContext context) => await store.ResetPassword(Actor(context), id, request));
app.MapGet("/api/admin/users/page", (string? query, int? page, int? pageSize, HttpContext context) => PageWindow.Create(store.Users(Actor(context)).Where(x => string.IsNullOrEmpty(query) || (x.Username + " " + x.DisplayName).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase), page ?? 1, pageSize ?? 20));
app.MapGet("/api/admin/groups/page", (string? query, int? page, int? pageSize, HttpContext context) => PageWindow.Create(store.Groups(Actor(context)).Where(x => string.IsNullOrEmpty(query) || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase), page ?? 1, pageSize ?? 20));
app.MapGet("/api/browser/entries", (string? folder, string? query, bool? trash, string? sort, bool? descending, int? page, int? pageSize, HttpContext context) => store.Browse(Actor(context), folder, query, trash ?? false, sort, descending ?? false, page ?? 1, pageSize ?? 20));
app.MapGet("/api/browser/entry", (string path, HttpContext context) => store.Lookup(Actor(context), path));
app.MapGet("/api/browser/media", (string? folder, int? page, int? pageSize, string? after, HttpContext context) => store.MediaAlbum(Actor(context), folder, page ?? 1, pageSize ?? 24, after));
app.MapGet("/api/browser/media/{id:guid}/neighbors", (Guid id, string? folder, HttpContext context) => store.MediaNeighbors(Actor(context), id, folder));
app.MapGet("/api/media/{id:guid}/thumbnail", async (Guid id, string? version, HttpContext context, CancellationToken ct) =>
{
    var thumbnail = await store.Thumbnail(Actor(context), id, version, ct);
    return Results.File(thumbnail, "image/jpeg", lastModified: File.GetLastWriteTimeUtc(thumbnail));
});
app.MapGet("/api/browser/folders", (string? query, int? page, int? pageSize, HttpContext context) => PageWindow.Create(store.SyncFolders(Actor(context)).Where(x => string.IsNullOrEmpty(query) || x.Path.Contains(query, StringComparison.OrdinalIgnoreCase)), page ?? 1, pageSize ?? 20));
app.MapGet("/api/browser/versions/{id:guid}", (Guid id, int? page, int? pageSize, HttpContext context) => PageWindow.Create(store.Versions(Actor(context), id), page ?? 1, pageSize ?? 20));
app.MapGet("/api/admin/audit/page", (string? action, string? user, string? kind, int? page, int? pageSize, HttpContext context) => { store.RequireAdmin(Actor(context)); return audit.Page(action, user, kind, page ?? 1, pageSize ?? 20); });
app.MapGet("/api/admin/audit", (string? action, string? user, string? kind, HttpContext context) => { store.RequireAdmin(Actor(context)); return audit.Read(action, user, kind); });
app.MapGet("/api/admin/settings", (HttpContext context) => { store.RequireAdmin(Actor(context)); return protocols.Status; });
app.MapGet("/api/admin/environment-settings", (HttpContext context) => captureEnvironment.Settings(Actor(context)));
app.MapPut("/api/admin/environment-settings", (EnvironmentSettings request, HttpContext context) => captureEnvironment.Save(Actor(context), request));
app.MapPost("/api/mobile/environment", (EnvironmentQuery request, HttpContext context, CancellationToken ct) => captureEnvironment.Resolve(Actor(context), request, ct));
app.MapGet("/api/admin/backup", async (HttpContext context, CancellationToken ct) =>
{
    var files = await protocols.CaptureBackupFiles(Actor(context), ct);
    foreach (var file in captureEnvironment.BackupFiles(Actor(context))) files.Add(file.Key, file.Value);
    // Keep pre-existing migration records without loading the retired worker.
    var history = Path.Combine(dataPath, "reconstruction");
    if (Directory.Exists(history)) foreach (var directory in Directory.EnumerateDirectories(history))
    {
        var name = Path.GetFileName(directory); var record = Path.Combine(directory, "job.json");
        if (Guid.TryParseExact(name, "N", out _) && File.Exists(record) && new FileInfo(record).Length <= 256 * 1024)
            files.Add("reconstruction/" + name + "/job.json", await File.ReadAllBytesAsync(record, ct));
    }
    var backup = await store.CreateBackup(Actor(context), apiKey, files, audit, ct);
    return Results.File(backup, "application/zip", "yunsevo-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip");
});
app.MapPut("/api/admin/settings", async (ProtocolSettings request, HttpContext context) => await protocols.Save(Actor(context), request));
app.MapPost("/api/admin/users/{id:guid}/smb-password", (Guid id, HttpContext context) => protocols.ResetSmbPassword(Actor(context), id));
app.MapGet("/api/internal/smb-password", (string username) => protocols.GetSmbPassword(username));
app.MapPost("/api/internal/audit", (AuditEntry request) => { audit.Write(request with { Source = "SMB" }); return Results.NoContent(); });
app.MapPost("/api/admin/users", async (SaveUser request, HttpContext context) => { context.Items["auditLabel"] = request.Username; return await store.SaveUser(Actor(context), null, request); });
app.MapPut("/api/admin/users/{id:guid}", async (Guid id, SaveUser request, HttpContext context) => await store.SaveUser(Actor(context), id, request));
app.MapGet("/api/admin/groups", (HttpContext context) => store.Groups(Actor(context)));
app.MapPost("/api/admin/groups", async (SaveGroup request, HttpContext context) => { context.Items["auditLabel"] = request.Name; return await store.SaveGroup(Actor(context), null, request); });
app.MapPut("/api/admin/groups/{id:guid}", async (Guid id, SaveGroup request, HttpContext context) => await store.SaveGroup(Actor(context), id, request));
app.MapDelete("/api/admin/groups/{id:guid}", async (Guid id, HttpContext context) => { await store.DeleteGroup(Actor(context), id); return Results.NoContent(); });
app.MapGet("/api/principals/users", () => store.Principals());
app.MapGet("/api/principals/groups", () => store.GroupNames());
app.MapGet("/api/entries", (HttpContext context) =>
{
    var result = store.ConditionalSnapshot(Actor(context), context.Request.Query["scope"], context.Request.Headers.IfNoneMatch);
    context.Response.Headers.ETag = result.ETag;
    return result.Catalog == null ? Results.StatusCode(304) : Results.Json(result.Catalog, ApiJson.Default.Catalog);
});
app.MapGet("/api/sync-folders", (HttpContext context) => store.SyncFolders(Actor(context)));
app.MapGet("/api/capabilities", () => new ServerCapabilities(maxUpload, true, UploadSessions.ChunkBytes, true));
app.MapPost("/api/uploads", async (StartUpload request, HttpContext context, CancellationToken ct) => { context.Items["auditLabel"] = request.Path; return await uploads.Begin(Actor(context), request, ct); });
app.MapGet("/api/uploads/{id:guid}", async (Guid id, HttpContext context, CancellationToken ct) => await uploads.Status(Actor(context), id, ct));
app.MapPut("/api/uploads/{id:guid}", async (Guid id, HttpContext context, CancellationToken ct) => await uploads.Chunk(Actor(context), id, context.Request.Headers.ContentRange.ToString(), context.Request.Headers["X-Chunk-SHA256"].ToString(), context.Request.Body, context.Request.ContentLength, ct));
app.MapPost("/api/uploads/{id:guid}/complete", async (Guid id, HttpContext context, CancellationToken ct) => await uploads.Complete(Actor(context), id, ct));
app.MapDelete("/api/uploads/{id:guid}", async (Guid id, HttpContext context, CancellationToken ct) => { await uploads.Cancel(Actor(context), id, ct); return Results.NoContent(); });
app.MapGet("/api/trash", (HttpContext context) => store.Snapshot(Actor(context), true));
app.MapPost("/api/folders", async (CreateFolder request, HttpContext context) => { context.Items["auditLabel"] = request.Path; return await store.Folder(Actor(context), request.Path); });
app.MapGet("/api/folders/{id:guid}/policy", (Guid id, HttpContext context) => store.Policy(Actor(context), id));
app.MapPut("/api/folders/{id:guid}/policy", async (Guid id, FolderPolicy request, HttpContext context) => await store.SavePolicy(Actor(context), id, request));
app.MapDelete("/api/folders/{id:guid}/policy", async (Guid id, HttpContext context) => { await store.InheritPolicy(Actor(context), id); return Results.NoContent(); });
app.MapPut("/api/content", async (HttpContext context, CancellationToken ct) =>
{
    var request = context.Request;
    return await store.Upload(Actor(context), request.Query["path"].ToString(), request.Body, request.ContentLength, request.Headers.IfMatch.ToString().Trim('"'),
        request.Headers.IfNoneMatch == "*", maxUpload, ct, PhotoRequest.Read(context));
});
IResult FileResult((FileStream Stream, CloudEntry Entry) item, string type = "application/octet-stream") => Results.File(item.Stream, type, enableRangeProcessing: true,
    lastModified: item.Entry.ModifiedUtc, entityTag: new('"' + item.Entry.Version + '"'));
app.MapGet("/api/content/{id:guid}", async (Guid id, string? version, HttpContext context, CancellationToken ct) => FileResult(await store.Download(Actor(context), id, version, ct)));
app.MapGet("/api/trash/{id:guid}/content", async (Guid id, string? version, HttpContext context, CancellationToken ct) => FileResult(await store.DownloadTrash(Actor(context), id, version, ct)));
app.MapGet("/api/trash/{id:guid}/preview", async (Guid id, string? version, HttpContext context, CancellationToken ct) =>
{
    var item = await store.DownloadTrash(Actor(context), id, version, ct);
    var type = PreviewTypes.Get(item.Entry.Path);
    if (type == null) { await item.Stream.DisposeAsync(); throw new StoreException(415, "此格式暂不支持预览，请下载后打开。"); }
    return FileResult(item, type);
});
app.MapGet("/api/preview/{id:guid}", async (Guid id, string? version, HttpContext context, CancellationToken ct) =>
{
    var item = await store.Download(Actor(context), id, version, ct);
    var type = PreviewTypes.Get(item.Entry.Path);
    if (type == null) { await item.Stream.DisposeAsync(); throw new StoreException(415, "此格式暂不支持预览，请下载后打开。"); }
    return FileResult(item, type);
});
app.MapGet("/api/entries/{id:guid}/versions", (Guid id, HttpContext context) => store.Versions(Actor(context), id));
app.MapGet("/api/entries/{id:guid}/versions/{version}", async (Guid id, string version, HttpContext context, CancellationToken ct) => FileResult(await store.DownloadVersion(Actor(context), id, version, ct)));
app.MapPost("/api/entries/{id:guid}/versions/{version}/restore", async (Guid id, string version, HttpContext context) =>
    await store.RestoreVersion(Actor(context), id, version, context.Request.Headers.IfMatch.ToString().Trim('"')));
app.MapPatch("/api/entries/{id:guid}", async (Guid id, MoveEntry request, HttpContext context) => await store.Move(Actor(context), id, request));
app.MapDelete("/api/entries/{id:guid}", async (Guid id, HttpContext context) =>
{
    var revision = long.TryParse(context.Request.Headers["X-Catalog-Revision"], out var parsed) ? parsed : (long?)null;
    await store.Delete(Actor(context), id, context.Request.Headers.IfMatch.ToString().Trim('"'), revision, RequestOrigin.From(context)); return Results.NoContent();
});
app.MapPost("/api/trash/{id:guid}/restore", async (Guid id, HttpContext context) => { await store.Restore(Actor(context), id); return Results.NoContent(); });
app.MapDelete("/api/trash/{id:guid}", async (Guid id, HttpContext context) => { await store.Purge(Actor(context), id); return Results.NoContent(); });
app.MapGet("/api/events", async (HttpContext context) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    var revision = long.TryParse(context.Request.Query["after"], out var supplied) ? supplied : -1;
    var token = Token(context);
    while (!context.RequestAborted.IsCancellationRequested)
    {
        var headerHash = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
        var authenticated = legacyKey && CryptographicOperations.FixedTimeEquals(keyHash, headerHash) ? store.BootstrapActor() : store.Authenticate(token);
        if (authenticated == null) break;
        var current = store.Revision;
        if (current != revision)
        {
            revision = current;
            await context.Response.WriteAsync("event: change\ndata: " + JsonSerializer.Serialize(new ChangeNotice(current), ApiJson.Default.ChangeNotice) + "\n\n", context.RequestAborted);
        }
        else await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        try { await store.WaitForChange(revision, context.RequestAborted); } catch (TimeoutException) { }
    }
});
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine(ProductBrand.Product + " 管理网页：" + string.Join(", ", app.Urls));
    Console.WriteLine("使用安装时设置的管理员账号登录；兼容 API 密钥文件：" + keyFilePath);
    Console.WriteLine("服务端数据目录：" + dataPath);
});
await app.StartAsync();
await protocols.Initialize(builder.Configuration["Yunsevo:GatewayServerUrl"] ?? app.Urls.First().Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1"));
await app.WaitForShutdownAsync();
