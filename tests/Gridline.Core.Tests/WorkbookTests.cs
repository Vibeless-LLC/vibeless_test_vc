using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;

namespace Gridline.Core.Tests;

/// <summary>Workbook Phase 1 API (§9.1, §9.2; rulings R5, R23, R24).</summary>
public class WorkbookTests
{
    private static CellValue N(double d) => CellValue.FromNumber(d);
    private static CellValue E(string code) => CellValue.FromError(code);

    /// <summary>Records every CellsChanged event raised by a workbook.</summary>
    private sealed class EventLog
    {
        public List<IReadOnlyCollection<string>> Events { get; } = [];
        public EventLog(Workbook wb) => wb.CellsChanged += (sender, e) =>
        {
            Assert.Same(wb, sender);
            Events.Add(e.Addresses);
        };
    }

    [Theory]
    [InlineData("=-2^2", "N:4")]
    [InlineData("=2^3^2", "N:64")]
    [InlineData("=ROUND(2.675,2)", "N:2.68")]
    [InlineData("=A1", "E:#CIRC!")]
    [InlineData("=1+", "E:#PARSE!")]
    [InlineData("=1e999", "E:#NUM!")]                // R53
    [InlineData("=#n/a", "E:#N/A")]                  // R56
    [InlineData("=#div/0!", "E:#DIV/0!")]            // R56
    [InlineData("=#n/a/2", "E:#N/A")]                // R56: '/' after a lower-case code is divide
    [InlineData("=#N/AX", "E:#PARSE!")]
    [InlineData("=#FOO!", "E:#PARSE!")]
    [InlineData("=#N/A", "E:#N/A")]
    [InlineData("=SUM(1,1e999)", "E:#NUM!")]         // R53
    [InlineData("=IF(TRUE,1,1e999)", "N:1")]         // R53: untaken branch is never evaluated
    [InlineData("=IFERROR(1e999,7)", "N:7")]
    public void WorkedExamples_ThroughWorkbook(string input, string expected)
    {
        var wb = new Workbook();
        wb.SetCell("A1", input);
        Assert.Equal(expected, wb.GetValue("A1").ToCanonicalString());
    }

    // ---- §9.1 constants (R23) ----

    [Fact]
    public void Constants_MatchPhase1Surface()
    {
        Assert.Equal(100000, Workbook.MaxRows);
        Assert.Equal(702, Workbook.MaxColumns);
        Assert.Equal(32767, Workbook.MaxInputLength);
        Assert.Equal(100, Workbook.UndoLimit);
        Assert.Equal(CellInputInterpreter.MaxInputLength, Workbook.MaxInputLength);
    }

    // ---- SetCell / GetValue / GetInput round trips (§9.1) ----

    [Fact]
    public void NewWorkbook_IsEmpty()
    {
        var wb = new Workbook();
        Assert.Empty(wb.GetUsedAddresses());
        Assert.Equal(CellValue.Blank, wb.GetValue("A1"));
        Assert.Equal("", wb.GetInput("A1"));
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Theory]
    [InlineData("42", "N:42")]
    [InlineData("1.5e3", "N:1500")]
    [InlineData("50%", "N:0.5")]
    [InlineData("true", "B:TRUE")]
    [InlineData("FALSE", "B:FALSE")]
    [InlineData("hello", "T:hello")]
    [InlineData("'123", "T:123")]
    [InlineData("'", "T:")]
    [InlineData("=1+2", "N:3")]
    [InlineData("=\"a\"&\"b\"", "T:ab")]
    [InlineData("=1/0", "E:#DIV/0!")]
    [InlineData("=SUM(1,2,3)", "N:6")]
    [InlineData("=NOSUCH(1)", "E:#NAME?")]
    [InlineData("=1+", "E:#PARSE!")]
    [InlineData("=ABS(1,2)", "E:#PARSE!")]
    public void SetCell_RoundTripsInputAndValue(string input, string canonical)
    {
        var wb = new Workbook();
        wb.SetCell("B2", input);
        Assert.Equal(input, wb.GetInput("B2"));
        Assert.Equal(canonical, wb.GetValue("B2").ToCanonicalString());
        Assert.Equal(["B2"], wb.GetUsedAddresses());
    }

