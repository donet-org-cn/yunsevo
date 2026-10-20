using System.Net;
using System.Net.Http.Json;
using DiskAccessLibrary.FileSystems.Abstractions;
using Yunsevo.Contracts;

namespace Yunsevo.SmbGateway;

internal sealed class CloudFileSystem(HttpClient http, string username, string staging, string? device = null, string? address = null) : IFileSystem
{
    public string Name => "Yunsevo"; public long Size => 1L << 40; public long FreeSpace => 1L << 39; public bool SupportsNamedStreams => false;
    private static string PathOf(string path) { var value = path.Replace('\\', '/').Trim('/'); return value.Length == 0 ? "" : CloudPath.Normalize(value); }
    internal HttpResponseMessage Send(HttpRequestMessage request)
    {
        using (request)
        {
            request.Headers.Add("X-Yunsevo-User", username);
            if (!string.IsNullOrEmpty(device)) request.Headers.Add("X-Yunsevo-Device", System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(device)));
            if (!string.IsNullOrEmpty(address)) request.Headers.Add("X-Yunsevo-RemoteAddress", address);
            var response = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode; response.Dispose();
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("Yunsevo permission denied.");
            if (status == HttpStatusCode.NotFound) throw new FileNotFoundException("Yunsevo file not found.");
            throw new IOException("Yunsevo rejected the operation: HTTP " + (int)status);
        }
    }
    internal T Read<T>(HttpRequestMessage request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    { using var response = Send(request); return response.Content.ReadFromJsonAsync(type).GetAwaiter().GetResult()!; }
    private Catalog Catalog() => Read(new(HttpMethod.Get, "/api/entries"), ApiJson.Default.Catalog);
    internal CloudEntry Entry(string path) => Catalog().Entries.Find(x => x.Path.Equals(PathOf(path), StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException(path);
    internal void Writable(string path)
    {
        var normalized = PathOf(path); var parent = CloudPath.Parent(normalized);
        var entries = Catalog().Entries; var entry = entries.Find(x => x.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ?? entries.Find(x => x.Path.Equals(parent, StringComparison.OrdinalIgnoreCase));
        // The API repeats authorization under its mutation gate. This early check prevents writable handles on read-only entries.
        if (entry != null && (entry.Access & 2) == 0) throw new UnauthorizedAccessException();
    }
    private static FileSystemEntry Convert(CloudEntry entry) => new("\\" + entry.Path.Replace('/', '\\'), entry.Path.Split('/').Last(), entry.IsDirectory, (ulong)entry.Size,
        entry.ModifiedUtc.UtcDateTime, entry.ModifiedUtc.UtcDateTime, entry.ModifiedUtc.UtcDateTime, false, (entry.Access & 2) == 0, !entry.IsDirectory);
    public FileSystemEntry GetEntry(string path) => PathOf(path).Length == 0 ? new("\\", "", true, 0, DateTime.UnixEpoch, DateTime.UnixEpoch, DateTime.UnixEpoch, false, false, false) : Convert(Entry(path));
    public FileSystemEntry CreateFile(string path)
    { return Convert(Put(PathOf(path), new MemoryStream(), null)); }
    public FileSystemEntry CreateDirectory(string path)
    { using var request = new HttpRequestMessage(HttpMethod.Post, "/api/folders") { Content = JsonContent.Create(new CreateFolder(PathOf(path)), ApiJson.Default.CreateFolder) }; return Convert(Read(request, ApiJson.Default.CloudEntry)); }
    public void Move(string source, string destination)
    { var entry = Entry(source); using var response = Send(new(HttpMethod.Patch, "/api/entries/" + entry.Id) { Content = JsonContent.Create(new MoveEntry(PathOf(destination), entry.Version), ApiJson.Default.MoveEntry) }); }
    public void Delete(string path)
    {
        var catalog = Catalog(); var entry = catalog.Entries.Find(x => x.Path.Equals(PathOf(path), StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException(path);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/entries/" + entry.Id); request.Headers.TryAddWithoutValidation("If-Match", '"' + entry.Version + '"'); request.Headers.Add("X-Catalog-Revision", catalog.Revision.ToString()); using var response = Send(request);
    }
    public List<FileSystemEntry> ListEntriesInDirectory(string path)
    { var normalized = PathOf(path); if (normalized.Length > 0 && !Entry(normalized).IsDirectory) throw new DirectoryNotFoundException(); return Catalog().Entries.Where(x => (CloudPath.Parent(x.Path) ?? "").Equals(normalized, StringComparison.OrdinalIgnoreCase)).Select(Convert).ToList(); }
    public List<KeyValuePair<string, ulong>> ListDataStreams(string path) => [];
    public void SetAttributes(string path, bool? isHidden, bool? isReadonly, bool? isArchived) => Writable(path);
    public void SetDates(string path, DateTime? creationDT, DateTime? lastWriteDT, DateTime? lastAccessDT) => Writable(path);
    internal CloudEntry Put(string path, Stream stream, string? version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString(path)) { Content = new StreamContent(new BorrowedStream(stream)) };
        request.Headers.TryAddWithoutValidation(version == null ? "If-None-Match" : "If-Match", version == null ? "*" : '"' + version + '"');
        return Read(request, ApiJson.Default.CloudEntry);
    }
    public Stream OpenFile(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options)
    {
        var normalized = PathOf(path); var entry = Entry(normalized);
        if (access != FileAccess.Read) Writable(path);
        Directory.CreateDirectory(staging); var temp = System.IO.Path.Combine(staging, Guid.NewGuid().ToString("N") + ".pending");
        var stream = new StagedStream(temp, this, entry, access, (options & FileOptions.DeleteOnClose) != 0);
        try
        {
            if (mode != FileMode.Truncate)
            { using var response = Send(new(HttpMethod.Get, "/api/content/" + entry.Id + "?version=" + entry.Version)); response.Content.CopyToAsync(stream).GetAwaiter().GetResult(); }
            stream.Position = 0; stream.Initialize(mode == FileMode.Truncate); return stream;
        }
        catch { stream.Abort(); throw; }
    }
    private sealed class BorrowedStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class StagedStream(string file, CloudFileSystem fs, CloudEntry entry, FileAccess access, bool deleteOnClose) : FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
    {
        private CloudEntry current = entry; private bool dirty, initialized, disposed;
        public override bool CanWrite => access != FileAccess.Read;
        public void Initialize(bool truncated) { initialized = true; dirty = truncated; }
        public override void Write(byte[] buffer, int offset, int count) { if (initialized && !CanWrite) throw new UnauthorizedAccessException(); if (Position + count > 20L * 1024 * 1024 * 1024) throw new IOException("Single file limit exceeded."); base.Write(buffer, offset, count); if (initialized) dirty = true; }
        public override void SetLength(long value) { if (!CanWrite) throw new UnauthorizedAccessException(); if (value > 20L * 1024 * 1024 * 1024) throw new IOException("Single file limit exceeded."); base.SetLength(value); dirty = true; }
        public override void Flush()
        {
            base.Flush(); if (!initialized || !dirty || deleteOnClose) return;
            var position = Position; try { Position = 0; current = fs.Put(current.Path, this, current.Version); dirty = false; } finally { Position = position; }
        }
        public void Abort() { initialized = false; dirty = false; Dispose(); }
        protected override void Dispose(bool disposing)
        {
            if (disposed) return; disposed = true; bool committed = false;
            try { if (disposing) { if (deleteOnClose) fs.Delete(current.Path); else Flush(); } committed = true; }
            finally { var filePath = Name; base.Dispose(disposing); if (committed) File.Delete(filePath); }
        }
    }
}
