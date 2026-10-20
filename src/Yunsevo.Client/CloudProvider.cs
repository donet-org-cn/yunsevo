using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Yunsevo.Contracts;
using Yunsevo.Sync;
using static Yunsevo.Client.CloudNative;

namespace Yunsevo.Client;

public sealed class CloudProvider : IDisposable
{
    private readonly CloudApi api;
    private readonly Action<string> log;
    private readonly ConcurrentDictionary<(long Transfer, long Request), CancellationTokenSource> requests = new();
    private readonly ConcurrentDictionary<int, Task> tasks = new();
    private readonly CancellationTokenSource lifetime = new();
    private static readonly SemaphoreSlim hydrationSlots = new(TransferLimits.Concurrency);
    private GCHandle context;
    private long connection;
    private int nextTask;
    public string Root { get; }
    internal bool CreatedRegistration { get; private set; }

    public CloudProvider(string root, CloudApi api, Action<string> log, bool updateRegistration = false)
    {
        Root = Path.GetFullPath(root);
        this.api = api;
        this.log = log;
        Directory.CreateDirectory(Root);
        try { Connect(updateRegistration); }
        catch { lifetime.Dispose(); throw; }
    }

    private unsafe void Connect(bool hasState)
    {
        var previous = QueryRegistration(Root);
        if (previous != null)
        {
            if (!previous.IsRoot) throw new InvalidOperationException("本地目录位于另一个云盘同步目录内，请选择云盘目录之外的独立文件夹。");
            var sameBinding = CloudApi.SameRootIdentity(previous.Identity, api.RootIdentity);
            if (previous.ProviderName != "Yunsevo" && !sameBinding) throw new InvalidOperationException("本地目录已被其他云盘占用，请选择独立的空文件夹。");
            if (previous.Identity.Length > 0 && !sameBinding &&
                (hasState || Directory.EnumerateFileSystemEntries(Root).Any()))
                throw new InvalidOperationException("本地目录仍绑定着另一个服务器或云端目录。请恢复原云端目录设置，或为新云端目录选择新的空本地文件夹；原文件已保留。");
        }
        var identity = Encoding.UTF8.GetBytes(api.RootIdentity);
        fixed (char* name = "Yunsevo", version = "1.0")
        fixed (byte* identityBytes = identity)
        {
            var registration = new Registration
            {
                Size = (uint)sizeof(Registration),
                Name = name,
                Version = version,
                RootIdentity = (nint)identityBytes,
                RootIdentityLength = (uint)identity.Length,
                ProviderId = new("64cb5459-3b19-429c-8d1c-700c5f2f7298")
            };
            var policies = new Policies { Size = (uint)sizeof(Policies), Hydration = 2, Population = 3, InSync = 0x100 };
            Check(CfRegisterSyncRoot(Root, registration, policies, previous != null ? 5u : 4u), "注册云盘目录");
            CreatedRegistration = previous == null;
        }
        context = GCHandle.Alloc(this);
        try
        {
            CallbackRegistration* callbacks = stackalloc CallbackRegistration[3];
            callbacks[0] = new() { Type = 0, Callback = &Fetch };
            callbacks[1] = new() { Type = 2, Callback = &Cancel };
            callbacks[2] = new() { Type = -1, Callback = null };
            Check(CfConnectSyncRoot(Root, callbacks, GCHandle.ToIntPtr(context), 4, out connection), "连接云盘目录");
            Check(CfUpdateSyncProviderStatus(connection, 1), "更新云盘状态");
        }
        catch
        {
            if (connection != 0) { _ = CfDisconnectSyncRoot(connection); connection = 0; }
            context.Free();
            throw;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void Fetch(CallbackInfo* info, CallbackParameters* parameters)
    {
        // No exceptions or borrowed native pointers may escape the callback.
        try
        {
            var provider = (CloudProvider)GCHandle.FromIntPtr(info->Context).Target!;
            var identity = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(info->Identity, checked((int)info->IdentityLength)));
            var operation = new OperationInfo
            {
                Size = (uint)sizeof(OperationInfo),
                Type = 0,
                ConnectionKey = info->ConnectionKey,
                TransferKey = info->TransferKey,
                RequestKey = info->RequestKey
            };
            var path = (Marshal.PtrToStringUni(info->VolumeDos) ?? "") + new string(info->NormalizedPath);
            provider.Enqueue(operation, identity, info->FileSize, parameters->Offset, parameters->Length, path);
        }
        catch
        {
            var operation = new OperationInfo
            {
                Size = (uint)sizeof(OperationInfo),
                ConnectionKey = info->ConnectionKey,
                TransferKey = info->TransferKey,
                RequestKey = info->RequestKey
            };
            var failed = new TransferParameters { Size = 40, Status = unchecked((int)0xC000CF01), Offset = parameters->Offset, Length = parameters->Length };
            _ = CfExecute(operation, ref failed);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void Cancel(CallbackInfo* info, CallbackParameters* parameters)
    {
        try
        {
            var provider = (CloudProvider)GCHandle.FromIntPtr(info->Context).Target!;
            if (provider.requests.TryGetValue((info->TransferKey, info->RequestKey), out var cts)) cts.Cancel();
        }
        catch { /* The request may have already finished. */ }
    }

    private void Enqueue(OperationInfo operation, string identity, long fileSize, long offset, long length, string path)
    {
        var taskId = Interlocked.Increment(ref nextTask);
        var task = Task.Run(() => Hydrate(operation, identity, fileSize, offset, length, path));
        tasks[taskId] = task;
        _ = task.ContinueWith(completed => { tasks.TryRemove(taskId, out _); }, TaskScheduler.Default);
    }

    private async Task Hydrate(OperationInfo operation, string identity, long fileSize, long offset, long length, string path)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var key = (operation.TransferKey, operation.RequestKey);
        requests[key] = cts;
        var acquired = false; byte[]? buffer = null;
        long start = offset / 4096 * 4096;
        long end = Math.Min(fileSize, checked(offset + length));
        end = Math.Min(fileSize, checked((end + 4095) / 4096 * 4096));
        try
        {
            var parts = identity.Split('|');
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var id) || !Guid.TryParseExact(parts[1], "N", out _))
                throw new InvalidDataException("占位文件标识无效。");
            if (end <= start) { Transfer(operation, [], start, 0); return; }
            await hydrationSlots.WaitAsync(cts.Token); acquired = true;
            _ = ShellTransfer.Set(path, 4, 0, end - start);
            buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
            var position = start;
            var failures = 0;
            while (position < end)
            {
                try
                {
                    var rangeEnd = Math.Min(end, position + 8L * 1024 * 1024);
                    using var response = await api.Download(id, parts[1], position, rangeEnd - position, cts.Token);
                    await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                    while (position < rangeEnd)
                    {
                        var count = (int)Math.Min(1024 * 1024, rangeEnd - position);
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cts.Token);
                        Transfer(operation, buffer.AsSpan(0, count), position, 0); position += count;
                        _ = CfReportProviderProgress(operation.ConnectionKey, operation.TransferKey, end - start, position - start);
                        _ = ShellTransfer.Set(path, position < end ? 4u : 0u, position - start, end - start);
                    }
                    failures = 0;
                }
                catch (Exception ex) when (!cts.IsCancellationRequested && ++failures <= 3 && (ex is IOException || ex is HttpRequestException { StatusCode: null }))
                { await Task.Delay(250 * failures, cts.Token); }
            }
            log("已下载：" + Path.GetRelativePath(Root, path).Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            TransferFailure(operation, offset, length);
            _ = ShellTransfer.Set(path, 0x12);
            log("按需下载失败：" + ex.Message);
        }
        finally { if (buffer != null) System.Buffers.ArrayPool<byte>.Shared.Return(buffer); if (acquired) hydrationSlots.Release(); requests.TryRemove(key, out _); }
    }

