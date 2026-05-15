namespace Winpepper.Core.Io;

/// <summary>
/// Writes files atomically: write to a temp file, flush to disk, then rename
/// over the destination. A crash mid-write leaves the destination either
/// untouched or fully replaced — never corrupted.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
        => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var tmp = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(contents, 0, contents.Length);
                fs.Flush(flushToDisk: true);
            }
            // File.Move(..., overwrite: true) is atomic on Windows (MoveFileEx with REPLACE_EXISTING)
            // and on Linux/macOS via rename(2).
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }
}
