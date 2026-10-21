using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.IO;

/// <summary>
/// Reads and writes the canonical .gridline format (§8.1-§8.3). Writing is byte-exact (UTF-8 without BOM, LF, trailing
/// LF); loading is strict and atomic: any failure throws <see cref="GridlineFormatException"/> and no workbook exists.
/// </summary>
public static class GridlineSerializer
{
    /// <summary>
    /// The canonical bytes of <paramref name="workbook"/> (§8.1). Names are the workbook's
    /// <see cref="Workbook.GetNames"/>, sorted OrdinalIgnoreCase, including <c>#REF!</c> targets (R59).
    /// </summary>
    /// <remarks>Interim bridge until the version 2 writer (CR1.5, D-53): names are sheet-qualified in the workbook (R77),
    /// but a version 1 file holds one sheet and unqualified names, so for a one-sheet workbook the sheet prefix is
    /// removed and the loader qualifies it again with the first sheet.</remarks>
    public static byte[] Serialize(Workbook workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        return CanonicalWriter.Write(workbook, VersionOneNames(workbook));
    }

    /// <summary>The names as a version 1 file writes them: unqualified when the workbook has exactly one sheet.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> VersionOneNames(Workbook workbook)
    {
        var names = workbook.GetNames();
        if (workbook.SheetNames.Count != 1 || names.Count == 0) return names;
        string prefix = SheetName.Format(workbook.SheetNames[0]) + "!";
        var result = new KeyValuePair<string, string>[names.Count];
        for (int i = 0; i < result.Length; i++)
        {
            string refersTo = names[i].Value;
            result[i] = new(names[i].Key, refersTo.StartsWith(prefix, StringComparison.Ordinal) ? refersTo[prefix.Length..] : refersTo);
        }
        return result;
    }

    /// <summary>Writes the canonical bytes of <paramref name="workbook"/> to <paramref name="destination"/> (§8.1).</summary>
    public static void Serialize(Workbook workbook, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Serialize(workbook));
    }

    /// <summary>Loads a workbook from .gridline bytes (§8.2, R27).</summary>
    /// <exception cref="GridlineFormatException">The content is invalid in any §8.2 way; nothing is loaded.</exception>
    public static Workbook Deserialize(ReadOnlySpan<byte> data) => StrictLoader.Load(data);

    /// <summary>Loads a workbook from a stream positioned at the start of .gridline content (§8.2, R27).</summary>
    /// <exception cref="GridlineFormatException">The content is invalid in any §8.2 way; nothing is loaded.</exception>
    public static Workbook Deserialize(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return StrictLoader.Load(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }
}
