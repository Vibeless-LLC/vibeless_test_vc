using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Gridline.Core;

namespace Gridline.IO.Tests;

/// <summary>File round-trip property (§8.1-§8.3): Serialize, Deserialize, Serialize is byte-identical and inputs survive.</summary>
public class RoundTripPropertyTests
{
    private static readonly char[] Specials =
        ['"', '\\', '\b', '\f', '\n', '\r', '\t', '\u0000', '\u001f', '\u007f', '/', 'é', '日', '\ud800', '\udc00', ' ', 'a', 'Z', '=', '<'];

    private static Gen<string> TextGen =>
        Gen.Elements(Specials).ArrayOf().Select(a => "t" + new string(a)) // leading 't' keeps it non-empty text
            .Or(Gen.Elements("\U0001F600", "x\U0001F680y", "say \"hi\"", "line1\nline2", "tab\there"));

    private static Gen<string> InputGen =>
        Gen.OneOf(
            Gen.Choose(-100000, 100000).Select(n => n.ToString()),
            Gen.Choose(0, 9999).Select(n => (n / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Gen.Elements("TRUE", "FALSE", "true"),
            TextGen,
            Gen.Elements("=1+2", "=A1*2", "=SUM(A1:B2)", "=IF(A1>1,\"y\",\"n\")", "=B3&\"x\"", "=1+", "=#N/A"));

    private static Gen<(string Address, string Input)> CellGen =>
        from col in Gen.Choose(1, 702)
        from row in Gen.OneOf(Gen.Choose(1, 30), Gen.Choose(99990, 100000))
        from input in InputGen
        select (new Core.Addressing.CellAddress(col, row).ToString(), input);

    [Fact]
    public void RoundTrip_Property()
    {
        var gen = CellGen.ListOf().Select(l => l.ToArray());
        Prop.ForAll(gen.ToArbitrary(), cells =>
        {
            var wb = new Workbook();
            foreach (var (a, i) in cells) wb.SetCell(a, i);

            var bytes1 = GridlineSerializer.Serialize(wb);
            var back = GridlineSerializer.Deserialize(bytes1);
            var bytes2 = GridlineSerializer.Serialize(back);

            if (!bytes1.AsSpan().SequenceEqual(bytes2)) return false;
            var used = wb.GetUsedAddresses();
            if (!used.SequenceEqual(back.GetUsedAddresses())) return false;
            foreach (var a in used)
                if (!string.Equals(wb.GetInput(a), back.GetInput(a), StringComparison.Ordinal)) return false;
            return true;
        }).QuickCheckThrowOnFailure();
    }
}
