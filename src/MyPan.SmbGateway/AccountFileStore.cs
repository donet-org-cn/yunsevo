using System.Net.Http;
using SMBLibrary;
using SMBLibrary.Adapters;
using Utilities;

namespace MyPan.SmbGateway;

// Each SMB handle retains its authenticated account; concurrent sessions never share an ambient identity.
internal sealed class AccountFileStore(HttpClient http, string staging) : INTFileStore
{
    private sealed record Handle(NTFileSystemAdapter Store, object Inner);
    private readonly NTFileSystemAdapter volume = new(new CloudFileSystem(http, "", staging));
    private static NTStatus Failure(Exception ex) => ex is UnauthorizedAccessException ? NTStatus.STATUS_ACCESS_DENIED : NTStatus.STATUS_DISK_FULL;
    public NTStatus CreateFile(out object handle, out FileStatus fileStatus, string path, AccessMask desiredAccess, SMBLibrary.FileAttributes fileAttributes, ShareAccess shareAccess, CreateDisposition createDisposition, CreateOptions createOptions, SecurityContext securityContext)
    {
        handle = null!; fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
        try
        {
            var fs = new CloudFileSystem(http, securityContext.UserName, staging, securityContext.MachineName, securityContext.ClientEndPoint?.Address.ToString());
            if (((uint)desiredAccess & 0x500D0116) != 0 && path.Trim('\\', '/').Length > 0) fs.Writable(path);
            var store = new NTFileSystemAdapter(fs); var status = store.CreateFile(out var inner, out fileStatus, path, desiredAccess, fileAttributes, shareAccess, createDisposition, createOptions, securityContext);
            if (status == NTStatus.STATUS_SUCCESS) handle = new Handle(store, inner); return status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return Failure(ex); }
    }
    private static Handle H(object handle) => (Handle)handle;
    public NTStatus CloseFile(object handle) { var h = H(handle); try { return h.Store.CloseFile(h.Inner); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failure(ex); } }
    public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount) { var h = H(handle); return h.Store.ReadFile(out data, h.Inner, offset, maxCount); }
    public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data) { var h = H(handle); return h.Store.WriteFile(out numberOfBytesWritten, h.Inner, offset, data); }
    public NTStatus FlushFileBuffers(object handle) { var h = H(handle); try { return h.Store.FlushFileBuffers(h.Inner); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failure(ex); } }
    public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock) { var h = H(handle); return h.Store.LockFile(h.Inner, byteOffset, length, exclusiveLock); }
    public NTStatus UnlockFile(object handle, long byteOffset, long length) { var h = H(handle); return h.Store.UnlockFile(h.Inner, byteOffset, length); }
    public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result, object handle, string fileName, FileInformationClass informationClass) { var h = H(handle); return h.Store.QueryDirectory(out result, h.Inner, fileName, informationClass); }
    public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass) { var h = H(handle); return h.Store.GetFileInformation(out result, h.Inner, informationClass); }
    public NTStatus SetFileInformation(object handle, FileInformation information) { var h = H(handle); try { return h.Store.SetFileInformation(h.Inner, information); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failure(ex); } }
    public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass) => volume.GetFileSystemInformation(out result, informationClass);
    public NTStatus SetFileSystemInformation(FileSystemInformation information) => NTStatus.STATUS_NOT_SUPPORTED;
    public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation) { var h = H(handle); return h.Store.GetSecurityInformation(out result, h.Inner, securityInformation); }
    public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation, SecurityDescriptor securityDescriptor) => NTStatus.STATUS_NOT_SUPPORTED;
    public NTStatus NotifyChange(out object ioRequest, object handle, NotifyChangeFilter completionFilter, bool watchTree, int outputBufferSize, OnNotifyChangeCompleted onNotifyChangeCompleted, object context) { ioRequest = null!; return NTStatus.STATUS_NOT_SUPPORTED; }
    public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_NOT_SUPPORTED;
    public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength) { var h = H(handle); return h.Store.DeviceIOControl(h.Inner, ctlCode, input, out output, maxOutputLength); }
}
