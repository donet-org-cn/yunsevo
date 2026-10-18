using System.Text.Json;
using MyPan.Contracts;

namespace MyPan.Server;

public sealed class AuditLog(string directory)
{
    private readonly object gate = new();
    private string FileName => Path.Combine(directory, "audit.jsonl");
    public List<(string Name, FileStream Stream, long Size)> CaptureBackupFiles()
    {
        lock (gate)
        {
            var files = new List<(string, FileStream, long)>();
            try
            {
                foreach (var path in Enumerable.Range(0, 6).Select(i => i == 0 ? FileName : FileName + "." + i).Where(File.Exists))
                {
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    files.Add((Path.GetFileName(path), stream, stream.Length));
                }
                return files;
            }
            catch { foreach (var file in files) file.Item2.Dispose(); throw; }
        }
    }
    public void Write(AuditEntry entry)
    {
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(FileName) && new FileInfo(FileName).Length > 20 * 1024 * 1024)
            {
                for (var i = 4; i >= 1; i--)
                { var from = FileName + "." + i; if (File.Exists(from)) File.Move(from, FileName + "." + (i + 1), true); }
                File.Move(FileName, FileName + ".1", true);
            }
            File.AppendAllText(FileName, JsonSerializer.Serialize(entry, ApiJson.Default.AuditEntry) + Environment.NewLine);
        }
    }
    public List<AuditEntry> Read(string? action, string? user, string? kind, int count = 300)
    {
        lock (gate)
        {
            return Enumerable.Range(0, 6).Select(i => i == 0 ? FileName : FileName + "." + i).Where(File.Exists).Reverse()
                .SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize(line, ApiJson.Default.AuditEntry))
                .OfType<AuditEntry>().Where(x => (string.IsNullOrEmpty(action) || x.Action.Contains(action, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(user) || x.User.Contains(user, StringComparison.OrdinalIgnoreCase)))
                .Where(x => kind switch {
                    "trash" => x.Target.Contains("/trash/", StringComparison.Ordinal) || x.Action == "DELETE" && x.Target.Contains("/entries/", StringComparison.Ordinal) || x.Action is "DELE" or "RMD",
                    "accounts" => x.Target.StartsWith("/api/admin/users", StringComparison.Ordinal) || x.Target.StartsWith("/api/admin/groups", StringComparison.Ordinal),
                    "settings" => x.Target.Contains("/policy", StringComparison.Ordinal) || x.Target.Contains("/settings", StringComparison.Ordinal), _ => true })
                .TakeLast(Math.Clamp(count, 1, 1000)).Reverse().ToList();
        }
    }
    public PagedResult<AuditEntry> Page(string? action, string? user, string? kind, int page, int pageSize)
    {
        lock (gate)
        {
            // Page across all retained audit files, rather than paginating a capped
            // 300-record browser snapshot. Rotation still bounds retained storage.
            var records = Enumerable.Range(0, 6).Select(i => i == 0 ? FileName : FileName + "." + i).Where(File.Exists).Reverse()
                .SelectMany(File.ReadLines).Where(x => !string.IsNullOrWhiteSpace(x)).Select(line => JsonSerializer.Deserialize(line, ApiJson.Default.AuditEntry))
                .OfType<AuditEntry>().Where(x => (string.IsNullOrEmpty(action) || x.Action.Contains(action, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(user) || x.User.Contains(user, StringComparison.OrdinalIgnoreCase)))
                .Where(x => kind switch {
                    "trash" => x.Target.Contains("/trash/", StringComparison.Ordinal) || x.Action == "DELETE" && x.Target.Contains("/entries/", StringComparison.Ordinal) || x.Action is "DELE" or "RMD",
                    "accounts" => x.Target.StartsWith("/api/admin/users", StringComparison.Ordinal) || x.Target.StartsWith("/api/admin/groups", StringComparison.Ordinal),
                    "settings" => x.Target.Contains("/policy", StringComparison.Ordinal) || x.Target.Contains("/settings", StringComparison.Ordinal), _ => true }).Reverse();
            return PageWindow.Create(records, page, pageSize);
        }
    }
}
