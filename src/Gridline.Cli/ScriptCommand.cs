using Gridline.IO;

namespace Gridline.Cli;

/// <summary>`gridline script [--file &lt;path&gt;]` (§10.3): reads commands from stdin against a new or preloaded workbook.</summary>
public static class ScriptCommand
{
    public static int Run(string[] args, CliContext ctx)
    {
        // args[0] is "script"; the only forms are `script` and `script --file <path>`.
        Core.Workbook wb;
        if (args.Length == 1) wb = new Core.Workbook();
        else if (args.Length == 3 && args[1] == "--file")
        {
            if (args[2].Length == 0) throw new CliException(ExitCodes.Usage, "--file requires a non-empty path");
            wb = GridlineFile.LoadFromPath(args[2]); // failure maps to exit 2/3 before stdin is touched
        }
        else if (args.Length == 2 && args[1] == "--file") throw new CliException(ExitCodes.Usage, "--file requires a path");
        else throw new CliException(ExitCodes.Usage, "usage: gridline script [--file <path>]");

        var interpreter = new ScriptInterpreter(wb, ctx);
        string? line;
        while ((line = ReadLfLine(ctx.Stdin)) is not null)
        {
            interpreter.Execute(line);
            ctx.FlushStdout(); // stream per command: output never accumulates
        }
        return interpreter.AnyError ? ExitCodes.ScriptError : ExitCodes.Success;
    }

    /// <summary>Reads up to the next LF (R31: TextReader.ReadLine would also split on a lone CR). The CR before LF is stripped by the interpreter.</summary>
    private static string? ReadLfLine(TextReader reader)
    {
        var sb = new System.Text.StringBuilder();
        int c;
        while ((c = reader.Read()) >= 0)
        {
            if (c == '\n') return sb.ToString();
            sb.Append((char)c);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }
}
