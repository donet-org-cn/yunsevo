using System.Text.Json;
using System.Text.Json.Serialization;
using MyPan.Contracts;
using MyPan.Sync;

Console.OutputEncoding = System.Text.Encoding.UTF8;
try
{
    string? Value(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    if (args.Contains("--help") || Value("--config") == null)
    {
        Console.WriteLine("MyPan.Headless --config client.json [--once | --list-deletions | --resolve-delete relative/path --decision cloud|local|defer]\nPassword: MYPAN_PASSWORD or MYPAN_PASSWORD_FILE. No offline deletion is propagated without an explicit decision."); return;
    }
    var configPath = Path.GetFullPath(Value("--config")!); var config = JsonSerializer.Deserialize(File.ReadAllText(configPath), HeadlessJson.Default.HeadlessConfig) ?? throw new InvalidDataException("Invalid config.");
    var pendingPath = configPath + ".deletions.json"; var gate = new object();
    FileStream PendingLock()
    {
        for (var attempt = 0; ; attempt++)
        { try { return new(pendingPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); } catch (IOException) when (attempt < 50) { Thread.Sleep(20); } }
    }
    List<PendingDeletion> LoadPending() => File.Exists(pendingPath) ? JsonSerializer.Deserialize(File.ReadAllText(pendingPath), HeadlessJson.Default.ListPendingDeletion) ?? [] : [];
    void SavePending(List<PendingDeletion> entries) => AtomicFile.WriteText(pendingPath, JsonSerializer.Serialize(entries, HeadlessJson.Default.ListPendingDeletion));
    if (args.Contains("--list-deletions")) { using var lease = PendingLock(); foreach (var entry in LoadPending()) Console.WriteLine(entry.Decision + "\t" + entry.Path); return; }
    if (Value("--resolve-delete") is { } relative)
    {
        using var lease = PendingLock();
        var decision = Value("--decision"); if (decision is not ("cloud" or "local" or "defer")) throw new ArgumentException("--decision cloud|local|defer");
        var entries = LoadPending(); var index = entries.FindIndex(x => x.Path == relative); if (index < 0) throw new ArgumentException("Pending path not found.");
        entries[index] = entries[index] with { Decision = decision }; SavePending(entries); Console.WriteLine("Decision saved. The running service will apply it on its next pass."); return;
    }
    var password = Environment.GetEnvironmentVariable("MYPAN_PASSWORD");
    if (password == null && Environment.GetEnvironmentVariable("MYPAN_PASSWORD_FILE") is { } passwordFile) password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
    if (password == null) throw new ArgumentException("Set MYPAN_PASSWORD or MYPAN_PASSWORD_FILE.");
    using var instance = new FileStream(configPath + ".sync.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var term = !OperatingSystem.IsWindows() ? System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); }) : null;
    using var connection = new CloudConnection(config.Server); var login = await connection.Login(config.Username, password, stop.Token); password = null;
    var history = new ActivityLog(Path.Combine(Path.GetDirectoryName(configPath)!, "activity"));
    void Log(string message) { history.Write(message); Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + message); }
    Task<DeletionChoice> Confirm(DeletionRequest request, CancellationToken ct)
    {
        lock (gate)
        {
            using var lease = PendingLock();
            var entries = LoadPending(); var pending = entries.Find(x => x.Path == request.Path);
            if (pending == null) { pending = new(request.Path, "pending"); entries.Add(pending); SavePending(entries); Log("删除等待确认：" + request.Path + "；使用 --list-deletions / --resolve-delete 查看和处理。"); }
            var decision = pending.Decision switch { "cloud" => DeletionChoice.DeleteCloud, "local" => DeletionChoice.LocalOnly, _ => DeletionChoice.Defer };
            if (decision != DeletionChoice.Defer) { entries.Remove(pending); SavePending(entries); }
            return Task.FromResult(decision);
        }
    }
    using var sync = new PortableSync(connection, config.Server, login.User.Id, config.RemoteDirectory, config.SyncRoot, configPath + ".state.json", Log, Confirm);
    try { if (args.Contains("--once")) await sync.SyncOnce(stop.Token); else await sync.Run(stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }

internal sealed record HeadlessConfig(string Server, string Username, string RemoteDirectory, string SyncRoot);
internal sealed record PendingDeletion(string Path, string Decision);
[JsonSerializable(typeof(HeadlessConfig))]
[JsonSerializable(typeof(List<PendingDeletion>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
internal partial class HeadlessJson : JsonSerializerContext;
