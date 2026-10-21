using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;

namespace Gridline.Core.Tests;

// Requirement IDs: §9.5 (InsertRows, DeleteRows, InsertColumns, DeleteColumns: validation, moving cells, the reference
// adjustment table for rows and columns, verbatim unchanged formulas, #PARSE! verbatim), §5.5 (re-emission, #REF!,
// name casing), §9.6 (names adjusted, #REF! names), §9.2 (atomicity, CellsChanged, LastRecalc), §9.3 (one undo step,
// exact restore, redo), §9.1, R39, R61 (clamping), R62 (argument checks), R63 (no-op, full re-evaluation), R64 (event
// contents), §6.7 (incremental recalc on the rebuilt graph).
public class WorkbookStructureTests
{
    private static CellValue N(double d) => CellValue.FromNumber(d);
    private static CellValue E(string code) => CellValue.FromError(code);
    private static KeyValuePair<string, string> P(string a, string b) => new(a, b);

    private sealed class EventLog
    {
        public List<string[]> Events { get; } = [];
        public EventLog(Workbook wb) => wb.CellsChanged += (sender, e) =>
        {
            Assert.Same(wb, sender);
            Events.Add([.. e.Addresses]);
        };
    }

    /// <summary>Built-ins plus BOOM(x): returns x, but throws while <see cref="Armed"/>.</summary>
    private sealed class ArmableResolver : IFunctionResolver
    {
        public bool Armed { get; set; }

        private sealed class Boom(ArmableResolver owner) : IFormulaFunction
        {
            public CellValue Invoke(IFunctionArguments arguments) =>
                owner.Armed ? throw new InvalidOperationException("boom") : arguments.Evaluate(0);
        }

        public bool TryResolve(string upperName, [NotNullWhen(true)] out IFormulaFunction? function)
        {
            if (upperName == "BOOM") { function = new Boom(this); return true; }
            return BuiltInFunctionTable.Default.TryResolve(upperName, out function);
        }
    }

    private static Dictionary<string, (string Input, CellValue Value)> Snapshot(Workbook wb) =>
        wb.GetUsedAddresses().ToDictionary(a => a, a => (wb.GetInput(a), wb.GetValue(a)));

    private static void Do(Workbook wb, string op, int start, int count)
    {
        switch (op)
        {
            case "IR": wb.InsertRows(start, count); break;
            case "DR": wb.DeleteRows(start, count); break;
            case "IC": wb.InsertColumns(start, count); break;
            case "DC": wb.DeleteColumns(start, count); break;
            default: throw new ArgumentException(op);
        }
    }

    /// <summary>Puts <paramref name="formula"/> alone in a cell that the edit does not delete, applies it, and returns
    /// the formula's new input (wherever the formula moved).</summary>
    private static string Adjust(string formula, string op, int start, int count)
    {
        // Row edits: column ZZ, row 1 unless row 1 is deleted (then row 100000). Column edits: row 1, column A unless
        // column A is deleted (then ZZ). The formula cell never moves past the grid for the cases below.
        string home = op switch
        {
            "IR" => "ZZ1",
            "DR" => start == 1 ? "ZZ100000" : "ZZ1",
            "IC" => "A1",
            _ => start == 1 ? "ZZ1" : "A1",
        };
        var wb = Workbook.FromCells([P(home, formula)]);
        Do(wb, op, start, count);
        var used = wb.GetUsedAddresses();
        Assert.Single(used);
        return wb.GetInput(used[0]);
    }

    // ---- The §9.5 reference adjustment table, rows ----

