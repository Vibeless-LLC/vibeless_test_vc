using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.IO.Tests;

/// <summary>File round-trip property (§8.1-§8.3, §14; CR1.5 version 2 with sheets): Serialize, Deserialize, Serialize is
/// byte-identical and the state survives.</summary>
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
    /// the loader accepts, R59) loads and re-saves byte-identically, and names, inputs and values survive. CR1.5: the
    /// file is version 2, so its targets are written sheet-qualified (R86).</summary>
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
                : "  \"names\": {\n" + string.Join(",\n", order.Select(n => $"    \"{n}\": \"{Qualified(distinct[n])}\"")) + "\n  },\n";
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
            // Loaded names are qualified with the only sheet (CR1.4, R77); #REF! stays as it is (R59).
            var expectedNames = order.Select(n => new KeyValuePair<string, string>(n, Qualified(distinct[n])));
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

    private static string Qualified(string target) => target == "#REF!" ? "#REF!" : "Sheet1!" + target;

    // ---- §14 with sheets (CR1.5) ----

    /// <summary>Sheet names, including ones that need quoting (space, quote, digits first, cell-like, TRUE, non-ASCII,
    /// a JSON-escaped quote character).</summary>
    private static readonly string[] SheetPool =
        ["Data", "My Sheet", "O'Brien", "2024", "AB12", "TRUE", "日本", "x.y_z", "q\"t", "Sheet2"];

    /// <summary>Inputs that reference other sheets (qualified cells and ranges, quoted prefixes, a missing sheet), the
    /// workbook's names, or nothing.</summary>
    private static readonly string[] CrossSheetFormulas =
    [
        "=Data!A1*2", "='My Sheet'!B2+1", "=SUM('O''Brien'!A1:B3)", "='2024'!$A$1&\"x\"", "=AB12!C3-1", "='TRUE'!A1",
        "=日本!A2", "=SUM(Data!A1:A5)+Rate", "=Total*2", "=Missing!A1", "=A1+Sheet1!B2", "=SUM(Total)", "=1+",
    ];

    private static Gen<(int Sheet, string Address, string Input)> SheetCellGen(int sheets) =>
        from s in Gen.Choose(0, sheets - 1)
        from col in Gen.Choose(1, 6)
        from row in Gen.OneOf(Gen.Choose(1, 6), Gen.Choose(99999, 100000))
        from input in Gen.OneOf(InputGen, Gen.Elements(CrossSheetFormulas))
        select (s, new Core.Addressing.CellAddress(col, row).ToString(), input);

    private sealed record SheetCase(
        string[] Sheets, (int Sheet, string Address, string Input)[] Cells,
        (string Name, int Sheet, string Cells)[] Names, int Active, bool DeleteOne);

    private static Gen<SheetCase> SheetCaseGen =>
        from extra in Gen.Elements(SheetPool).ArrayOf()
        let sheets = new[] { "Sheet1" }.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray()
        from cells in SheetCellGen(sheets.Length).ArrayOf()
        from names in (from n in Gen.Elements("Rate", "Total", "Far", "q.r")
                       from s in Gen.Choose(0, sheets.Length - 1)
                       from t in Gen.Elements("$A$1", "$B$2", "$A$1:$A$5", "$C$3:$D$100000")
                       select (n, s, t)).ArrayOf()
        from active in Gen.Choose(0, sheets.Length - 1)
        from deleteOne in Gen.Elements(false, true)
        select new SheetCase(sheets, cells, names, active, deleteOne);

    /// <summary>Builds the case through the public API. With <see cref="SheetCase.DeleteOne"/> a sheet "Gone" that names
    /// and formulas point into is deleted, so the file holds #REF! names and #REF! references (R85).</summary>
    private static Workbook Build(SheetCase c)
    {
        var wb = new Workbook();
        for (int i = 1; i < c.Sheets.Length; i++) wb.AddSheet(c.Sheets[i]);
        if (c.DeleteOne)
        {
            wb.AddSheet("Gone", 0);
            wb.SetCell("Gone!A1", "5");
            wb.SetCell("Sheet1!F6", "=Gone!A1+SUM(Gone!A1:B2)");
            wb.DefineName("Lost", "Gone!$A$1");
        }
        foreach (var (s, a, input) in c.Cells) wb.SetCell(SheetName.Format(c.Sheets[s]) + "!" + a, input);
        foreach (var (n, s, t) in c.Names) wb.DefineName(n, SheetName.Format(c.Sheets[s]) + "!" + t);
        if (c.DeleteOne) wb.DeleteSheet("Gone");
        wb.ActiveSheet = c.Sheets[c.Active];
        return wb;
    }

    /// <summary>Sheets, active sheet, names, used cells, inputs and values all equal.</summary>
    internal static bool SameState(Workbook a, Workbook b)
    {
        if (!a.SheetNames.SequenceEqual(b.SheetNames, StringComparer.Ordinal)) return false;
        if (!string.Equals(a.ActiveSheet, b.ActiveSheet, StringComparison.Ordinal)) return false;
        if (!a.GetNames().SequenceEqual(b.GetNames())) return false;
        var used = a.GetUsedAddresses();
        if (!used.SequenceEqual(b.GetUsedAddresses())) return false;
        foreach (var address in used)
        {
            if (!string.Equals(a.GetInput(address), b.GetInput(address), StringComparison.Ordinal)) return false;
            if (!a.GetValue(address).Equals(b.GetValue(address))) return false;
        }
        return true;
    }

    /// <summary>§14 with sheets (CR1.5): multi-sheet workbooks with quoted sheet names, cross-sheet formulas, qualified
    /// names (and #REF! ones) and any active sheet save, load and save again byte-identically, with an equal state; the
    /// loaded workbook is clean with an empty history.</summary>
    [Fact]
    public void RoundTrip_WithSheets_Property()
    {
        Prop.ForAll(SheetCaseGen.ToArbitrary(), c =>
        {
            var wb = Build(c);
            var bytes1 = GridlineSerializer.Serialize(wb);
            var back = GridlineSerializer.Deserialize(bytes1);
            var bytes2 = GridlineSerializer.Serialize(back);
            if (!bytes1.AsSpan().SequenceEqual(bytes2)) return false;
            if (back.IsDirty || back.CanUndo || back.CanRedo) return false;
            return SameState(wb, back);
        }).QuickCheckThrowOnFailure();
    }
}
