using System.Text;
using FsCheck;
using FsCheck.Fluent;
using Gridline.Core;

namespace Gridline.IO.Tests;

/// <summary>CSV round-trip property (R71, §14, §8.5): with W1 = Import(Encode(F)) and W2 = Import(Export(W1)), every cell
/// has an equal value in W1 and W2 and Export(W2) is byte-equal to Export(W1).</summary>
public class CsvRoundTripPropertyTests
{
    private static readonly string[] Pool =
    [
        "", " ", "  ", "a", "hello world", "a,b", "a;b", "a\tb", "say \"hi\"", "\"", "\"\"", "l1\nl2", "l1\r\nl2", "l1\rl2",
        "\n", "\r\n", " lead", "trail ", " both ", "=1+2", "=SUM(A1:A3)", "'abc", "'", "=", "'=x", "0", "-5", "1.5", "1e3",
        "5%", "-12.5%", "00123", "007", "true", "TRUE", "False", "42", "3.14159", "1e-7", "café", "日本語",
        "\U0001F600", "#N/A", "x\"y", "a,\"b\",c", ";", ",", " 5", "5 ", "+3", ".5",
    ];

    private static Gen<string> FieldGen =>
        Gen.OneOf(
            Gen.Elements(Pool),
            Gen.Choose(-1000, 1000).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Gen.Elements(',', ';', '"', '\r', '\n', ' ', '=', '\'', 'a', '5', '%', '\t', 'é', '日', '.', 'e')
                .ArrayOf().Select(a => new string(a)));

    private static Gen<string[][]> GridGen =>
        FieldGen.ArrayOf().Select(a => a.Length > 6 ? a[..6] : a)
            .ArrayOf().Select(rows => rows.Length > 8 ? rows[..8] : rows)
            .Select(rows => rows.Select(r => r.Length == 0 ? [""] : r).ToArray());

    /// <summary>The test's own RFC 4180 encoder: quote when needed, CRLF after every record.</summary>
    private static byte[] Encode(string[][] grid, char delimiter)
    {
        var sb = new StringBuilder();
        foreach (var record in grid)
        {
            for (int j = 0; j < record.Length; j++)
            {
                if (j > 0) sb.Append(delimiter);
                string f = record[j];
                bool quote = f.AsSpan().IndexOfAny(delimiter, '"') >= 0 || f.AsSpan().IndexOfAny('\r', '\n') >= 0
                    || f.StartsWith(' ') || f.EndsWith(' ');
                if (quote) sb.Append('"').Append(f.Replace("\"", "\"\"")).Append('"');
                else sb.Append(f);
            }
            sb.Append("\r\n");
        }
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static bool SameValues(Workbook a, Workbook b)
    {
        var addresses = a.GetUsedAddresses();
        if (!addresses.SequenceEqual(b.GetUsedAddresses())) return false;
        return addresses.All(x => a.GetValue(x).Equals(b.GetValue(x)));
    }

    [Theory]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData('\t')]
    public void CsvRoundTrip_Property(char delimiter)
    {
        Prop.ForAll(GridGen.ToArbitrary(), grid =>
        {
            var w1 = CsvFormat.Import(Encode(grid, delimiter), delimiter);
            var e1 = CsvFormat.Export(w1, delimiter);
            var w2 = CsvFormat.Import(e1, delimiter);
            return SameValues(w1, w2) && CsvFormat.Export(w2, delimiter).AsSpan().SequenceEqual(e1);
        }).QuickCheckThrowOnFailure();
    }
}
