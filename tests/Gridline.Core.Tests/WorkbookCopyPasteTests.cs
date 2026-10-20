using System.Diagnostics.CodeAnalysis;
using System.Text;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;

namespace Gridline.Core.Tests;

// Requirement IDs: §9.4 (CopyPaste: snapshot, overlap, grid bounds, verbatim constants, blank clears, shifting of
// relative/absolute/mixed references, #REF!, canonical re-emission), §5.5 (canonical printing, #REF!), §9.2
// (atomicity, one recalc, one CellsChanged), §9.3 (one undo step, no-op), §4.2 (range syntax), R24, R39, R55.
public class WorkbookCopyPasteTests
{
    private static CellValue N(double d) => CellValue.FromNumber(d);
    private static KeyValuePair<string, string> P(string a, string i) => new(a, i);

    private static Workbook Book(params (string Address, string Input)[] cells) =>
        Workbook.FromCells(cells.Select(c => P(c.Address, c.Input)));

    private sealed class EventLog
    {
        public List<string[]> Events { get; } = [];
        public EventLog(Workbook wb) => wb.CellsChanged += (_, e) => Events.Add([.. e.Addresses]);
    }

    private static Dictionary<string, (string Input, CellValue Value)> Snapshot(Workbook wb) =>
        wb.GetUsedAddresses().ToDictionary(a => a, a => (wb.GetInput(a), wb.GetValue(a)));

    private static string Pasted(string formula, string from, string to)
    {
        var wb = Book((from, formula));
        wb.CopyPaste(from, to);
        return wb.GetInput(to);
    }

    // ---- shifting (§9.4) ----

    [Theory]
    [InlineData("=B1+1", "A1", "A2", "=B2+1")]           // relative, down
    [InlineData("=B1+1", "A1", "C3", "=D3+1")]           // relative, down and right
    [InlineData("=D5", "E6", "B2", "=A1")]               // relative, up and left
    [InlineData("=$B$1*2", "A1", "D9", "=$B$1*2")]       // absolute
    [InlineData("=$B1", "A1", "C4", "=$B4")]             // absolute column, relative row
    [InlineData("=B$1", "A1", "C4", "=D$1")]             // relative column, absolute row
    [InlineData("=SUM($A$1:B2)", "C3", "D4", "=SUM($A$1:C3)")] // corners with different $ flags
    [InlineData("=SUM(A$1:$B2)", "C3", "D5", "=SUM(B$1:$B4)")]
    [InlineData("=SUM($A1:B$2)", "C3", "E4", "=SUM($A2:D$2)")]
    [InlineData("=SUM(b2:a1)", "C3", "C4", "=SUM(B3:A2)")] // corners keep their written order
    [InlineData("=IF(A1>0,\"a\"\"b\",1.50)&x", "B1", "B2", "=IF(A2>0,\"a\"\"b\",1.50)&x")]
    public void Formula_ReferencesShiftByTheOffset(string formula, string from, string to, string expected) =>
        Assert.Equal(expected, Pasted(formula, from, to));

    [Theory]
    [InlineData("=A1", "A2", "A1")]                   // top edge: row 0
    [InlineData("=A1", "B1", "A1")]                   // left edge: column 0
    [InlineData("=A99999", "A1", "A3")]               // bottom edge: row 100001
    [InlineData("=ZY1", "A1", "C1")]                  // right edge: column 703
    [InlineData("=A$1", "A2", "A1", false)]           // absolute row does not move
    [InlineData("=$A1", "B1", "A1", false)]           // absolute column does not move
    public void Formula_ReferenceShiftedOffTheGrid_BecomesRef(string formula, string from, string to, bool becomesRef = true)
    {
        var wb = Book((from, formula));
        wb.CopyPaste(from, to);
        if (becomesRef)
        {
            Assert.Equal("=#REF!", wb.GetInput(to));
            Assert.Equal(CellValue.FromError(ErrorCodes.Ref), wb.GetValue(to));
        }
        else Assert.Equal(formula, wb.GetInput(to));
    }