    private static unsafe void Transfer(OperationInfo operation, ReadOnlySpan<byte> bytes, long offset, int status)
    {
        fixed (byte* buffer = bytes)
        {
            var parameters = new TransferParameters
            {
                Size = (uint)sizeof(TransferParameters),
                Buffer = buffer,
                Offset = offset,
                Length = bytes.Length,
                Status = status
            };
            Check(CfExecute(operation, ref parameters));
        }
    }
    private static unsafe void TransferFailure(OperationInfo operation, long offset, long length)
    {
        var parameters = new TransferParameters { Size = 40, Status = unchecked((int)0xC000CF01), Offset = offset, Length = length };
        _ = CfExecute(operation, ref parameters);
    }

    public string LocalPath(string relative)
    {
        CloudPath.Normalize(relative);
        var full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("路径超出同步目录。");
        return full;
    }
    private static Metadata ToMetadata(CloudEntry entry) => new()
    {
        Creation = entry.ModifiedUtc.UtcDateTime.ToFileTimeUtc(),
        Write = entry.ModifiedUtc.UtcDateTime.ToFileTimeUtc(),
        Attributes = entry.IsDirectory ? 0x10u : 0x80u,
        FileSize = entry.Size
    };
    private static byte[] Identity(CloudEntry entry) => Encoding.UTF8.GetBytes($"{entry.Id:D}|{entry.Version}");

    public unsafe void Create(CloudEntry entry)
    {
        var path = LocalPath(entry.Path);
        var identity = Identity(entry);
        fixed (char* name = Path.GetFileName(path))
        fixed (byte* bytes = identity)
        {
            var placeholder = new Placeholder
            {
                Name = name,
                Metadata = ToMetadata(entry),
                Identity = bytes,
                IdentityLength = (uint)identity.Length,
                Flags = entry.IsDirectory ? 3u : 2u
            };
            Check(CfCreatePlaceholders(Path.GetDirectoryName(path)!, ref placeholder, 1, 1, out var processed));
            Check(placeholder.Result);
            if (processed != 1) throw new IOException("未创建占位文件。");
        }
        ShellIntegration.NotifyItem(path);
    }

