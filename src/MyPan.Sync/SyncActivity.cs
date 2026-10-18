namespace MyPan.Sync;

public enum DeletionChoice { DeleteCloud, LocalOnly, Defer }
public sealed record DeletionRequest(string Path, bool IsDirectory, bool DetectedAtStartup, bool DetectedWhileOnline = false);
public delegate Task<DeletionChoice> ConfirmDeletion(DeletionRequest request, CancellationToken ct);

// Append-only activity history is shared by GUI and headless clients. No credentials are logged.
public sealed class ActivityLog(string directory)
{
    private readonly object gate = new();
    public string DirectoryPath { get; } = directory;
    public void Write(string message)
    {
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + "  " + message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
            foreach (var old in Directory.EnumerateFiles(DirectoryPath, "*.log").OrderDescending().Skip(30)) File.Delete(old);
        }
    }
    public string Recent() => Directory.Exists(DirectoryPath) ? string.Join(Environment.NewLine,
        Directory.EnumerateFiles(DirectoryPath, "*.log").OrderDescending().Take(2).Reverse().SelectMany(File.ReadLines).TakeLast(150)) : "";
}
