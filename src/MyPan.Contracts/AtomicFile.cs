namespace MyPan.Contracts;

public static class AtomicFile
{
    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(true);
            }
            Replace(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Replace(string temp, string path)
    {
        // Antivirus/indexers can briefly open a newly written file without FILE_SHARE_DELETE.
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temp, path, true); return; }
            catch (Exception ex) when (attempt < 5 && (ex is IOException or UnauthorizedAccessException))
            { Thread.Sleep(25 << attempt); }
        }
    }
}
