namespace MyPan.Server;

internal static class PreviewTypes
{
    public static string? Get(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".glb" => "model/gltf-binary",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".avif" => "image/avif", ".bmp" => "image/bmp",
        ".mp4" => "video/mp4", ".webm" => "video/webm", ".ogv" => "video/ogg", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg",
        ".txt" or ".log" or ".md" or ".csv" or ".json" or ".cs" or ".xml" or ".yaml" or ".yml" => "text/plain; charset=utf-8", _ => null
    };
}
