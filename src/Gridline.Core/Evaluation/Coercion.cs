using System.Globalization;

namespace Gridline.Core.Evaluation;

/// <summary>
/// Value coercions of §6.2, shared by the operators (§6.3) and the function library (§7). Every method passes an
/// error input through unchanged.
/// </summary>
public static class Coercion
{
    /// <summary>Longest text a formula may produce (§6.3); longer results are #VALUE!.</summary>
    public const int MaxTextLength = 32767;

    private static readonly CellValue ValueError = CellValue.FromError(ErrorCodes.Value);

    /// <summary>
    /// Number coercion (§6.2): number as is; boolean 1/0; blank 0; text trimmed of U+0020 only and then matched by the
    /// §4.3 number grammar (<see cref="NumberGrammar"/>), else #VALUE!. On failure <paramref name="error"/> is the
    /// input error or #VALUE!.
    /// </summary>
    public static bool TryToNumber(CellValue value, out double number, out CellValue error)
    {
        error = default;
        switch (value.Kind)
        {
            case ValueKind.Number: number = value.Number; return true;
            case ValueKind.Boolean: number = value.Boolean ? 1 : 0; return true;
            case ValueKind.Blank: number = 0; return true;
            case ValueKind.Text:
                if (NumberGrammar.TryParse(value.Text.AsSpan().Trim(' '), out number)) return true;
                error = ValueError;
                return false;
            default:
                number = 0;
                error = value;
                return false;
        }
    }

    /// <summary>Number value, or the error (the input error, or #VALUE! for non-numeric text).</summary>
    public static CellValue ToNumber(CellValue value) =>
        TryToNumber(value, out var n, out var error) ? CellValue.FromNumber(n) : error;

    /// <summary>Text coercion (§6.2): number via <see cref="NumberToText"/>; boolean "TRUE"/"FALSE"; blank "".</summary>
    public static CellValue ToText(CellValue value) => value.Kind switch
    {
        ValueKind.Text => value,
        ValueKind.Error => value,
        _ => CellValue.FromText(ScalarToText(value)),
    };

    /// <summary>
    /// Boolean coercion (§6.2): number non-zero is TRUE; blank FALSE; text "TRUE"/"FALSE" case-insensitively with no
    /// trimming, other text (numeric text included) #VALUE!.
    /// </summary>
    public static CellValue ToBoolean(CellValue value)
    {
        switch (value.Kind)
        {
            case ValueKind.Boolean:
            case ValueKind.Error: return value;
            case ValueKind.Number: return CellValue.FromBoolean(value.Number != 0);
            case ValueKind.Blank: return CellValue.FromBoolean(false);
            default:
                var t = value.Text;
                if (string.Equals(t, "TRUE", StringComparison.OrdinalIgnoreCase)) return CellValue.FromBoolean(true);
                if (string.Equals(t, "FALSE", StringComparison.OrdinalIgnoreCase)) return CellValue.FromBoolean(false);
                return ValueError;
        }
    }

    /// <summary>
    /// Text form of a number (§6.2, ruling R14): exactly <c>ToString("G15", InvariantCulture)</c>, e.g. "1E-05",
    /// "1.25E-07", "1E+15"; the only adjustment is that -0 prints as "0".
    /// </summary>
    public static string NumberToText(double number) =>
        number == 0 ? "0" : number.ToString("G15", CultureInfo.InvariantCulture);

    /// <summary>Text of a non-error, non-text value; text returns itself.</summary>
    internal static string ScalarToText(CellValue value) => value.Kind switch
    {
        ValueKind.Number => NumberToText(value.Number),
        ValueKind.Boolean => value.Boolean ? "TRUE" : "FALSE",
        ValueKind.Text => value.Text,
        ValueKind.Blank => "",
        _ => throw new ArgumentException("Errors have no text form.", nameof(value)),
    };
}