    [Theory]
    // Single cell, insert at r, n rows: x >= r -> x + n; past MaxRows -> #REF!.
    [InlineData("=A5", "IR", 3, 2, "=A7")]
    [InlineData("=A5", "IR", 5, 1, "=A6")]          // x == r moves
    [InlineData("= a5", "IR", 6, 1, "= a5")]        // x < r: unchanged, verbatim
    [InlineData("=A99999", "IR", 1, 2, "=#REF!")]   // 100001 > MaxRows
    [InlineData("=A100000", "IR", 100000, 1, "=#REF!")]
    [InlineData("=A99999", "IR", 1, 1, "=A100000")] // exactly the bound is valid
    [InlineData("=A5", "IR", 2, int.MaxValue, "=#REF!")] // no overflow
    // Relative and absolute alike; each reference keeps its own $ markers.
    [InlineData("=$A$5+A$5+$A5+A5", "IR", 1, 1, "=$A$6+A$6+$A6+A6")]
    // Single cell, delete [r, r+n-1]: inside -> #REF!; below -> x - n.
    [InlineData("=A5", "DR", 3, 2, "=A3")]
    [InlineData("=A5", "DR", 5, 1, "=#REF!")]
    [InlineData("=A5", "DR", 4, 2, "=#REF!")]       // last row of the span
    [InlineData("=$A$5", "DR", 5, 3, "=#REF!")]     // first row of the span, absolute alike
    [InlineData("= a5", "DR", 6, 1, "= a5")]        // above the span: verbatim
    [InlineData("=A5", "DR", 1, 4, "=A1")]
    [InlineData("=A100000", "DR", 1, 99999, "=A1")]
    // Range, insert: each endpoint independently >= r -> +n (spec examples first).
    [InlineData("=SUM(A1:A5)", "IR", 3, 2, "=SUM(A1:A7)")]
    [InlineData("=SUM(A1:A5)", "IR", 1, 2, "=SUM(A3:A7)")]
    [InlineData("=SUM(A1:A5)", "IR", 5, 1, "=SUM(A1:A6)")]
    [InlineData("=SUM( A1:A5 )", "IR", 6, 1, "=SUM( A1:A5 )")]
    [InlineData("=SUM(A5:A1)", "IR", 3, 2, "=SUM(A7:A1)")]    // written bottom-up: orientation kept
    [InlineData("=SUM(B$2:$C5)", "IR", 1, 1, "=SUM(B$3:$C6)")] // mixed $
    [InlineData("=SUM(A1:C5)", "IR", 2, 3, "=SUM(A1:C8)")]     // 2-D range: only the rows change
    // R61: a range whose end alone passes MaxRows is clamped; one whose start passes it is #REF!.
    [InlineData("=SUM(A99996:A99999)", "IR", 1, 3, "=SUM(A99999:A100000)")]
    [InlineData("=SUM(A99999:A99996)", "IR", 1, 3, "=SUM(A100000:A99999)")]
    [InlineData("=SUM(A99999:A100000)", "IR", 1, 2, "=SUM(#REF!)")]
    [InlineData("=SUM(A100000:A100000)", "IR", 100000, 1, "=SUM(#REF!)")]
    [InlineData("=SUM(A1:A100000)", "IR", 5, 1, "=SUM(A1:A100000)")] // end clamped back to itself: unchanged
    [InlineData("=SUM(a1:a100000)", "IR", 5, 1, "=SUM(a1:a100000)")] // ... and so verbatim
    // Range, delete: keep the surviving rows; none survive -> #REF!; endpoints = first/last survivors after the shift.
    [InlineData("=SUM(A1:A10)", "DR", 3, 2, "=SUM(A1:A8)")]
    [InlineData("=SUM(A1:A10)", "DR", 1, 2, "=SUM(A1:A8)")]
    [InlineData("=SUM(A1:A10)", "DR", 9, 5, "=SUM(A1:A8)")]  // span covers the bottom end
    [InlineData("=SUM(A3:A10)", "DR", 1, 4, "=SUM(A1:A6)")]  // span covers the top end
    [InlineData("=SUM(A5:A10)", "DR", 1, 2, "=SUM(A3:A8)")]  // span entirely above: shifts
    [InlineData("=SUM(A3:A4)", "DR", 3, 2, "=SUM(#REF!)")]   // exactly the span
    [InlineData("=SUM(A3:A4)", "DR", 2, 4, "=SUM(#REF!)")]   // inside the span
    [InlineData("=SUM(A5:A5)", "DR", 5, 1, "=SUM(#REF!)")]
    [InlineData("=SUM(A10:A1)", "DR", 1, 2, "=SUM(A8:A1)")]  // bottom-up kept
    [InlineData("=SUM($B$1:C10)", "DR", 3, 2, "=SUM($B$1:C8)")]
    [InlineData("=SUM(A1:C10)", "DR", 2, 3, "=SUM(A1:C7)")]
    [InlineData("=SUM( A1:A10 )", "DR", 11, 1, "=SUM( A1:A10 )")] // below the range: verbatim
    // Several references, some changed: the whole formula is re-emitted canonically (§5.5).
    [InlineData("= a1 + sum( a2:a3 ) + a9 * 1.50", "DR", 2, 2, "=A1+SUM(#REF!)+A7*1.50")]
    [InlineData("= sum( a5 , \"x\"\"y\" , (1) )", "IR", 1, 1, "=SUM(A6,\"x\"\"y\",(1))")]
    [InlineData("=A5+#REF!", "IR", 1, 1, "=A6+#REF!")]
    [InlineData("= #REF! + a1", "IR", 5, 1, "= #REF! + a1")] // an error literal is not a reference
    // #PARSE! formulas are kept verbatim (R39).
    [InlineData("=SUM(A5", "IR", 1, 1, "=SUM(A5")]
    [InlineData("=SUM(A5", "DR", 5, 1, "=SUM(A5")]
    public void Rows_ReferenceAdjustment(string formula, string op, int start, int count, string expected) =>
        Assert.Equal(expected, Adjust(formula, op, start, count));

    // ---- The same table, columns (ZY = 701, ZZ = 702 = MaxColumns) ----

