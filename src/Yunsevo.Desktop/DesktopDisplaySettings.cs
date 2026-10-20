using System.Text.Json;
using System.Text.Json.Serialization;
using Yunsevo.Contracts;

namespace Yunsevo.Desktop;

internal sealed record DesktopDisplaySettings(bool CloseToTray = true, bool CompactRows = true, bool ShowFullRemotePath = false, bool ShowRecentInTray = true);
[JsonSerializable(typeof(DesktopDisplaySettings))]
internal partial class DesktopDisplayJson : JsonSerializerContext;

internal static class DisplayPreferences
{
    public static DesktopDisplaySettings Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize(File.ReadAllText(path), DesktopDisplayJson.Default.DesktopDisplaySettings) ?? new() : new();
    public static void Save(string path, DesktopDisplaySettings settings) => AtomicFile.WriteText(path, JsonSerializer.Serialize(settings, DesktopDisplayJson.Default.DesktopDisplaySettings));
}
