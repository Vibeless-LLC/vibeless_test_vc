namespace Gridline.IO;

/// <summary>A .gridline file is malformed, unsupported or violates the §8.2 loader rules; nothing was loaded.</summary>
public sealed class GridlineFormatException : Exception
{
    public GridlineFormatException(string message) : base(message) { }

    public GridlineFormatException(string message, Exception? innerException) : base(message, innerException) { }
}
