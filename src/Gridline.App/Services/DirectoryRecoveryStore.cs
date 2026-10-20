using System.IO;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.App.Services;

/// <summary>
/// <see cref="IRecoveryStore"/> over a directory (§11.6). Recovery files are named by the session guid in the <c>D</c>
/// format plus <c>.gridline</c>; other files in the directory (including SaveToPath's <c>.gl-*.tmp</c> temp files) are
/// not recovery files and are ignored. Writes go through the atomic <see cref="GridlineFile.SaveToPath"/> (R48).
/// </summary>
public sealed class DirectoryRecoveryStore : IRecoveryStore
{
    private const string Extension = ".gridline";

    public DirectoryRecoveryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
    }

    public string Directory { get; }

    /// <summary><c>%LOCALAPPDATA%\Gridline\Recovery</c>. The variable is read from the environment itself:
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> asks the shell for the known folder and ignores
    /// a changed <c>LOCALAPPDATA</c> (checked on Windows 11), which the tests rely on (R51). Falls back to the known
    /// folder when the variable is unset.</summary>
    public static string DefaultDirectory()
    {
        string? local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(local)) local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Gridline", "Recovery");
    }

    public string PathFor(Guid sessionId) => Path.Combine(Directory, sessionId.ToString("D") + Extension);

    public IReadOnlyList<RecoveryFile> List()
    {
        var files = new List<RecoveryFile>();
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return files;
            foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension))
            {
                if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out Guid id)) continue;
                files.Add(new RecoveryFile(id, path, File.GetLastWriteTime(path)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // an unreadable directory offers what was found so far
        }
        files.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
        return files;
    }

    public void Write(Workbook workbook, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        System.IO.Directory.CreateDirectory(Directory); // on demand (R48)
        GridlineFile.SaveToPath(workbook, PathFor(sessionId));
    }

    public bool TryDelete(Guid sessionId)
    {
        string path = PathFor(sessionId);
        try
        {
            File.Delete(path); // no error when it does not exist
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return !File.Exists(path);
        }
    }
}
