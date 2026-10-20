using Gridline.Core;
using Gridline.IO;

namespace Gridline.Cli;

/// <summary>`gridline script [--file &lt;path&gt;]` (§10.3): reads commands from stdin against a new or preloaded workbook.</summary>
public static class ScriptCommand
{
    public static int Run(string[] args, CliContext ctx)
    {
        // args[0] is "script"; the only forms are `script` and `script --file <path>`.
        Workbook wb;
        if (args.Length == 1) wb = new Workbook();
        else if (args.Length == 3 && args[1] == "--file")
        {
            if (args[2].Length == 0) throw new CliException(ExitCodes.Usage, "--file requires a non-empty path");
            wb = GridlineFile.LoadFromPath(args[2]); // failure maps to exit 2/3 before stdin is touched
        }
        else if (args.Length == 2 && args[1] == "--file") throw new CliException(ExitCodes.Usage, "--file requires a path");
        else throw new CliException(ExitCodes.Usage, "usage: gridline script [--file <path>]");

        var interpreter = new ScriptInterpreter(wb, ctx);
        bool errDelivered = false;
        while (true)
        {
            string? line = ReadLfLine(ctx.Stdin, out bool tooLong);
            if (line is null) break;
            if (tooLong) interpreter.RejectLine($"line too long (limit {MaxLineLength} characters)");
            else interpreter.Execute(line);
            try
            {
                ctx.FlushStdout(); // stream per command: output never accumulates
                errDelivered = interpreter.AnyError; // every ERR so far was in a flush that succeeded
            }
            catch (Exception ex) when (ex is not CliException)
            {
                // Output failure (e.g. broken pipe), §10.1, R31: 4 only if an ERR line was actually delivered, else 1.
                throw new CliException(errDelivered ? ExitCodes.ScriptError : ExitCodes.Usage,
                    "cannot write output: " + ex.Message);
            }
        }
        interpreter.Finish();
        try { ctx.FlushStdout(); }
        catch (Exception ex) when (ex is not CliException)
        {
            throw new CliException(errDelivered ? ExitCodes.ScriptError : ExitCodes.Usage,
                "cannot write output: " + ex.Message);
        }
        return interpreter.AnyError ? ExitCodes.ScriptError : ExitCodes.Success;
    }

    /// <summary>Longest script line, in UTF-16 units, not counting one trailing CR (§10.3, R33): 2 x MaxInputLength + 64. SETX escapes can double the raw length (a tab is 2 characters), and 64 covers the command word, address and spaces.</summary>
    public const int MaxLineLength = 2 * Workbook.MaxInputLength + 64;

    /// <summary>
    /// Reads up to the next LF (R31: TextReader.ReadLine would also split on a lone CR). The CR before LF is stripped by the
    /// interpreter. A line longer than <see cref="MaxLineLength"/> is consumed up to its LF without being buffered and
    /// reported through <paramref name="tooLong"/> (the returned text is then empty).
    /// </summary>
    private static string? ReadLfLine(TextReader reader, out bool tooLong)
    {
        tooLong = false;
        var sb = new System.Text.StringBuilder();
        int count = 0;
        int last = -1;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            if (c == '\n') return Finish(sb, count, last, ref tooLong);
            count++;
            last = c;
            if (tooLong) continue;
            if (count > MaxLineLength + 1) { tooLong = true; sb.Clear(); continue; } // +1: a trailing CR is not counted
            sb.Append((char)c);
        }
        return count > 0 ? Finish(sb, count, last, ref tooLong) : null;
    }

    private static string Finish(System.Text.StringBuilder sb, int count, int last, ref bool tooLong)
    {
        if (count == MaxLineLength + 1 && last != '\r') tooLong = true;
        return tooLong ? "" : sb.ToString();
    }
}
