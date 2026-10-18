using MyPan.Contracts;

namespace MyPan.Sync;

// Keep the original observation context across retries. A successful reconnect must
// not turn an offline deletion or a deferred startup decision into an online delete.
public sealed class DeletionSafety : IDisposable
{
    private readonly object gate = new();
    private readonly FileSystemWatcher watcher;
    private readonly Dictionary<string, bool> uncertain = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> onlineChanges = new(StringComparer.OrdinalIgnoreCase);
    private bool online, established;
    public DeletionSafety(string root)
    {
        root = Path.GetFullPath(root);
        watcher = new(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
        void Missing(string path)
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            lock (gate)
            {
                if (online && established && !uncertain.ContainsKey(relative)) onlineChanges.Add(relative);
                else uncertain.TryAdd(relative, !established);
            }
        }
        watcher.Deleted += (_, e) => Missing(e.FullPath);
        watcher.Renamed += (_, e) => Missing(e.OldFullPath);
        watcher.Error += (_, _) => { lock (gate) { established = false; ConnectionChanged(false, false); } };
        watcher.EnableRaisingEvents = true;
    }
    public void ConnectionChanged(bool connected, bool expired)
    {
        lock (gate)
        {
            online = connected && !expired;
            if (!online)
            {
                foreach (var path in onlineChanges) uncertain.TryAdd(path, false);
                onlineChanges.Clear();
            }
        }
    }
    public void CompletePass() { lock (gate) established = true; }
    public DeletionRequest Request(string path, bool directory, bool startup)
    {
        lock (gate)
        {
            var ancestor = uncertain.Keys.FirstOrDefault(x => path.Equals(x, StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(path, x) || directory && CloudPath.IsUnder(x, path));
            if (ancestor != null) return new(path, directory, uncertain[ancestor]);
            if (!startup && established && online) { onlineChanges.Add(path); return new(path, directory, false, true); }
            uncertain[path] = startup;
            return new(path, directory, startup);
        }
    }
    public void Present(IEnumerable<string> paths)
    {
        lock (gate) foreach (var path in paths) { uncertain.Remove(path); onlineChanges.Remove(path); }
    }
    public void Forget(string path)
    {
        lock (gate)
        {
            foreach (var key in uncertain.Keys.Where(x => x.Equals(path, StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(x, path)).ToArray()) uncertain.Remove(key);
            onlineChanges.RemoveWhere(x => x.Equals(path, StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(x, path));
        }
    }
    public void Dispose() => watcher.Dispose();
}
