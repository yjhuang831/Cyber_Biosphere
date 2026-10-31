using System;
using System.IO;
using System.IO.Compression;
using System.Text;

/// <summary>
/// File helpers shared by the snapshot, species library, and preference writers.
/// </summary>
internal static class SaveFileUtility
{
    private const string TempSuffix = ".tmp";
    // First two bytes of every gzip file. JSON text starts with '{' (or a BOM), so plain files can't be mistaken for gzip
    private const int GzipByte1 = 0x1F;
    private const int GzipByte2 = 0x8B;

    /// <summary>
    /// Writes to a temp file first, then swaps it into place, so a crash or full disk mid-write
    /// leaves the previous file intact instead of a truncated one.
    /// </summary>
    internal static void WriteAllTextAtomic(string path, string contents)
    {
        string tempPath = PrepareTempPath(path);
        File.WriteAllText(tempPath, contents);
        SwapIntoPlace(tempPath, path);
    }

    /// <summary>
    /// WriteAllTextAtomic, but gzip-compressed. Read it back with ReadAllTextOrGzip.
    /// </summary>
    internal static void WriteCompressedTextAtomic(string path, string contents)
    {
        string tempPath = PrepareTempPath(path);
        using (FileStream file = File.Create(tempPath))
        using (GZipStream gzip = new GZipStream(file, CompressionLevel.Optimal))
        using (StreamWriter writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            writer.Write(contents);
        }
        SwapIntoPlace(tempPath, path);
    }

    /// <summary>
    /// Reads a text file written plainly or by WriteCompressedTextAtomic, whatever its extension.
    /// </summary>
    internal static string ReadAllTextOrGzip(string path)
    {
        using (FileStream file = File.OpenRead(path))
        {
            bool isGzip = file.ReadByte() == GzipByte1 && file.ReadByte() == GzipByte2;
            file.Position = 0;

            using (Stream source = isGzip ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file)
            using (StreamReader reader = new StreamReader(source, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }

    private static string PrepareTempPath(string path)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return path + TempSuffix;
    }

    private static void SwapIntoPlace(string tempPath, string path)
    {
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
