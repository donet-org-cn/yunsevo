using MyPan.Mobile.Android;
using System.Security.Cryptography;

var root = Path.GetFullPath(".test-data/scan-revisions-" + Guid.NewGuid().ToString("N"));
AppServices.Repository.Home = root;
var original = new ScanSession(Guid.NewGuid(), "owner", "设备", new(Guid.NewGuid(), "个人/test", "private"), DateTimeOffset.UtcNow,
    State: "ready", Inputs: ["frame-old.jpg"], RemoteJob: Guid.NewGuid(), Quality: 0);
ScanStore.Save(original);
var video = Path.Combine(ScanStore.Home(original.Id), "video-fixture.mp4");
File.WriteAllBytes(video, RandomNumberGenerator.GetBytes(65537));
File.WriteAllText(video + ".calibration.json", "{\"focal35mm\":24}");
File.WriteAllBytes(ScanStore.Model(original.Id), RandomNumberGenerator.GetBytes(64));
File.WriteAllBytes(Path.Combine(ScanStore.Photos(original.Id), "frame-old.jpg"), [1, 2, 3]);
var snapshots = Directory.GetFiles(ScanStore.Home(original.Id), "*", SearchOption.AllDirectories).ToDictionary(x => x, x => SHA256.HashData(File.ReadAllBytes(x)));
void Check(bool ok, string description) { if (!ok) throw new Exception(description); Console.WriteLine("PASS " + description); }
var revision = ScanStore.CreateVideoRevision(original.Id, video, 1, CancellationToken.None);
Check(revision.Session.DerivedFrom == original.Id && revision.Session.Quality == 1 && revision.Session.State == "collecting", "Completed scan can create an independently selectable fine revision");
Check(revision.Session.RemoteJob == null && revision.Session.Inputs == null && ScanStore.Files(revision.Session.Id).Length == 0 && !File.Exists(ScanStore.Model(revision.Session.Id)), "New frames cannot mix with old frames, jobs or model");
Check(File.ReadAllBytes(video).SequenceEqual(File.ReadAllBytes(revision.Video)) && File.ReadAllText(video + ".calibration.json") == File.ReadAllText(revision.Video + ".calibration.json"), "Original video and camera calibration survive byte-for-byte");
Check(snapshots.All(x => SHA256.HashData(File.ReadAllBytes(x.Key)).SequenceEqual(x.Value)), "Old model, photos, video and session are unchanged");
var count = ScanStore.List().Count;
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { ScanStore.CreateVideoRevision(original.Id, video, 1, cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
Check(ScanStore.List().Count == count && Directory.GetDirectories(Path.Combine(root, "scans")).Length == count, "Cancelled revision leaves no partial scan and preserves originals");
try { ScanStore.CreateVideoRevision(original.Id, Path.Combine(root, "video-outside.mp4"), 1, CancellationToken.None); throw new Exception("Path check failed"); } catch (IOException) { }
Check(true, "Video must belong to the selected scan");
ScanStore.Edit(original.Id, x => x with { State = "running" });
try { ScanStore.CreateVideoRevision(original.Id, video, 1, CancellationToken.None); throw new Exception("Running scan accepted"); } catch (InvalidOperationException) { }
Check(true, "Cannot fork a running scan");
Console.WriteLine(root);

namespace MyPan.Mobile.Android
{
    internal static class AppServices
    {
        public static TestClient Client { get; } = new();
        public static TestRepository Repository { get; } = new();
    }
    internal sealed class TestClient { public string AccountKey => "owner"; }
    internal sealed class TestRepository { public string Home { get; set; } = ""; }
}
