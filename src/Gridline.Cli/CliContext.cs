using System.Text;

namespace Gridline.Cli;

/// <summary>
/// The streams a command runs against. Output is UTF-8 without BOM; commands write "\n" explicitly. Stdout is buffered
/// and only reaches the real stream on <see cref="Flush"/>, so a command that fails can call <see cref="DiscardStdout"/>
/// and leave stdout empty. Script mode shares this type.
/// </summary>
public sealed class CliContext
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly Stream _stdoutStream;
    private readonly Stream _stderrStream;
    private readonly StringBuilder _out = new();

    public CliContext(TextReader stdin, Stream stdout, Stream stderr)
    {
        Stdin = stdin;
        _stdoutStream = stdout;
        _stderrStream = stderr;
    }

    public TextReader Stdin { get; }

    /// <summary>Appends text to the pending stdout (no newline added).</summary>
    public void Out(string text) => _out.Append(text);

    public void DiscardStdout() => _out.Clear();

    /// <summary>Writes the pending stdout and clears it.</summary>
    public void FlushStdout()
    {
        string text = _out.ToString();
        _out.Clear();
        Write(_stdoutStream, text);
    }

    /// <summary>Writes the single `error: ...` line to stderr.</summary>
    public void WriteError(string message) => Write(_stderrStream, "error: " + OneLine(message) + "\n");

    /// <summary>Collapses CR and LF runs to one space (or nothing at the end) so a message is exactly one line.</summary>
    public static string OneLine(string message)
    {
        var sb = new StringBuilder(message.Length);
        bool pendingBreak = false;
        foreach (char c in message)
        {
            if (c is '\r' or '\n') { pendingBreak = true; continue; }
            if (pendingBreak && sb.Length > 0) sb.Append(' ');
            pendingBreak = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void Write(Stream stream, string text)
    {
        if (text.Length == 0) return;
        byte[] bytes = Utf8NoBom.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }
}
