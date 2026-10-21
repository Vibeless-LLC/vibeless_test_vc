using Gridline.Core.Dependencies;
using Gridline.Core.Parsing;
using static Gridline.Core.Tests.Dependencies.TestSheet;

namespace Gridline.Core.Tests.Dependencies;

/// <summary>CR1.2, D-52: the dependency graph across sheets. The <see cref="Gridline.Core.Addressing.CellAddress"/>
/// members act on sheet 0; qualified precedents resolve through the sheet-name bindings.</summary>
public class SheetGraphTests
{
    private static readonly FunctionArityAdapter Arities = new();
    private static ParsedFormula P(string text) => FormulaParser.Parse(text, Arities);

    [Fact]
    public void QualifiedPrecedents_AreNotSheet0Edges_UntilTheSheetIsBound()
    {
        var g = new DependencyGraph();
        g.SetFormula(A("C1"), P("Data!A1+A2"));
        g.SetFormula(A("C2"), P("SUM(Data!A1:A3)"));
        Assert.Empty(g.GetDependents(A("A1")));            // sheet 0's A1 is not Data!A1
        Assert.Equal([A("C1")], g.GetDependents(A("A2")));

        g.SetSheet("DATA", 0);                              // Data is sheet 0 now
        Assert.Equal(2, g.GetDependents(A("A1")).Count);
        g.SetSheet("data", null);
        Assert.Empty(g.GetDependents(A("A1")));
    }

    [Fact]
    public void Rebinding_ReEdgesFormulas_AndClearingAFormulaRemovesItsSheetTracking()
    {
        var g = new DependencyGraph();
        g.SetFormula(A("C1"), P("Data!A1"));
        g.SetSheet("Data", 0);
        g.SetFormula(A("C1"), null);
        g.SetSheet("Data", null);                           // nothing left to re-edge
        g.SetSheet("Data", 0);
        Assert.Empty(g.GetDependents(A("A1")));
    }

    [Fact]
    public void ChangedSheets_SeedTheFormulasThatNameThem()
    {
        var sheet = new TestSheet();
        sheet.Load(("C1", "=Data!A1+1"), ("C2", "=C1*2"), ("C3", "=A1"));
        Assert.Equal(E(ErrorCodes.Ref), sheet["C2"]);
        sheet.Graph.SetSheet("Data", 0); // binds Data to the test sheet itself
        var r = sheet.Graph.Recalculate(Array.Empty<long>(), null, ["data"], sheet, null);
        Assert.Equal(2, r.EvaluatedFormulaCount); // C1 and its dependent C2, not C3
    }
}
