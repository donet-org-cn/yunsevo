using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MyPan.Contracts;
using MyPan.Server;

var root = Path.Combine(Environment.CurrentDirectory, ".test-data", "migration-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
const string legacy = """
{"Revision":7,"Entries":[
  {"Id":"11111111-1111-4111-8111-111111111111","Path":"旧资料","IsDirectory":true,"Size":0,"ModifiedUtc":"2026-01-01T00:00:00Z","Version":"folder-v1"},
  {"Id":"22222222-2222-4222-8222-222222222222","Path":"旧资料/file.txt","IsDirectory":false,"Size":12,"ModifiedUtc":"2026-01-01T00:00:00Z","Version":"file-v1"}],
 "Trash":[{"Entry":{"Id":"33333333-3333-4333-8333-333333333333","Path":"deleted.txt","IsDirectory":false,"Size":13,"ModifiedUtc":"2026-01-01T00:00:00Z","Version":"deleted-v1"},"Batch":"44444444-4444-4444-8444-444444444444"}]}
""";
var catalogPath = Path.Combine(root, "data", "catalog.json");
Directory.CreateDirectory(Path.Combine(root, "data", "blobs"));
File.WriteAllText(catalogPath, legacy, new UTF8Encoding(false));
File.WriteAllText(Path.Combine(root, "data", "blobs", "22222222222242228222222222222222_file-v1"), "legacy bytes", new UTF8Encoding(false));
File.WriteAllText(Path.Combine(root, "data", "blobs", "33333333333343338333333333333333_deleted-v1"), "deleted bytes", new UTF8Encoding(false));
var checks = 0;
void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}
try
{
    var original = File.ReadAllBytes(catalogPath);
    string token;
    string accountState;
    using (var store = new CloudStore(Path.GetDirectoryName(catalogPath)!, key))
    {
        var actor = store.BootstrapActor()!;
        var files = store.Snapshot(actor);
        Assert(files.Revision >= 7 && files.Entries.Any(x => x.Id == Guid.Parse("11111111-1111-4111-8111-111111111111") && x.Path == "旧资料") && files.Entries.Single(x => !x.IsDirectory).Version == "file-v1", "Legacy catalog paths, IDs and versions survive migration and additive personal folders");
        Assert(files.Entries.Any(x => x.Path == "个人/admin" && x.IsDirectory), "Existing bootstrap administrator acquires a default personal folder during migration");
        var trash = store.Snapshot(actor, true);
        Assert(trash.Entries.Single().Version == "deleted-v1", "Legacy recycle-bin records remain visible");
        var (stream, _) = await store.Download(actor, Guid.Parse("22222222-2222-4222-8222-222222222222"), null, default);
        using (stream) using (var reader = new StreamReader(stream)) Assert(await reader.ReadToEndAsync() == "legacy bytes", "Legacy file content remains downloadable");
        var migrated = JsonNode.Parse(File.ReadAllText(catalogPath))!;
        foreach (var field in new[] { "Users", "Groups", "Policies", "History", "Sessions" })
            Assert(migrated[field] is JsonArray, "Migration initializes " + field);
        var backup = Directory.GetFiles(Path.GetDirectoryName(catalogPath)!, "catalog.json.v1-backup-*").Single();
        Assert(File.ReadAllBytes(backup).SequenceEqual(original), "Pre-migration backup matches the original catalog byte for byte");
        var login = await store.Login(new("admin", key));
        token = login.AccessToken;
        Assert(login.User.IsAdmin, "Migrated administrator can log in with the existing API key");
        await store.Restore(actor, Guid.Parse("33333333-3333-4333-8333-333333333333"));
        var (restored, _) = await store.Download(actor, Guid.Parse("33333333-3333-4333-8333-333333333333"), null, default);
        using (restored) using (var reader = new StreamReader(restored)) Assert(await reader.ReadToEndAsync() == "deleted bytes", "Legacy recycle-bin content can be restored and downloaded");
        accountState = JsonNode.Parse(File.ReadAllText(catalogPath))!["Users"]!.ToJsonString();
    }
    using (var store = new CloudStore(Path.GetDirectoryName(catalogPath)!, new string('x', 64)))
    {
        Assert(store.Authenticate(token)?.IsAdmin == true, "Restart preserves migrated account and session instead of resetting credentials");
        Assert(store.Snapshot(store.BootstrapActor()!).Entries.Any(x => x.Id == Guid.Parse("33333333-3333-4333-8333-333333333333")), "Restored legacy entries survive restart");
        Assert(JsonNode.Parse(File.ReadAllText(catalogPath))!["Users"]!.ToJsonString() == accountState && Directory.GetFiles(Path.GetDirectoryName(catalogPath)!, "catalog.json.v1-backup-*").Length == 1, "Restart neither replaces accounts nor repeats migration");
    }
    var valid = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
    void Reject(JsonObject invalid, string label)
    {
        var path = Path.Combine(root, "invalid-" + checks);
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "catalog.json");
        File.WriteAllText(file, invalid.ToJsonString());
        var before = File.ReadAllBytes(file);
        var rejected = false;
        try { using var store = new CloudStore(path, key); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected && File.ReadAllBytes(file).SequenceEqual(before) && Directory.GetFiles(path, "catalog.json.v1-backup-*").Length == 0, label);
        // A failed constructor must release its exclusive store lock so an operator can retry after repair.
        File.WriteAllText(file, valid.ToJsonString());
        using var retry = new CloudStore(path, key);
        Assert(retry.Authenticate(token) != null, "A failed load releases the store lock and allows a repaired catalog to reopen");
    }
    foreach (var field in new[] { "Entries", "Trash", "Users", "Groups", "Policies", "History", "Sessions" })
    {
        var invalid = (JsonObject)valid.DeepClone(); invalid[field] = null;
        Reject(invalid, "Explicit null " + field + " is rejected without overwriting catalog or reinitializing security");
    }
    foreach (var field in new[] { "Entries", "Trash", "Users", "Groups", "Policies", "History", "Sessions" })
    {
        var invalid = (JsonObject)valid.DeepClone(); invalid.Remove(field);
        Reject(invalid, "Incomplete V2 catalog missing " + field + " is rejected without data loss");
    }
    var emptyUsers = (JsonObject)valid.DeepClone(); emptyUsers["Users"] = new JsonArray();
    Reject(emptyUsers, "Empty V2 user database cannot silently rebootstrap the administrator");
    using (var store = new CloudStore(Path.Combine(root, "fresh"), key))
        Assert(store.Users(store.BootstrapActor()!).Count == 1, "Fresh installation still creates its initial administrator");
    var journalDirectory = Path.Combine(root, "content-journal"); CloudEntry journalEntry;
    using (var store = new CloudStore(journalDirectory, key))
    {
        var actor = store.BootstrapActor()!;
        await store.Folder(actor, "files");
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("durable journal bytes"));
        journalEntry = await store.Upload(actor, "files/recovered.txt", bytes, bytes.Length, null, true, 1024, default);
        Assert(File.Exists(Path.Combine(journalDirectory, "catalog.journal")), "A file commit writes a durable content journal without rewriting the complete catalog");
    }
    File.AppendAllText(Path.Combine(journalDirectory, "catalog.journal"), "interrupted");
    using (var store = new CloudStore(journalDirectory, key))
    {
        var actor = store.BootstrapActor()!;
        Assert(store.Snapshot(actor).Entries.Any(x => x.Id == journalEntry.Id && x.Version == journalEntry.Version), "Restart replays a committed file after an incomplete journal tail");
        var (content, _) = await store.Download(actor, journalEntry.Id, null, default);
        using (content) using (var reader = new StreamReader(content)) Assert(await reader.ReadToEndAsync() == "durable journal bytes", "Recovered content journal references the original durable blob bytes");
        await store.Folder(actor, "checkpoint");
        Assert(!File.Exists(Path.Combine(journalDirectory, "catalog.journal")), "A metadata snapshot checkpoints the content journal under a new generation");
    }
    if (args.Contains("--native"))
    {
        var source = args.FirstOrDefault(x => x.StartsWith("--legacy-data="))?[14..];
        var sourceCatalog = source == null ? Directory.GetFiles(Path.GetDirectoryName(catalogPath)!, "catalog.json.v1-backup-*").Single()
            : Path.Combine(Path.GetFullPath(source), "catalog.json");
        await NativeMigrationChecks.Run(Environment.CurrentDirectory, root, sourceCatalog, Assert);
    }
    File.WriteAllText(Path.Combine(root, "verification.json"), "{\"checksPassed\":" + checks + "}");
    Console.WriteLine($"ALL PASSED: {checks} migration checks. Artifacts: {root}");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
