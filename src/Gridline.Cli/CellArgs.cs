using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.Cli;

/// <summary>Argument helpers shared by the file commands and, later, script mode.</summary>
public static class CellArgs
{
    /// <summary>Parses a single-cell address into its canonical form (`a1` becomes `A1`) or throws exit code 5.</summary>
    public static string Canonical(string text)
    {
        if (!CellAddress.TryParse(text, out var address))
            throw new CliException(ExitCodes.InvalidAddress, $"invalid cell address '{text}'");
        return address.ToString();
    }

    public static void CheckInputLength(string input)
    {
        if (input.Length > Workbook.MaxInputLength)
            throw new CliException(ExitCodes.Usage, $"input exceeds {Workbook.MaxInputLength} characters");
    }
}
