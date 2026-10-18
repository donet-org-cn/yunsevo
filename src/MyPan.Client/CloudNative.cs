using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MyPan.Client;

// Windows SDK cfapi.h ABI. Struct layouts and pointer widths are checked in MyPan.Tests.
internal static unsafe partial class CloudNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Registration
    {
        public uint Size; public char* Name; public char* Version; public nint RootIdentity; public uint RootIdentityLength;
        public nint FileIdentity; public uint FileIdentityLength; public Guid ProviderId;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Policies
    {
        public uint Size; public ushort Hydration, HydrationModifier, Population, PopulationModifier;
        public uint InSync, HardLink, PlaceholderManagement;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Metadata
    {
        public long Creation, Access, Write, Change; public uint Attributes; public long FileSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Placeholder
    {
        public char* Name; public Metadata Metadata; public byte* Identity; public uint IdentityLength, Flags;
        public int Result; public long Usn;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CallbackInfo
    {
        public uint Size; public long ConnectionKey; public nint Context, VolumeGuid, VolumeDos; public uint VolumeSerial;
        public long RootFileId; public nint RootIdentity; public uint RootIdentityLength; public long FileId, FileSize;
        public byte* Identity; public uint IdentityLength; public char* NormalizedPath; public long TransferKey;
        public byte Priority; public nint CorrelationVector, ProcessInfo; public long RequestKey;
    }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct CallbackParameters
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(8)] public uint Flags;
        [FieldOffset(16)] public long Offset;
        [FieldOffset(24)] public long Length;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CallbackRegistration
    { public int Type; public delegate* unmanaged[Stdcall]<CallbackInfo*, CallbackParameters*, void> Callback; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct OperationInfo
    {
        public uint Size; public int Type; public long ConnectionKey, TransferKey;
        public nint CorrelationVector, SyncStatus; public long RequestKey;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct TransferParameters
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(8)] public uint Flags;
        [FieldOffset(12)] public int Status;
        [FieldOffset(16)] public byte* Buffer;
        [FieldOffset(24)] public long Offset;
        [FieldOffset(32)] public long Length;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct AttributeTag { public uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SyncRootInfoHeader
    {
        public long FileId;
        public ushort Hydration, HydrationModifier, Population, PopulationModifier;
        public uint InSync, HardLink, ProviderStatus;
        public fixed char ProviderName[256];
        public fixed char ProviderVersion[256];
        public uint IdentityLength;
    }
    internal sealed record RootRegistration(long FileId, string ProviderName, string Identity, bool IsRoot);

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int CfRegisterSyncRoot(string root, in Registration registration, in Policies policies, uint flags);
    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int CfUnregisterSyncRoot(string root);
    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int CfGetSyncRootInfoByPath(string path, uint infoClass, byte* buffer, uint length, out uint returned);
    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int CfConnectSyncRoot(string root, CallbackRegistration* callbacks, nint context, uint flags, out long connection);
    [LibraryImport("cldapi.dll")] internal static partial int CfDisconnectSyncRoot(long connection);
    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int CfCreatePlaceholders(string parent, ref Placeholder placeholder, uint count, uint flags, out uint processed);
    [LibraryImport("cldapi.dll")] internal static partial int CfExecute(in OperationInfo info, ref TransferParameters parameters);
    [LibraryImport("cldapi.dll")] internal static partial int CfReportProviderProgress(long connection, long transfer, long total, long completed);
    [LibraryImport("cldapi.dll")] internal static partial int CfUpdateSyncProviderStatus(long connection, uint status);
    [LibraryImport("cldapi.dll")] internal static partial int CfUpdatePlaceholder(nint handle, in Metadata metadata, byte* identity, uint identityLength, nint ranges, uint rangeCount, uint flags, nint usn, nint overlapped);
    [LibraryImport("cldapi.dll")] internal static partial int CfConvertToPlaceholder(nint handle, byte* identity, uint identityLength, uint flags, nint usn, nint overlapped);
    [LibraryImport("cldapi.dll")] internal static partial int CfSetInSyncState(nint handle, uint state, uint flags, nint usn);
    [LibraryImport("cldapi.dll")] internal static partial int CfSetPinState(nint handle, uint state, uint flags, nint overlapped);
    [LibraryImport("cldapi.dll")] internal static partial int CfHydratePlaceholder(nint handle, long offset, long length, uint flags, nint overlapped);
    [LibraryImport("cldapi.dll")] internal static partial int CfDehydratePlaceholder(nint handle, long offset, long length, uint flags, nint overlapped);
    [LibraryImport("cldapi.dll")] internal static partial int CfGetPlaceholderInfo(nint handle, uint infoClass, byte* buffer, uint length, out uint returned);
    [LibraryImport("cldapi.dll")] internal static partial uint CfGetPlaceholderStateFromAttributeTag(uint attributes, uint tag);
    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int CfOpenFileWithOplock(string path, uint flags, out nint handle);
    [LibraryImport("cldapi.dll")] internal static partial nint CfGetWin32HandleFromProtectedHandle(nint handle);
    [LibraryImport("cldapi.dll")] internal static partial void CfCloseHandle(nint handle);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag tag, uint size);

    internal static void Check(int hr, string? operation = null)
    {
        if (hr < 0) throw new COMException($"{operation ?? "Windows 云文件操作"}失败 (0x{hr:X8})。", hr);
    }
    internal static RootRegistration? QueryRegistration(string path)
    {
        if (!Directory.Exists(path)) return null;
        var buffer = new byte[sizeof(SyncRootInfoHeader) + 65536];
        fixed (byte* bytes = buffer)
        {
            var result = CfGetSyncRootInfoByPath(path, 1, bytes, (uint)buffer.Length, out var returned);
            if (result < 0) return null;
            if (returned < sizeof(SyncRootInfoHeader)) throw new InvalidDataException("Windows 云盘注册信息不完整。");
            var header = (SyncRootInfoHeader*)bytes;
            if (header->IdentityLength > returned - sizeof(SyncRootInfoHeader)) throw new InvalidDataException("Windows 云盘身份长度无效。");
            var name = new string(header->ProviderName, 0, 256).TrimEnd('\0');
            var identity = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(bytes + sizeof(SyncRootInfoHeader), (int)header->IdentityLength));
            var isRoot = true;
            var parent = Directory.GetParent(Path.GetFullPath(path))?.FullName;
            long parentRootId = 0;
            if (parent != null && CfGetSyncRootInfoByPath(parent, 0, (byte*)&parentRootId, 8, out _) >= 0)
                isRoot = parentRootId != header->FileId;
            return new(header->FileId, name, identity, isRoot);
        }
    }
    internal static SafeFileHandle OpenMetadata(string path)
    {
        var handle = CreateFile(path, 0x80, 7, 0, 3, 0x02200000, 0); // READ_ATTRIBUTES, BACKUP_SEMANTICS | OPEN_REPARSE_POINT
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        return handle;
    }
    internal static FileStream OpenUploadSource(string path)
    {
        // Read file content; Cloud Files accepts WRITE_DATA or WRITE_DAC for metadata management.
        // Use WRITE_DAC as a fallback for read-only files without removing their read-only attribute.
        // Deny other writers/deletes, and use synchronous handles for the Cf* management calls.
        var handle = CreateFile(path, 0x80000102, 1, 0, 3, 0x08000000, 0);
        if (handle.IsInvalid && Marshal.GetLastPInvokeError() == 5)
        {
            handle.Dispose();
            handle = CreateFile(path, 0x80040100, 1, 0, 3, 0x08000000, 0);
        }
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"无法访问“{path}”：{new Win32Exception(error).Message}");
        }
        try { return new FileStream(handle, FileAccess.Read, 128 * 1024, false); }
        catch { handle.Dispose(); throw; }
    }
    internal static (uint State, uint Attributes, Guid? Id) Inspect(string path)
    {
        using var handle = OpenMetadata(path);
        if (!GetFileInformationByHandleEx(handle, 9, out var tag, 8)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var state = CfGetPlaceholderStateFromAttributeTag(tag.Attributes, tag.Tag);
        Guid? id = null;
        if (state != uint.MaxValue && (state & 1) != 0)
        {
            byte* buffer = stackalloc byte[8192];
            Check(CfGetPlaceholderInfo(handle.DangerousGetHandle(), 0, buffer, 8192, out var returned));
            var identityLength = *(uint*)(buffer + 24);
            if (identityLength <= returned - 28 && identityLength > 0)
            {
                var identity = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buffer + 28, (int)identityLength));
                if (Guid.TryParse(identity.Split('|')[0], out var parsed)) id = parsed;
            }
        }
        return (state, tag.Attributes, id);
    }
    internal sealed class Oplock : IDisposable
    {
        private readonly nint handle;
        public nint Win32 => CfGetWin32HandleFromProtectedHandle(handle);
        public Oplock(string path, bool exclusive = false)
        { Check(CfOpenFileWithOplock(path, exclusive ? 3u : 2u, out handle)); }
        public void Dispose() => CfCloseHandle(handle);
    }
}