    [Theory]
    [InlineData("=SUM(A1:B5)+1", "A2", "A1", "=SUM(#REF!)+1")]       // start corner off the top
    [InlineData("=SUM(A1:A99999)+1", "B1", "B5", "=SUM(#REF!)+1")]   // end corner off the bottom
    [InlineData("=SUM(A1:$A$5)", "B2", "A2", "=SUM(#REF!)")]         // relative start off the left, absolute end fine
    [InlineData("=COUNT(ZX1:ZZ$1)", "A1", "B1", "=COUNT(#REF!)")]    // end corner off the right
    [InlineData("=SUM($A$1:A2)", "B3", "B2", "=SUM($A$1:A1)")]       // stays on the grid
    public void Formula_RangeWithOneCornerOffTheGrid_BecomesRef(string formula, string from, string to, string expected) =>
        Assert.Equal(expected, Pasted(formula, from, to));

    [Fact]
    public void ZeroOffset_ReEmitsCanonically()
    {
        var wb = Book(("A1", "3"), ("B1", "= sum( a1 )"), ("C1", "=  $a$1 * 2"));
        var log = new EventLog(wb);
        wb.CopyPaste("A1:C1", "A1");
        Assert.Equal("=SUM(A1)", wb.GetInput("B1"));
        Assert.Equal("=$A$1*2", wb.GetInput("C1"));
        Assert.Equal("3", wb.GetInput("A1"));
        Assert.Equal([["B1", "C1"]], log.Events); // the constant did not change; the values did not either
        Assert.True(wb.CanUndo);
        Assert.True(wb.Undo());
        Assert.Equal("= sum( a1 )", wb.GetInput("B1")); // §9.3: exact prior text
        Assert.Equal("=  $a$1 * 2", wb.GetInput("C1"));
    }

    [Fact]
    public void ParseErrorFormula_IsCopiedVerbatimUnshifted()
    {
        var wb = Book(("A1", "=SUM( A1"), ("A2", "= 1 +"), ("A3", "=ABS(1,2)"));
        wb.CopyPaste("A1:A3", "C5");
        Assert.Equal("=SUM( A1", wb.GetInput("C5")); // R39
        Assert.Equal("= 1 +", wb.GetInput("C6"));
        Assert.Equal("=ABS(1,2)", wb.GetInput("C7")); // arity error is a parse failure (§5.4)
        Assert.Equal(CellValue.FromError(ErrorCodes.Parse), wb.GetValue("C5"));
        Assert.Equal(CellValue.FromError(ErrorCodes.Parse), wb.GetValue("C7"));
    }

    [Fact]
    public void Constants_AreCopiedVerbatim()
    {
        var wb = Book(("A1", "'0123"), ("A2", "1.50"), ("A3", "true"), ("A4", " 12"), ("A5", "'=A1"), ("A6", "-0"), ("A7", "A1"));
        wb.CopyPaste("A1:A7", "B2");
        Assert.Equal("'0123", wb.GetInput("B2"));
        Assert.Equal("1.50", wb.GetInput("B3"));
        Assert.Equal("true", wb.GetInput("B4"));
        Assert.Equal(" 12", wb.GetInput("B5"));
        Assert.Equal("'=A1", wb.GetInput("B6"));
        Assert.Equal("-0", wb.GetInput("B7"));
        Assert.Equal("A1", wb.GetInput("B8"));
        Assert.Equal(N(1.5), wb.GetValue("B3"));
        Assert.Equal(CellValue.FromText("0123"), wb.GetValue("B2"));
    }

    // ---- rectangles, overlap and blanks (§9.4) ----

    [Fact]
    public void BlankSourceCells_ClearDestinationCells()
    {
        var wb = Book(("A1", "1"), ("C2", "=A1"), ("D1", "x"), ("E1", "y"), ("D2", "z"), ("E2", "w"), ("F1", "keep"));
        wb.CopyPaste("A1:B2", "D1");
        Assert.Equal("1", wb.GetInput("D1"));
        Assert.Equal("", wb.GetInput("E1"));
        Assert.Equal("", wb.GetInput("D2"));
        Assert.Equal("", wb.GetInput("E2"));
        Assert.Equal("keep", wb.GetInput("F1")); // outside the destination rectangle
        Assert.Equal(["A1", "D1", "F1", "C2"], wb.GetUsedAddresses());
    }

