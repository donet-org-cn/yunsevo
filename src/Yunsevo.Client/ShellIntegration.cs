using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.Provider;
using Yunsevo.Contracts;

namespace Yunsevo.Client;

public static partial class ShellIntegration
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(int eventId, uint flags, string path, nint other);

    public static void NotifyItem(string path)
    {
        SHChangeNotify(0x2000, 0x2005, Path.GetFullPath(path), 0); // UPDATEITEM, PATHW | FLUSHNOWAIT
    }

    public static void NotifyFolder(string path)
    {
        SHChangeNotify(0x1000, 0x2005, Path.GetFullPath(path), 0); // UPDATEDIR
        NotifyItem(path);
    }
    private static string Id(string root) => "Yunsevo!" + WindowsIdentity.GetCurrent().User!.Value + "!" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..16];
    private static readonly Guid ProviderId = new("64cb5459-3b19-429c-8d1c-700c5f2f7298");
    private static StorageProviderSyncRootInfo? RegisteredInfo(StorageFolder folder)
    {
        var path = folder.Path;
        // A brand / assembly rename must not detach existing placeholders from
        // their provider. Reuse the OS identity and update its displayed name.
        try
        {
            // Query only this folder. Enumerating all providers can touch stale
            // registrations whose backing storage has been removed.
            var existing = StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder);
            // Windows does not round-trip ProviderId on all supported builds.
            // The CFAPI registration is validated by SyncEngine before this call.
            if (CloudNative.QueryRegistration(path) is { IsRoot: true, ProviderName: "Yunsevo" } || existing.DisplayNameResource == ProductBrand.Product)
            {
                // Verify the current user's exact root mapping before reusing
                // an OS identity. A parent root must never be unregistered.
                using var roots = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + existing.Id + @"\UserSyncRoots");
                if (roots?.GetValue(WindowsIdentity.GetCurrent().User!.Value) is string registered &&
                    string.Equals(Path.TrimEndingDirectorySeparator(registered), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
                    return existing;
            }
        }
        catch (Exception ex) when (ex.HResult is unchecked((int)0x80070490) or unchecked((int)0x80070002) or unchecked((int)0x80070003)) { }
        return null;
    }

    public static async Task Register(string root, Action<string>? log = null)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(root));
        var info = RegisteredInfo(folder) ?? new StorageProviderSyncRootInfo { Id = Id(folder.Path) };
        info.Path = folder;
        info.DisplayNameResource = ProductBrand.Product;
        info.IconResource = @"%SystemRoot%\system32\imageres.dll,-1040";
        info.HydrationPolicy = StorageProviderHydrationPolicy.Full;
        info.PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull;
        info.InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime;
        info.HardlinkPolicy = StorageProviderHardlinkPolicy.None;
        info.AllowPinning = true;
        info.Version = "1.0.0";
        info.ProviderId = ProviderId;
        StorageProviderSyncRootManager.Register(info);
        var indexStatus = SearchIndex.Ensure(root);
        if (indexStatus != 0) log?.Invoke($"Windows Search 状态缓存未就绪 (0x{indexStatus:X8})；请检查 Windows Search 服务和该目录的索引设置。");
        NotifyFolder(root);
    }
    public static void Unregister(string root)
    {
        var folder = StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(root)).AsTask().GetAwaiter().GetResult();
        try { StorageProviderSyncRootManager.Unregister(RegisteredInfo(folder)?.Id ?? Id(folder.Path)); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490)) { }
        SearchIndex.RemoveOwned(root);
    }
    public static void SetStartup(string configPath, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var name = "Yunsevo-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath))))[..12];
        if (enabled)
        {
            var executable = Environment.ProcessPath!;
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("开机启动请使用发布后的 Yunsevo.Client.exe。");
            key.SetValue(name, $"\"{executable}\" --config \"{Path.GetFullPath(configPath)}\" --background");
        }
        else key.DeleteValue(name, false);
    }
}
