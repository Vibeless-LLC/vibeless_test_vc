using System.Diagnostics;
using System.Globalization;
using Gridline.Core.Dependencies;
using Gridline.Core.Functions;
using Gridline.Core.Parsing;
using static Gridline.Core.Tests.Dependencies.TestSheet;

namespace Gridline.Core.Tests.Dependencies;

/// <summary>§6.1 incremental recalc, §6.7 iterative scheduling and cycle detection (R9, R21, R22).</summary>
public class RecalculationTests
{
    [Fact]
    public void Edit_recomputes_dependents_in_topological_order()
    {
        var s = new TestSheet();
        s.Load(("A1", "1"), ("B1", "=A1*2"), ("C1", "=B1+A1"), ("D1", "=C1&\"!\""));
        Assert.Equal(N(3), s["C1"]);
        var r = s.Set("A1", "5");
        Assert.Equal(N(10), s["B1"]);
        Assert.Equal(N(15), s["C1"]);
        Assert.Equal(CellValue.FromText("15!"), s["D1"]);
        Assert.Equal(3, r.EvaluatedFormulaCount);
    }

    [Fact]
    public void Diamond_evaluates_each_formula_once()
    {
        var s = new TestSheet();
        s.Load(("A1", "1"), ("B1", "=A1+1"), ("B2", "=A1+2"), ("C1", "=B1+B2"), ("D1", "=C1+B1+A1"));
        int before = s.ComputedWrites;
        var r = s.Set("A1", "2");
        Assert.Equal(4, r.EvaluatedFormulaCount);
        Assert.Equal(4, s.ComputedWrites - before);
        Assert.Equal(N(7), s["C1"]);
        Assert.Equal(N(12), s["D1"]);
    }

    [Fact]
    public void Count_is_formula_cells_in_changed_set_plus_dependents()
    {
        var s = new TestSheet();
        s.Load(("A1", "1"), ("A2", "=A1"), ("A3", "=A2"), ("Z9", "=1"));
        // Changing a formula cell: it plus its one dependent.
        var r = s.Set("A2", "=A1+10");
        Assert.Equal(2, r.EvaluatedFormulaCount);
        Assert.Equal(N(11), s["A3"]);
        // Several changed cells at once; overlapping dependents are counted once.
        r = s.SetMany(("A1", "2"), ("A2", "=A1+20"), ("Z9", "=2"));
        Assert.Equal(3, r.EvaluatedFormulaCount);
        Assert.Equal(N(22), s["A3"]);
        // A constant with no dependents: nothing is evaluated.
        r = s.Set("Q5", "7");
        Assert.Equal(0, r.EvaluatedFormulaCount);
        Assert.Empty(r.ChangedCells);
    }

    [Fact]
    public void Self_reference_is_circ()
    {
        var s = new TestSheet();
        var r = s.Set("A1", "=A1+1");
        Assert.Equal(E(ErrorCodes.Circ), s["A1"]);
        Assert.Equal(1, r.EvaluatedFormulaCount);
        Assert.Equal(new[] { A("A1") }, r.ChangedCells);
    }

    [Fact]
    public void Three_cell_cycle_is_circ_and_downstream_sees_an_ordinary_error()
    {
        var s = new TestSheet();
        s.Load(("A1", "=C1+1"), ("B1", "=A1+1"), ("C1", "=B1+1"), ("D1", "=A1*2"), ("E1", "=ISERROR(D1)"));
        Assert.Equal(E(ErrorCodes.Circ), s["A1"]);
        Assert.Equal(E(ErrorCodes.Circ), s["B1"]);
        Assert.Equal(E(ErrorCodes.Circ), s["C1"]);
        Assert.Equal(E(ErrorCodes.Circ), s["D1"]); // R22: evaluated normally, propagates the error
        Assert.Equal(CellValue.FromBoolean(true), s["E1"]);
        var r = s.Set("B1", "=A1+2"); // still a cycle
        Assert.Equal(5, r.EvaluatedFormulaCount); // R21: cycle members count
        Assert.Equal(E(ErrorCodes.Circ), s["B1"]);
        Assert.Empty(r.ChangedCells);
    }