    [Theory]
    [InlineData("=E1", "IC", 3, 2, "=G1")]
    [InlineData("=E1", "IC", 5, 1, "=F1")]
    [InlineData("= e1", "IC", 6, 1, "= e1")]
    [InlineData("=ZY1", "IC", 1, 2, "=#REF!")]
    [InlineData("=ZZ1", "IC", 702, 1, "=#REF!")]
    [InlineData("=ZY1", "IC", 1, 1, "=ZZ1")]
    [InlineData("=E1", "IC", 2, int.MaxValue, "=#REF!")]
    [InlineData("=$E$1+E$1+$E1+E1", "IC", 1, 1, "=$F$1+F$1+$F1+F1")]
    [InlineData("=E1", "DC", 3, 2, "=C1")]
    [InlineData("=E1", "DC", 5, 1, "=#REF!")]
    [InlineData("=E1", "DC", 4, 2, "=#REF!")]
    [InlineData("=$E$1", "DC", 5, 3, "=#REF!")]
    [InlineData("= e1", "DC", 6, 1, "= e1")]
    [InlineData("=E1", "DC", 1, 4, "=A1")]
    [InlineData("=ZZ1", "DC", 2, 700, "=B1")]
    [InlineData("=SUM(A1:E1)", "IC", 3, 2, "=SUM(A1:G1)")]
    [InlineData("=SUM(A1:E1)", "IC", 1, 2, "=SUM(C1:G1)")]
    [InlineData("=SUM(A1:E1)", "IC", 5, 1, "=SUM(A1:F1)")]
    [InlineData("=SUM( A1:E1 )", "IC", 6, 1, "=SUM( A1:E1 )")]
    [InlineData("=SUM(E1:A1)", "IC", 3, 2, "=SUM(G1:A1)")]
    [InlineData("=SUM($B2:C$5)", "IC", 1, 1, "=SUM($C2:D$5)")]
    [InlineData("=SUM(A1:E3)", "IC", 2, 3, "=SUM(A1:H3)")]
    [InlineData("=SUM(ZU1:ZX1)", "IC", 1, 3, "=SUM(ZX1:ZZ1)")]  // R61 clamp (ZX+3 = 703 > 702)
    [InlineData("=SUM(ZX1:ZU1)", "IC", 1, 3, "=SUM(ZZ1:ZX1)")]
    [InlineData("=SUM(ZY1:ZZ1)", "IC", 1, 2, "=SUM(#REF!)")]    // R61 start passes
    [InlineData("=SUM(ZZ1:ZZ1)", "IC", 702, 1, "=SUM(#REF!)")]
    [InlineData("=SUM(A1:ZZ1)", "IC", 5, 1, "=SUM(A1:ZZ1)")]
    [InlineData("=SUM(a1:zz1)", "IC", 5, 1, "=SUM(a1:zz1)")]
    [InlineData("=SUM(A1:J1)", "DC", 3, 2, "=SUM(A1:H1)")]
    [InlineData("=SUM(A1:J1)", "DC", 1, 2, "=SUM(A1:H1)")]
    [InlineData("=SUM(A1:J1)", "DC", 9, 5, "=SUM(A1:H1)")]
    [InlineData("=SUM(C1:J1)", "DC", 1, 4, "=SUM(A1:F1)")]
    [InlineData("=SUM(E1:J1)", "DC", 1, 2, "=SUM(C1:H1)")]
    [InlineData("=SUM(C1:D1)", "DC", 3, 2, "=SUM(#REF!)")]
    [InlineData("=SUM(C1:D1)", "DC", 2, 4, "=SUM(#REF!)")]
    [InlineData("=SUM(E1:E1)", "DC", 5, 1, "=SUM(#REF!)")]
    [InlineData("=SUM(J1:A1)", "DC", 1, 2, "=SUM(H1:A1)")]
    [InlineData("=SUM($A$2:J3)", "DC", 3, 2, "=SUM($A$2:H3)")]
    [InlineData("=SUM(A1:J3)", "DC", 2, 3, "=SUM(A1:G3)")]
    [InlineData("=SUM( A1:J1 )", "DC", 11, 1, "=SUM( A1:J1 )")]
    [InlineData("= a1 + sum( b1:c1 ) + i1 * 1.50", "DC", 2, 2, "=A1+SUM(#REF!)+G1*1.50")]
    [InlineData("=SUM(E1", "IC", 1, 1, "=SUM(E1")]
    [InlineData("=SUM(E1", "DC", 5, 1, "=SUM(E1")]
    public void Columns_ReferenceAdjustment(string formula, string op, int start, int count, string expected) =>
        Assert.Equal(expected, Adjust(formula, op, start, count));

    // ---- Moving cells ----

