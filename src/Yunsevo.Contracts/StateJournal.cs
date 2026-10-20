using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Yunsevo.Contracts;

// A generation tag prevents an old journal from reverting a newer atomic snapshot.
public sealed class StateJournal<T>(string path, Guid generation, JsonTypeInfo<T> type)
{
    private Guid generation = generation;
    public int Count { get; private set; }
    public IEnumerable<T> Replay()
    {
        if (!File.Exists(path)) yield break;
        var bytes = File.ReadAllBytes(path); var end = Array.LastIndexOf(bytes, (byte)'\n');
        if (end < 0) { File.WriteAllBytes(path, []); yield break; }
        foreach (var line in Encoding.UTF8.GetString(bytes, 0, end + 1).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 99 || !Guid.TryParseExact(line[..32], "N", out var id)) throw new InvalidDataException("同步增量记录损坏，请保留状态文件。");
            if (id != generation) continue;
            var payload = line[98..];
            if (!Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).Equals(line.Substring(33, 64), StringComparison.Ordinal)) throw new InvalidDataException("同步增量记录校验失败。");
            Count++; yield return JsonSerializer.Deserialize(payload, type) ?? throw new InvalidDataException("同步增量记录为空。");
        }
        if (end + 1 != bytes.Length) { using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None); file.SetLength(end + 1); file.Flush(true); }
    }
    public void Append(T value)
        => AppendRange([value]);
    public void AppendRange(IEnumerable<T> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var frames = new StringBuilder(); var count = 0;
        foreach (var value in values)
        {
            var payload = JsonSerializer.Serialize(value, type);
            frames.Append(generation.ToString("N")).Append(' ').Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))).Append(' ').Append(payload).Append('\n'); count++;
        }
        if (count == 0) return;
        var frame = Encoding.UTF8.GetBytes(frames.ToString());
        // Flush(true) below is the durable commit boundary; avoid also flushing every buffered write.
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var previous = file.Length;
        try { file.Write(frame); file.Flush(true); Count += count; }
        catch { file.SetLength(previous); file.Flush(true); throw; }
    }
    public void Reset(Guid next)
    {
        generation = next; Count = 0;
        try { File.Delete(path); } catch (IOException) { }
    }
}
