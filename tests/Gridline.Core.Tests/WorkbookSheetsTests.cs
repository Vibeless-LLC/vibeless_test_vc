using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;

namespace Gridline.Core.Tests;

// Requirement IDs: CR1.1 (sheet list, names, 255 limit), CR1.2 (qualified references evaluate against their sheet,
// missing sheet #REF!), CR1.3 (SheetNames, ActiveSheet, AddSheet, qualified API addresses, GetUsedAddresses), CR1.10
// (single sheet unchanged), §6.7 (cross-sheet cycles, incremental recalc, no recursion), §9.2 (atomicity, CellsChanged),
// §9.3 (AddSheet undo/redo), R78, R80, R81, R82, R83, R84 (ruling P5-2), R77 (names qualified with the active sheet; see WorkbookQualifiedNamesTests).
public class WorkbookSheetsTests
{
    private static CellValue N(double d) => CellValue.FromNumber(d);
    private static CellValue E(string code) => CellValue.FromError(code);

    private sealed class EventLog
    {
        public List<string[]> Events { get; } = [];
        public EventLog(Workbook wb) => wb.CellsChanged += (_, e) => Events.Add([.. e.Addresses]);
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

    /// <summary>A workbook with sheets Sheet1, then <paramref name="more"/> appended.</summary>
    private static Workbook Book(params string[] more)
    {
        var wb = new Workbook();
        foreach (var name in more) wb.AddSheet(name);
        return wb;
    }

    // ---- New workbook, SheetNames, ActiveSheet (CR1.1, CR1.3, R80) ----