    [Fact]
    public void Two_independent_cycles_sharing_a_downstream_cell_propagate_circ_and_count_it_once()
    {
        // §6.7, R21, R22: two separate SCCs {A1,B1} and {C1,D1}; E1 depends on both but is in neither.
        var s = new TestSheet();
        var load = s.Load(("A1", "=B1+1"), ("B1", "=A1+1"), ("C1", "=D1+1"), ("D1", "=C1+1"), ("E1", "=A1+C1"));
        Assert.Equal(5, load.EvaluatedFormulaCount);
        foreach (var cell in new[] { "A1", "B1", "C1", "D1" }) Assert.Equal(E(ErrorCodes.Circ), s[cell]);
        Assert.Equal(E(ErrorCodes.Circ), s["E1"]); // via propagation of A1's error

        // An edit inside one cycle reaches that cycle plus E1 only.
        var r = s.Set("A1", "=B1+2");
        Assert.Equal(3, r.EvaluatedFormulaCount);
        Assert.Empty(r.ChangedCells);

        // Edits inside both cycles: E1 is reached from both but evaluated and counted once.
        int before = s.ComputedWrites;
        r = s.SetMany(("A1", "=B1+3"), ("C1", "=D1+3"));
        Assert.Equal(5, r.EvaluatedFormulaCount);
        Assert.Equal(5, s.ComputedWrites - before);
        Assert.Equal(E(ErrorCodes.Circ), s["E1"]);

        // Breaking one cycle: E1 still sees the other cycle's #CIRC!.
        r = s.Set("B1", "1");
        Assert.Equal(2, r.EvaluatedFormulaCount); // A1 and E1
        Assert.Equal(N(4), s["A1"]);
        Assert.Equal(E(ErrorCodes.Circ), s["E1"]);
        Assert.Equal(E(ErrorCodes.Circ), s["C1"]);

        // Breaking the other: E1 gets a number.
        r = s.Set("D1", "10");
        Assert.Equal(2, r.EvaluatedFormulaCount); // C1 and E1
        Assert.Equal(N(13), s["C1"]);
        Assert.Equal(N(17), s["E1"]);
        Assert.Equal(new[] { A("C1"), A("E1") }, r.ChangedCells);
    }

    [Fact]
    public void Downstream_cells_of_a_cycle_are_evaluated_not_marked_as_members()
    {
        // R22: only SCC members get #CIRC!; downstream formulas are evaluated, so functions that inspect or
        // swallow errors produce ordinary values, and a chain below them computes normally.
        var s = new TestSheet();
        var load = s.Load(
            ("A1", "=B1"), ("B1", "=A1"),           // the cycle
            ("C1", "=IFERROR(A1,10)+1"),           // catches the error
            ("D1", "=C1*2"),                        // downstream of a downstream cell
            ("E1", "=ISERROR(B1)"),
            ("F1", "=COUNT(A1:B1)+5"),              // range over the cycle; COUNT ignores errors
            ("G1", "=A1+1"));                       // plain propagation
        Assert.Equal(7, load.EvaluatedFormulaCount); // R21
        Assert.Equal(E(ErrorCodes.Circ), s["A1"]);
        Assert.Equal(E(ErrorCodes.Circ), s["B1"]);
        Assert.Equal(N(11), s["C1"]);
        Assert.Equal(N(22), s["D1"]);
        Assert.Equal(CellValue.FromBoolean(true), s["E1"]);
        Assert.Equal(N(5), s["F1"]);
        Assert.Equal(E(ErrorCodes.Circ), s["G1"]);

        // Re-evaluated on an edit inside the cycle: still ordinary values, all seven counted.
        var r = s.Set("B1", "=A1*1");
        Assert.Equal(7, r.EvaluatedFormulaCount);
        Assert.Equal(N(22), s["D1"]);
        Assert.Equal(N(5), s["F1"]);
        Assert.Empty(r.ChangedCells);

        // Editing a downstream cell evaluates it and its dependents, but not the cycle upstream of it.
        r = s.Set("C1", "=IFERROR(A1,20)+1");
        Assert.Equal(2, r.EvaluatedFormulaCount);
        Assert.Equal(N(42), s["D1"]);
    }

