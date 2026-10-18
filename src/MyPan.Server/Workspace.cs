using System.Net;
using System.Text;
using MyPan.Contracts;

namespace MyPan.Server;

public static class PageWindow
{
    public static PagedResult<T> Create<T>(IEnumerable<T> source, int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new StoreException(400, "页码必须大于 0，每页数量须为 1–100。");
        var list = source.ToList();
        page = Math.Min(page, Math.Max(1, (int)Math.Ceiling(list.Count / (double)pageSize)));
        return new(list.Skip((page - 1) * pageSize).Take(pageSize).ToList(), list.Count, page, pageSize);
    }
}
public static class RequestOrigin
{
    public static DeletionOrigin From(HttpContext context)
    {
        var source = context.Items["source"]?.ToString() ?? (context.Request.Headers["X-MyPan-Client"] == "web" ? "Web" : "Sync/API");
        string? device = null;
        var encoded = context.Request.Headers["X-MyPan-Device"].ToString();
        if (encoded.Length > 0)
        {
            if (encoded.Length > 768) throw new StoreException(400, "设备名称过长。");
            try { device = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded)); }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { throw new StoreException(400, "设备名称编码无效。"); }
            if (device.Length is < 1 or > 128 || device.Any(char.IsControl)) throw new StoreException(400, "设备名称无效。");
        }
        var address = context.Connection.RemoteIpAddress?.ToString();
        if (source == "SMB")
        {
            // Only the authenticated private gateway can set source=SMB.
            address = IPAddress.TryParse(context.Request.Headers["X-MyPan-RemoteAddress"], out var peer) ? peer.ToString() : null;
        }
        return new(device, source, address);
    }
}
public sealed partial class CloudStore
{
    public EntryLookup Lookup(RequestActor actor, string path)
    {
        path = CloudPath.Normalize(path);
        var snapshot = Volatile.Read(ref state);
        var entry = snapshot.Entries.Find(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return new(null);
        var access = Access(snapshot, entry.Path, actor);
        return new((access & 1) == 0 ? null : entry with { Access = access });
    }
    public PagedResult<BrowserEntry> Browse(RequestActor actor, string? folder, string? query, bool trash, string? sort, bool descending, int page, int pageSize)
    {
        var snapshot = Volatile.Read(ref state);
        folder = string.IsNullOrEmpty(folder) ? "" : CloudPath.Normalize(folder);
        query = query?.Trim() ?? "";
        if (query.Length > 2000) throw new StoreException(400, "搜索内容过长。");
        var catalog = Snapshot(snapshot, actor, trash);
        var folderAccess = actor.IsAdmin && actor.WebAdmin ? 7 : 8;
        if (!trash && folder.Length > 0)
        {
            var selected = catalog.Entries.Find(x => x.IsDirectory && x.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)) ?? throw new StoreException(404, "文件夹不存在或无权访问。");
            folderAccess = selected.Access;
        }
        var selectedEntries = catalog.Entries.Where(x => (trash || (query.Length > 0 ? folder.Length == 0 || CloudPath.IsUnder(x.Path, folder) : (CloudPath.Parent(x.Path) ?? "").Equals(folder, StringComparison.OrdinalIgnoreCase))) &&
            (query.Length == 0 || x.Path.Contains(query, StringComparison.OrdinalIgnoreCase)));
        IOrderedEnumerable<CloudEntry> ordered = selectedEntries.OrderByDescending(x => x.IsDirectory);
        if (sort == "size") ordered = descending ? ordered.ThenByDescending(x => x.Size) : ordered.ThenBy(x => x.Size);
        else if (sort == "modified") ordered = descending ? ordered.ThenByDescending(x => x.ModifiedUtc) : ordered.ThenBy(x => x.ModifiedUtc);
        else if (trash && sort == "deleted") ordered = descending ? ordered.ThenByDescending(x => snapshot.Trash.Find(t => t.Entry.Id == x.Id)?.Deleted?.Time) : ordered.ThenBy(x => snapshot.Trash.Find(t => t.Entry.Id == x.Id)?.Deleted?.Time);
        else ordered = descending ? ordered.ThenByDescending(x => x.Path, StringComparer.OrdinalIgnoreCase) : ordered.ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var records = ordered.ThenBy(x => x.Id).Select(x => new BrowserEntry(x, trash ? snapshot.Trash.Find(t => t.Entry.Id == x.Id)?.Deleted : null));
        var result = PageWindow.Create(records, page, pageSize);
        return result with { Revision = snapshot.Revision, FolderAccess = folderAccess, TotalFiles = catalog.Entries.LongCount(x => !x.IsDirectory), TotalBytes = catalog.Entries.Where(x => !x.IsDirectory).Sum(x => x.Size) };
    }
}
