using Yunsevo.Contracts;

namespace Yunsevo.Server;

public sealed partial class CloudStore
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".heic", ".heif", ".avif", ".mp4", ".mov", ".m4v", ".webm", ".3gp" };
    private List<CloudEntry> VisibleMedia(RequestActor actor, string? folder)
    {
        var snapshot = Volatile.Read(ref state);
        var catalog = Snapshot(snapshot, actor, false);
        folder = string.IsNullOrEmpty(folder) ? "" : CloudPath.Normalize(folder);
        if (folder.Length > 0 && !catalog.Entries.Any(x => x.IsDirectory && x.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)))
            throw new StoreException(404, "相册目录不存在或无权访问。");
        return catalog.Entries.Where(x => !x.IsDirectory && MediaExtensions.Contains(Path.GetExtension(x.Path)) &&
            (folder.Length == 0 || CloudPath.IsUnder(x.Path, folder)))
            .OrderByDescending(x => x.Origin?.CapturedUtc ?? x.ModifiedUtc).ThenBy(x => x.Id).ToList();
    }
    public PagedResult<BrowserEntry> MediaAlbum(RequestActor actor, string? folder, int page, int pageSize, string? after = null)
    {
        var media = VisibleMedia(actor, folder); var total = media.Count; IEnumerable<CloudEntry> selected = media;
        if (!string.IsNullOrEmpty(after))
        {
            long ticks; Guid id;
            try
            {
                if (after.Length > 160) throw new FormatException();
                var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(after)).Split(':');
                if (parts.Length != 2 || !long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ticks) ||
                    ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out id)) throw new FormatException();
            }
            catch (FormatException) { throw new StoreException(400, "相册继续加载标识无效，请刷新相册。"); }
            selected = selected.Where(x => (x.Origin?.CapturedUtc ?? x.ModifiedUtc).UtcTicks < ticks ||
                (x.Origin?.CapturedUtc ?? x.ModifiedUtc).UtcTicks == ticks && x.Id.CompareTo(id) > 0);
        }
        var result = PageWindow.Create(selected.Select(x => new BrowserEntry(x, null)), string.IsNullOrEmpty(after) ? page : 1, pageSize);
        var more = result.Page * result.PageSize < result.Total;
        var last = result.Items.LastOrDefault()?.Entry;
        var next = more && last != null ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            (last.Origin?.CapturedUtc ?? last.ModifiedUtc).UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + last.Id.ToString("N"))) : null;
        return result with { Total = total, Page = Math.Max(1, page), Revision = Volatile.Read(ref state).Revision, NextCursor = next };
    }
    public MediaNeighbors MediaNeighbors(RequestActor actor, Guid id, string? folder)
    {
        var media = VisibleMedia(actor, folder); var index = media.FindIndex(x => x.Id == id);
        if (index < 0) throw new StoreException(404, "照片已移动或没有访问权限，请刷新相册。");
        return new(index == 0 ? null : media[index - 1], media[index], index + 1 == media.Count ? null : media[index + 1]);
    }
}
