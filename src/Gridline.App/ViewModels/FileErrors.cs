using System.IO;
using Gridline.IO;

namespace Gridline.App.ViewModels;

/// <summary>The exceptions a file operation (Open, Save, Import CSV, Export CSV, reading a recovery file) reports to the
/// user instead of crashing (§8.4, §11.5): format and I/O failures from <see cref="GridlineFile"/> and
/// <see cref="CsvFormat"/>, and the path errors Path and the file system raise. Anything else is a bug and propagates.</summary>
internal static class FileErrors
{
    public static bool IsFileError(Exception ex) =>
        ex is GridlineFormatException or GridlineIOException or ArgumentException or NotSupportedException or IOException;
}
