using Gridline.Core;

namespace Gridline.IO;

/// <summary>Atomic file-path persistence for .gridline files (§8.4).</summary>
public static class GridlineFile
{
    /// <summary>
    /// Saves <paramref name="workbook"/> to <paramref name="path"/> atomically: the canonical bytes go to a uniquely named
    /// temp file in the same directory, are flushed to disk, and replace the target in one step. The target is always
    /// either its complete old content or the complete new content.
    /// </summary>
    /// <exception cref="ArgumentException">Null workbook, or null or empty path.</exception>
    /// <exception cref="GridlineIOException">Any I/O or access failure.</exception>
    public static void SaveToPath(Workbook workbook, string path) => Save(workbook, path, null);

    /// <summary>Loads a workbook from <paramref name="path"/> (§8.2, §8.4).</summary>
    /// <exception cref="ArgumentException">Null or empty path.</exception>
    /// <exception cref="GridlineIOException">The file cannot be read (missing, directory, access denied, I/O error).</exception>
    /// <exception cref="GridlineFormatException">The content is not a valid .gridline file.</exception>
    public static Workbook LoadFromPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            throw new GridlineIOException($"Cannot read '{path}': {ex.Message}", ex);
        }
        return GridlineSerializer.Deserialize(bytes);
    }

    /// <summary>Test seam: <paramref name="beforeReplace"/> runs with the temp path after flush and close, before the replace.</summary>
    internal static void SaveToPath(Workbook workbook, string path, Action<string>? beforeReplace) =>
        Save(workbook, path, beforeReplace);

    private static void Save(Workbook workbook, string path, Action<string>? beforeReplace)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentException.ThrowIfNullOrEmpty(path);

        byte[] bytes = GridlineSerializer.Serialize(workbook); // a serialization failure never touches the disk

        string tmp = "";
        bool replaced = false;
        try
        {
            if (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar)
                throw new IOException($"'{path}' ends with a directory separator and names no file.");
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full) ?? throw new IOException($"'{path}' has no parent directory.");
            // Fixed-length name independent of the target's, so a valid long target name cannot overflow the limit.
            tmp = Path.Combine(dir, $".gl-{Guid.NewGuid():N}.tmp");

            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            beforeReplace?.Invoke(tmp);
            File.Move(tmp, full, overwrite: true);
            replaced = true;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            throw new GridlineIOException($"Cannot write '{path}': {ex.Message}", ex);
        }
        finally
        {
            if (!replaced) TryDelete(tmp); // every failure path, whatever the exception type
        }
    }

    private static bool IsIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException
            or ArgumentException; // invalid path characters surface as ArgumentException from the file APIs

    private static void TryDelete(string tmp)
    {
        if (tmp.Length == 0) return;
        try { File.Delete(tmp); } catch (Exception) { /* best effort; leftovers are acceptable */ }
    }
}
