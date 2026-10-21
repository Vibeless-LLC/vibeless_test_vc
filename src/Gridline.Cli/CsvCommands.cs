using Gridline.Core;
using Gridline.Core.Addressing;
using Gridline.IO;

namespace Gridline.Cli;

/// <summary>
/// `import-csv` and `export-csv` (§10.2, R72, CR1.7, R87): two paths, then the options `--delimiter &lt;c&gt;` and
/// `--sheet &lt;name&gt;` in either order, each at most once. Arguments (the delimiter and the sheet name's validity) are
/// validated before any file is touched; nothing is printed on success. Failures map through <see cref="CliApp"/>:
/// missing or unwritable file 2, bad .gridline or CSV content 3, bad arguments or an unknown sheet 1.
/// </summary>
public static class CsvCommands
{
    public static int Import(string[] args)
    {
        var (input, output, delimiter, sheet) = Parse(args, "import-csv", "<csv> <out.gridline>");
        Workbook imported = CsvFormat.ImportFromPath(input, delimiter);
        Workbook wb = sheet is null ? imported : SingleSheet(imported, sheet);
        GridlineFile.SaveToPath(wb, output);
        return ExitCodes.Success;
    }

    public static int Export(string[] args)
    {
        var (input, output, delimiter, sheet) = Parse(args, "export-csv", "<file> <out.csv>");
        Workbook wb = GridlineFile.LoadFromPath(input);
        if (sheet is not null && CellArgs.DefinedSheetName(wb, sheet) is null)
            throw Usage($"unknown sheet '{sheet}'"); // R87: exit 1, before the output is written
        CsvFormat.ExportToPath(wb, output, delimiter, sheet);
        return ExitCodes.Success;
    }

    /// <summary>
    /// R87: a workbook whose only sheet is <paramref name="name"/>, holding the imported inputs verbatim. The sheet is
    /// renamed while still empty, so no formula is rewritten (an imported <c>=Sheet1!A1</c> keeps its text), and then the
    /// cells are applied as one batch, so formulas naming <paramref name="name"/> resolve.
    /// </summary>
    private static Workbook SingleSheet(Workbook imported, string name)
    {
        var wb = new Workbook();
        wb.RenameSheet(wb.ActiveSheet, name);
        var cells = new List<KeyValuePair<string, string>>();
        foreach (string a in imported.GetUsedAddresses()) cells.Add(new(a, imported.GetInput(a)));
        if (cells.Count > 0) wb.ApplyBatch(cells);
        return wb;
    }

    private static (string input, string output, char delimiter, string? sheet) Parse(string[] args, string command, string shape)
    {
        // args[0] is the command; args[1] and args[2] are the paths; then option/value pairs.
        if (args.Length < 3) throw Usage($"{command} requires {shape}");
        char? delimiter = null;
        string? sheet = null;
        for (int i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--delimiter":
                    if (i + 1 >= args.Length) throw Usage("--delimiter requires a character");
                    if (delimiter is not null) throw Usage("--delimiter given more than once");
                    delimiter = ParseDelimiter(args[++i]);
                    break;
                case "--sheet":
                    if (i + 1 >= args.Length || args[i + 1] is "--sheet" or "--delimiter") throw Usage("--sheet requires a sheet name");
                    if (sheet is not null) throw Usage("--sheet given more than once");
                    sheet = args[++i];
                    if (!SheetName.IsValid(sheet)) throw Usage($"invalid sheet name '{sheet}'");
                    break;
                default:
                    throw Usage("too many arguments");
            }
        }
        if (args[1].Length == 0 || args[2].Length == 0) throw Usage("file arguments must not be empty");
        return (args[1], args[2], delimiter ?? ',', sheet);
    }

    private static char ParseDelimiter(string text)
    {
        if (text.Length != 1) throw Usage("--delimiter requires exactly one character");
        if (text[0] is '"' or '\r' or '\n') throw Usage("--delimiter must not be a quote, CR or LF");
        return text[0];
    }

    private static CliException Usage(string message) => new(ExitCodes.Usage, message);
}