    [Fact]
    public void Overlap_Down()
    {
        var wb = Book(("A1", "1"), ("A2", "2"), ("A3", "=A1*10"), ("A4", "old"));
        wb.CopyPaste("A1:A3", "A2");
        Assert.Equal(["1", "1", "2", "=A2*10"], new[] { "A1", "A2", "A3", "A4" }.Select(wb.GetInput));
        Assert.Equal(N(10), wb.GetValue("A4"));
    }

    [Fact]
    public void Overlap_Up()
    {
        var wb = Book(("A1", "old"), ("A2", "=A3+1"), ("A3", "5"), ("A4", "=A2"));
        wb.CopyPaste("A2:A4", "A1");
        Assert.Equal(["=A2+1", "5", "=A1", "=A2"], new[] { "A1", "A2", "A3", "A4" }.Select(wb.GetInput));
        Assert.Equal(N(6), wb.GetValue("A1"));
        Assert.Equal(N(6), wb.GetValue("A3"));
    }

    [Fact]
    public void Overlap_Right()
    {
        var wb = Book(("A1", "1"), ("C1", "=A1+B1"), ("D1", "old"));
        wb.CopyPaste("A1:C1", "B1");
        Assert.Equal(["1", "1", "", "=B1+C1"], new[] { "A1", "B1", "C1", "D1" }.Select(wb.GetInput));
        Assert.Equal(N(1), wb.GetValue("D1"));
    }

    [Fact]
    public void Overlap_Left()
    {
        var wb = Book(("A1", "old"), ("B1", "=C1"), ("D1", "7"));
        wb.CopyPaste("B1:D1", "A1");
        Assert.Equal(["=B1", "", "7", "7"], new[] { "A1", "B1", "C1", "D1" }.Select(wb.GetInput));
    }

    [Fact]
    public void Overlap_Diagonal_TwoByTwo()
    {
        var wb = Book(("A1", "a"), ("B1", "b"), ("A2", "c"), ("B2", "=A1"));
        wb.CopyPaste("A1:B2", "B2");
        Assert.Equal(["a", "b", "c", "a", "b", "c", "=B2"],
            new[] { "A1", "B1", "A2", "B2", "C2", "B3", "C3" }.Select(wb.GetInput));
    }

    [Theory]
    [InlineData("$B$2:a1")]
    [InlineData("b2:A1")]
    [InlineData("A2:B1")]
    [InlineData("a1:$b$2")]
    public void SourceRange_CornersInAnyOrder_DollarMarkersAllowed(string source)
    {
        var wb = Book(("A1", "1"), ("B2", "=A1"));
        wb.CopyPaste(source, "$c$3");
        Assert.Equal("1", wb.GetInput("C3"));
        Assert.Equal("=C3", wb.GetInput("D4"));
    }

    [Fact]
    public void SingleCellSource_IsAOneByOneRectangle()
    {
        var wb = Book(("A1", "=B1"), ("A2", "x"));
        wb.CopyPaste("A1", "A3");
        Assert.Equal("=B3", wb.GetInput("A3"));
        Assert.Equal("x", wb.GetInput("A2"));
    }

    [Fact]
    public void DestinationAtTheGridCorner_Fits()
    {
        var wb = Book(("A1", "=B2"), ("B2", "1"));
        wb.CopyPaste("A1:B2", "ZY99999");
        Assert.Equal("=ZZ100000", wb.GetInput("ZY99999"));
        Assert.Equal("1", wb.GetInput("ZZ100000"));
        Assert.Equal(N(1), wb.GetValue("ZY99999"));
    }

    // ---- invalid arguments and atomicity (§9.2, §9.4, R55) ----

