namespace MyPan.Server;

internal static class WebInterface
{
    public static bool IsPublic(PathString path) => path == "/" || path == "/setup" || path == "/api/setup/status" || path == "/assets/app.css" || path == "/assets/app.js" || path == "/assets/admin.js" || path == "/assets/reconstruction.js" || path.StartsWithSegments("/downloads");

    public static void Map(WebApplication app, bool installation = false)
    {
        var downloads = new ClientDownloads(app.Configuration["MyPan:ClientDownloadsPath"] ?? Path.Combine(AppContext.BaseDirectory, "downloads"));
        app.MapGet("/", () =>
        {
            using var stream = OpenResource(installation ? "setup.html" : "index.html");
            using var reader = new StreamReader(stream);
            return Results.Content(reader.ReadToEnd().Replace("<!--CLIENT_DOWNLOADS-->", downloads.Html(), StringComparison.Ordinal), "text/html; charset=utf-8");
        });
        app.MapGet("/setup", () => Results.Redirect("/"));
        if (!installation) app.MapGet("/api/setup/status", () => Results.Json(new InstallationStatus(false), InstallationJson.Default.InstallationStatus));
        app.MapMethods("/downloads/{fileName}", ["GET", "HEAD"], (string fileName) => downloads.Download(fileName));
        app.MapGet("/assets/app.css", () => Resource("app.css", "text/css; charset=utf-8"));
        app.MapGet("/assets/app.js", () => Resource("app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/assets/admin.js", () => Resource("admin.js", "text/javascript; charset=utf-8"));
        app.MapGet("/assets/reconstruction.js", () => Resource("reconstruction.js", "text/javascript; charset=utf-8"));
        if (!installation) app.MapGet("/model-viewer/{name}", (string name, HttpContext context) =>
        {
            if (name is not ("index.html" or "viewer.js" or "viewer.css" or "model-viewer.min.js")) return Results.NotFound();
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; connect-src 'self' blob:; img-src 'self' blob: data:; worker-src blob:; frame-ancestors 'self'; base-uri 'none'";
            var stream = typeof(WebInterface).Assembly.GetManifestResourceStream("MyPan.ModelViewer." + name)!;
            return Results.Stream(stream, name.EndsWith(".js") ? "text/javascript" : name.EndsWith(".css") ? "text/css" : "text/html");
        });
        if (installation) app.MapGet("/assets/setup.js", () => Resource("setup.js", "text/javascript; charset=utf-8"));
    }

    private static Stream OpenResource(string name) =>
        typeof(WebInterface).Assembly.GetManifestResourceStream("MyPan.Server.Web." + name)
            ?? throw new InvalidOperationException("缺少内嵌网页资源。");

    private static IResult Resource(string name, string type) => Results.Stream(OpenResource(name), type);
}
