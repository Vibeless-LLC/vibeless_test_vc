using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.Cli;

/// <summary>
/// A parsed address argument (CR1.7, R83, R87): the sheet prefix as written with quotes removed (null when unqualified)
/// and the canonical cell part (`a1` becomes `A1`).
/// </summary>
public readonly record struct AddressArg(string? Sheet, string Cell);

/// <summary>Argument helpers shared by the file commands and script mode.</summary>
public static class CellArgs
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
    /// <c>'My Sheet'!$B$2</c>. A malformed prefix or cell part throws exit code 5. Whether the sheet exists is checked
    /// later by <see cref="Resolve"/>, once a workbook is loaded.
    /// </summary>
    public static AddressArg ParseAddress(string text)
    {
        if (!TrySplitQualified(text, out string? sheet, out string rest) || !CellAddress.TryParse(rest, out var address))
            throw new CliException(ExitCodes.InvalidAddress, $"invalid cell address '{text}'");
        return new AddressArg(sheet, address.ToString());
    }

    /// <summary>
    /// Splits <paramref name="text"/> into an optional sheet prefix and the rest after the <c>!</c>, with the R82 prefix
    /// rules (D-58: built on the public <see cref="SheetName"/> checks, so the CLI needs no Core internals). A quoted
    /// prefix <c>'…'</c> uses <c>''</c> for <c>'</c>, must hold a valid sheet name and be followed at once by <c>!</c>;
    /// an unquoted prefix must be a valid name that needs no quoting. Text with no leading <c>'</c> and no <c>!</c> is
    /// unqualified: <paramref name="sheet"/> is null and <paramref name="rest"/> is the text. The rest is not checked.
    /// False for a malformed prefix.
    /// </summary>
    public static bool TrySplitQualified(string text, out string? sheet, out string rest)
    {
        sheet = null;
        rest = text;
        if (text.Length > 0 && text[0] == '\'')
        {
            if (!TryReadQuoted(text, out string name, out int end)) return false;
            if (end >= text.Length || text[end] != '!' || !SheetName.IsValid(name)) return false;
            sheet = name;
            rest = text[(end + 1)..];
            return true;
        }
        int bang = text.IndexOf('!');
        if (bang < 0) return true;
        string prefix = text[..bang];
        if (!SheetName.IsValid(prefix) || SheetName.RequiresQuoting(prefix)) return false;
        sheet = prefix;
        rest = text[(bang + 1)..];
        return true;
    }

    /// <summary>
    /// Reads the quoted name that starts <paramref name="text"/> (which begins with <c>'</c>): <c>''</c> stands for
    /// <c>'</c>. <paramref name="end"/> is the index just past the closing quote. False when the quote is unterminated.
    /// </summary>
    public static bool TryReadQuoted(string text, out string name, out int end)
    {
        var sb = new System.Text.StringBuilder();
        int i = 1;
        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                name = sb.ToString();
                end = i + 1;
                return true;
            }
            sb.Append(text[i++]);
        }
        name = "";
        end = text.Length;
        return false;
    }

    /// <summary>
    /// The canonical address to echo and pass to the workbook (R87): unqualified when given unqualified; otherwise the
    /// sheet in its defined casing, quoted only when required, then <c>!</c> and the cell. An unknown sheet throws exit
    /// code 5.
    /// </summary>
    public static string Resolve(Workbook wb, AddressArg arg)
    {
        if (arg.Sheet is null) return arg.Cell;
        string defined = DefinedSheetName(wb, arg.Sheet)
            ?? throw new CliException(ExitCodes.InvalidAddress, $"unknown sheet '{arg.Sheet}'");
        return SheetName.Format(defined) + "!" + arg.Cell;
    }

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
