using System.Text;
using Gridline.Core.Addressing;
using Gridline.Core.Dependencies;
using Gridline.Core.Parsing;
using Gridline.Core.Tests.Evaluation;

namespace Gridline.Core.Tests.Dependencies;

/// <summary>§6.1 static precedent extraction: every reference, range and name in the text, untaken branches included (R9, R20).</summary>
public class PrecedentExtractorTests
{
    private static readonly FunctionArityAdapter Arities = new();
    private static FormulaPrecedents Extract(string text) => PrecedentExtractor.Extract(FormulaParser.Parse(text, Arities));

    [Fact]
    public void Collects_cells_ranges_and_names_including_untaken_branches()
    {
        var p = Extract("IF(TRUE,1,B1+SUM($C$3:A1)) & myName & -D4% & (E5)");
        Assert.Equal(new[] { CellAddress.Parse("B1"), CellAddress.Parse("D4"), CellAddress.Parse("E5") },
            p.Cells.OrderBy(a => a.Row).ThenBy(a => a.Column));
        Assert.Equal(new[] { CellRange.Parse("A1:C3") }, p.Ranges); // normalised
        Assert.Equal(new[] { "MYNAME" }, p.Names);                  // R20: upper-cased key
    }

    [Fact]
    public void Duplicates_are_collapsed()
    {
        var p = Extract("A1+A1+$A$1+SUM(B1:B2,B2:B1)+x+X");
        Assert.Single(p.Cells);
        Assert.Single(p.Ranges);
        Assert.Single(p.Names);
    }

    [Fact]
    public void Many_names_are_deduplicated_case_insensitively_in_first_appearance_order()
    {
        // §6.1, R20: thousands of names (each written twice in different case) dedupe to one upper-cased key each.
        var text = new StringBuilder("rate0"); // 4 letters: never a cell reference
        for (int i = 1; i < 3000; i++) text.Append("+rate").Append(i);
        for (int i = 2999; i >= 0; i--) text.Append("+RATE").Append(i);
        var p = Extract(text.ToString());
        Assert.Equal(Enumerable.Range(0, 3000).Select(i => "RATE" + i), p.Names);
    }

    [Fact]
    public void Failed_parse_and_null_have_no_precedents()
    {
        var failed = FormulaParser.Parse("A1+", Arities);
        Assert.False(failed.Success);
        Assert.True(PrecedentExtractor.Extract(failed).IsEmpty);
        Assert.True(PrecedentExtractor.Extract(null).IsEmpty);
        Assert.True(Extract("1+2").IsEmpty);
    }

    [Fact]
    public void Deep_operator_chain_is_walked_without_recursion()
    {
        // ~16k-deep left-associative tree (R9) on a 256 KB thread.
        var sb = new StringBuilder("A1");
        for (int i = 2; i <= 16000; i++) sb.Append("+A").Append(1 + i % 50);
        var parsed = FormulaParser.Parse(sb.ToString(), Arities);
        Assert.True(parsed.Success);
        FormulaPrecedents? p = null;
        Eval.OnSmallStack(256 * 1024, () => p = PrecedentExtractor.Extract(parsed));
        Assert.Equal(50, p!.Cells.Count);
    }
}
