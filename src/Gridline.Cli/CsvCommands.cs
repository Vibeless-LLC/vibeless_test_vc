using Gridline.Core;
using Gridline.IO;

namespace Gridline.Cli;

/// <summary>
/// `import-csv` and `export-csv` (§10.2, R72). Strictly positional like `set`: two paths, optionally followed by
/// `--delimiter &lt;c&gt;`. Arguments (including the delimiter) are validated before any file is touched; nothing is printed on
/// success. Failures map through <see cref="CliApp"/>: missing or unwritable file 2, bad .gridline or CSV content 3.
/// </summary>
public static class CsvCommands
{
    public static int Import(string[] args)
    {
        var (input, output, delimiter) = Parse(args, "import-csv", "<csv> <out.gridline>");
        Workbook wb = CsvFormat.ImportFromPath(input, delimiter);
        GridlineFile.SaveToPath(wb, output);
        return ExitCodes.Success;
    }

    public static int Export(string[] args)
    {
        var (input, output, delimiter) = Parse(args, "export-csv", "<file> <out.csv>");
        Workbook wb = GridlineFile.LoadFromPath(input);
        CsvFormat.ExportToPath(wb, output, delimiter);
        return ExitCodes.Success;
    }

    private static (string input, string output, char delimiter) Parse(string[] args, string command, string shape)
    {
        // args[0] is the command. Shapes: 3 args, or 5 args with args[3] == "--delimiter".
        if (args.Length < 3) throw Usage($"{command} requires {shape}");
        char delimiter = ',';
        if (args.Length == 4 && args[3] == "--delimiter") throw Usage("--delimiter requires a character");
        if (args.Length == 5 && args[3] == "--delimiter") delimiter = ParseDelimiter(args[4]);
        else if (args.Length > 3) throw Usage("too many arguments");
        if (args[1].Length == 0 || args[2].Length == 0) throw Usage("file arguments must not be empty");
        return (args[1], args[2], delimiter);
    }

    private static char ParseDelimiter(string text)
    {
        if (text.Length != 1) throw Usage("--delimiter requires exactly one character");
        if (text[0] is '"' or '\r' or '\n') throw Usage("--delimiter must not be a quote, CR or LF");
        return text[0];
    }

    private static CliException Usage(string message) => new(ExitCodes.Usage, message);
}
