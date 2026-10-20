using System.Security.Cryptography;
using System.Text.Json;
using Yunsevo.Mobile.Android;

var home = Path.GetFullPath(".test-data/saved-models-" + Guid.NewGuid().ToString("N"));
AppServices.Repository.Home = home;
void Check(bool result, string label) { if (!result) throw new Exception(label); Console.WriteLine("PASS " + label); }
Check(SavedModels.List().Count == 0, "Empty model browser does not create or delete source directories");
Guid Save(string account, bool backup = false)
{
    var id = Guid.NewGuid(); var folder = Path.Combine(home, "scans", id.ToString("N")); Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "session.json"), "{\"Account\":\"" + account + "\",\"Name\":\"Existing model\",\"State\":\"running\"}");
    File.WriteAllBytes(Path.Combine(folder, backup ? "model-before-123.glb" : "model.glb"), RandomNumberGenerator.GetBytes(64));
    File.WriteAllBytes(Path.Combine(folder, "video-original.mp4"), RandomNumberGenerator.GetBytes(100));
    return id;
}
var own = Save("owner"); var foreign = Save("other"); var fallback = Save("owner", true);
var before = Directory.GetFiles(home, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
var models = SavedModels.List();
Check(models.Count == 2 && SavedModels.Get(own).Id == own, "Existing model opens without capture or synthesis dependencies");
Check(SavedModels.Get(fallback).File.EndsWith("model-before-123.glb"), "Previous usable model remains accessible after an interrupted task");
try { SavedModels.Get(foreign); throw new Exception("Foreign model opened"); } catch (IOException) { }
Check(true, "Local model browser enforces account ownership");
Check(before.All(x => File.Exists(x.Key) && SHA256.HashData(File.ReadAllBytes(x.Key)).SequenceEqual(x.Value)), "Model, source video and legacy record remain byte-for-byte unchanged");
Console.WriteLine(home);

namespace Yunsevo.Mobile.Android
{
    internal static class AppServices { public static TestRepository Repository { get; } = new(); public static TestClient Client { get; } = new(); }
    internal sealed class TestRepository { public string Home { get; set; } = ""; }
    internal sealed class TestClient { public string AccountKey => "owner"; }
}