    [Theory]
    [InlineData("A1:B2", "ZZ1")]      // one column too far right
    [InlineData("A1:A2", "A100000")]  // one row too far down
    [InlineData("A1:ZZ100000", "B1")]
    [InlineData("A1:ZZ100000", "A2")]
    public void DestinationExceedingTheGrid_ThrowsAndChangesNothing(string source, string dest)
    {
        var wb = Book(("A1", "1"), ("B2", "=A1"), ("ZZ1", "x"));
        var before = Snapshot(wb);
        var stats = wb.LastRecalc;
        var log = new EventLog(wb);
        var ex = Assert.Throws<ArgumentException>(() => wb.CopyPaste(source, dest));
        Assert.Equal("destinationTopLeft", ex.ParamName);
        Assert.Equal(before, Snapshot(wb));
        Assert.Empty(log.Events);
        Assert.False(wb.CanUndo);
        Assert.False(wb.IsDirty);
        Assert.Equal(stats, wb.LastRecalc);
    }

    [Theory]
    [InlineData("", "A1", "sourceRange")]
    [InlineData("A0", "A1", "sourceRange")]
    [InlineData("A1:", "A1", "sourceRange")]
    [InlineData("A1:B2:C3", "A1", "sourceRange")]
    [InlineData("A1 :B2", "A1", "sourceRange")]
    [InlineData("AAA1", "A1", "sourceRange")]
    [InlineData("A1", "A1:B2", "destinationTopLeft")]
    [InlineData("A1", "", "destinationTopLeft")]
    [InlineData("A1", "A100001", "destinationTopLeft")]
    public void InvalidArguments_ThrowAndChangeNothing(string source, string dest, string param)
    {
        var wb = Book(("A1", "1"));
        var log = new EventLog(wb);
        var ex = Assert.Throws<ArgumentException>(() => wb.CopyPaste(source, dest));
        Assert.Equal(param, ex.ParamName);
        Assert.Equal("1", wb.GetInput("A1"));
        Assert.Empty(log.Events);
        Assert.False(wb.CanUndo);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var wb = new Workbook();
        Assert.Equal("sourceRange", Assert.Throws<ArgumentNullException>(() => wb.CopyPaste(null!, "A1")).ParamName);
        Assert.Equal("destinationTopLeft", Assert.Throws<ArgumentNullException>(() => wb.CopyPaste("A1", null!)).ParamName);
    }

    [Fact]
    public void RewrittenFormulaOverMaxInputLength_ThrowsAndChangesNothing()
    {
        // 10,921 references "A1" (32,764 characters); shifted off the top each prints "#REF!", about 65,500 characters.
        var sb = new StringBuilder("=A1");
        while (sb.Length + 3 <= Workbook.MaxInputLength) sb.Append("+A1");
        string formula = sb.ToString();
        var wb = Book(("A2", formula), ("A3", "5"), ("B1", "x"));
        var before = Snapshot(wb);
        var log = new EventLog(wb);
        var ex = Assert.Throws<ArgumentException>(() => wb.CopyPaste("A2:B3", "A1"));
        Assert.Equal("sourceRange", ex.ParamName); // R55
        Assert.Equal(before, Snapshot(wb));
        Assert.Empty(log.Events);
        Assert.False(wb.CanUndo);

        // The same formula at an in-grid offset prints at its own length and pastes fine.
        wb.CopyPaste("A2", "A3");
        Assert.Equal(formula.Replace("A1", "A2", StringComparison.Ordinal), wb.GetInput("A3"));
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

    [Fact]
    public void ThrowDuringRecalc_RollsBackThePaste()
    {
        var resolver = new ArmableResolver();
        var wb = Workbook.FromCells([P("A1", "1"), P("B1", "=BOOM(A1)"), P("C1", "old"), P("D1", "keep")], resolver);
        var before = Snapshot(wb);
        var log = new EventLog(wb);
        resolver.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.CopyPaste("A1:B1", "C1"));
        resolver.Armed = false;
        Assert.Equal(before, Snapshot(wb));
        Assert.Empty(log.Events);
        Assert.False(wb.CanUndo);
        wb.SetCell("A1", "4"); // edges intact
        Assert.Equal(N(4), wb.GetValue("B1"));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    // ---- one step, one recalc, one event (§9.2, §9.3) ----

    [Fact]
    public void Paste_IsOneUndoStep_UndoRestoresExactTextAndRedoReapplies()
    {
        var wb = Book(("A1", "1"), ("A2", "=a1 + 1"), ("C1", "=  sum(A1:A2)"), ("C2", "'text"), ("C3", "stay"));
        wb.MarkSaved();
        var before = Snapshot(wb);
        wb.CopyPaste("A1:A2", "C1");
        Assert.Equal(["1", "=C1+1", "stay"], new[] { "C1", "C2", "C3" }.Select(wb.GetInput));
        var after = Snapshot(wb);
        Assert.True(wb.IsDirty);
        Assert.True(wb.Undo());
        Assert.False(wb.CanUndo);
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal("=  sum(A1:A2)", wb.GetInput("C1"));
        Assert.Equal("=a1 + 1", wb.GetInput("A2"));
        Assert.False(wb.IsDirty);
        Assert.True(wb.Redo());
        Assert.Equal(after, Snapshot(wb));
    }

    [Fact]
    public void Paste_RaisesOneEvent_WithEditedCellsAndChangedDependents_AndOneRecalc()
    {
        var wb = Book(("A1", "1"), ("A2", "=A1*2"), ("B1", "9"), ("C1", "=B1+B2"), ("D1", "=C1"));
        var log = new EventLog(wb);
        wb.CopyPaste("A1:A2", "B1"); // B1 = 1, B2 = =B1*2
        Assert.Single(log.Events);
        Assert.Equal(["B1", "C1", "D1", "B2"], log.Events[0]);
        Assert.Equal(N(3), wb.GetValue("C1"));
        Assert.Equal(N(3), wb.GetValue("D1"));
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount); // B2, C1, D1
    }

