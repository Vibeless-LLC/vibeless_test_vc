using Gridline.Core;

namespace Gridline.IO;

/// <summary>
/// Reads and writes the canonical .gridline format (§8.1-§8.3, CR1.5): writes version 2, reads versions 1 and 2.
/// Writing is byte-exact (UTF-8 without BOM, LF, trailing LF); loading is strict and atomic: any failure throws
/// <see cref="GridlineFormatException"/> and no workbook exists.
/// </summary>
public static class GridlineSerializer
{
    /// <summary>
    /// The canonical bytes of <paramref name="workbook"/>, always format version 2 (CR1.5, §8.1): the active sheet, the
    /// workbook's <see cref="Workbook.GetNames"/> (sheet-qualified, R77, or <c>#REF!</c>, R59) sorted OrdinalIgnoreCase,
    /// and every sheet in workbook order with its cells row-major under unqualified addresses.
    /// </summary>
    public static byte[] Serialize(Workbook workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        return CanonicalWriter.Write(workbook);
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
