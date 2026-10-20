using System.Text.Json;

namespace Yunsevo.Mobile.Android;

internal sealed record SavedModel(Guid Id, string Name, string File);

// Read existing model files only. Older capture records remain in place and
// are never resumed, rewritten or deleted by the model browser.
internal static class SavedModels
{
    public static List<SavedModel> List()
    {
        var root = Path.Combine(AppServices.Repository.Home, "scans");
        var result = new List<SavedModel>();
        if (!Directory.Exists(root)) return result;
        foreach (var home in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(home), "N", out var id)) continue;
            try
            {
                var record = Path.Combine(home, "session.json");
                if (!File.Exists(record) || new FileInfo(record).Length > 64 * 1024) continue;
                using var json = JsonDocument.Parse(File.ReadAllText(record));
                var value = json.RootElement;
                if (value.GetProperty("Account").GetString() != AppServices.Client.AccountKey) continue;
                var file = Path.Combine(home, "model.glb");
                if (!File.Exists(file)) file = Directory.GetFiles(home, "model-before-*.glb").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
                if (file == null || !File.Exists(file)) continue;
                result.Add(new(id, value.GetProperty("Name").GetString() ?? "3D 模型", file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static SavedModel Get(Guid id) => List().FirstOrDefault(x => x.Id == id) ?? throw new IOException("模型不存在或不属于当前账号。");
}
