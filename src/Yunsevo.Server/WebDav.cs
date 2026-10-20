using System.Text;
using System.Xml.Linq;
using Yunsevo.Contracts;

namespace Yunsevo.Server;

public static class WebDav
{
    public static void Map(WebApplication app, CloudStore store, ProtocolManager protocols, long maxUpload)
    {
        app.MapMethods("/dav/{**path}", ["OPTIONS", "PROPFIND", "GET", "HEAD", "PUT", "MKCOL", "DELETE", "MOVE", "COPY"], async (HttpContext context, CancellationToken ct) =>
        {
            if (!protocols.WebDav) throw new StoreException(404, "WebDAV 未启用。");
            var actor = (RequestActor)context.Items["actor"]!; var request = context.Request;
            var path = request.RouteValues["path"]?.ToString()?.Trim('/') ?? ""; if (path.Length > 0) path = CloudPath.Normalize(path);
            var catalog = store.Snapshot(actor); var entry = catalog.Entries.Find(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            context.Response.Headers["DAV"] = "1";
            switch (request.Method)
            {
                case "OPTIONS": context.Response.Headers.Allow = "OPTIONS, PROPFIND, GET, HEAD, PUT, MKCOL, DELETE, MOVE, COPY"; return Results.Ok();
                case "PROPFIND":
                    if (request.Headers["Depth"].ToString() is not ("0" or "1")) throw new StoreException(403, "请使用 Depth: 0 或 1 分页列出目录。");
                    if (path.Length > 0 && entry == null) throw new StoreException(404, "目录不存在。");
                    XNamespace d = "DAV:";
                    var entries = new List<CloudEntry> { entry ?? new(Guid.Empty, "", true, 0, DateTimeOffset.UnixEpoch, "root", actor.IsAdmin ? 7 : 8) };
                    if (request.Headers["Depth"] == "1") entries.AddRange(catalog.Entries.Where(x => (CloudPath.Parent(x.Path) ?? "").Equals(path, StringComparison.OrdinalIgnoreCase)));
                    var xml = new XElement(d + "multistatus", entries.Select(x => new XElement(d + "response",
                        new XElement(d + "href", "/dav/" + string.Join('/', x.Path.Split('/').Select(Uri.EscapeDataString)) + (x.IsDirectory && x.Path.Length > 0 ? "/" : "")),
                        new XElement(d + "propstat", new XElement(d + "prop", new XElement(d + "displayname", x.Path.Split('/').Last()),
                            new XElement(d + "resourcetype", x.IsDirectory ? new XElement(d + "collection") : null),
                            new XElement(d + "getcontentlength", x.Size), new XElement(d + "getlastmodified", x.ModifiedUtc.ToString("R")),
                            new XElement(d + "getetag", '"' + x.Version + '"')), new XElement(d + "status", "HTTP/1.1 200 OK")))));
                    return Results.Text("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + xml.ToString(SaveOptions.DisableFormatting), "application/xml; charset=utf-8", Encoding.UTF8, 207);
                case "GET": case "HEAD":
                    if (entry is not { IsDirectory: false }) throw new StoreException(404, "文件不存在，请用 PROPFIND 列出目录。");
                    var item = await store.Download(actor, entry.Id, null, ct);
                    return Results.File(item.Stream, "application/octet-stream", enableRangeProcessing: true, lastModified: item.Entry.ModifiedUtc, entityTag: new('"' + item.Entry.Version + '"'));
                case "PUT":
                    if (path.Length == 0) throw new StoreException(403, "不能覆盖根目录。");
                    var expected = request.Headers.IfMatch.ToString().Trim('"');
                    await store.Upload(actor, path, request.Body, request.ContentLength, expected.Length > 0 ? expected : entry?.Version,
                        request.Headers.IfNoneMatch == "*" || entry == null, maxUpload, ct); return Results.StatusCode(entry == null ? 201 : 204);
                case "MKCOL": await store.Folder(actor, path); return Results.StatusCode(201);
                case "DELETE":
                    if (entry == null) throw new StoreException(404, "项目不存在。");
                    await store.Delete(actor, entry.Id, entry.Version, catalog.Revision, RequestOrigin.From(context) with { Source = "WebDAV" }); return Results.NoContent();
                case "MOVE": case "COPY":
                    if (entry == null) throw new StoreException(404, "源项目不存在。");
                    if (!Uri.TryCreate(request.Headers["Destination"], UriKind.Absolute, out var destination) || !destination.AbsolutePath.StartsWith("/dav/", StringComparison.Ordinal) ||
                        !destination.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase)) throw new StoreException(400, "Destination 必须指向本服务的 /dav/ 目录。");
                    var target = CloudPath.Normalize(Uri.UnescapeDataString(destination.AbsolutePath[5..]).Trim('/'));
                    if (catalog.Entries.Any(x => x.Path.Equals(target, StringComparison.OrdinalIgnoreCase))) throw new StoreException(412, "目标已存在，请先重命名或删除。");
                    if (request.Method == "MOVE") await store.Move(actor, entry.Id, new(target, entry.Version));
                    else
                    {
                        if (entry.IsDirectory) throw new StoreException(501, "请逐项复制文件，暂不支持递归 COPY。");
                        var content = await store.Download(actor, entry.Id, entry.Version, ct); await using var stream = content.Stream;
                        await store.Upload(actor, target, stream, entry.Size, null, true, maxUpload, ct);
                    }
                    return Results.StatusCode(201);
            }
            return Results.StatusCode(405);
        });
    }
}
