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

    private static readonly string[] NamePool =
        ["Rate", "rate", "Total", "_x", "a.b", "Tax_2", "Zed", "q", "Name.With.Dots", "B2x", "Alpha"];

    private static Gen<(string Name, string Target)> NameGen =>
        from n in Gen.Elements(NamePool)
        from t in Gen.OneOf(
            Gen.Elements("$A$1", "$B$2", "$ZZ$100000", "$A$1:$A$1", "$A$1:$C$10", "$AA$5:$AB$9", "#REF!"),
            from c in Gen.Choose(1, 702)
            from r in Gen.Choose(1, 100000)
            select CellRef(c, r))
        select (n, t);

    private static string CellRef(int col, int row)
    {
        var a = new Core.Addressing.CellAddress(col, row).ToString(); // letters then digits
        int i = 0;
        while (char.IsLetter(a[i])) i++;
        return "$" + a[..i] + "$" + a[i..];
    }

    /// <summary>§14 with names: a canonical file (generated names, cell and range targets, and #REF! targets that only
    /// the loader accepts, R59) loads and re-saves byte-identically, and names, inputs and values survive.</summary>
    [Fact]
    public void RoundTrip_WithNames_Property()
    {
        var gen = from cells in CellGen.ListOf().Select(l => l.ToArray())
                  from names in NameGen.ListOf().Select(l => l.ToArray())
                  select (cells, names);
        Prop.ForAll(gen.ToArbitrary(), data =>
        {
            var (cells, names) = data;
            var plain = new Workbook();
            foreach (var (a, i) in cells) plain.SetCell(a, i);
            // Formulas that use the generated names, in rows (100..) the cell generator never touches.
            var users = names.Select((n, k) => (Address: new Core.Addressing.CellAddress(1 + k % 10, 100 + k / 10).ToString(),
                Name: n.Name, Input: k % 2 == 0 ? "=SUM(" + n.Name + ")" : "=" + n.Name + "+1")).ToArray();
            foreach (var u in users) plain.SetCell(u.Address, u.Input);
            string head = System.Text.Encoding.UTF8.GetString(GridlineSerializer.Serialize(plain));

            // Distinct names (case-insensitive, first wins), sorted as the writer sorts them.
            var distinct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var (n, t) in names)
                if (distinct.TryAdd(n, t)) order.Add(n);
            order.Sort(StringComparer.OrdinalIgnoreCase);
            var section = order.Count == 0
                ? "  \"names\": {},\n"
                : "  \"names\": {\n" + string.Join(",\n", order.Select(n => $"    \"{n}\": \"{distinct[n]}\"")) + "\n  },\n";
            string file = head.Replace("  \"names\": {},\n", section);
            var bytes1 = System.Text.Encoding.UTF8.GetBytes(file);

            var back = GridlineSerializer.Deserialize(bytes1);
            var bytes2 = GridlineSerializer.Serialize(back);
            if (!bytes1.AsSpan().SequenceEqual(bytes2)) return false;
            // Loaded values equal those of a workbook with the same names defined live; #REF! names give #REF!.
            var live = new Workbook();
            foreach (var a in plain.GetUsedAddresses()) live.SetCell(a, plain.GetInput(a));
            foreach (var n in order)
                if (distinct[n] != "#REF!") live.DefineName(n, distinct[n]);
            foreach (var u in users)
            {
                if (distinct[u.Name] == "#REF!")
                {
                    if (back.GetValue(u.Address).Error != "#REF!") return false;
                }
                else if (!live.GetValue(u.Address).Equals(back.GetValue(u.Address))) return false;
            }
            var expectedNames = order.Select(n => new KeyValuePair<string, string>(n, distinct[n]));
            if (!expectedNames.SequenceEqual(back.GetNames())) return false;
            // Without #REF! names the live workbook is equivalent, so every loaded value must match it.
            bool anyDeleted = order.Any(n => distinct[n] == "#REF!");
            foreach (var a in plain.GetUsedAddresses())
            {
                if (!string.Equals(plain.GetInput(a), back.GetInput(a), StringComparison.Ordinal)) return false;
                if (!anyDeleted && !live.GetValue(a).Equals(back.GetValue(a))) return false;
            }

            // A workbook built through DefineName (public path, no #REF!) round-trips too.
            var built = new Workbook();
            foreach (var (a, i) in cells) built.SetCell(a, i);
            foreach (var (n, t) in names)
                if (t != "#REF!") built.DefineName(n, t);
            var b1 = GridlineSerializer.Serialize(built);
            var b2 = GridlineSerializer.Serialize(GridlineSerializer.Deserialize(b1));
            return b1.AsSpan().SequenceEqual(b2);
        }).QuickCheckThrowOnFailure();
    }
}
