using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.Cli;

/// <summary>
/// A parsed address argument (CR1.7, R83, R87): the sheet prefix as written with quotes removed (null when unqualified)
/// and the canonical cell part (`a1` becomes `A1`).
/// </summary>
internal readonly record struct AddressArg(string? Sheet, string Cell);

/// <summary>Argument helpers shared by the file commands and script mode.</summary>
internal static class CellArgs
{
    /// <summary>Parses a single-cell address into its canonical form (`a1` becomes `A1`) or throws exit code 5.</summary>
    public static string Canonical(string text)
    {
        if (!CellAddress.TryParse(text, out var address))
            throw new CliException(ExitCodes.InvalidAddress, $"invalid cell address '{text}'");
        return address.ToString();
    }

    /// <summary>
    /// Parses a cell address with an optional sheet prefix under the R82 quoting rules (CR1.7, R83): <c>Data!a1</c>,
    /// <c>'My Sheet'!$B$2</c>, through Core's <see cref="SheetName.TrySplitQualified"/> (InternalsVisibleTo, D-58). A
    /// malformed prefix or cell part throws exit code 5. Whether the sheet exists is checked later by
    /// <see cref="Resolve"/>, once a workbook is loaded.
    /// </summary>
    public static AddressArg ParseAddress(string text)
    {
        if (!SheetName.TrySplitQualified(text, out string? sheet, out string rest) || !CellAddress.TryParse(rest, out var address))
            throw new CliException(ExitCodes.InvalidAddress, $"invalid cell address '{text}'");
        return new AddressArg(sheet, address.ToString());
    }

    /// <summary>
    /// The canonical address to pass to the workbook (R87): unqualified when given unqualified; otherwise the sheet in
    /// its defined casing, quoted only when required, then <c>!</c> and the cell. An unknown sheet throws exit code 5.
    /// This is the raw string; print it through <see cref="Echo"/>.
    /// </summary>
    public static string Resolve(Workbook wb, AddressArg arg)
    {
        if (arg.Sheet is null) return arg.Cell;
        string defined = DefinedSheetName(wb, arg.Sheet)
            ?? throw new CliException(ExitCodes.InvalidAddress, $"unknown sheet '{arg.Sheet}'");
        return SheetName.Format(defined) + "!" + arg.Cell;
    }

    /// <summary>
    /// Text that holds a sheet name (an address, a qualified refersTo, a bare name) as printed on a stdout data line
    /// (D-58): TAB, LF and CR, which CR1.1 allows in sheet names, are escaped with the §8.3 escapes so a name cannot
    /// forge output lines or fields. A sheet name cannot contain <c>\</c>, so text without control characters prints
    /// verbatim.
    /// </summary>
    public static string Echo(string text) => CellValue.EscapeText(text);

    /// <summary>The sheet's defined casing, looked up case-insensitively (R78), or null when no sheet has that name.</summary>
    public static string? DefinedSheetName(Workbook wb, string name)
    {
        foreach (string s in wb.SheetNames)
            if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    public static void CheckInputLength(string input)
    {
        if (input.Length > Workbook.MaxInputLength)
            throw new CliException(ExitCodes.Usage, $"input exceeds {Workbook.MaxInputLength} characters");
    }
}
