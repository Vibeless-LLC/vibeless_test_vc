namespace Gridline.IO;

/// <summary>Options for <see cref="CsvFormat.ImportInto"/> (CR1.6, R76).</summary>
public sealed class CsvOptions
{
    /// <summary>The field delimiter; <c>"</c>, CR and LF are rejected (R72). Default <c>,</c>.</summary>
    public char Delimiter { get; init; } = ',';
}
