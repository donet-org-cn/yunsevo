using System.Threading.Channels;

namespace MyPan.Sync;

public sealed class SyncSignals : IDisposable
{
    private readonly Channel<byte> changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly FileSystemWatcher watcher;
    public SyncSignals(string root)
    {
        watcher = new(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes };
        watcher.Created += (_, _) => Notify(); watcher.Changed += (_, _) => Notify(); watcher.Deleted += (_, _) => Notify(); watcher.Renamed += (_, _) => Notify();
        watcher.Error += (_, _) => Notify(); watcher.EnableRaisingEvents = true;
    }
    public void Notify() => changes.Writer.TryWrite(0);
    public async Task Wait(CancellationToken ct)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct); wake.CancelAfter(TimeSpan.FromSeconds(5));
        try { _ = await changes.Reader.ReadAsync(wake.Token); await Task.Delay(200, ct); while (changes.Reader.TryRead(out _)) { } }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
    public void Dispose() => watcher.Dispose();
}
