using Gridline.IO;

namespace Gridline.Cli;

/// <summary>
/// The testable CLI entry point (§10.1, §10.2). Task 16 adds script mode by registering another command in
/// <see cref="Dispatch"/>; it reuses <see cref="CliContext"/>, <see cref="CliException"/> and <see cref="ExitCodes"/>.
/// </summary>
public static class CliApp
{
    public static int Run(string[] args, TextReader stdin, Stream stdout, Stream stderr) =>
        RunCore(args, new CliContext(stdin, stdout, stderr), Dispatch);

    /// <summary>Test seam: runs with an injectable dispatcher so the internal-error path is reachable.</summary>
    internal static int RunCore(string[] args, CliContext ctx, Func<string[], CliContext, int> dispatch)
    {
        int code;
        string? error = null;
        try
        {
            code = dispatch(args, ctx);
        }
        catch (CliException ex) { code = ex.ExitCode; error = ex.Message; }
        catch (GridlineIOException ex) { code = ExitCodes.FileIo; error = ex.Message; }
        catch (GridlineFormatException ex) { code = ExitCodes.Format; error = ex.Message; }
        catch (Exception ex) { code = ExitCodes.Usage; error = "internal error: " + ex.Message; }

        if (error is not null) ctx.DiscardStdout();
        try
        {
            ctx.FlushStdout();
        }
        catch (Exception ex) // closed pipe or handle: never escape Run (R29: exit codes stay 0-5)
        {
            if (code == ExitCodes.Success) { code = ExitCodes.Usage; error = "cannot write output: " + ex.Message; }
        }
        if (error is not null)
        {
            try { ctx.WriteError(error); } catch (Exception) { /* stderr unavailable; nothing more can be reported */ }
        }
        return code;
    }

    private static int Dispatch(string[] args, CliContext ctx)
    {
        if (args.Length == 0) throw new CliException(ExitCodes.Usage, "missing command (expected eval or set)");
        return args[0] switch
        {
            "eval" => FileCommands.Eval(args, ctx),
            "set" => FileCommands.Set(args, ctx),
            _ => throw new CliException(ExitCodes.Usage, $"unknown command '{args[0]}' (expected eval or set)"),
        };
    }
}