    [Fact]
    public void Iferror_over_a_cycle_returns_its_fallback()
    {
        var s = new TestSheet();
        s.Load(("A1", "=B1"), ("B1", "=A1"), ("C1", "=IFERROR(A1,0)"));
        Assert.Equal(E(ErrorCodes.Circ), s["A1"]);
        Assert.Equal(N(0), s["C1"]);
    }

    [Fact]
    public void Cycle_through_a_range_is_circ()
    {
        var s = new TestSheet();
        s.Load(("A2", "1"), ("A3", "2"), ("A1", "=SUM(A1:A3)"), ("B1", "=IFERROR(A1,-1)"));
        Assert.Equal(E(ErrorCodes.Circ), s["A1"]);
        Assert.Equal(N(-1), s["B1"]);
        // A cycle formed by a range edge plus a cell edge.
        var t = new TestSheet();
        t.Load(("A1", "=B1"), ("B1", "=SUM(A1:A2)"));
        Assert.Equal(E(ErrorCodes.Circ), t["A1"]);
        Assert.Equal(E(ErrorCodes.Circ), t["B1"]);
    }

    [Fact]
    public void Breaking_a_cycle_restores_values()
    {
        var s = new TestSheet();
        s.Load(("A1", "=C1+1"), ("B1", "=A1+1"), ("C1", "=B1+1"), ("D1", "=B1*10"));
        Assert.Equal(E(ErrorCodes.Circ), s["B1"]);
        var r = s.Set("C1", "5");
        Assert.Equal(N(6), s["A1"]);
        Assert.Equal(N(7), s["B1"]);
        Assert.Equal(N(70), s["D1"]);
        Assert.Equal(3, r.EvaluatedFormulaCount);
        // Breaking a self-reference by rewriting the formula.
        var t = new TestSheet();
        t.Load(("A1", "=A1+1"), ("B1", "=A1"));
        t.Set("A1", "=2");
        Assert.Equal(N(2), t["A1"]);
        Assert.Equal(N(2), t["B1"]);
    }

    [Fact]
    public void Setting_a_never_used_blank_cell_inside_a_range_triggers_dependents()
    {
        var s = new TestSheet();
        s.Load(("B1", "=SUM(A1:A100)"), ("B2", "=COUNTBLANK(A1:A100)"));
        Assert.Equal(N(0), s["B1"]);
        var r = s.Set("A57", "4");
        Assert.Equal(N(4), s["B1"]);
        Assert.Equal(N(99), s["B2"]);
        Assert.Equal(2, r.EvaluatedFormulaCount);
        // Clearing it again also triggers them.
        r = s.Set("A57", "");
        Assert.Equal(N(0), s["B1"]);
        Assert.Equal(N(100), s["B2"]);
        Assert.Equal(2, r.EvaluatedFormulaCount);
    }

    [Fact]
    public void Precedents_in_an_untaken_if_branch_are_still_edges()
    {
        var s = new TestSheet();
        s.Load(("A1", "TRUE"), ("C1", "=IF(A1,1,B1)"));
        var r = s.Set("B1", "9");
        Assert.Equal(1, r.EvaluatedFormulaCount);
        Assert.Empty(r.ChangedCells); // re-evaluated, value unchanged
        r = s.Set("A1", "FALSE");
        Assert.Equal(N(9), s["C1"]);
        Assert.Equal(new[] { A("C1") }, r.ChangedCells);
    }

    [Fact]
    public void Formula_changed_to_constant_no_longer_recalculates_from_old_precedents()
    {
        var s = new TestSheet();
        s.Load(("A1", "1"), ("B1", "=A1+1"), ("C1", "=B1"));
        var r = s.Set("B1", "100");
        Assert.Equal(1, r.EvaluatedFormulaCount); // C1 only
        Assert.Equal(N(100), s["C1"]);
        r = s.Set("A1", "2");
        Assert.Equal(0, r.EvaluatedFormulaCount);
        Assert.Equal(N(100), s["B1"]);
    }