    [Fact]
    public void InsertRows_MovesCellsAtAndBelowTheRow()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("A2", "2"), P("B3", "'t"), P("C1", "=  a2 * 2")]);
        wb.InsertRows(2, 2);
        Assert.Equal(["A1", "C1", "A4", "B5"], wb.GetUsedAddresses());
        Assert.Equal("2", wb.GetInput("A4"));
        Assert.Equal("'t", wb.GetInput("B5"));
        Assert.Equal("=A4*2", wb.GetInput("C1"));
        Assert.Equal(N(4), wb.GetValue("C1"));
    }

    [Fact]
    public void DeleteRows_RemovesTheSpanAndMovesCellsBelowUp()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("A2", "2"), P("A3", "3"), P("B4", "4"), P("C9", "=A1+B4")]);
        wb.DeleteRows(2, 2);
        Assert.Equal(["A1", "B2", "C7"], wb.GetUsedAddresses());
        Assert.Equal("=A1+B2", wb.GetInput("C7"));
        Assert.Equal(N(5), wb.GetValue("C7"));
    }

    [Fact]
    public void InsertColumns_MovesCellsAtAndRightOfTheColumn()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("B1", "2"), P("C2", "'t"), P("A3", "=  b1 * 2")]);
        wb.InsertColumns(2, 2);
        Assert.Equal(["A1", "D1", "E2", "A3"], wb.GetUsedAddresses());
        Assert.Equal("2", wb.GetInput("D1"));
        Assert.Equal("'t", wb.GetInput("E2"));
        Assert.Equal("=D1*2", wb.GetInput("A3"));
        Assert.Equal(N(4), wb.GetValue("A3"));
    }

    [Fact]
    public void DeleteColumns_RemovesTheSpanAndMovesCellsRightOfItLeft()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("B1", "2"), P("C1", "3"), P("D2", "4"), P("I3", "=A1+D2")]);
        wb.DeleteColumns(2, 2);
        Assert.Equal(["A1", "B2", "G3"], wb.GetUsedAddresses());
        Assert.Equal("=A1+B2", wb.GetInput("G3"));
        Assert.Equal(N(5), wb.GetValue("G3"));
    }

    [Fact]
    public void Moved_UnchangedFormula_KeepsVerbatimText_Rows_And_Columns()
    {
        var wb = Workbook.FromCells([P("C5", "=  a1 + sum( b1:b2 )"), P("A1", "1"), P("B1", "2"), P("B2", "3")]);
        wb.InsertRows(3, 2);
        Assert.Equal("=  a1 + sum( b1:b2 )", wb.GetInput("C7"));
        Assert.Equal(N(6), wb.GetValue("C7"));
        wb.InsertColumns(3, 1);
        Assert.Equal("=  a1 + sum( b1:b2 )", wb.GetInput("D7"));
        wb.DeleteRows(4, 2);
        Assert.Equal("=  a1 + sum( b1:b2 )", wb.GetInput("D5"));
        wb.DeleteColumns(3, 1);
        Assert.Equal("=  a1 + sum( b1:b2 )", wb.GetInput("C5"));
        Assert.Equal(N(6), wb.GetValue("C5"));
    }

    [Fact]
    public void ParseErrorFormula_MovesVerbatim()
    {
        var wb = Workbook.FromCells([P("A2", "=SUM(A1"), P("A1", "1")]);
        wb.InsertRows(1, 1);
        Assert.Equal("=SUM(A1", wb.GetInput("A3"));
        Assert.Equal(E(ErrorCodes.Parse), wb.GetValue("A3"));
    }

    [Fact]
    public void RewrittenFormula_UsesNameDefinitionCasing()
    {
        var wb = Workbook.FromCells([P("A5", "2"), P("B1", "= rate * a5 + UNKNOWN_x")]);
        wb.DefineName("Rate", "$C$1");
        wb.SetCell("C1", "10");
        wb.InsertRows(1, 1);
        Assert.Equal("=Rate*A6+UNKNOWN_x", wb.GetInput("B2"));
    }

    [Fact]
    public void ReferencesIntoDeletedRows_EvaluateToRef()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("A2", "2"), P("B1", "=A2*2"), P("B3", "=SUM(A1:A2)")]);
        wb.DeleteRows(2, 1);
        Assert.Equal("=#REF!*2", wb.GetInput("B1"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("B1"));
        Assert.Equal("=SUM(A1:A1)", wb.GetInput("B2"));
        Assert.Equal(N(1), wb.GetValue("B2"));
    }

    [Fact]
    public void RebuiltGraph_IncrementalRecalcFollowsMovedCells()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("A2", "=A1*2"), P("A3", "=SUM(A1:A2)")]);
        wb.InsertRows(1, 2);
        Assert.Equal("=A3*2", wb.GetInput("A4"));
        Assert.Equal("=SUM(A3:A4)", wb.GetInput("A5"));
        wb.SetCell("A3", "5");
        Assert.Equal(N(10), wb.GetValue("A4"));
        Assert.Equal(N(15), wb.GetValue("A5"));
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("A1", "100"); // the vacated row is no longer a precedent
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.DeleteColumns(1, 1); // everything in column A is removed
        Assert.Empty(wb.GetUsedAddresses());
    }

    [Fact]
    public void CyclesSurviveAndBreakAcrossStructureEdits()
    {
        var wb = Workbook.FromCells([P("A1", "=A3"), P("A3", "=A1"), P("B1", "=A2")]);
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("A1"));
        wb.InsertRows(2, 1);
        Assert.Equal("=A4", wb.GetInput("A1"));
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("A4"));
        wb.DeleteRows(4, 1);
        Assert.Equal("=#REF!", wb.GetInput("A1"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
    }

    // ---- Names (§9.6) ----

    [Fact]
    public void Names_AreAdjustedLikeReferences_Rows()
    {
        var wb = Workbook.FromCells([P("A5", "3"), P("A6", "4"), P("B1", "=Rate*2"), P("B2", "=SUM(Total)")]);
        wb.DefineName("Rate", "$A$5");
        wb.DefineName("Total", "$A$5:$A$6");
        wb.DefineName("Above", "$A$1:$C$2");
        wb.InsertRows(3, 2);
        Assert.Equal([P("Above", "Sheet1!$A$1:$C$2"), P("Rate", "Sheet1!$A$7"), P("Total", "Sheet1!$A$7:$A$8")], wb.GetNames());
        Assert.Equal("=Rate*2", wb.GetInput("B1")); // name references are not cell references: verbatim
        Assert.Equal(N(6), wb.GetValue("B1"));
        Assert.Equal(N(7), wb.GetValue("B2"));

        wb.DeleteRows(8, 1);
        Assert.Equal([P("Above", "Sheet1!$A$1:$C$2"), P("Rate", "Sheet1!$A$7"), P("Total", "Sheet1!$A$7:$A$7")], wb.GetNames());
        Assert.Equal(N(3), wb.GetValue("B2"));

        wb.DeleteRows(7, 1); // both targets fully deleted
        Assert.Equal([P("Above", "Sheet1!$A$1:$C$2"), P("Rate", "#REF!"), P("Total", "#REF!")], wb.GetNames());
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("B1"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("B2"));

        wb.InsertRows(1, 1); // a deleted target stays deleted
        Assert.Equal([P("Above", "Sheet1!$A$2:$C$3"), P("Rate", "#REF!"), P("Total", "#REF!")], wb.GetNames());
    }

    [Fact]
    public void Names_AreAdjustedLikeReferences_Columns()
    {
        var wb = Workbook.FromCells([P("E1", "3"), P("F1", "4"), P("A2", "=rate*2"), P("A3", "=SUM(total)")]);
        wb.DefineName("Rate", "$E$1");
        wb.DefineName("Total", "$E$1:$F$1");
        wb.InsertColumns(3, 2);
        Assert.Equal([P("Rate", "Sheet1!$G$1"), P("Total", "Sheet1!$G$1:$H$1")], wb.GetNames());
        Assert.Equal(N(6), wb.GetValue("A2"));
        Assert.Equal("=rate*2", wb.GetInput("A2"));
        wb.DeleteColumns(7, 1);
        Assert.Equal([P("Rate", "#REF!"), P("Total", "Sheet1!$G$1:$G$1")], wb.GetNames());
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A2"));
        Assert.Equal(N(4), wb.GetValue("A3"));
    }

    [Fact]
    public void Names_R61_ClampAndRef()
    {
        var wb = new Workbook();
        wb.DefineName("Edge", "$A$100000");
        wb.DefineName("Tail", "$A$99999:$A$100000");
        wb.DefineName("Low", "$A$99998:$B$99999");
        wb.DefineName("Wide", "$ZY$1:$ZZ$1");
        wb.InsertRows(99999, 2);
        Assert.Equal([P("Edge", "#REF!"), P("Low", "Sheet1!$A$99998:$B$100000"), P("Tail", "#REF!"), P("Wide", "Sheet1!$ZY$1:$ZZ$1")], wb.GetNames());
        Assert.True(wb.CanUndo);
        wb.InsertColumns(702, 1); // Wide's end alone passes the bound: clamped back to itself, so nothing changes
        Assert.Equal([P("Edge", "#REF!"), P("Low", "Sheet1!$A$99998:$B$100000"), P("Tail", "#REF!"), P("Wide", "Sheet1!$ZY$1:$ZZ$1")], wb.GetNames());
        wb.InsertColumns(701, 1); // start ZY -> ZZ, end clamped
        Assert.Equal([P("Edge", "#REF!"), P("Low", "Sheet1!$A$99998:$B$100000"), P("Tail", "#REF!"), P("Wide", "Sheet1!$ZZ$1:$ZZ$1")], wb.GetNames());
        wb.InsertColumns(702, 1); // the start passes the bound
        Assert.Equal([P("Edge", "#REF!"), P("Low", "Sheet1!$A$99998:$B$100000"), P("Tail", "#REF!"), P("Wide", "#REF!")], wb.GetNames());
    }

    [Fact]
    public void NamesOnlyChange_IsAStep_WithNoEventWhenNoValueChanges()
    {
        var wb = Workbook.FromCells([], [P("Far", "$A$9")]); // internal loader entry: no DefineName step
        var log = new EventLog(wb);
        wb.InsertRows(5, 1);
        Assert.Equal([P("Far", "Sheet1!$A$10")], wb.GetNames());
        Assert.Empty(log.Events);
        Assert.True(wb.CanUndo);
        Assert.True(wb.Undo());
        Assert.Equal([P("Far", "Sheet1!$A$9")], wb.GetNames());
        Assert.False(wb.CanUndo);
        Assert.True(wb.Redo());
        Assert.Equal([P("Far", "Sheet1!$A$10")], wb.GetNames());
    }

    [Fact]
    public void NameTargetDependents_RecalcAfterTheEdit()
    {
        var wb = Workbook.FromCells([P("A5", "3"), P("B1", "=Rate")]);
        wb.DefineName("Rate", "$A$5");
        wb.InsertRows(1, 1);
        wb.SetCell("A6", "8");
        Assert.Equal(N(8), wb.GetValue("B2"));
        wb.SetCell("A5", "1"); // the old target position no longer matters
        Assert.Equal(N(8), wb.GetValue("B2"));
    }

    // ---- Validation (R62) ----

    [Theory]
    [InlineData("IR", 0, 1, "row")]
    [InlineData("IR", -1, 1, "row")]
    [InlineData("IR", 100001, 1, "row")]
    [InlineData("IR", 1, 0, "count")]
    [InlineData("IR", 1, -5, "count")]
    [InlineData("DR", 0, 1, "row")]
    [InlineData("DR", 100001, 1, "row")]
    [InlineData("DR", int.MaxValue, 1, "row")]
    [InlineData("DR", 1, 0, "count")]
    [InlineData("DR", 1, -1, "count")]
    [InlineData("DR", 100000, 2, "count")]
    [InlineData("DR", 1, 100001, "count")]
    [InlineData("DR", 2, int.MaxValue, "count")]
    [InlineData("IC", 0, 1, "column")]
    [InlineData("IC", 703, 1, "column")]
    [InlineData("IC", 1, 0, "count")]
    [InlineData("DC", 0, 1, "column")]
    [InlineData("DC", 703, 1, "column")]
    [InlineData("DC", 1, 0, "count")]
    [InlineData("DC", 702, 2, "count")]
    [InlineData("DC", 1, 703, "count")]
    [InlineData("DC", 2, int.MaxValue, "count")]
    public void InvalidArguments_ThrowArgumentOutOfRange_AndChangeNothing(string op, int start, int count, string param)
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("B2", "=A1")]);
        wb.DefineName("Rate", "$A$1");
        var before = Snapshot(wb);
        var log = new EventLog(wb);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Do(wb, op, start, count));
        Assert.Equal(param, ex.ParamName);
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal([P("Rate", "Sheet1!$A$1")], wb.GetNames());
        Assert.Empty(log.Events);
        Assert.True(wb.Undo()); // the newest step is still DefineName
        Assert.Empty(wb.GetNames());
        Assert.False(wb.CanUndo);
    }

    [Theory]
    [InlineData("IR", 100000, 1)]
    [InlineData("DR", 100000, 1)]
    [InlineData("DR", 1, 100000)]
    [InlineData("IC", 702, 1)]
    [InlineData("DC", 702, 1)]
    [InlineData("DC", 1, 702)]
    public void BoundaryArguments_AreValid(string op, int start, int count)
    {
        var wb = Workbook.FromCells([P("A1", "1")]);
        Do(wb, op, start, count);
    }

    [Theory]
    [InlineData("A100000", "IR", 1, 1)]
    [InlineData("A100000", "IR", 100000, 1)]
    [InlineData("C99999", "IR", 5, 2)]
    [InlineData("C99999", "IR", 2, int.MaxValue)]
    [InlineData("ZZ1", "IC", 1, 1)]
    [InlineData("ZZ5", "IC", 702, 1)]
    [InlineData("ZY3", "IC", 3, 2)]
    public void Insert_PushingInputPastTheGrid_ThrowsInvalidOperation_AndChangesNothing(string edge, string op, int start, int count)
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("B1", "=A1+A2"), P(edge, "'edge")]);
        wb.DefineName("Rate", "$A$2");
        wb.SetCell("A2", "2");
        var before = Snapshot(wb);
        int recalc = wb.LastRecalc.EvaluatedFormulaCount;
        var log = new EventLog(wb);
        Assert.Throws<InvalidOperationException>(() => Do(wb, op, start, count));
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal([P("Rate", "Sheet1!$A$2")], wb.GetNames());
        Assert.Empty(log.Events);
        Assert.Equal(recalc, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.True(wb.Undo()); // the history is unchanged: the newest step is still SetCell A2
        Assert.Equal("", wb.GetInput("A2"));
    }

    [Fact]
    public void RewrittenFormulaTooLong_ThrowsInvalidOperation_AndChangesNothing()
    {
        string formula = "=" + string.Join("+", Enumerable.Repeat("A2", 10_000)); // 30,000 chars; #REF! makes it 60,000
        var wb = Workbook.FromCells([P("A2", "1"), P("B1", formula)], [P("Rate", "$A$3"), P("Gone", "#REF!")]);
        var names = wb.GetNames();
        var before = Snapshot(wb);
        var recalc = wb.LastRecalc;
        var log = new EventLog(wb);
        Assert.Throws<InvalidOperationException>(() => wb.DeleteRows(2, 1));
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal(names, wb.GetNames()); // Rate would have become $A$2: unchanged
        Assert.Empty(log.Events);
        Assert.Equal(recalc, wb.LastRecalc);
        Assert.False(wb.CanUndo);
    }

    [Fact]
    public void RebuiltGraph_AfterDeleteRows_RecalculatesIncrementally()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("A2", "x"), P("A3", "3"), P("B4", "=A1+A3"), P("B5", "=SUM(A1:A4)")]);
        wb.DeleteRows(2, 1);
        Assert.Equal("=A1+A2", wb.GetInput("B3"));
        Assert.Equal("=SUM(A1:A3)", wb.GetInput("B4"));
        wb.SetCell("A2", "10"); // the moved 3
        Assert.Equal(N(11), wb.GetValue("B3"));
        Assert.Equal(N(11), wb.GetValue("B4"));
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("A5", "7"); // below the shrunken range: no longer a precedent of anything
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("A3", "4"); // inside SUM(A1:A3) only
        Assert.Equal(N(15), wb.GetValue("B4"));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void RebuiltGraph_AfterAColumnEdit_RecalculatesIncrementally()
    {
        var wb = Workbook.FromCells([P("A1", "2"), P("B1", "3"), P("C1", "=A1*B1"), P("D1", "=SUM(A1:B1)")]);
        wb.DefineName("Rate", "$B$1");
        wb.SetCell("E1", "=Rate+1");
        wb.InsertColumns(2, 1); // B..E move to C..F
        Assert.Equal("=A1*C1", wb.GetInput("D1"));
        Assert.Equal("=SUM(A1:C1)", wb.GetInput("E1"));
        Assert.Equal([P("Rate", "Sheet1!$C$1")], wb.GetNames());
        wb.SetCell("C1", "5");
        Assert.Equal(N(10), wb.GetValue("D1"));
        Assert.Equal(N(7), wb.GetValue("E1"));
        Assert.Equal(N(6), wb.GetValue("F1"));
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("B1", "100"); // the inserted column is inside SUM(A1:C1) only
        Assert.Equal(N(107), wb.GetValue("E1"));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        wb.DeleteColumns(1, 1);
        wb.SetCell("B1", "1"); // the old C1, now B1
        Assert.Equal("=#REF!*B1", wb.GetInput("C1"));
        Assert.Equal(N(2), wb.GetValue("E1"));
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount);
    }

    // ---- R63 no-op, LastRecalc, R64 events ----

    [Theory]
    [InlineData("IR", 1, 1)]
    [InlineData("DR", 1, 5)]
    [InlineData("IC", 1, 1)]
    [InlineData("DC", 1, 702)]
    public void EmptyWorkbook_IsANoOp(string op, int start, int count)
    {
        var wb = new Workbook();
        var log = new EventLog(wb);
        Do(wb, op, start, count);
        Assert.False(wb.CanUndo);
        Assert.False(wb.IsDirty);
        Assert.Empty(log.Events);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Theory]
    [InlineData("IR", 4, 3)]
    [InlineData("DR", 4, 10)]
    [InlineData("IC", 4, 3)]
    [InlineData("DC", 4, 10)]
    public void NothingMovesNoFormulaOrNameChanges_IsANoOp_KeepingRedo(string op, int start, int count)
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("B2", "= a1 + c3"), P("C3", "2")]);
        wb.DefineName("Rate", "$C$3:$C$3");
        var log = new EventLog(wb);
        wb.SetCell("B3", "x");
        wb.Undo(); // B3 cleared again; redo available
        log.Events.Clear();
        Do(wb, op, start, count);
        Assert.Empty(log.Events);
        Assert.True(wb.CanRedo);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal("= a1 + c3", wb.GetInput("B2"));
    }

    [Fact]
    public void OnlyAFormulaChanges_IsAStep()
    {
        var wb = Workbook.FromCells([P("A1", "=A9")]);
        var log = new EventLog(wb);
        wb.InsertRows(5, 1);
        Assert.Equal("=A10", wb.GetInput("A1"));
        Assert.Equal([["A1"]], log.Events);
        Assert.True(wb.CanUndo);
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void LastRecalc_CountsEveryFormulaCell()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("A2", "=A1"), P("Z50", "=1+1"), P("ZZ99", "=Q1"), P("C1", "=SUM(")]);
        wb.InsertRows(2, 1);
        Assert.Equal(4, wb.LastRecalc.EvaluatedFormulaCount); // every formula cell, #PARSE! included
        wb.DeleteColumns(1, 1);
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount);
        wb.Undo();
        Assert.Equal(4, wb.LastRecalc.EvaluatedFormulaCount);
        wb.Redo();
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void CellsChanged_CarriesEveryAddressWhoseInputOrValueDiffers_R64()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("A2", "=A1*2"), P("A3", "5"), P("B3", "5"), P("B4", "5")]);
        var log = new EventLog(wb);
        wb.InsertRows(2, 1);
        // A1 unchanged; A2 emptied; A3 now holds the formula; A4 the 5; B3 emptied; B4 same input and value (5 <- 5);
        // B5 new.
        Assert.Equal([["A2", "A3", "B3", "A4", "B5"]], log.Events);

        log.Events.Clear();
        Assert.True(wb.Undo());
        Assert.Equal([["A2", "A3", "B3", "A4", "B5"]], log.Events);
        log.Events.Clear();
        Assert.True(wb.Redo());
        Assert.Equal([["A2", "A3", "B3", "A4", "B5"]], log.Events);
    }

    [Fact]
    public void CellsChanged_IncludesValueOnlyChanges_R64()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("A2", "2"), P("A3", "3"), P("B1", "=SUM(Total)"), P("C1", "=A1")]);
        wb.DefineName("Total", "$A$1:$A$3");
        Assert.Equal(N(6), wb.GetValue("B1"));
        var log = new EventLog(wb);
        wb.DeleteRows(2, 1);
        Assert.Equal("=SUM(Total)", wb.GetInput("B1")); // input unchanged, value 6 -> 4
        Assert.Equal(N(4), wb.GetValue("B1"));
        Assert.Equal([["B1", "A2", "A3"]], log.Events);
    }

    // ---- Undo (§9.3) ----

    [Fact]
    public void OneUndoStep_RestoresExactInputsAndNames_RedoReapplies()
    {
        string[] inputs = ["1", "= sum( a1:a4 )", "'text", "= a3 * 2", "=SUM(A1", "= Rate + total"];
        var wb = Workbook.FromCells([P("A1", inputs[0]), P("B1", inputs[1]), P("A3", inputs[2]), P("A4", inputs[3]),
            P("C5", inputs[4]), P("D9", inputs[5])], [P("Rate", "$A$3"), P("total", "$A$2:$A$3")]);
        wb.MarkSaved();
        var before = Snapshot(wb);
        var names = wb.GetNames();

        wb.DeleteRows(2, 2);
        Assert.Equal("=SUM(A1:A2)", wb.GetInput("B1"));
        Assert.Equal("=#REF!*2", wb.GetInput("A2"));
        Assert.Equal([P("Rate", "#REF!"), P("total", "#REF!")], wb.GetNames());
        var after = Snapshot(wb);
        Assert.True(wb.IsDirty);

        Assert.True(wb.Undo());
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal(names, wb.GetNames());
        Assert.False(wb.CanUndo); // exactly one step
        Assert.False(wb.IsDirty);

        Assert.True(wb.Redo());
        Assert.Equal(after, Snapshot(wb));
        Assert.True(wb.Undo());
        Assert.Equal(before, Snapshot(wb));
    }

    [Theory]
    [InlineData("IR", 2, 3)]
    [InlineData("DR", 2, 3)]
    [InlineData("IC", 2, 3)]
    [InlineData("DC", 2, 3)]
    [InlineData("DR", 1, 100000)]
    [InlineData("DC", 1, 702)]
    public void EachOperation_UndoRestoresExactly(string op, int start, int count)
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("B2", "= a1 + b3 + sum( a1:c5 )"), P("C3", "=  $B$2"), P("B3", "4"),
            P("E5", "= Rate"), P("A6", "=sum(a1:e1)"), P("F1", "'x")]);
        wb.DefineName("Rate", "$C$3");
        wb.DefineName("Box", "$A$1:$E$5");
        var before = Snapshot(wb);
        var names = wb.GetNames();
        Do(wb, op, start, count);
        var after = Snapshot(wb);
        var afterNames = wb.GetNames();
        Assert.True(wb.Undo());
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal(names, wb.GetNames());
        Assert.True(wb.Redo());
        Assert.Equal(after, Snapshot(wb));
        Assert.Equal(afterNames, wb.GetNames());
    }

    [Fact]
    public void NewEditAfterUndo_ClearsRedo()
    {
        var wb = Workbook.FromCells([P("A1", "1")]);
        wb.InsertRows(1, 1);
        wb.Undo();
        Assert.True(wb.CanRedo);
        wb.InsertColumns(1, 1);
        Assert.False(wb.CanRedo);
        Assert.Equal("1", wb.GetInput("B1"));
    }

    // ---- Atomicity (§9.2) ----

    [Theory]
    [InlineData("IR")]
    [InlineData("DR")]
    [InlineData("IC")]
    [InlineData("DC")]
    public void ThrowingRecalc_LeavesTheWorkbookUnchanged(string op)
    {
        var resolver = new ArmableResolver();
        var wb = Workbook.FromCells([P("B2", "=BOOM(C3)"), P("C3", "3"), P("A1", "x"), P("D4", "=B2+1")], [P("Rate", "$C$3")], resolver);
        var before = Snapshot(wb);
        var log = new EventLog(wb);
        resolver.Armed = true;
        Assert.Throws<InvalidOperationException>(() => Do(wb, op, 1, 1));
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal([P("Rate", "Sheet1!$C$3")], wb.GetNames());
        Assert.False(wb.CanUndo);
        Assert.Empty(log.Events);

        resolver.Armed = false; // the previous graph is back: incremental recalc still works
        wb.SetCell("C3", "7");
        Assert.Equal(N(7), wb.GetValue("B2"));
        Assert.Equal(N(8), wb.GetValue("D4"));
    }

    [Fact]
    public void ThrowingUndoAndRedo_LeaveTheWorkbookAndHistoryUnchanged()
    {
        var resolver = new ArmableResolver();
        var wb = Workbook.FromCells([P("B2", "=BOOM(C3)"), P("C3", "3")], resolver);
        wb.DefineName("Rate", "$C$3");
        wb.InsertRows(1, 1);
        var after = Snapshot(wb);
        resolver.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.Undo());
        Assert.Equal(after, Snapshot(wb));
        Assert.Equal([P("Rate", "Sheet1!$C$4")], wb.GetNames());
        Assert.True(wb.CanUndo);
        resolver.Armed = false;
        Assert.True(wb.Undo());
        var before = Snapshot(wb);
        resolver.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.Redo());
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal([P("Rate", "Sheet1!$C$3")], wb.GetNames());
        Assert.True(wb.CanRedo);
        resolver.Armed = false;
        Assert.True(wb.Redo());
        Assert.Equal(after, Snapshot(wb));
    }

    [Fact]
    public void ThrowingSubscriber_LeavesTheEditInPlace()
    {
        var wb = Workbook.FromCells([P("A1", "1")]);
        wb.CellsChanged += (_, _) => throw new InvalidOperationException("subscriber");
        Assert.Throws<InvalidOperationException>(() => wb.InsertRows(1, 1));
        Assert.Equal("1", wb.GetInput("A2"));
        Assert.True(wb.CanUndo);
    }
}
