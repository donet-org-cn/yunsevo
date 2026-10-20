using System.Globalization;

namespace Yunsevo.Desktop;

internal sealed record ClientActivity(DateTimeOffset? Time, string Task, string Kind, string Path, string Message, bool Issue)
{
    public bool IsFile => Kind is "upload" or "download" or "trash" or "rename" or "local";
    public string Label => Kind switch { "upload" => "已上传", "download" => "已下载", "trash" => "已删除", "rename" => "已重命名", "local" => "仅本地删除", "issue" => "需要处理", _ => "同步记录" };
    public string Glyph => Kind is "upload" or "download" or "trash" ? Kind : Issue ? "warning" : "log";
    public static ClientActivity Parse(string line)
    {
        DateTimeOffset? time = null; var message = line; var task = "";
        var separator = line.IndexOf("  ", StringComparison.Ordinal);
        if (separator > 0)
        {
            var stamp = line[..separator];
            if (DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var full)) time = full;
            message = line[(separator + 2)..];
        }
        if (message.StartsWith('[') && message.IndexOf("] ", StringComparison.Ordinal) is var end && end > 0) { task = message[1..end]; message = message[(end + 2)..]; }
        var kind = "info"; var path = "";
        foreach (var (prefix, value) in new[] { ("已上传：", "upload"), ("已下载：", "download"), ("已移入云端回收站：", "trash"), ("已同步重命名：", "rename"), ("仅保留本地删除，云端文件保留：", "local") })
            if (message.StartsWith(prefix, StringComparison.Ordinal)) { kind = value; path = message[prefix.Length..]; break; }
        var issue = message.Contains("失败", StringComparison.Ordinal) || message.Contains("待重试", StringComparison.Ordinal) || message.Contains("待处理", StringComparison.Ordinal) || message.Contains("冲突", StringComparison.Ordinal) || message.Contains("已断开", StringComparison.Ordinal) || message.Contains("已失效", StringComparison.Ordinal);
        if (issue) kind = "issue";
        return new(time, task, kind, path, message, issue);
    }
    public string RelativeTime()
    {
        if (Time == null) return "";
        var age = DateTimeOffset.Now - Time.Value;
        return age.TotalMinutes < 1 ? "刚刚" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} 分钟前" : age.TotalDays < 1 ? $"{(int)age.TotalHours} 小时前" : $"{(int)age.TotalDays} 天前";
    }
}
