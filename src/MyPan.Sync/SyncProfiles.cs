using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyPan.Contracts;

namespace MyPan.Sync;

public sealed record SyncTaskProfile(Guid Id, string Server, string Username, Guid UserId, string Name,
    string Remote, string Local, bool OnDemand, bool Enabled, Guid? RemoteId = null);
public sealed record ClientPreferences(string Server, string Username, List<SyncTaskProfile> Tasks);
[JsonSerializable(typeof(ClientPreferences))]
internal partial class ProfileJson : JsonSerializerContext;

public static class SyncProfiles
{
    public static ClientPreferences Load(string file)
    {
        if (!File.Exists(file)) return new("http://localhost:5080", "admin", []);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var json = doc.RootElement;
        if (json.TryGetProperty("Tasks", out var tasks))
        {
            if (tasks.ValueKind != JsonValueKind.Array) throw new InvalidDataException("同步任务配置损坏，请保留 preferences.json 并恢复备份。");
            var settings = json.Deserialize(ProfileJson.Default.ClientPreferences) ?? throw new InvalidDataException("同步任务配置为空。");
            if (settings.Tasks.Any(x => x == null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Server) || string.IsNullOrWhiteSpace(x.Local)) || settings.Tasks.Select(x => x.Id).Distinct().Count() != settings.Tasks.Count)
                throw new InvalidDataException("同步任务配置缺少必要字段或任务 ID 重复。");
            return settings;
        }
        // Import the previous single-task desktop preferences after a successful
        // login to the same server and username. Never bind them to another account.
        var server = json.GetProperty("Server").GetString() ?? "";
        var user = json.GetProperty("Username").GetString() ?? "";
        var local = json.GetProperty("Local").GetString() ?? "";
        var remote = json.GetProperty("Remote").GetString() ?? "";
        return new(server, user, string.IsNullOrWhiteSpace(local) ? [] :
            [new(Guid.NewGuid(), server, user, Guid.Empty, "原有同步任务", remote, Path.GetFullPath(local), json.GetProperty("OnDemand").GetBoolean(), true)]);
    }
    public static void Save(string file, ClientPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        if (File.Exists(file)) File.Copy(file, file + ".bak", true);
        AtomicFile.WriteText(file, JsonSerializer.Serialize(preferences, ProfileJson.Default.ClientPreferences));
    }
    public static bool SameServer(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    public static bool Matches(SyncTaskProfile task, string server, UserInfo user) => SameServer(task.Server, server) && task.UserId == user.Id;
    // Presentation only. The stored path and state identity must stay unchanged.
    public static string DisplayRemote(string path, string username)
    {
        var personal = "个人/" + username;
        return path.Equals(personal, StringComparison.OrdinalIgnoreCase) ? "个人"
            : path.StartsWith(personal + "/", StringComparison.OrdinalIgnoreCase) ? "个人" + path[personal.Length..] : path;
    }
    public static string StatePath(string home, SyncTaskProfile task)
    {
        // Preserve the previous desktop baseline identity when importing its task.
        var mode = task.OnDemand && OperatingSystem.IsWindows() ? "placeholders" : "full-copy";
        var binding = task.Server.TrimEnd('/') + "\n" + task.UserId + "\n" + task.Remote + "\n" + Path.GetFullPath(task.Local) + "\n" + mode;
        return Path.Combine(home, "states", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding))) + ".json");
    }
    public static void ValidateNew(SyncTaskProfile task, IEnumerable<SyncTaskProfile> all)
    {
        if (task.UserId == Guid.Empty || task.RemoteId == null || string.IsNullOrWhiteSpace(task.Name)) throw new ArgumentException("请登录并选择有效的云端文件夹、填写任务名称。");
        CloudPath.Normalize(task.Remote);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(task.Local));
        if (root == Path.GetPathRoot(root)) throw new ArgumentException("不能使用磁盘根目录同步。");
        foreach (var other in all.Where(x => x.Id != task.Id))
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other.Local));
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (root.Equals(path, comparison) || root.StartsWith(path + Path.DirectorySeparatorChar, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("本地目录与已有任务重叠，请选择彼此独立的目录（包括暂停或其他账户的任务）。");
            if (SameServer(task.Server, other.Server) && task.UserId == other.UserId &&
                (task.Remote.Equals(other.Remote, StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(task.Remote, other.Remote) || CloudPath.IsUnder(other.Remote, task.Remote) || other.Remote.Length == 0))
                throw new ArgumentException("所选云端目录与已有任务重叠，请选择其他目录，或使用已有任务。");
        }
        if (task.OnDemand && !OperatingSystem.IsWindows()) throw new ArgumentException("当前平台使用完整副本同步；Windows 支持按需下载。");
    }
}
