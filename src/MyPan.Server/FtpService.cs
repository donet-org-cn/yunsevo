using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MyPan.Contracts;

namespace MyPan.Server;

public sealed class FtpService(CloudStore store, AuditLog audit, IPAddress address, int port, long maxUpload) : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly TcpListener listener = new(address, port);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task> clients = new();
    private Task? accepting; private int sequence;
    public void Start() { listener.Start(32); accepting = Accept(); }
    private async Task Accept()
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client; try { client = await listener.AcceptTcpClientAsync(cancellation.Token); } catch (OperationCanceledException) { break; } catch (SocketException) when (cancellation.IsCancellationRequested) { break; }
            if (clients.Count >= 32) { client.Dispose(); continue; }
            var id = Interlocked.Increment(ref sequence); var task = Session(client, cancellation.Token); clients[id] = task;
            _ = task.ContinueWith(_ => clients.TryRemove(id, out var ignored), TaskScheduler.Default);
        }
    }
    private async Task Session(TcpClient client, CancellationToken stopping)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(5)); var ct = timeout.Token; var stream = client.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\r\n" };
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            TcpListener? passive = null; string username = "", cwd = "", rename = ""; RequestActor? actor = null; int failed = 0;
            var source = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
            async Task<string?> ReadCommand()
            {
                var command = new StringBuilder(); var character = new char[1];
                while (await reader.ReadAsync(character.AsMemory(), ct) > 0)
                { if (character[0] == '\n') return command.ToString().TrimEnd('\r'); if (command.Length >= 4096) throw new IOException("FTP command too long."); command.Append(character[0]); }
                return null;
            }
            async Task Reply(int code, string text) => await writer.WriteLineAsync(code + " " + text.Replace('\r', ' ').Replace('\n', ' '));
            string Resolve(string text)
            {
                var parts = (text.StartsWith('/') ? text : cwd + "/" + text).Split('/', StringSplitOptions.RemoveEmptyEntries); var normalized = new List<string>();
                foreach (var part in parts) { if (part == ".") continue; if (part == "..") { if (normalized.Count > 0) normalized.RemoveAt(normalized.Count - 1); } else normalized.Add(part); }
                return normalized.Count == 0 ? "" : CloudPath.Normalize(string.Join('/', normalized));
            }
            async Task<TcpClient> Data()
            {
                if (passive == null) throw new StoreException(425, "Use PASV or EPSV first.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var accepted = await passive.AcceptTcpClientAsync(deadline.Token); passive.Stop(); passive = null;
                if (!((IPEndPoint)accepted.Client.RemoteEndPoint!).Address.Equals(((IPEndPoint)client.Client.RemoteEndPoint!).Address)) { accepted.Dispose(); throw new StoreException(425, "Data peer mismatch."); }
                return accepted;
            }
            try
            {
                await Reply(220, "MyPan FTP ready; UTF-8, passive mode.");
                while (!ct.IsCancellationRequested)
                {
                    var line = await ReadCommand(); if (line == null) break;
                    timeout.CancelAfter(TimeSpan.FromMinutes(5)); if (line.Length > 4096) { await Reply(500, "Command too long."); break; }
                    var split = line.IndexOf(' '); var command = (split < 0 ? line : line[..split]).ToUpperInvariant(); var arg = split < 0 ? "" : line[(split + 1)..]; var target = cwd;
                    try
                    {
                        if (command == "QUIT") { await Reply(221, "Bye."); break; }
                        if (command == "USER") { username = arg; actor = null; await Reply(331, "Password required."); continue; }
                        if (command == "PASS")
                        {
                            try { actor = store.PasswordActor(username, arg); await Reply(230, "Logged in."); audit.Write(new(DateTimeOffset.UtcNow, actor.Username, "FTP " + source, "LOGIN", "/", 200)); }
                            catch (StoreException) { await Reply(530, "Invalid credentials."); audit.Write(new(DateTimeOffset.UtcNow, username, "FTP " + source, "LOGIN", "/", 401)); if (++failed >= 5) break; }
                            continue;
                        }
                        if (command == "FEAT") { await writer.WriteLineAsync("211-Features\r\n UTF8\r\n EPSV\r\n SIZE\r\n MDTM\r\n MLSD\r\n211 End"); continue; }
                        if (command == "SYST") { await Reply(215, "UNIX Type: L8"); continue; }
                        if (command == "OPTS" && arg.Equals("UTF8 ON", StringComparison.OrdinalIgnoreCase)) { await Reply(200, "UTF8 enabled."); continue; }
                        if (actor == null) { await Reply(530, "Login required."); continue; } actor = store.ProtocolActor(actor.Username);
                        var catalog = store.Snapshot(actor); CloudEntry? Entry(string path) => catalog.Entries.Find(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                        switch (command)
                        {
                            case "PWD": await Reply(257, "\"/" + cwd + "\""); break;
                            case "TYPE": case "NOOP": await Reply(200, "OK."); break;
                            case "CWD": case "CDUP": target = Resolve(command == "CDUP" ? ".." : arg); if (target.Length > 0 && Entry(target) is not { IsDirectory: true }) throw new StoreException(404, "Directory not found."); cwd = target; await Reply(250, "Directory changed."); break;
                            case "PASV": case "EPSV":
                                passive?.Stop(); var local = ((IPEndPoint)client.Client.LocalEndPoint!).Address; passive = new(local, 0); passive.Start(); var dataPort = ((IPEndPoint)passive.LocalEndpoint).Port;
                                await Reply(command == "EPSV" ? 229 : 227, command == "EPSV" ? "Entering Extended Passive Mode (|||" + dataPort + "|)" : "Entering Passive Mode (" + local.ToString().Replace('.', ',') + "," + dataPort / 256 + "," + dataPort % 256 + ")"); break;
                            case "LIST": case "NLST": case "MLSD":
                                target = Resolve(arg.StartsWith('-') ? "" : arg); if (target.Length > 0 && Entry(target) is not { IsDirectory: true }) throw new StoreException(404, "Directory not found.");
                                await Reply(150, "Opening listing."); using (var data = await Data()) using (var listing = new StreamWriter(data.GetStream(), new UTF8Encoding(false)) { NewLine = "\r\n" })
                                {
                                    foreach (var item in catalog.Entries.Where(x => (CloudPath.Parent(x.Path) ?? "").Equals(target, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        var name = item.Path.Split('/').Last();
                                        await listing.WriteLineAsync(command == "NLST" ? name : command == "MLSD" ? $"type={(item.IsDirectory ? "dir" : "file")};size={item.Size};modify={item.ModifiedUtc:yyyyMMddHHmmss}; {name}" :
                                            $"{(item.IsDirectory ? 'd' : '-')}{((item.Access & 2) != 0 ? "rwxr-xr-x" : "r-xr-xr-x")} 1 mypan mypan {item.Size.ToString(CultureInfo.InvariantCulture)} {item.ModifiedUtc.ToString("MMM dd yyyy", CultureInfo.InvariantCulture)} {name}");
                                    }
                                }
                                await Reply(226, "Listing complete."); break;
                            case "SIZE": case "MDTM": target = Resolve(arg); var info = Entry(target) ?? throw new StoreException(404, "File not found."); await Reply(213, command == "SIZE" ? info.Size.ToString(CultureInfo.InvariantCulture) : info.ModifiedUtc.ToString("yyyyMMddHHmmss")); break;
                            case "RETR":
                                target = Resolve(arg); var file = Entry(target) ?? throw new StoreException(404, "File not found."); var download = await store.Download(actor, file.Id, null, ct);
                                await using (var input = download.Stream) { await Reply(150, "Opening file."); using var data = await Data(); await input.CopyToAsync(data.GetStream(), ct); }
                                await Reply(226, "Transfer complete."); break;
                            case "STOR":
                                target = Resolve(arg); var existing = Entry(target); await Reply(150, "Opening upload.");
                                using (var data = await Data()) await store.Upload(actor, target, data.GetStream(), null, existing?.Version, existing == null, maxUpload, ct);
                                await Reply(226, "Upload committed."); audit.Write(new(DateTimeOffset.UtcNow, actor.Username, "FTP " + source, "STOR", target, 200)); break;
                            case "MKD": target = Resolve(arg); await store.Folder(actor, target); await Reply(257, "Directory created."); audit.Write(new(DateTimeOffset.UtcNow, actor.Username, "FTP " + source, "MKD", target, 201)); break;
                            case "DELE": case "RMD":
                                target = Resolve(arg); var remove = Entry(target) ?? throw new StoreException(404, "Not found."); await store.Delete(actor, remove.Id, remove.Version, catalog.Revision, new(null, "FTP", source));
                                await Reply(250, "Moved to recycle bin."); audit.Write(new(DateTimeOffset.UtcNow, actor.Username, "FTP " + source, command, target, 204)); break;
                            case "RNFR": target = Resolve(arg); if (Entry(target) == null) throw new StoreException(404, "Not found."); rename = target; await Reply(350, "Send RNTO."); break;
                            case "RNTO": target = Resolve(arg); var moving = Entry(rename) ?? throw new StoreException(404, "Send RNFR first."); await store.Move(actor, moving.Id, new(target, moving.Version)); rename = ""; await Reply(250, "Renamed."); audit.Write(new(DateTimeOffset.UtcNow, actor.Username, "FTP " + source, "MOVE", moving.Path + " -> " + target, 200)); break;
                            default: await Reply(502, "Command not supported; use passive mode."); break;
                        }
                    }
                    catch (Exception ex) when (ex is StoreException or IOException or ArgumentException)
                    {
                        var status = ex is StoreException se ? se.Status : 500; await Reply(status == 507 ? 552 : status == 425 ? 425 : 550, ex.Message);
                        audit.Write(new(DateTimeOffset.UtcNow, actor?.Username ?? username, "FTP " + source, command, target, status));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
            finally { passive?.Stop(); }
        }
    }
    public async ValueTask DisposeAsync()
    { cancellation.Cancel(); listener.Stop(); if (accepting != null) await accepting; await Task.WhenAll(clients.Values); cancellation.Dispose(); }
}
