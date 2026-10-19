using Gridline.Core.Addressing;
using Gridline.Core.Dependencies;
using Gridline.Core.Parsing;
using static Gridline.Core.Tests.Dependencies.TestSheet;

namespace Gridline.Core.Tests.Dependencies;

/// <summary>§6.1 graph maintenance: forward edges, reverse lookup and the range index.</summary>
public class DependencyGraphTests
{
    private static readonly FunctionArityAdapter Arities = new();
    private static ParsedFormula P(string text) => FormulaParser.Parse(text, Arities);

    private static CellAddress[] Sorted(IEnumerable<CellAddress> cells) =>
        cells.OrderBy(a => a.Column).ThenBy(a => a.Row).ToArray();

    [Fact]
    public void Reverse_lookup_finds_direct_and_range_dependents_including_never_set_cells()
    {
        var g = new DependencyGraph();
        g.SetFormula(A("C1"), P("A1+1"));
        g.SetFormula(A("C2"), P("SUM(A1:B10)"));
        g.SetFormula(A("C3"), P("SUM(B5:B6)"));
        Assert.Equal(new[] { A("C1"), A("C2") }, Sorted(g.GetDependents(A("A1"))));
        Assert.Equal(new[] { A("C2"), A("C3") }, Sorted(g.GetDependents(A("B5"))));
        Assert.Equal(new[] { A("C2") }, Sorted(g.GetDependents(A("B10"))));
        Assert.Empty(g.GetDependents(A("B11")));
        Assert.Empty(g.GetDependents(A("C2")));
    }

    [Fact]
    public void Formula_changed_to_constant_drops_its_edges()
    {
        var g = new DependencyGraph();
        g.SetFormula(A("C1"), P("A1+SUM(B1:B3)+n"));
        Assert.True(g.IsFormulaCell(A("C1")));
        g.SetFormula(A("C1"), null);
        Assert.False(g.IsFormulaCell(A("C1")));
        Assert.Equal(0, g.FormulaCount);
        Assert.Empty(g.GetDependents(A("A1")));
        Assert.Empty(g.GetDependents(A("B2")));
        Assert.Empty(g.GetNameDependents("N"));
    }

    [Fact]
    public void Replacing_a_formula_replaces_its_edges()
    {
        var g = new DependencyGraph();
        g.SetFormula(A("C1"), P("A1"));
        g.SetFormula(A("C1"), P("SUM(B1:B2)"));
        Assert.Empty(g.GetDependents(A("A1")));
        Assert.Equal(new[] { A("C1") }, Sorted(g.GetDependents(A("B2"))));
        Assert.Equal(CellRange.Parse("B1:B2"), Assert.Single(g.GetPrecedents(A("C1")).Ranges));
    }

    [Fact]
    public void Shared_range_survives_until_its_last_dependent_goes()
    {
        var g = new DependencyGraph();
        g.SetFormula(A("C1"), P("SUM(A1:A3)"));
        g.SetFormula(A("C2"), P("SUM(A3:A1)"));
        g.SetFormula(A("C1"), null);
        Assert.Equal(new[] { A("C2") }, Sorted(g.GetDependents(A("A2"))));
        g.SetFormula(A("C2"), null);
        Assert.Empty(g.GetDependents(A("A2")));
    }

    [Fact]
    public void Name_precedents_are_recorded_case_insensitively()
    {
        var g = new DependencyGraph();
        g.SetFormula(A("C1"), P("Rate*2"));
        Assert.Equal(new[] { A("C1") }, g.GetNameDependents("RATE"));
        Assert.Equal(new[] { A("C1") }, g.GetNameDependents("rate"));
    }

    [Fact]
    public void Failed_parse_is_a_formula_cell_with_no_precedents()
    {
        var g = new DependencyGraph();
        g.SetFormula(A("C1"), P("A1+"));
        Assert.True(g.IsFormulaCell(A("C1")));
        Assert.True(g.GetPrecedents(A("C1")).IsEmpty);
    }

    [Fact]
    public void Range_index_handles_sliding_windows_and_whole_sheet_ranges()
    {
        var g = new DependencyGraph();
        for (int r = 1; r <= 1000; r++)
            g.SetFormula(new CellAddress(2, r), P($"SUM(A{r}:A{r + 9})"));
        g.SetFormula(A("C1"), P("SUM(A1:ZZ100000)"));
        // A500 lies in the windows starting at rows 491..500, plus the whole-sheet range.
        var deps = Sorted(g.GetDependents(A("A500")));
        Assert.Equal(11, deps.Length);
        Assert.Equal(Enumerable.Range(491, 10).Select(r => new CellAddress(2, r)).Append(A("C1")), deps);
        Assert.Equal(new[] { A("C1") }, Sorted(g.GetDependents(A("ZZ100000"))));
    }

    [Fact]
    public void Range_index_matches_brute_force_on_random_ranges()
    {
        var rng = new Random(1234);
        var g = new DependencyGraph();
        var ranges = new Dictionary<CellAddress, CellRange>();
        for (int i = 0; i < 400; i++)
        {
            var a = new CellAddress(rng.Next(1, 60), rng.Next(1, 3000));
            var b = new CellAddress(rng.Next(1, 60), rng.Next(1, 3000));
            if (rng.Next(3) == 0) b = new CellAddress(Math.Min(702, a.Column + rng.Next(0, 3)), Math.Min(100000, a.Row + rng.Next(0, 3)));
            var owner = new CellAddress(100 + i % 50, 1 + i / 50);
            var range = new CellRange(a, b);
            ranges[owner] = range;
            g.SetFormula(owner, P($"SUM({range})"));
        }
        // Remove a few to exercise index removal.
        foreach (var owner in ranges.Keys.Take(50).ToList()) { g.SetFormula(owner, null); ranges.Remove(owner); }
        for (int i = 0; i < 2000; i++)
        {
            var probe = new CellAddress(rng.Next(1, 62), rng.Next(1, 3002));
            var expected = ranges.Where(kv => kv.Value.Contains(probe)).Select(kv => kv.Key);
            Assert.Equal(Sorted(expected), Sorted(g.GetDependents(probe)));
        }
    }
}