    [Fact]
    public void Paste_ThatChangesNothing_CreatesNoStepAndNoEvent()
    {
        var wb = Book(("A1", "1"), ("B1", "=A1"), ("D1", "1"));
        wb.SetCell("E1", "2");
        wb.SetCell("A1", "3"); // LastRecalc = 1 (B1)
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.True(wb.Undo());
        Assert.True(wb.CanRedo);
        var log = new EventLog(wb);

        wb.CopyPaste("A1", "D1");       // same constant
        wb.CopyPaste("B1", "B1");       // already canonical
        wb.CopyPaste("F1:G9", "H20");   // blank onto blank
        wb.CopyPaste("A1:B1", "A1");    // whole range onto itself

        Assert.Empty(log.Events);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount); // R24
        Assert.True(wb.CanRedo); // a no-op is not a mutation and keeps the redo stack
        Assert.True(wb.Redo());
    }

    [Fact]
    public void Paste_ClearsRedo()
    {
        var wb = Book(("A1", "1"));
        wb.SetCell("A2", "2");
        Assert.True(wb.Undo());
        wb.CopyPaste("A1", "B1");
        Assert.False(wb.CanRedo);
    }

    // ---- proportional to used cells (§9.4, §12) ----

    [Fact]
    public void WholeGridSource_OnASparseSheet_TouchesOnlyUsedCells()
    {
        var wb = Book(("A1", "= a2"), ("ZZ100000", "=  zz99999"), ("M500", "x"));
        var log = new EventLog(wb);
        wb.CopyPaste("A1:ZZ100000", "A1"); // whole grid at zero offset: canonicalizes the two formulas
        Assert.Equal(["A1", "ZZ100000"], log.Events.Single());
        Assert.Equal("=A2", wb.GetInput("A1"));
        Assert.Equal("=ZZ99999", wb.GetInput("ZZ100000"));

        wb.CopyPaste("A1:ZZ50000", "A50001"); // 35.1 million cells of area, three used
        Assert.Equal("=A50002", wb.GetInput("A50001"));
        Assert.Equal("x", wb.GetInput("M50500"));
        Assert.Equal("", wb.GetInput("ZZ100000")); // used destination cell under a blank source cell
        Assert.Equal(["A1", "M500", "A50001", "M50500"], wb.GetUsedAddresses());
    }
}
