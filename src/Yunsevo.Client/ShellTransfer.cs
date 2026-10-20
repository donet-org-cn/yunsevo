using System.Runtime.InteropServices;

namespace Yunsevo.Client;

// Native COM calls avoid runtime-generated interop and keep Explorer progress compatible with AOT.
internal static unsafe partial class ShellTransfer
{
    private static readonly object propertyGate = new();
    [ThreadStatic] internal static int LastResult;
    private static bool Succeeded(int result) { LastResult = result; return result >= 0; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint Value;
        [FieldOffset(16)] public ulong* Elements;
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHGetPropertyStoreFromParsingName(string path, nint context, uint flags, in Guid iid, out nint store);

    public static bool Set(string path, uint status, long completed = 0, long total = 0)
    {
        // Each volatile store commits a snapshot. Concurrent progress/completion commits must not overwrite each other.
        lock (propertyGate) return SetCore(path, status, completed, total);
    }

    private static bool SetCore(string path, uint status, long completed, long total)
    {
        var initialized = CoInitializeEx(0, 0);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) return false; // Existing STA is also valid.
        nint store = 0;
        try
        {
            var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"); // IPropertyStore
            if (!Succeeded(SHGetPropertyStoreFromParsingName(path, 0, 0x1002, iid, out store))) return false;
            var table = *(nint**)store;
            var setValue = (delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropertyValue*, int>)table[6];
            var commit = (delegate* unmanaged[Stdcall]<nint, int>)table[7];
            var progressKey = new PropertyKey(new("E77E90DF-6271-4F5B-834F-2DD1F245DDA4"), 4);
            ulong* progress = stackalloc ulong[2] { (ulong)Math.Max(0, completed), (ulong)Math.Max(0, total) };
            var value = new PropertyValue { Type = 0x1015, Value = 2, Elements = progress }; // VT_VECTOR | VT_UI8
            if (!Succeeded(setValue(store, &progressKey, &value))) return false;
            var statusKey = new PropertyKey(new("FCEFF153-E839-4CF3-A9E7-EA22832094B8"), 103);
            value = new() { Type = 19, Value = status }; // VT_UI4
            if (!Succeeded(setValue(store, &statusKey, &value)) || !Succeeded(commit(store))) return false;
            ShellIntegration.NotifyItem(path);
            return true;
        }
        finally
        {
            if (store != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)store)[2])(store);
            if (initialized >= 0) CoUninitialize();
        }
    }
}
