using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Yunsevo.Client;

// CloudMirror's Shell integration requires indexed sync roots for cached transfer properties.
// Register only this root, and remember ownership so unregister never removes an existing user scope.
internal static unsafe partial class SearchIndex
{
    private const string RegistryPath = @"Software\Yunsevo\IndexScopes";
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    private static string OwnerKey(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
    private static string Url(string root) => new Uri(Path.GetFullPath(root).TrimEnd('\\') + "\\").AbsoluteUri;

    private sealed class Scope : IDisposable
    {
        private readonly bool uninitialize;
        private nint manager, catalog;
        public nint Handle { get; private set; }
        public Scope()
        {
            var initialized = CoInitializeEx(0, 0);
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
            uninitialize = initialized >= 0;
            try
            {
                Marshal.ThrowExceptionForHR(CoCreateInstance(new("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39"), 0, 5,
                    new("AB310581-AC80-11D1-8DF3-00C04FB6EF69"), out manager));
                nint result;
                fixed (char* name = "SystemIndex")
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)(*(nint**)manager)[10])(manager, name, &result));
                catalog = result;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)catalog)[28])(catalog, &result));
                Handle = result;
            }
            catch { Dispose(); throw; }
        }
        public bool Included(string url)
        {
            int included = 0;
            fixed (char* text = url)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int*, int>)(*(nint**)Handle)[13])(Handle, text, &included));
            return included != 0;
        }
        public void Add(string url)
        {
            fixed (char* text = url)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, uint, int>)(*(nint**)Handle)[3])(Handle, text, 1, 1));
        }
        public void Remove(string url)
        {
            fixed (char* text = url)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)Handle)[18])(Handle, text));
        }
        public void Save() => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)Handle)[16])(Handle));
        private static void Release(nint value)
        { if (value != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)value)[2])(value); }
        public void Dispose()
        {
            Release(Handle); Release(catalog); Release(manager);
            Handle = 0; catalog = 0; manager = 0;
            if (uninitialize) CoUninitialize();
        }
    }

    public static int Ensure(string root)
    {
        try
        {
            using var scope = new Scope();
            var url = Url(root);
            if (scope.Included(url)) return 0;
            using var owner = Registry.CurrentUser.CreateSubKey(RegistryPath);
            owner.SetValue(OwnerKey(root), Path.GetFullPath(root));
            scope.Add(url);
            scope.Save();
            return scope.Included(url) ? 0 : 1; // A user exclusion or group policy can override a default rule.
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException) { return ex.HResult; }
    }

    public static void RemoveOwned(string root)
    {
        using var owner = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        if (owner?.GetValue(OwnerKey(root)) is not string path || !path.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return;
        using var scope = new Scope();
        scope.Remove(Url(root));
        scope.Save();
        owner.DeleteValue(OwnerKey(root), false);
    }

    internal static bool IsIncluded(string root)
    { using var scope = new Scope(); return scope.Included(Url(root)); }
}
