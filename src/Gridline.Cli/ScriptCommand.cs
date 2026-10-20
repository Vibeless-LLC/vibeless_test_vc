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
        while (true)
        {
            string? line = ReadLfLine(ctx.Stdin, out bool tooLong);
            if (line is null) break;
            if (tooLong) interpreter.RejectLine($"line too long (limit {MaxLineLength} characters)");
            else interpreter.Execute(line);
            try
            {
                ctx.FlushStdout(); // stream per command: output never accumulates
            }
            catch (Exception ex) when (ex is not CliException)
            {
                // Output failure (e.g. broken pipe), §10.1: 4 if an ERR line was already produced, else 1; never "internal error".
                throw new CliException(interpreter.AnyError ? ExitCodes.ScriptError : ExitCodes.Usage,
                    "cannot write output: " + ex.Message);
            }
        }
        return interpreter.AnyError ? ExitCodes.ScriptError : ExitCodes.Success;
    }

    /// <summary>Longest script line read, in UTF-16 units (§10.3): MaxInputLength plus a 64-character allowance for the command word, address and spaces.</summary>
    public const int MaxLineLength = Workbook.MaxInputLength + 64;

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
        int c;
        while ((c = reader.Read()) >= 0)
        {
            if (c == '\n') return tooLong ? "" : sb.ToString();
            count++;
            if (tooLong) continue;
            if (count > MaxLineLength) { tooLong = true; sb.Clear(); continue; }
            sb.Append((char)c);
        }
        if (tooLong) return "";
        return count > 0 ? sb.ToString() : null;
    }
}
