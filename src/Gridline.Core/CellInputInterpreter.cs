namespace Gridline.Core;

/// <summary>Applies the §4.3 input rules in order; the first match wins.</summary>
public static class CellInputInterpreter
{
    /// <summary>Maximum input length in UTF-16 code units (§4.3).</summary>
    public const int MaxInputLength = 32767;

    public static CellInputResult Interpret(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaxInputLength)
            throw new ArgumentException($"Input exceeds {MaxInputLength} UTF-16 code units.", nameof(input));

        if (input.Length == 0) return new(CellInputKind.Blank, CellValue.Blank);
        if (input[0] == '=') return new(CellInputKind.Formula, CellValue.Blank, input.Substring(1));
        if (input[0] == '\'') return new(CellInputKind.Text, CellValue.FromText(input.Substring(1)));
        if (input.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            return new(CellInputKind.Boolean, CellValue.FromBoolean(true));
        if (input.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            return new(CellInputKind.Boolean, CellValue.FromBoolean(false));
        if (NumberGrammar.TryParse(input, out var d))
            return new(CellInputKind.Number, CellValue.FromNumber(d));
        return new(CellInputKind.Text, CellValue.FromText(input));
    }
}
