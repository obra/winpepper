using System.Formats.Tar;
using System.IO.Compression;

namespace Winpepper.Models;

/// <summary>
/// Idempotent .tar.gz extraction for model-bundle archives (the transcribe.cpp
/// native runtime). A marker file "&lt;archive&gt;.extracted" containing the
/// archive's SHA-256 records a completed extraction; a missing or stale marker
/// triggers a clean re-extract (destination dir is deleted first, so a
/// half-extracted tree can never be mistaken for a good one).
/// ORDERING IS LOAD-BEARING: the destination tree is deleted BEFORE the marker,
/// so a failed delete (Windows locks a loaded transcribe.dll and its tree)
/// leaves the old marker + old tree consistent instead of latching a sticky
/// "no marker, undeletable dir" state. A locked tree surfaces as a clear
/// restart-required error; the engine holder caches a loaded engine for the
/// process lifetime anyway, so an in-process runtime swap could never take
/// effect — restart-required is the honest contract.
/// TarFile.ExtractToDirectory rejects path-traversal entries by design.
/// </summary>
public static class TarGzExtractor
{
    public static void EnsureExtracted(string archivePath, string destinationDir, string archiveSha256)
    {
        var marker = archivePath + ".extracted";
        if (File.Exists(marker) && Directory.Exists(destinationDir) &&
            string.Equals(File.ReadAllText(marker).Trim(), archiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(destinationDir))
        {
            try
            {
                Directory.Delete(destinationDir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Loaded native DLLs lock the tree (engine already running).
                throw new IOException(
                    $"Cannot replace the extracted runtime at '{destinationDir}': files are in " +
                    "use (the streaming engine is loaded in this or another process). " +
                    "Restart the app, then retry the install.", e);
            }
        }
        if (File.Exists(marker)) File.Delete(marker);   // only after the tree is gone
        Directory.CreateDirectory(destinationDir);

        using (var fs = File.OpenRead(archivePath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        {
            TarFile.ExtractToDirectory(gz, destinationDir, overwriteFiles: true);
        }

        File.WriteAllText(marker, archiveSha256);
    }
}
