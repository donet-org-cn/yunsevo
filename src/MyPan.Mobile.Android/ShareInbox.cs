using Android.Content;
using Android.Provider;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyPan.Contracts;

namespace MyPan.Mobile.Android;

internal sealed record InboxFile(Guid Id, string Name, string MimeType, long Size);
[JsonSerializable(typeof(List<InboxFile>))]
internal partial class InboxJson : JsonSerializerContext;
internal static class ShareInbox
{
    private static readonly SemaphoreSlim gate = new(1);
    private static string DirectoryPath => Path.Combine(AppServices.Context.FilesDir!.AbsolutePath, "inbox");
    private static string ManifestPath => Path.Combine(DirectoryPath, "inbox.json");
    public static List<InboxFile> Read()
    {
        lock (typeof(ShareInbox)) return File.Exists(ManifestPath)
            ? JsonSerializer.Deserialize(File.ReadAllBytes(ManifestPath), InboxJson.Default.ListInboxFile) ?? [] : [];
    }
    public static string FilePath(InboxFile file) => Path.Combine(DirectoryPath, file.Id.ToString("N"));
    public static async Task Import(Context context, IEnumerable<global::Android.Net.Uri> uris, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            foreach (var uri in uris)
            {
                if (!string.Equals(uri.Scheme, "content", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("请通过系统文件选择器或应用的文件分享功能发送已授权文件。");
                string name = "分享文件";
                using (var cursor = context.ContentResolver!.Query(uri, [IOpenableColumns.DisplayName], null, null, null))
                    if (cursor?.MoveToFirst() == true) name = cursor.GetString(0) ?? name;
                var id = Guid.NewGuid(); var target = Path.Combine(DirectoryPath, id.ToString("N"));
                var partial = target + ".partial"; long total = 0;
                try
                {
                    await using var source = context.ContentResolver!.OpenInputStream(uri) ?? throw new IOException("无法读取分享文件。");
                    await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        var maximum = new global::Android.OS.StatFs(DirectoryPath).AvailableBytes - 64L * 1024 * 1024;
                        var buffer = new byte[131072]; int count;
                        while ((count = await source.ReadAsync(buffer, ct)) != 0)
                        {
                            total += count; if (total > maximum) throw new IOException("手机可用空间不足，已保留来源文件。");
                            await output.WriteAsync(buffer.AsMemory(0, count), ct);
                        }
                        output.Flush(true);
                    }
                    File.Move(partial, target);
                    lock (typeof(ShareInbox))
                    {
                        var entries = Read(); entries.Add(new(id, name, context.ContentResolver.GetType(uri) ?? "application/octet-stream", total));
                        AtomicFile.WriteText(ManifestPath, JsonSerializer.Serialize(entries, InboxJson.Default.ListInboxFile));
                    }
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
        }
        finally { gate.Release(); }
    }
    public static void Remove(InboxFile file)
    {
        lock (typeof(ShareInbox))
            AtomicFile.WriteText(ManifestPath, JsonSerializer.Serialize(Read().Where(x => x.Id != file.Id).ToList(), InboxJson.Default.ListInboxFile));
        File.Delete(FilePath(file));
    }
}
