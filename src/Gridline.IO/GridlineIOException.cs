namespace Gridline.IO;

/// <summary>
/// A file could not be read or written (missing file or directory, access denied, path is a directory, other I/O
/// failure; §8.4). The CLI maps this to exit code 2. The underlying exception is <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class GridlineIOException : Exception
{
    public GridlineIOException(string message, Exception? innerException) : base(message, innerException) { }
}