    [Fact]
    public void Addresses_AreCaseInsensitiveAndCanonical()
    {
        var wb = new Workbook();
        wb.SetCell("aa10", "7");
        Assert.Equal(N(7), wb.GetValue("AA10"));
        Assert.Equal("7", wb.GetInput("Aa10"));
        Assert.Equal(["AA10"], wb.GetUsedAddresses());
    }

    [Fact]
    public void FormulaOverBlank_IsZero()
    {
        var wb = new Workbook();
        wb.SetCell("B1", "=A1");
        Assert.Equal(N(0), wb.GetValue("B1"));
        Assert.Equal(CellValue.Blank, wb.GetValue("A1"));
    }

    [Fact]
    public void FormulaReturningEmptyText_IsTextNotBlank()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=\"\"");
        wb.SetCell("B1", "=ISBLANK(A1)");
        wb.SetCell("C1", "=ISBLANK(Z9)");
        Assert.Equal(ValueKind.Text, wb.GetValue("A1").Kind);
        Assert.Equal("", wb.GetValue("A1").Text);
        Assert.Equal(CellValue.FromBoolean(false), wb.GetValue("B1"));
        Assert.Equal(CellValue.FromBoolean(true), wb.GetValue("C1"));
    }

    [Fact]
    public void Dependents_RecalculateOnEdit()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "2");
        wb.SetCell("A2", "3");
        wb.SetCell("A3", "=SUM(A1:A2)*10");
        Assert.Equal(N(50), wb.GetValue("A3"));
        wb.SetCell("A2", "5");
        Assert.Equal(N(70), wb.GetValue("A3"));
        wb.SetCell("A2", "");
        Assert.Equal(N(20), wb.GetValue("A3"));
    }

    [Fact]
    public void ClearingCell_RemovesItAndUpdatesDependents()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "5");
        wb.SetCell("B1", "=A1+1");
        var log = new EventLog(wb);
        wb.SetCell("A1", "");
        Assert.Equal("", wb.GetInput("A1"));
        Assert.Equal(CellValue.Blank, wb.GetValue("A1"));
        Assert.Equal(N(1), wb.GetValue("B1"));
        Assert.Equal(["B1"], wb.GetUsedAddresses());
        Assert.Equal([["A1", "B1"]], log.Events);

        wb.SetCell("B1", "");
        Assert.Empty(wb.GetUsedAddresses());
        Assert.Equal(CellValue.Blank, wb.GetValue("B1"));
    }

    [Fact]
    public void ClearingFormula_RemovesItsEdges()
    {
        var wb = new Workbook();
        wb.SetCell("B1", "=A1*2");
        wb.SetCell("B1", "");
        wb.SetCell("A1", "4");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(CellValue.Blank, wb.GetValue("B1"));
    }

    [Fact]
    public void ReplacingFormulaWithConstant_DropsOldEdges()
    {
        var wb = new Workbook();
        wb.SetCell("B1", "=A1*2");
        wb.SetCell("B1", "9");
        wb.SetCell("A1", "4");
        Assert.Equal(N(9), wb.GetValue("B1"));
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void Cycle_CreatedThenBrokenThroughSetCell()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=B1+1");
        wb.SetCell("B1", "=A1+1");
        wb.SetCell("C1", "=ISERROR(A1)");
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("A1"));
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("B1"));
        Assert.Equal(CellValue.FromBoolean(true), wb.GetValue("C1"));

        var log = new EventLog(wb);
        wb.SetCell("B1", "10");
        Assert.Equal(N(11), wb.GetValue("A1"));
        Assert.Equal(N(10), wb.GetValue("B1"));
        Assert.Equal(CellValue.FromBoolean(false), wb.GetValue("C1"));
        Assert.Equal([["A1", "B1", "C1"]], log.Events);
    }

    [Fact]
    public void SelfReference_IsCirc()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=A1+1");
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("A1"));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    // ---- Validation and atomicity after ArgumentException (§9.2) ----

    [Theory]
    [InlineData("")]
    [InlineData(" A1")]
    [InlineData("A0")]
    [InlineData("A100001")]
    [InlineData("AAA1")]
    [InlineData("ZZZ1")]
    [InlineData("A1:B2")]
    [InlineData("1A")]
    [InlineData("A")]
    [InlineData("Sheet2!A1")] // CR1.3, R83: "Sheet1!A1" is now a valid qualified address; an unknown sheet still throws (D-52)
    public void InvalidAddress_ThrowsArgumentException_AndChangesNothing(string address)
    {
        var wb = Seeded();
        var log = new EventLog(wb);
        Assert.ThrowsAny<ArgumentException>(() => wb.SetCell(address, "1"));
        Assert.ThrowsAny<ArgumentException>(() => wb.GetValue(address));
        Assert.ThrowsAny<ArgumentException>(() => wb.GetInput(address));
        Assert.Empty(log.Events);
        AssertSeeded(wb);
    }

    [Fact]
    public void NullArguments_Throw_AndChangeNothing()
    {
        var wb = Seeded();
        var log = new EventLog(wb);
        Assert.ThrowsAny<ArgumentException>(() => wb.SetCell(null!, "1"));
        Assert.ThrowsAny<ArgumentException>(() => wb.SetCell("A1", null!));
        Assert.ThrowsAny<ArgumentException>(() => wb.GetValue(null!));
        Assert.ThrowsAny<ArgumentException>(() => wb.GetInput(null!));
        Assert.Empty(log.Events);
        AssertSeeded(wb);
    }

    [Fact]
    public void OversizedInput_ThrowsArgumentException_AndChangesNothing()
    {
        var wb = Seeded();
        var log = new EventLog(wb);
        Assert.Throws<ArgumentException>(() => wb.SetCell("A1", new string('x', Workbook.MaxInputLength + 1)));
        Assert.Throws<ArgumentException>(() => wb.SetCell("A1", "=" + new string('1', Workbook.MaxInputLength)));
        Assert.Empty(log.Events);
        AssertSeeded(wb);
    }

    [Fact]
    public void InputAtMaxLength_IsAccepted()
    {
        var wb = new Workbook();
        var text = new string('x', Workbook.MaxInputLength);
        wb.SetCell("A1", text);
        Assert.Equal(text, wb.GetInput("A1"));
    }

    [Fact]
    public void GridCorners_AreValid()
    {
        var wb = new Workbook();
        wb.SetCell("ZZ100000", "1");
        wb.SetCell("A1", "=ZZ100000+1");
        Assert.Equal(N(2), wb.GetValue("A1"));
        Assert.Equal(["A1", "ZZ100000"], wb.GetUsedAddresses());
    }

    private static Workbook Seeded()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1*10");
        return wb;
    }

    private static void AssertSeeded(Workbook wb)
    {
        Assert.Equal("1", wb.GetInput("A1"));
        Assert.Equal(N(1), wb.GetValue("A1"));
        Assert.Equal("=A1*10", wb.GetInput("B1"));
        Assert.Equal(N(10), wb.GetValue("B1"));
        Assert.Equal(["A1", "B1"], wb.GetUsedAddresses());
        wb.SetCell("A1", "2"); // edges intact
        Assert.Equal(N(20), wb.GetValue("B1"));
    }

    // ---- CellsChanged (§9.2) ----

    [Fact]
    public void CellsChanged_RaisedOnceWithAllChangedAddresses_RowMajor()
    {
        var wb = new Workbook();
        wb.SetCell("C3", "1");
        wb.SetCell("B1", "=C3+1");
        wb.SetCell("A2", "=C3*2");
        wb.SetCell("D1", "=B1+A2");
        wb.SetCell("E5", "=C3*0"); // value does not change when C3 changes
        var log = new EventLog(wb);

        wb.SetCell("c3", "5");

        var single = Assert.Single(log.Events);
        Assert.Equal(["B1", "D1", "A2", "C3"], single);
    }

    [Fact]
    public void CellsChanged_IncludesEditedCell_WhenOnlyInputChanged()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1");
        var log = new EventLog(wb);
        wb.SetCell("A1", "1.0"); // same value, different input
        Assert.Equal([["A1"]], log.Events);
        Assert.Equal("1.0", wb.GetInput("A1"));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void CellsChanged_NewFormulaCell_ReportedOnce()
    {
        var wb = new Workbook();
        var log = new EventLog(wb);
        wb.SetCell("A1", "=1+1");
        Assert.Equal([["A1"]], log.Events);
    }

    [Fact]
    public void IdenticalInput_IsCompleteNoOp()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "5");
        wb.SetCell("B1", "=A1+1");
        var log = new EventLog(wb);

        wb.SetCell("B1", "=A1+1");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("A1", "5");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("Z9", "");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);

        Assert.Empty(log.Events);
        Assert.Equal(["A1", "B1"], wb.GetUsedAddresses());
    }

    [Fact]
    public void InputComparison_IsOrdinal()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "abc");
        var log = new EventLog(wb);
        wb.SetCell("A1", "ABC");
        Assert.Equal("ABC", wb.GetInput("A1"));
        Assert.Single(log.Events);
    }

    [Fact]
    public void ClearingBlankCell_RaisesNothing()
    {
        var wb = new Workbook();
        var log = new EventLog(wb);
        wb.SetCell("A1", "");
        Assert.Empty(log.Events);
        Assert.Empty(wb.GetUsedAddresses());
    }

    // ---- LastRecalc / EvaluatedFormulaCount (R21) ----

    [Fact]
    public void LastRecalc_CountsAffectedFormulaCells()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount); // constant, no dependents
        wb.SetCell("B1", "=A1+1");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount); // the new formula itself
        wb.SetCell("C1", "=B1+1");
        wb.SetCell("D1", "=A1");
        wb.SetCell("E1", "7");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("A1", "2");
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount); // B1, C1, D1
        wb.SetCell("B1", "=A1+2");
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount); // B1, C1
        wb.SetCell("C1", "");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void LastRecalc_CountsCycleMembers()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=B1");
        wb.SetCell("B1", "=A1");
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void RecalcStats_IsValueRecord()
    {
        Assert.Equal(new RecalcStats(3), new RecalcStats(3));
        Assert.Equal(3, new RecalcStats(3).EvaluatedFormulaCount);
        Assert.Equal(0, default(RecalcStats).EvaluatedFormulaCount);
    }

    // ---- GetUsedAddresses order ----

    [Fact]
    public void GetUsedAddresses_IsRowMajorAndCanonical()
    {
        var wb = new Workbook();
        foreach (var a in new[] { "b2", "A10", "AA1", "Z1", "A2", "C1", "a1", "ZZ2" }) wb.SetCell(a, "1");
        Assert.Equal(["A1", "C1", "Z1", "AA1", "A2", "B2", "ZZ2", "A10"], wb.GetUsedAddresses());
    }

    [Fact]
    public void GetUsedAddresses_ExcludesClearedCells()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("A2", "=A1");
        wb.SetCell("A1", "");
        Assert.Equal(["A2"], wb.GetUsedAddresses());
    }

    // ---- Atomicity when evaluation throws (§9.2) ----

    /// <summary>Built-ins plus BOOM(x): returns x, but throws when x is 3.</summary>
    private sealed class ThrowingResolver : IFunctionResolver
    {
        public int Invocations { get; private set; }

        private sealed class Boom(ThrowingResolver owner) : IFormulaFunction
        {
            public CellValue Invoke(IFunctionArguments arguments)
            {
                owner.Invocations++;
                var v = arguments.Evaluate(0);
                if (v.Kind == ValueKind.Number && v.Number == 3) throw new InvalidOperationException("boom");
                return v;
            }
        }

        public bool TryResolve(string upperName, [NotNullWhen(true)] out IFormulaFunction? function)
        {
            if (upperName == "BOOM") { function = new Boom(this); return true; }
            return BuiltInFunctionTable.Default.TryResolve(upperName, out function);
        }
    }

    [Fact]
    public void ThrowDuringRecalc_RollsBackEditAndWrittenValues()
    {
        var wb = new Workbook(new ThrowingResolver());
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1+1");
        wb.SetCell("C1", "=BOOM(B1)");
        wb.SetCell("D1", "=C1*10");
        Assert.Equal(N(2), wb.GetValue("C1"));
        Assert.Equal(N(20), wb.GetValue("D1"));
        int lastCount = wb.LastRecalc.EvaluatedFormulaCount;
        var log = new EventLog(wb);

        // B1 is written as 3 before C1 throws.
        var ex = Assert.Throws<InvalidOperationException>(() => wb.SetCell("A1", "2"));
        Assert.Equal("boom", ex.Message);

        Assert.Empty(log.Events);
        Assert.Equal("1", wb.GetInput("A1"));
        Assert.Equal(N(1), wb.GetValue("A1"));
        Assert.Equal(N(2), wb.GetValue("B1"));
        Assert.Equal(N(2), wb.GetValue("C1"));
        Assert.Equal(N(20), wb.GetValue("D1"));
        Assert.Equal(lastCount, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(["A1", "B1", "C1", "D1"], wb.GetUsedAddresses());

        // The workbook still works and its edges are intact.
        wb.SetCell("A1", "4");
        Assert.Equal(N(5), wb.GetValue("B1"));
        Assert.Equal(N(50), wb.GetValue("D1"));
    }

    [Fact]
    public void TwoFailingEditsWithLargeUndoLogs_LeaveEverythingUnchanged()
    {
        // §9.2: each failing edit writes ~2,000 values (more than the 1,024-entry retained undo capacity), so the
        // log is trimmed after the rollback; two in a row must still leave values and edges intact.
        const int n = 2000;
        var wb = new Workbook(new ThrowingResolver());
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1");
        for (int i = 2; i <= n; i++) wb.SetCell("B" + i, $"=B{i - 1}+1");
        wb.SetCell("C1", $"=BOOM(B{n}-{n - 4})"); // B2000 = A1 + 1999, so BOOM sees 3 when A1 = 0
        Assert.Equal(N(n), wb.GetValue("B" + n));
        Assert.Equal(N(4), wb.GetValue("C1"));
        var addresses = wb.GetUsedAddresses().ToList();
        var before = addresses.ToDictionary(a => a, a => wb.GetValue(a));
        var log = new EventLog(wb);

        Assert.Throws<InvalidOperationException>(() => wb.SetCell("A1", "0"));
        Assert.Throws<InvalidOperationException>(() => wb.SetCell("A1", "0.0"));

        Assert.Empty(log.Events);
        Assert.Equal("1", wb.GetInput("A1"));
        Assert.Equal(addresses, wb.GetUsedAddresses().ToList());
        foreach (var (addr, value) in before) Assert.Equal(value, wb.GetValue(addr));

        // Edges intact: a good edit recomputes the whole chain and C1.
        wb.SetCell("A1", "11");
        Assert.Equal(N(10 + n), wb.GetValue("B" + n));
        Assert.Equal(N(14), wb.GetValue("C1"));
        Assert.Equal(n + 1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void ThrowingNewFormula_LeavesCellEmptyAndNoEdges()
    {
        var wb = new Workbook(new ThrowingResolver());
        wb.SetCell("A1", "3");
        var log = new EventLog(wb);

        Assert.Throws<InvalidOperationException>(() => wb.SetCell("E1", "=BOOM(A1)"));
        Assert.Empty(log.Events);
        Assert.Equal("", wb.GetInput("E1"));
        Assert.Equal(CellValue.Blank, wb.GetValue("E1"));
        Assert.Equal(["A1"], wb.GetUsedAddresses());

        // If E1's edge to A1 survived, this recalc would find a formula cell with no formula and throw.
        wb.SetCell("A1", "5");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Single(log.Events);
    }

    [Fact]
    public void ThrowingReplacement_RestoresPreviousFormulaAndEdges()
    {
        var wb = new Workbook(new ThrowingResolver());
        wb.SetCell("A1", "3");
        wb.SetCell("B1", "7");
        wb.SetCell("E1", "=B1+1");
        wb.SetCell("F1", "=E1");

        Assert.Throws<InvalidOperationException>(() => wb.SetCell("E1", "=BOOM(A1)"));
        Assert.Equal("=B1+1", wb.GetInput("E1"));
        Assert.Equal(N(8), wb.GetValue("E1"));
        Assert.Equal(N(8), wb.GetValue("F1"));

        wb.SetCell("B1", "1");
        Assert.Equal(N(2), wb.GetValue("E1"));
        Assert.Equal(N(2), wb.GetValue("F1"));
        wb.SetCell("A1", "9");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount); // the failed formula's edge to A1 was rolled back
    }

    [Fact]
    public void ThrowingReplacement_RestoresRangeEdgesInASharedBucket()
    {
        // §6.1, §9.2: the rollback re-adds the old range after its index entry was swap-removed from buckets it
        // shares with other same-shaped ranges; every range must still be found afterwards.
        var wb = new Workbook(new ThrowingResolver());
        wb.SetCell("A1", "3");
        wb.SetCell("E1", "=SUM(B1:B4)");
        wb.SetCell("E2", "=SUM(B2:B5)");
        wb.SetCell("E3", "=SUM(B3:B6)");
        wb.SetCell("E4", "=SUM(B1:B3)");

        Assert.Throws<InvalidOperationException>(() => wb.SetCell("E1", "=BOOM(A1)+SUM(B1:B2)"));
        Assert.Equal("=SUM(B1:B4)", wb.GetInput("E1"));

        wb.SetCell("B4", "1");
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount); // E1, E2, E3
        Assert.Equal(N(1), wb.GetValue("E1"));
        wb.SetCell("B1", "2");
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount); // E1, E4
        Assert.Equal(N(3), wb.GetValue("E1"));
        wb.SetCell("B6", "4");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount); // E3
        wb.SetCell("E2", "");
        wb.SetCell("B5", "8");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount); // E3 only; E2's range is gone
        wb.SetCell("B2", "16");
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount); // E1, E4
        Assert.Equal(N(19), wb.GetValue("E1"));
    }

    [Fact]
    public void ThrowingFormulaReplacingConstant_RestoresConstant()
    {
        var wb = new Workbook(new ThrowingResolver());
        wb.SetCell("A1", "3");
        wb.SetCell("B1", "hello");
        wb.SetCell("C1", "=B1&\"!\"");

        Assert.Throws<InvalidOperationException>(() => wb.SetCell("B1", "=BOOM(A1)"));
        Assert.Equal("hello", wb.GetInput("B1"));
        Assert.Equal(CellValue.FromText("hello"), wb.GetValue("B1"));
        Assert.Equal(CellValue.FromText("hello!"), wb.GetValue("C1"));
    }

    // ---- FromCells (R5, R23) ----

    [Fact]
    public void FromCells_BuildsAndRecalculatesOnce_WithoutEvents()
    {
        var wb = Workbook.FromCells(
        [
            new("c1", "=A1+B1"),
            new("A1", "2"),
            new("B1", "3"),
            new("A2", ""),
            new("D1", "=C1*2"),
        ]);
        Assert.Equal(N(5), wb.GetValue("C1"));
        Assert.Equal(N(10), wb.GetValue("D1"));
        Assert.Equal("=A1+B1", wb.GetInput("C1"));
        Assert.Equal(["A1", "B1", "C1", "D1"], wb.GetUsedAddresses());
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount);

        var log = new EventLog(wb);
        wb.SetCell("A1", "10");
        Assert.Equal(N(26), wb.GetValue("D1"));
        Assert.Equal([["A1", "C1", "D1"]], log.Events);
    }

    [Fact]
    public void FromCells_Empty_GivesEmptyWorkbook()
    {
        var wb = Workbook.FromCells([]);
        Assert.Empty(wb.GetUsedAddresses());
    }

    [Fact]
    public void FromCells_CyclesAndErrors()
    {
        var wb = Workbook.FromCells([new("A1", "=B1"), new("B1", "=A1"), new("C1", "=1/0"), new("D1", "=(")]);
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("A1"));
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("B1"));
        Assert.Equal(E(ErrorCodes.Div0), wb.GetValue("C1"));
        Assert.Equal(E(ErrorCodes.Parse), wb.GetValue("D1"));
    }

    public static TheoryData<KeyValuePair<string, string>[]> BadCellSets => new()
    {
        new KeyValuePair<string, string>[] { new("A1", "1"), new("A1", "2") },
        new KeyValuePair<string, string>[] { new("A1", "1"), new("a1", "2") },
        new KeyValuePair<string, string>[] { new("A1", ""), new("a1", "2") },
        new KeyValuePair<string, string>[] { new("A1", "1"), new("$A$1", "2") },
        new KeyValuePair<string, string>[] { new("A1", "1"), new("A1:B2", "2") },
        new KeyValuePair<string, string>[] { new("A1", "1"), new("A0", "2") },
        new KeyValuePair<string, string>[] { new("A1", "1"), new("ZZZ1", "2") },
        new KeyValuePair<string, string>[] { new("A1", "1"), new(null!, "2") },
        new KeyValuePair<string, string>[] { new("A1", "1"), new("B1", null!) },
        new KeyValuePair<string, string>[] { new("A1", "1"), new("B1", new string('x', 32768)) },
    };

    [Theory]
    [MemberData(nameof(BadCellSets))]
    public void FromCells_InvalidOrDuplicateEntry_Throws(KeyValuePair<string, string>[] cells)
    {
        Assert.ThrowsAny<ArgumentException>(() => Workbook.FromCells(cells));
    }

    [Fact]
    public void FromCells_NullSequence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Workbook.FromCells(null!));
    }

    [Fact]
    public void FromCells_ValidatesEverythingBeforeEvaluating()
    {
        var resolver = new ThrowingResolver();
        Assert.ThrowsAny<ArgumentException>(() =>
            Workbook.FromCells([new("A1", "=BOOM(1)"), new("B1", "=BOOM(2)"), new("A1", "x")], resolver));
        Assert.Equal(0, resolver.Invocations); // nothing was built or evaluated
    }

    // ---- Task 2 hardening (§9.1, §9.2, R24, R25) ----

    [Fact]
    public void CellsChangedAddresses_AreReadOnly()
    {
        var wb = new Workbook();
        IReadOnlyCollection<string>? seen = null;
        wb.CellsChanged += (_, e) => seen = e.Addresses;
        wb.SetCell("A1", "1");
        Assert.NotNull(seen);
        var list = Assert.IsAssignableFrom<IList<string>>(seen);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = "Z9");
        Assert.Equal(["A1"], seen);

        // The public constructor also exposes its argument read-only.
        var args = new CellsChangedEventArgs(new[] { "B2" });
        Assert.Throws<NotSupportedException>(() => ((IList<string>)args.Addresses)[0] = "Z9");
    }

    [Fact]
    public void GetUsedAddresses_RowMajorAcrossRowsAndColumns()
    {
        var wb = new Workbook();
        foreach (var a in new[] { "B2", "AA1", "A2", "A100000", "ZZ1", "B1", "A1" }) wb.SetCell(a, "x");
        Assert.Equal(["A1", "B1", "AA1", "ZZ1", "A2", "B2", "A100000"], wb.GetUsedAddresses());
    }

    [Fact]
    public void FormulaReplacedBySameValueFormula_StillRaisesEventWithEditedCell()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1");
        var log = new EventLog(wb);
        wb.SetCell("B1", "=A1*1");
        Assert.Equal(N(1), wb.GetValue("B1"));
        Assert.Single(log.Events);
        Assert.Contains("B1", log.Events[0]);
    }

    [Fact]
    public void ClearingEmptyTextCell_RaisesEvent()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "'");
        Assert.Equal(CellValue.FromText(""), wb.GetValue("A1"));
        var log = new EventLog(wb);
        wb.SetCell("A1", "");
        Assert.Single(log.Events);
        Assert.Equal(["A1"], log.Events[0]);
        Assert.Equal(CellValue.Blank, wb.GetValue("A1"));
        Assert.Empty(wb.GetUsedAddresses());
    }

    [Fact]
    public void SubscriberException_Propagates_EditStaysCommitted()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1+1");
        wb.CellsChanged += (_, _) => throw new InvalidOperationException("handler");
        var ex = Assert.Throws<InvalidOperationException>(() => wb.SetCell("A1", "5"));
        Assert.Equal("handler", ex.Message);
        Assert.Equal("5", wb.GetInput("A1"));
        Assert.Equal(N(6), wb.GetValue("B1"));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void DollarAddresses_AcceptedAsPlainA1()
    {
        var wb = new Workbook();
        wb.SetCell("$A$1", "7");
        Assert.Equal(N(7), wb.GetValue("A1"));
        Assert.Equal(N(7), wb.GetValue("$A$1"));
        Assert.Equal("7", wb.GetInput("$A$1"));
        Assert.Equal(["A1"], wb.GetUsedAddresses());
    }

    [Fact]
    public void ThrowingReplacement_OfRangeFormula_KeepsRangeEdgesWorking()
    {
        var wb = new Workbook(new ThrowingResolver());
        wb.SetCell("A1", "1");
        wb.SetCell("A2", "2");
        wb.SetCell("A3", "3");
        wb.SetCell("A4", "3");
        wb.SetCell("C1", "=SUM(A1:A3)");
        Assert.Equal(N(6), wb.GetValue("C1"));

        Assert.Throws<InvalidOperationException>(() => wb.SetCell("C1", "=BOOM(A4)"));
        Assert.Equal("=SUM(A1:A3)", wb.GetInput("C1"));
        Assert.Equal(N(6), wb.GetValue("C1"));

        wb.SetCell("A2", "10");
        Assert.Equal(N(14), wb.GetValue("C1"));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("A4", "100"); // the failed formula's edge to A4 must be gone
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }
}