    [Fact]
    public void Changed_cells_exclude_formulas_whose_value_did_not_change()
    {
        var s = new TestSheet();
        s.Load(("A1", "1"), ("B1", "=A1*0"), ("C1", "=A1+1"), ("D1", "=B1+1"));
        var r = s.Set("A1", "2");
        Assert.Equal(3, r.EvaluatedFormulaCount);
        Assert.Equal(new[] { A("C1") }, r.ChangedCells);
    }

    [Fact]
    public void Parse_failure_is_a_formula_cell_that_evaluates_to_parse_error()
    {
        var s = new TestSheet();
        s.Load(("A1", "=1+"), ("B1", "=A1"));
        Assert.Equal(E(ErrorCodes.Parse), s["A1"]);
        Assert.Equal(E(ErrorCodes.Parse), s["B1"]);
    }

    [Fact]
    public void Full_recalc_counts_every_formula()
    {
        var s = new TestSheet();
        var r = s.Load(("A1", "1"), ("B1", "=A1"), ("C1", "=C1"), ("D1", "=B1+1"));
        Assert.Equal(3, r.EvaluatedFormulaCount);
        Assert.Equal(N(2), s["D1"]);
        Assert.Equal(E(ErrorCodes.Circ), s["C1"]);
    }

    [Fact]
    public void Inconsistent_target_throws_before_writing_anything()
    {
        var s = new TestSheet();
        s.Load(("A1", "1"), ("B1", "=A1"));
        s.Graph.SetFormula(A("C1"), FormulaParser.Parse("A1", null)); // graph-only formula, unknown to the target
        int before = s.ComputedWrites;
        Assert.Throws<InvalidOperationException>(() => s.Graph.Recalculate([A("A1")], s, BuiltInFunctionTable.Default));
        Assert.Equal(before, s.ComputedWrites);
    }

    private static TestSheet BuildChain(int length)
    {
        var s = new TestSheet();
        s.Apply("A1", "1");
        for (int r = 2; r <= length; r++) s.Apply("A" + r, $"=A{r - 1}+1");
        return s;
    }

    [Fact]
    public void Chain_of_100000_cells_recalculates_without_overflow()
    {
        var s = BuildChain(100000);
        var load = s.Graph.RecalculateAll(s, BuiltInFunctionTable.Default);
        Assert.Equal(99999, load.EvaluatedFormulaCount);
        Assert.Equal(N(100000), s["A100000"]);

        var r = s.Set("A1", "2");
        Assert.Equal(99999, r.EvaluatedFormulaCount); // P-2, R21
        Assert.Equal(99999, r.ChangedCells.Count);
        Assert.Equal(N(100001), s["A100000"]);

        // Closing the chain into a 100,000-cell cycle is detected iteratively too.
        r = s.Set("A1", "=A100000");
        Assert.Equal(100000, r.EvaluatedFormulaCount);
        Assert.Equal(E(ErrorCodes.Circ), s["A1"]);
        Assert.Equal(E(ErrorCodes.Circ), s["A50000"]);

        // And breaking it restores every value.
        r = s.Set("A1", "1");
        Assert.Equal(99999, r.EvaluatedFormulaCount);
        Assert.Equal(N(100000), s["A100000"]);
    }

    [Fact]
    public void Edit_with_no_dependents_evaluates_nothing_even_with_many_range_formulas()
    {
        var s = new TestSheet();
        for (int r = 1; r <= 100000; r++) s.Apply("B" + r, $"=SUM(A{r}:A{Math.Min(100000, r + 9)})");
        s.Graph.RecalculateAll(s, BuiltInFunctionTable.Default);
        // Timing budgets live in PerformanceGates (D-30); this test is functional only.
        for (int i = 0; i < 100; i++)
            Assert.Equal(0, s.Set("Z5", (i % 7).ToString(CultureInfo.InvariantCulture)).EvaluatedFormulaCount);
        // An edit inside the windows reaches exactly the ten overlapping sums.
        var r2 = s.Set("A500", "1");
        Assert.Equal(10, r2.EvaluatedFormulaCount);
        Assert.Equal(N(1), s["B491"]);
        Assert.Equal(N(0), s["B501"]);
    }
}
