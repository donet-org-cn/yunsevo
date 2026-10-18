using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.Provider;
using MyPan.Contracts;

namespace MyPan.Client;

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
    private static string Id(string root) => "MyPan!" + WindowsIdentity.GetCurrent().User!.Value + "!" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..16];

    public static async Task Register(string root, Action<string>? log = null)
    {
        var info = new StorageProviderSyncRootInfo
        {
            Id = Id(root),
            Path = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(root)),
            DisplayNameResource = ProductBrand.Product,
            IconResource = @"%SystemRoot%\system32\imageres.dll,-1040",
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            AllowPinning = true,
            Version = "1.0.0",
            ProviderId = new("64cb5459-3b19-429c-8d1c-700c5f2f7298")
        };
        StorageProviderSyncRootManager.Register(info);
        var indexStatus = SearchIndex.Ensure(root);
        if (indexStatus != 0) log?.Invoke($"Windows Search 状态缓存未就绪 (0x{indexStatus:X8})；请检查 Windows Search 服务和该目录的索引设置。");
        NotifyFolder(root);
    }
    public static void Unregister(string root)
    {
        StorageProviderSyncRootManager.Unregister(Id(root));
        SearchIndex.RemoveOwned(root);
    }
    public static void SetStartup(string configPath, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var name = "MyPan-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath))))[..12];
        if (enabled)
        {
            var executable = Environment.ProcessPath!;
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("开机启动请使用发布后的 MyPan.Client.exe。");
            key.SetValue(name, $"\"{executable}\" --config \"{Path.GetFullPath(configPath)}\" --background");
        }
        else key.DeleteValue(name, false);
    }
}