    public unsafe void Update(CloudEntry entry, bool dehydrate)
    {
        var path = LocalPath(entry.Path);
        var pinned = dehydrate && (Inspect(path).Attributes & 0x80000) != 0;
        using (var handle = new Oplock(path, dehydrate))
        {
            if (pinned) Check(CfSetPinState(handle.Win32, 2, 0, 0));
            try
            {
                var identity = Identity(entry);
                fixed (byte* bytes = identity)
                    Check(CfUpdatePlaceholder(handle.Win32, ToMetadata(entry), bytes, (uint)identity.Length, 0, 0,
                        dehydrate ? 7u : 2u, 0, 0));
            }
            finally { if (pinned) Check(CfSetPinState(handle.Win32, 1, 0, 0)); }
        }
        if (pinned) Pin(entry);
        _ = ShellTransfer.Set(path, 0, 0, 0);
        ShellIntegration.NotifyItem(path);
    }

    public unsafe void PrepareUpload(string path, nint fileHandle, bool placeholder)
    {
        if (!placeholder)
        {
            // Fully present local bytes remain readable. A temporary identity survives interrupted uploads.
            var identity = Encoding.UTF8.GetBytes($"{Guid.NewGuid():D}|{Guid.Empty:N}");
            fixed (byte* bytes = identity)
                Check(CfConvertToPlaceholder(fileHandle, bytes, (uint)identity.Length, 0, 0, 0));
        }
        Check(CfSetInSyncState(fileHandle, 0, 0, 0));
        _ = ShellTransfer.Set(path, 1);
        ShellIntegration.NotifyItem(path);
    }

    public void SetStatus(uint status)
    {
        Check(CfUpdateSyncProviderStatus(connection, status));
        ShellIntegration.NotifyItem(Root);
    }

    public unsafe void MarkUploaded(CloudEntry entry, nint fileHandle, bool alreadyPlaceholder)
    {
        var identity = Identity(entry);
        fixed (byte* bytes = identity)
        {
            if (!alreadyPlaceholder) Check(CfConvertToPlaceholder(fileHandle, bytes, (uint)identity.Length, 1, 0, 0));
            Check(CfUpdatePlaceholder(fileHandle, ToMetadata(entry) with { Attributes = 0 }, bytes, (uint)identity.Length, 0, 0, 2, 0, 0));
        }
        ShellIntegration.NotifyItem(LocalPath(entry.Path));
        _ = ShellTransfer.Set(LocalPath(entry.Path), 0, entry.Size, entry.Size);
    }

    public void AdoptDirectory(CloudEntry entry)
    {
        using var handle = CreateFile(LocalPath(entry.Path), 0x80040100, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        MarkUploaded(entry, handle.DangerousGetHandle(), false);
    }

    public unsafe void MarkPreserved(CloudEntry entry, nint handle, bool placeholder)
    {
        // Bytes have been preserved in a conflict copy. Do not change size or timestamps here.
        var identity = Identity(entry);
        fixed (byte* bytes = identity)
        {
            if (!placeholder) Check(CfConvertToPlaceholder(handle, bytes, (uint)identity.Length, 1, 0, 0));
            else Check(CfSetInSyncState(handle, 1, 0, 0));
        }
    }

    public void Pin(CloudEntry entry)
    {
        using (var handle = new Oplock(LocalPath(entry.Path)))
        {
            Check(CfSetPinState(handle.Win32, 1, 0, 0));
            if (!entry.IsDirectory) Check(CfHydratePlaceholder(handle.Win32, 0, -1, 0, 0));
        }
        _ = ShellTransfer.Set(LocalPath(entry.Path), 0, entry.Size, entry.Size);
        ShellIntegration.NotifyItem(LocalPath(entry.Path));
    }
    public void Free(CloudEntry entry)
    {
        using (var handle = new Oplock(LocalPath(entry.Path), true))
        {
            Check(CfSetPinState(handle.Win32, 2, 0, 0));
            if (!entry.IsDirectory) Check(CfDehydratePlaceholder(handle.Win32, 0, -1, 0, 0));
        }
        _ = ShellTransfer.Set(LocalPath(entry.Path), 0);
        ShellIntegration.NotifyItem(LocalPath(entry.Path));
    }
    public static (bool Placeholder, bool InSync, bool Partial) State(string path)
    {
        var (state, _, _) = Inspect(path);
        return (state != uint.MaxValue && (state & 1) != 0, (state & 8) != 0, (state & 16) != 0);
    }
    public static void Unregister(string root) => Check(CfUnregisterSyncRoot(root));
    public void Dispose()
    {
        lifetime.Cancel();
        // Disconnect stops native callbacks before their GCHandle is released.
        if (connection != 0) { _ = CfDisconnectSyncRoot(connection); connection = 0; }
        Task.WhenAll(tasks.Values).GetAwaiter().GetResult();
        if (context.IsAllocated) context.Free();
        lifetime.Dispose();
    }
}
