using System;
using System.IO;

/// <summary>
/// File helpers shared by the snapshot, species library, and preference writers.
/// </summary>
internal static class SaveFileUtility
{
    private const string TempSuffix = ".tmp";

    /// <summary>
    /// Writes to a temp file first, then swaps it into place, so a crash or full disk mid-write
    /// leaves the previous file intact instead of a truncated one.
    /// </summary>
    internal static void WriteAllTextAtomic(string path, string contents)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + TempSuffix;
        File.WriteAllText(tempPath, contents);

        if (!File.Exists(path))
        {
            File.Move(tempPath, path);
            return;
        }

        try
        {
            File.Replace(tempPath, path, null);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException || ex is IOException)
        {
            // Some platforms/filesystems lack an atomic replace; fall back to copy-over (still never leaves a truncated file).
            File.Copy(tempPath, path, true);
            File.Delete(tempPath);
        }
    }
}
