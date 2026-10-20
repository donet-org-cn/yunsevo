using System.Globalization;
using System.Text;

namespace Yunsevo.Server;

// Public downloads are limited to these release artifacts, never arbitrary program or data files.
internal sealed class ClientDownloads(string directory)
{
    private static readonly (string Rid, string Label)[] Platforms =
    [
        ("win-x64", "Windows · x64"), ("win-arm64", "Windows · ARM64"),
        ("linux-x64", "Linux · x64"), ("linux-arm64", "Linux · ARM64"),
        ("osx-x64", "macOS · Intel"), ("osx-arm64", "macOS · Apple Silicon")
    ];

    private static IEnumerable<(string Name, string Label, bool Desktop)> Packages()
    {
        foreach (var (rid, label) in Platforms)
        {
            var extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
            yield return ($"yunsevo-drive-{rid}{extension}", label, true);
            yield return ($"yunsevo-headless-{rid}{extension}", label + " · 无界面", false);
        }
        yield return ("yunsevo-android-arm64.apk", "Android · ARM64", true);
        yield return ("yunsevo-android-x64.apk", "Android · x64", true);
    }

    private FileInfo? Find(string fileName)
    {
        if (!Packages().Any(x => string.Equals(x.Name, fileName, StringComparison.Ordinal))) return null;
        var root = new DirectoryInfo(directory);
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        var file = new FileInfo(Path.Combine(root.FullName, fileName));
        return file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint) ? file : null;
    }

    public IResult Download(string fileName)
    {
        var file = Find(fileName);
        return file is null ? Results.NotFound() : Results.File(file.FullName,
            fileName.EndsWith(".apk", StringComparison.Ordinal) ? "application/vnd.android.package-archive" :
                fileName.EndsWith(".zip", StringComparison.Ordinal) ? "application/zip" : "application/gzip",
            fileDownloadName: fileName, lastModified: file.LastWriteTimeUtc, enableRangeProcessing: true);
    }

    public string Html()
    {
        var html = new StringBuilder();
        foreach (var (name, label, desktop) in Packages())
        {
            var file = Find(name);
            if (file is null) continue;
            var size = (file.Length / 1048576d).ToString("0.#", CultureInfo.InvariantCulture);
            html.Append($"<a class=\"client-download{(desktop ? "" : " headless-download")}\" href=\"/downloads/{name}\" download>")
                .Append("<svg aria-hidden=\"true\"><use href=\"#i-download\"/></svg><span><strong>")
                .Append(name.EndsWith(".apk", StringComparison.Ordinal) ? "沄序手机版" : desktop ? "沄序 Drive" : "沄序无界面客户端")
                .Append($"</strong><small>{label} · {size} MB</small></span><span class=\"download-arrow\" aria-hidden=\"true\">↓</span></a>");
        }
        return html.Length > 0 ? html.ToString() : "<p class=\"field-help\">客户端下载包尚未发布，请联系管理员。</p>";
    }
}
