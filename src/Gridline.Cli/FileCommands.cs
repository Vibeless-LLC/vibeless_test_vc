using Gridline.Core;
using Gridline.IO;

namespace Gridline.Cli;

/// <summary>`eval` and `set` (§10.1). Arguments are fully validated before the file is touched or anything is printed.</summary>
public static class FileCommands
{
    public static int Eval(string[] args, CliContext ctx)
    {
        // args[0] is "eval"
        if (args.Length < 2) throw Usage("eval requires <file> and at least one <addr> or --all");
        string file = args[1];
        bool all = false;
        var addresses = new List<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--all") all = true;
            else if (args[i].StartsWith('-')) throw Usage($"unknown option '{args[i]}'");
            else addresses.Add(args[i]);
        }
        if (all && addresses.Count > 0) throw Usage("--all cannot be combined with addresses");
        if (!all && addresses.Count == 0) throw Usage("eval requires at least one <addr> or --all");

        var canonical = new List<string>(addresses.Count);
        foreach (string a in addresses) canonical.Add(CellArgs.Canonical(a)); // all validated before any output

        Workbook wb = GridlineFile.LoadFromPath(file);
        foreach (string addr in all ? wb.GetUsedAddresses() : canonical)
            ctx.Out(addr + "\t" + wb.GetValue(addr).ToCanonicalString() + "\n");
        return ExitCodes.Success;
    }

    public static int Set(string[] args, CliContext ctx)
    {
        // args[0] is "set"; form: set <file> <addr> <input> [--out <path>]. The input is taken verbatim by position.
        if (args.Length < 4) throw Usage("set requires <file> <addr> <input>");
        string file = args[1], addr = args[2], input = args[3];
        string? outPath = null;
        // R30: strictly positional. Shapes: 4 args, or 6 args with args[4] == "--out". Anything else is a usage error.
        if (args.Length == 5 && args[4] == "--out") throw Usage("--out requires a path");
        if (args.Length == 6 && args[4] == "--out") outPath = args[5];
        else if (args.Length > 4) throw Usage("too many arguments");
        if (outPath is { Length: 0 }) throw Usage("--out requires a non-empty path");

        CellArgs.CheckInputLength(input);
        string canonical = CellArgs.Canonical(addr);

        Workbook wb = GridlineFile.LoadFromPath(file);
        wb.SetCell(canonical, input);
        GridlineFile.SaveToPath(wb, outPath ?? file);
        return ExitCodes.Success;
    }

    private static CliException Usage(string message) => new(ExitCodes.Usage, message);
}