    [Fact]
    public void NewWorkbook_HasOneActiveSheet1()
    {
        var wb = new Workbook();
        Assert.Equal(["Sheet1"], wb.SheetNames);
        Assert.Equal("Sheet1", wb.ActiveSheet);
        Assert.False(wb.CanUndo);
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void FromCells_BuildsSingleSheet1()
    {
        var wb = Workbook.FromCells([new("A1", "1"), new("A2", "=A1+1")]);
        Assert.Equal(["Sheet1"], wb.SheetNames);
        Assert.Equal("Sheet1", wb.ActiveSheet);
        Assert.Equal(N(2), wb.GetValue("Sheet1!A2"));
    }

    [Fact]
    public void SheetNames_IsASnapshot()
    {
        var wb = new Workbook();
        var names = wb.SheetNames;
        wb.AddSheet("Data");
        Assert.Equal(["Sheet1"], names);
        Assert.Equal(["Sheet1", "Data"], wb.SheetNames);
    }

    [Fact]
    public void ActiveSheet_Setter_IsCaseInsensitive_StoresDefinedCasing_AndIsNotAStep()
    {
        var wb = Book("Data");
        wb.MarkSaved();
        var log = new EventLog(wb);
        wb.ActiveSheet = "DATA";
        Assert.Equal("Data", wb.ActiveSheet);
        wb.ActiveSheet = "sheet1";
        Assert.Equal("Sheet1", wb.ActiveSheet);
        Assert.Empty(log.Events);
        Assert.False(wb.IsDirty);
        Assert.False(wb.CanRedo);
        Assert.True(wb.Undo()); // the newest step is still AddSheet: activation made none
        Assert.Equal(["Sheet1"], wb.SheetNames);
        Assert.False(wb.CanUndo);
    }

    [Fact]
    public void ActiveSheet_Setter_UnknownOrNull_Throws_AndChangesNothing()
    {
        var wb = Book("Data");
        wb.ActiveSheet = "Data";
        Assert.Throws<ArgumentException>(() => wb.ActiveSheet = "Nope");
        Assert.Throws<ArgumentNullException>(() => wb.ActiveSheet = null!);
        Assert.Equal("Data", wb.ActiveSheet);
    }

    // ---- AddSheet (CR1.3, R78, R80) ----

    [Fact]
    public void AddSheet_AppendsByDefault_AndDoesNotChangeTheActiveSheet()
    {
        var wb = new Workbook();
        wb.AddSheet("Data");
        wb.AddSheet("My Sheet");
        Assert.Equal(["Sheet1", "Data", "My Sheet"], wb.SheetNames);
        Assert.Equal("Sheet1", wb.ActiveSheet);
        Assert.True(wb.CanUndo);
        Assert.True(wb.IsDirty);
    }

    [Theory]
    [InlineData(0, new[] { "New", "Sheet1", "A", "B" })]
    [InlineData(1, new[] { "Sheet1", "New", "A", "B" })]
    [InlineData(2, new[] { "Sheet1", "A", "New", "B" })]
    [InlineData(3, new[] { "Sheet1", "A", "B", "New" })]
    public void AddSheet_InsertsAtIndex(int index, string[] expected)
    {
        var wb = Book("A", "B");
        wb.AddSheet("New", index);
        Assert.Equal(expected, wb.SheetNames);
    }

    [Fact]
    public void AddSheet_EmptySheet_RaisesNoCellsChanged()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        var log = new EventLog(wb);
        wb.AddSheet("Data");
        Assert.Empty(log.Events);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    public static TheoryData<string?, int?, Type> AddSheetErrors => new()
    {
        { null, null, typeof(ArgumentNullException) },
        { "", null, typeof(ArgumentException) },
        { new string('x', 32), null, typeof(ArgumentException) },
        { "a:b", null, typeof(ArgumentException) },
        { "a[b", null, typeof(ArgumentException) },
        { "'quoted", null, typeof(ArgumentException) },
        { "Sheet1", null, typeof(ArgumentException) },
        { "SHEET1", null, typeof(ArgumentException) },
        { "data", null, typeof(ArgumentException) }, // duplicates "Data" case-insensitively
        { "Fine", -1, typeof(ArgumentOutOfRangeException) },
        { "Fine", 3, typeof(ArgumentOutOfRangeException) },
    };

    [Theory]
    [MemberData(nameof(AddSheetErrors))]
    public void AddSheet_Errors_LeaveTheWorkbookUnchanged(string? name, int? index, Type exception)
    {
        var wb = Book("Data");
        wb.SetCell("A1", "=Fine!A1");
        wb.MarkSaved();
        var log = new EventLog(wb);
        var ex = Assert.ThrowsAny<Exception>(() => wb.AddSheet(name!, index));
        Assert.IsType(exception, ex);
        Assert.Equal(["Sheet1", "Data"], wb.SheetNames);
        Assert.Equal("Sheet1", wb.ActiveSheet);
        Assert.False(wb.IsDirty);
        Assert.False(wb.CanRedo);
        Assert.Empty(log.Events);
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
    }

    [Fact]
    public void AddSheet_OverTheLimit_ThrowsInvalidOperation_AndChangesNothing()
    {
        var wb = new Workbook();
        for (int i = 2; i <= 255; i++) wb.AddSheet("S" + i);
        Assert.Equal(255, wb.SheetNames.Count);
        wb.MarkSaved();
        Assert.Throws<InvalidOperationException>(() => wb.AddSheet("One more"));
        Assert.Equal(255, wb.SheetNames.Count);
        Assert.False(wb.IsDirty);
    }

    // ---- Qualified API addresses (R83) ----

    [Fact]
    public void QualifiedAddresses_SetAndGetOnTheNamedSheet()
    {
        var wb = Book("Data", "My Sheet", "O'Brien");
        wb.SetCell("Data!B2", "21");
        wb.SetCell("'My Sheet'!A1", "hello");
        wb.SetCell("'O''Brien'!$C$3", "3");
        wb.SetCell("'Data'!B3", "1"); // redundant quotes are accepted
        Assert.Equal(N(21), wb.GetValue("data!b2"));
        Assert.Equal("21", wb.GetInput("DATA!$B$2"));
        Assert.Equal(CellValue.FromText("hello"), wb.GetValue("'my sheet'!A1"));
        Assert.Equal(N(3), wb.GetValue("'O''Brien'!C3"));
        Assert.Equal(N(1), wb.GetValue("Data!B3"));
        Assert.Equal(CellValue.Blank, wb.GetValue("B2")); // Sheet1 is active
    }

    [Fact]
    public void UnqualifiedAddresses_ReferToTheActiveSheet()
    {
        var wb = Book("Data");
        wb.SetCell("A1", "1");
        wb.ActiveSheet = "Data";
        wb.SetCell("A1", "2");
        Assert.Equal(N(2), wb.GetValue("A1"));
        Assert.Equal(N(1), wb.GetValue("Sheet1!A1"));
        Assert.Equal(N(2), wb.GetValue("Data!A1"));
        wb.ActiveSheet = "Sheet1";
        Assert.Equal(N(1), wb.GetValue("A1"));
    }

    [Theory]
    [InlineData("Nope!A1")]          // unknown sheet
    [InlineData("My Sheet!A1")]      // needs quotes (R82)
    [InlineData("2024!A1")]
    [InlineData("A1!B2")]
    [InlineData("TRUE!A1")]
    [InlineData("'Data!A1")]         // unterminated quote
    [InlineData("'Data'A1")]         // no '!'
    [InlineData("Data !A1")]
    [InlineData("Data! A1")]
    [InlineData("Data!")]
    [InlineData("Data!A1:B2")]       // a range, not a cell
    [InlineData("Data!Data!A1")]
    [InlineData("''!A1")]            // empty name
    public void BadQualifiedAddresses_Throw_AndChangeNothing(string address)
    {
        var wb = Book("Data", "My Sheet");
        wb.SetCell("Data!A1", "1");
        wb.MarkSaved();
        var log = new EventLog(wb);
        Assert.ThrowsAny<ArgumentException>(() => wb.SetCell(address, "2"));
        Assert.ThrowsAny<ArgumentException>(() => wb.GetValue(address));
        Assert.ThrowsAny<ArgumentException>(() => wb.GetInput(address));
        Assert.ThrowsAny<ArgumentException>(() => wb.ApplyBatch([new(address, "2")]));
        Assert.Empty(log.Events);
        Assert.False(wb.IsDirty);
        Assert.Equal(N(1), wb.GetValue("Data!A1"));
    }

    [Fact]
    public void ApplyBatch_QualifiedAndUnqualifiedOfTheSameCell_AreDuplicates_LastWins()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("A1", "1"), new("Sheet1!A1", "2"), new("sheet1!$A$1", "3"), new("Data!A1", "4")]);
        Assert.Equal(N(3), wb.GetValue("A1"));
        Assert.Equal(N(4), wb.GetValue("Data!A1"));
        Assert.True(wb.Undo());
        Assert.Equal(CellValue.Blank, wb.GetValue("A1"));
        Assert.Equal(CellValue.Blank, wb.GetValue("Data!A1"));
    }

    [Fact]
    public void SetCell_SameCellQualifiedOrNot_IsANoOpWhenTheInputIsEqual()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "5");
        wb.MarkSaved();
        wb.SetCell("Sheet1!A1", "5");
        Assert.False(wb.IsDirty);
    }

    // ---- Evaluation across sheets (CR1.2, R82) ----

    [Fact]
    public void QualifiedReference_EvaluatesAgainstItsSheet()
    {
        var wb = Book("Data");
        wb.SetCell("Data!B2", "21");
        wb.SetCell("B2", "100");
        wb.SetCell("A1", "=Data!B2*2");
        Assert.Equal(N(42), wb.GetValue("A1"));
        wb.SetCell("Data!B2", "5");
        Assert.Equal(N(10), wb.GetValue("A1"));
    }

    [Fact]
    public void UnqualifiedReferences_ReferToTheFormulasOwnSheet()
    {
        var wb = Book("Data");
        wb.SetCell("A1", "1");
        wb.SetCell("Data!A1", "10");
        wb.SetCell("Data!A2", "=A1+1");         // Data's own A1
        wb.SetCell("A2", "=A1+Data!A2");        // 1 + 11
        Assert.Equal(N(11), wb.GetValue("Data!A2"));
        Assert.Equal(N(12), wb.GetValue("A2"));
        wb.ActiveSheet = "Data";                // the active sheet does not change what a formula means
        Assert.Equal(N(12), wb.GetValue("Sheet1!A2"));
    }

    [Fact]
    public void MissingSheet_IsRef_AndResolvesAfterAddSheet_WithCellsChanged()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=Data!B2*2");
        wb.SetCell("A2", "=SUM(Data!A1:A3)");
        wb.SetCell("A3", "=A1+1");
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A2"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A3"));

        var log = new EventLog(wb);
        wb.AddSheet("data"); // case-insensitive match (R82)
        Assert.Equal(N(0), wb.GetValue("A1"));
        Assert.Equal(N(0), wb.GetValue("A2"));
        Assert.Equal(N(1), wb.GetValue("A3"));
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal([["Sheet1!A1", "Sheet1!A2", "Sheet1!A3"]], log.Events);

        wb.SetCell("Data!B2", "21");
        wb.SetCell("Data!A2", "4");
        Assert.Equal(N(42), wb.GetValue("A1"));
        Assert.Equal(N(4), wb.GetValue("A2"));
    }

    [Fact]
    public void MissingSheet_InFunctionArguments_IsRef()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=COUNT(Nope!A1:A3)");
        wb.SetCell("A3", "=IFERROR(Nope!A1,7)");
        wb.SetCell("A4", "=Nope!A1:A3");
        wb.SetCell("A5", "=SUM(Nope!A1:A3,1)");
        Assert.Equal(N(0), wb.GetValue("A1")); // COUNT does not count a direct error argument
        Assert.Equal(N(7), wb.GetValue("A3"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A4"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A5"));
    }

    [Fact]
    public void QualifiedRangeArguments_ReadTheirSheet()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Data!A1", "1"), new("Data!A2", "2"), new("Data!A3", "3"), new("A1", "100")]);
        wb.SetCell("B1", "=SUM(Data!A1:A3)");
        wb.SetCell("B2", "=SUMIF(Data!A1:A3,\">1\",Data!A1)"); // R68 rectangle on Data
        wb.SetCell("B3", "=VLOOKUP(2,Data!A1:A3,1,FALSE)");
        Assert.Equal(N(6), wb.GetValue("B1"));
        Assert.Equal(N(5), wb.GetValue("B2"));
        Assert.Equal(N(2), wb.GetValue("B3"));
        wb.SetCell("Data!A3", "30");
        Assert.Equal(N(33), wb.GetValue("B1"));
        Assert.Equal(N(32), wb.GetValue("B2"));
    }

    [Fact]
    public void CrossSheetCycle_IsCircOnBothCells_AndBreakingItRestoresValues()
    {
        var wb = Book("Data");
        wb.SetCell("A1", "=Data!A1+1");
        wb.SetCell("Data!A1", "=Sheet1!A1+1");
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("A1"));
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("Data!A1"));
        wb.SetCell("Data!A1", "5");
        Assert.Equal(N(6), wb.GetValue("A1"));
        Assert.Equal(N(5), wb.GetValue("Data!A1"));
    }

    [Fact]
    public void CycleThroughAMissingSheetForms_WhenTheSheetIsAdded()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=Data!A1");
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        wb.AddSheet("Data");
        wb.SetCell("Data!A1", "=Sheet1!A1");
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("A1"));
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("Data!A1"));
    }

    [Fact]
    public void IncrementalRecalc_CountsOnlyTransitiveDependents_AcrossSheets()
    {
        var wb = Book("Data", "Other");
        wb.ApplyBatch([
            new("A1", "1"),
            new("Data!A1", "=Sheet1!A1+1"),
            new("Data!A2", "=A1*2"),                 // Data!A1
            new("B1", "=Data!A2+1"),
            new("Other!A1", "=Data!A2+Sheet1!B1"),
            new("C1", "=1+1"),                        // unrelated
            new("Data!C1", "=Sheet1!C1"),             // depends on C1 only
            new("Other!B1", "=Other!A5"),             // unrelated
        ]);
        wb.SetCell("A1", "2");
        Assert.Equal(4, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(N(13), wb.GetValue("Other!A1")); // Data!A2 = 6, B1 = 7
        wb.SetCell("Other!A5", "9");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("Data!Z9", "1");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void LongChainAcrossTwoSheets_EvaluatesWithoutStackOverflow()
    {
        const int n = 50_000; // 100,000 cells: n on each sheet
        var wb = Book("Data");
        var edits = new List<KeyValuePair<string, string>>(2 * n) { new("A1", "1") };
        for (int r = 1; r <= n; r++)
        {
            edits.Add(new($"Data!A{r}", $"=Sheet1!A{r}+1"));
            if (r < n) edits.Add(new($"A{r + 1}", $"=Data!A{r}+1"));
        }
        wb.ApplyBatch(edits);
        Assert.Equal(N(2 * n), wb.GetValue($"Data!A{n}"));
        wb.SetCell("A1", "11");
        Assert.Equal(N(2 * n + 10), wb.GetValue($"Data!A{n}"));
        Assert.Equal(2 * n - 1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    // ---- GetUsedAddresses and CellsChanged (CR1.3, R81) ----

    [Fact]
    public void GetUsedAddresses_OneSheet_IsUnqualified()
    {
        var wb = new Workbook();
        wb.ApplyBatch([new("B2", "1"), new("A1", "2"), new("A2", "x")]);
        Assert.Equal(["A1", "A2", "B2"], wb.GetUsedAddresses());
    }

    [Fact]
    public void GetUsedAddresses_ThreeSheets_IsQualified_InSheetOrderThenRowMajor()
    {
        var wb = new Workbook();
        wb.AddSheet("Zeta");
        wb.AddSheet("My Sheet", 0);
        wb.AddSheet("Alpha", 1);
        // Order: My Sheet, Alpha, Sheet1, Zeta
        wb.ApplyBatch([
            new("Zeta!A1", "1"), new("B1", "1"), new("A2", "1"),
            new("'My Sheet'!C3", "1"), new("'My Sheet'!A3", "1"), new("Alpha!B1", "=1"),
        ]);
        Assert.Equal(["'My Sheet'!A3", "'My Sheet'!C3", "Alpha!B1", "Sheet1!B1", "Sheet1!A2", "Zeta!A1"], wb.GetUsedAddresses());
    }

    [Fact]
    public void GetUsedAddresses_EmptySheets_AreSkipped_ButStillQualify()
    {
        var wb = Book("Data");
        wb.SetCell("A1", "1");
        Assert.Equal(["Sheet1!A1"], wb.GetUsedAddresses());
    }

    [Fact]
    public void CellsChanged_IsUnqualifiedWithOneSheet()
    {
        var wb = new Workbook();
        var log = new EventLog(wb);
        wb.SetCell("Sheet1!B1", "=A1");
        wb.SetCell("A1", "1");
        Assert.Equal([["B1"], ["A1", "B1"]], log.Events);
    }

    [Fact]
    public void CellsChanged_IsQualified_InSheetOrderThenRowMajor_WithSeveralSheets()
    {
        var wb = new Workbook();
        wb.AddSheet("Data", 0); // order: Data, Sheet1
        wb.SetCell("B5", "=Data!A1");
        wb.SetCell("A9", "=Data!A1*2");
        wb.SetCell("Data!C1", "=A1");
        var log = new EventLog(wb);
        wb.SetCell("Data!A1", "1");
        Assert.Equal([["Data!A1", "Data!C1", "Sheet1!B5", "Sheet1!A9"]], log.Events); // Data!C1 is =A1 on Data
        wb.SetCell("Sheet1!A1", "3");
        Assert.Equal(["Sheet1!A1"], log.Events[1]);
    }

    [Fact]
    public void CellsChanged_QuotesSheetsThatNeedIt()
    {
        var wb = Book("My Sheet");
        var log = new EventLog(wb);
        wb.SetCell("'my sheet'!A1", "1");
        Assert.Equal([["'My Sheet'!A1"]], log.Events);
    }

    // ---- AddSheet undo and redo (§9.3, R80, R81) ----

    [Fact]
    public void AddSheet_Undo_RemovesTheSheet_AndRedoReaddsItAtTheSameIndex()
    {
        var wb = Book("A", "B");
        wb.SetCell("A1", "=New!A1+1");
        wb.AddSheet("New", 1);
        Assert.Equal(N(1), wb.GetValue("A1"));

        var log = new EventLog(wb);
        Assert.True(wb.Undo());
        Assert.Equal(["Sheet1", "A", "B"], wb.SheetNames);
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        Assert.Equal(["Sheet1!A1"], log.Events.Single());
        Assert.Throws<ArgumentException>(() => wb.GetValue("New!A1"));

        Assert.True(wb.Redo());
        Assert.Equal(["Sheet1", "New", "A", "B"], wb.SheetNames);
        Assert.Equal(N(1), wb.GetValue("A1"));
        Assert.Equal(2, log.Events.Count);
        wb.SetCell("New!A1", "5");
        Assert.Equal(N(6), wb.GetValue("A1"));
    }

    [Fact]
    public void AddSheet_UndoToOneSheet_ReportsUnqualifiedAddresses()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=Data!A1");
        wb.AddSheet("Data");
        var log = new EventLog(wb);
        wb.Undo();
        Assert.Equal([["A1"]], log.Events);
    }

    [Fact]
    public void Undo_OfAnActiveAddedSheet_ActivatesTheSheetAtItsIndex_OrTheLast()
    {
        var wb = Book("A", "B");
        wb.AddSheet("Mid", 1);
        wb.ActiveSheet = "Mid";
        wb.Undo();
        Assert.Equal("A", wb.ActiveSheet); // now at index 1
        wb.Redo();
        Assert.Equal("A", wb.ActiveSheet); // redo does not change the active sheet (R80)

        wb.AddSheet("Last");
        wb.ActiveSheet = "last";
        wb.Undo();
        Assert.Equal("B", wb.ActiveSheet); // index past the end: the last sheet
    }

    [Fact]
    public void Undo_OfAnInactiveAddedSheet_KeepsTheActiveSheet()
    {
        var wb = Book("A");
        wb.ActiveSheet = "A";
        wb.AddSheet("B", 0);
        wb.Undo();
        Assert.Equal("A", wb.ActiveSheet);
    }

    [Fact]
    public void UndoRedo_OfCellsOnAnAddedSheet_ThenOfTheSheet()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=Data!A1*2");
        wb.AddSheet("Data");
        wb.SetCell("Data!A1", "4");
        Assert.Equal(N(8), wb.GetValue("A1"));
        wb.Undo(); // the cell
        Assert.Equal(N(0), wb.GetValue("A1"));
        wb.Undo(); // the sheet
        Assert.Equal(["Sheet1"], wb.SheetNames);
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        wb.Redo();
        wb.Redo();
        Assert.Equal(N(4), wb.GetValue("Data!A1"));
        Assert.Equal(N(8), wb.GetValue("A1"));
        Assert.False(wb.CanRedo);
    }

    [Fact]
    public void AddSheet_ThatThrowsDuringRecalc_IsAtomic()
    {
        var functions = new ArmableResolver();
        var wb = new Workbook(functions);
        wb.SetCell("A1", "=BOOM(Data!A1)");
        wb.MarkSaved();
        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.AddSheet("Data"));
        Assert.Equal(["Sheet1"], wb.SheetNames);
        Assert.False(wb.IsDirty);
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        functions.Armed = false;
        wb.AddSheet("Data"); // the failed call allocated nothing that blocks a retry
        Assert.Equal(N(0), wb.GetValue("A1"));
    }

    [Fact]
    public void UndoOfAddSheet_ThatThrowsDuringRecalc_IsAtomic()
    {
        var functions = new ArmableResolver();
        var wb = new Workbook(functions);
        wb.SetCell("A1", "=BOOM(Data!A1)");
        wb.AddSheet("Data");
        wb.ActiveSheet = "Data";
        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.Undo());
        Assert.Equal(["Sheet1", "Data"], wb.SheetNames);
        Assert.Equal("Data", wb.ActiveSheet);
        Assert.Equal(N(0), wb.GetValue("Sheet1!A1"));
        functions.Armed = false;
        Assert.True(wb.Undo());
        Assert.Equal("Sheet1", wb.ActiveSheet);
    }

    // ---- Names qualified with the active sheet (CR1.4, R77) ----

    [Fact]
    public void Name_IsBoundToTheSheetActiveWhenDefined()
    {
        var wb = Book("Data");
        wb.SetCell("Data!B1", "7");
        wb.SetCell("B1", "1");
        wb.ActiveSheet = "Data";
        wb.DefineName("Rate", "$B$1");
        wb.ActiveSheet = "Sheet1";
        wb.SetCell("A1", "=Rate*2");
        wb.SetCell("A2", "=SUM(Rate)");
        Assert.Equal(N(14), wb.GetValue("A1"));
        Assert.Equal(N(7), wb.GetValue("A2"));
        Assert.Equal([new KeyValuePair<string, string>("Rate", "Data!$B$1")], wb.GetNames());
        wb.SetCell("Data!B1", "8");
        Assert.Equal(N(16), wb.GetValue("A1"));
        wb.SetCell("B1", "100"); // Sheet1!B1 is not the name's target
        Assert.Equal(N(16), wb.GetValue("A1"));
    }

    [Fact]
    public void Name_RedefinedWithTheSameTextOnAnotherSheet_IsAStep()
    {
        var wb = Book("Data");
        wb.SetCell("B1", "1");
        wb.SetCell("Data!B1", "2");
        wb.DefineName("Rate", "$B$1");
        wb.SetCell("A1", "=Rate");
        wb.ActiveSheet = "Data";
        wb.MarkSaved();
        wb.DefineName("Rate", "$B$1");
        Assert.True(wb.IsDirty);
        Assert.Equal(N(2), wb.GetValue("Sheet1!A1"));
        wb.Undo();
        Assert.Equal(N(1), wb.GetValue("Sheet1!A1"));
    }

    // ---- Structure edits act on the active sheet (ruling P5-2, R84) ----

    [Fact]
    public void InsertRows_ActsOnTheActiveSheet_AndAdjustsQualifiedReferencesToIt()
    {
        var wb = Book("Data");
        wb.ApplyBatch([
            new("A1", "1"), new("A2", "=A1+Data!A1"),          // Sheet1: unqualified refs stay, qualified shift
            new("B1", "=SUM(data!A1:A2)"),
            new("Data!A1", "10"), new("Data!A2", "=A1+Sheet1!A1"), // Data: unqualified shift, Sheet1 ref stays
        ]);
        wb.ActiveSheet = "Data";
        wb.InsertRows(1, 1);
        Assert.Equal("1", wb.GetInput("Sheet1!A1"));
        Assert.Equal("=A1+Data!A2", wb.GetInput("Sheet1!A2"));
        Assert.Equal("=SUM(Data!A2:A3)", wb.GetInput("Sheet1!B1"));
        Assert.Equal("", wb.GetInput("Data!A1"));
        Assert.Equal("10", wb.GetInput("Data!A2"));
        Assert.Equal("=A2+Sheet1!A1", wb.GetInput("Data!A3"));
        Assert.Equal(N(11), wb.GetValue("Sheet1!A2"));
        Assert.Equal(N(11), wb.GetValue("Data!A3"));

        wb.Undo();
        Assert.Equal("=A1+Data!A1", wb.GetInput("Sheet1!A2"));
        Assert.Equal("=SUM(data!A1:A2)", wb.GetInput("Sheet1!B1")); // verbatim prior text
        Assert.Equal("=A1+Sheet1!A1", wb.GetInput("Data!A2"));
        wb.Redo();
        Assert.Equal("=SUM(Data!A2:A3)", wb.GetInput("Sheet1!B1"));
    }

    [Fact]
    public void DeleteRows_OnTheActiveSheet_LeavesOtherSheetsCellsAndUnqualifiedReferences()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("A1", "1"), new("A2", "2"), new("A3", "=A2+Data!A3"), new("Data!A3", "5"), new("Data!A1", "=Sheet1!A2")]);
        wb.ActiveSheet = "Data";
        var log = new EventLog(wb);
        wb.DeleteRows(1, 1);
        Assert.Equal("=A2+Data!A2", wb.GetInput("Sheet1!A3"));
        Assert.Equal("2", wb.GetInput("Sheet1!A2"));
        Assert.Equal("", wb.GetInput("Data!A1"));
        Assert.Equal("5", wb.GetInput("Data!A2"));
        Assert.Equal(N(7), wb.GetValue("Sheet1!A3"));
        Assert.Equal(["Sheet1!A3", "Data!A1", "Data!A2", "Data!A3"], log.Events.Single()); // sheet order: Sheet1, Data
    }

    [Fact]
    public void DeleteRows_IntoAQualifiedReference_GivesRef()
    {
        var wb = Book("Data");
        wb.SetCell("A1", "=Data!A1*2");
        wb.ActiveSheet = "data";
        wb.DeleteRows(1, 1);
        Assert.Equal("=#REF!*2", wb.GetInput("Sheet1!A1"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("Sheet1!A1"));
    }

    [Fact]
    public void InsertColumns_DoesNotMoveNamesBoundToAnotherSheet()
    {
        var wb = Book("Data");
        wb.DefineName("OnSheet1", "$B$1");
        wb.ActiveSheet = "Data";
        wb.DefineName("OnData", "$B$1");
        wb.InsertColumns(1, 1);
        Assert.Equal(
            [new KeyValuePair<string, string>("OnData", "Data!$C$1"), new("OnSheet1", "Sheet1!$B$1")],
            wb.GetNames());
    }

    // ---- CopyPaste on one sheet (§9.4, R83) ----

    [Fact]
    public void CopyPaste_OnAnotherSheet_WithQualifiedAddresses()
    {
        var wb = Book("Data");
        wb.SetCell("Data!A1", "=B1+sheet1!B1");
        wb.SetCell("Data!B2", "3");
        wb.SetCell("B2", "4");
        wb.CopyPaste("Data!A1", "data!A2");
        Assert.Equal("=B2+Sheet1!B2", wb.GetInput("Data!A2")); // relative parts shift; sheet in defined casing (R82)
        Assert.Equal(N(7), wb.GetValue("Data!A2"));
        Assert.Equal("", wb.GetInput("A2"));
    }

    [Fact]
    public void CopyPaste_UnqualifiedMeansTheActiveSheet()
    {
        var wb = Book("Data");
        wb.ActiveSheet = "Data";
        wb.SetCell("A1", "x");
        wb.CopyPaste("A1", "Data!B1");
        Assert.Equal("x", wb.GetInput("Data!B1"));
        Assert.Equal("", wb.GetInput("Sheet1!B1"));
    }

    [Fact]
    public void CopyPaste_AcrossSheets_IsNotSupportedYet_AndChangesNothing()
    {
        var wb = Book("Data");
        wb.SetCell("A1", "1");
        wb.MarkSaved();
        Assert.Throws<NotSupportedException>(() => wb.CopyPaste("A1", "Data!A1"));
        Assert.ThrowsAny<ArgumentException>(() => wb.CopyPaste("Nope!A1", "A2"));
        Assert.ThrowsAny<ArgumentException>(() => wb.CopyPaste("A1", "Nope!A2"));
        Assert.False(wb.IsDirty);
        Assert.Equal("", wb.GetInput("Data!A1"));
    }

    // ---- Multi-sheet loader entry (CR1.5, for the v2 loader) ----

    private static KeyValuePair<string, string> C(string a, string i) => new(a, i);

    [Fact]
    public void FromSheets_BuildsSheetsInOrder_WithTheActiveSheet_Clean()
    {
        var wb = Workbook.FromSheets(
            [new LoadedSheet("Data", [C("A1", "2")]), new LoadedSheet("My Sheet", [C("B1", "=Data!A1*3"), C("B2", "=B1+1")])],
            "my sheet", [C("Rate", "$A$1")]);
        Assert.Equal(["Data", "My Sheet"], wb.SheetNames);
        Assert.Equal("My Sheet", wb.ActiveSheet);
        Assert.Equal(N(6), wb.GetValue("B1"));
        Assert.Equal(N(7), wb.GetValue("'My Sheet'!B2"));
        Assert.False(wb.CanUndo);
        Assert.False(wb.IsDirty);
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("Data!C1", "=Rate"); // an unqualified loaded name is qualified with the first sheet (CR1.5, R77)
        Assert.Equal(N(2), wb.GetValue("Data!C1"));
    }

    [Theory]
    [InlineData("no sheets")]
    [InlineData("duplicate")]
    [InlineData("invalid name")]
    [InlineData("unknown active")]
    [InlineData("qualified cell")]
    [InlineData("duplicate cell")]
    public void FromSheets_InvalidInput_ThrowsArgumentException(string error)
    {
        var (sheets, active) = error switch
        {
            "no sheets" => (Array.Empty<LoadedSheet>(), "Sheet1"),
            "duplicate" => ([new("Data", []), new("DATA", [])], "Data"),
            "invalid name" => ([new("a:b", [])], "a:b"),
            "unknown active" => ([new("Data", [])], "Other"),
            "qualified cell" => ([new("Data", [C("Data!A1", "1")])], "Data"),
            _ => (new LoadedSheet[] { new("Data", [C("A1", "1"), C("$a$1", "2")]) }, "Data"),
        };
        Assert.ThrowsAny<ArgumentException>(() => Workbook.FromSheets(sheets, active, []));
    }

    [Fact]
    public void FromSheets_The256thSheet_IsRejected()
    {
        var sheets = Enumerable.Range(1, 256).Select(i => new LoadedSheet("S" + i, [])).ToArray();
        Assert.ThrowsAny<ArgumentException>(() => Workbook.FromSheets(sheets, "S1", []));
        Assert.Equal(255, Workbook.FromSheets(sheets[..255], "S1", []).SheetNames.Count);
    }
}
