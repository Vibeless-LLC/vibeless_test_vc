using System.Diagnostics.CodeAnalysis;
using System.Text;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;

namespace Gridline.Core.Tests;

// Requirement IDs: CR1.3 (per-sheet InsertRows/DeleteRows/InsertColumns/DeleteColumns overloads; CopyPaste across
// sheets), R84 (adjust qualified references to the edited sheet from every sheet, unqualified references on it, names
// into it), R61-R64 per edit across all sheets, R78 (null/unknown sheet), R39 (#PARSE! verbatim), §5.5 (re-emission),
// §9.2 (atomic, CellsChanged), §9.3 (one undo step, verbatim undo), §9.4 (paste shifting), D-35 (paste work), D-43
// (MaxInputLength on a structure rewrite), D-55.
public class WorkbookSheetStructureTests
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

    private static Workbook Book(params string[] more)
    {
        var wb = new Workbook();
        foreach (var name in more) wb.AddSheet(name);
        return wb;
    }

    /// <summary>Everything observable except the history: sheet list, active sheet, every input and value, the names.</summary>
    private static string Dump(Workbook wb)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join("|", wb.SheetNames)).Append(" active=").Append(wb.ActiveSheet).Append('\n');
        foreach (var a in wb.GetUsedAddresses()) sb.Append(a).Append(" = ").Append(wb.GetInput(a)).Append(" -> ").Append(wb.GetValue(a)).Append('\n');
        foreach (var (n, r) in wb.GetNames()) sb.Append(n).Append(" := ").Append(r).Append('\n');
        return sb.ToString();
    }

    private static void Edit(Workbook wb, string op, string sheet, int start, int count)
    {
        switch (op)
        {
            case "InsertRows": wb.InsertRows(sheet, start, count); break;
            case "DeleteRows": wb.DeleteRows(sheet, start, count); break;
            case "InsertColumns": wb.InsertColumns(sheet, start, count); break;
            case "DeleteColumns": wb.DeleteColumns(sheet, start, count); break;
            default: throw new ArgumentOutOfRangeException(nameof(op));
        }
    }

    private static void EditActive(Workbook wb, string op, int start, int count)
    {
        switch (op)
        {
            case "InsertRows": wb.InsertRows(start, count); break;
            case "DeleteRows": wb.DeleteRows(start, count); break;
            case "InsertColumns": wb.InsertColumns(start, count); break;
            case "DeleteColumns": wb.DeleteColumns(start, count); break;
            default: throw new ArgumentOutOfRangeException(nameof(op));
        }
    }

    public static TheoryData<string> Ops => new() { "InsertRows", "DeleteRows", "InsertColumns", "DeleteColumns" };

    // ---- The brief's example (CR1.3, R84) ----

    [Fact]
    public void InsertRowsOnData_AdjustsQualifiedRefsFromSheet1_UnqualifiedOnData_NotUnqualifiedOnSheet1()
    {
        var wb = Book("Data");
        wb.ApplyBatch([
            new("Sheet1!A1", "=Data!B2"), new("Sheet1!A2", "=B2"), new("Sheet1!B2", "7"),
            new("Data!A1", "=B2"), new("Data!B2", "5"),
        ]);
        Assert.Equal("Sheet1", wb.ActiveSheet);
        var log = new EventLog(wb);

        wb.InsertRows("Data", 2, 1);

        Assert.Equal("=Data!B3", wb.GetInput("Sheet1!A1"));
        Assert.Equal("=B2", wb.GetInput("Sheet1!A2")); // unqualified on another sheet: unaffected
        Assert.Equal("7", wb.GetInput("Sheet1!B2"));   // other sheets' cells do not move
        Assert.Equal("=B3", wb.GetInput("Data!A1"));
        Assert.Equal("5", wb.GetInput("Data!B3"));
        Assert.Equal("", wb.GetInput("Data!B2"));
        Assert.Equal(N(5), wb.GetValue("Sheet1!A1"));
        Assert.Equal(N(7), wb.GetValue("Sheet1!A2"));
        Assert.Equal(N(5), wb.GetValue("Data!A1"));
        Assert.Equal("Sheet1", wb.ActiveSheet); // the overload does not activate the edited sheet
        Assert.Equal(["Sheet1!A1", "Data!A1", "Data!B2", "Data!B3"], log.Events.Single()); // R64 per R81, sheet order
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount); // R63: every formula on every sheet is re-evaluated
        WorkbookSheetChangesTests.AssertInstalledTreesMatchInputs(wb);
    }

    [Fact]
    public void AFormulaOnTheEditedSheet_AdjustsItsUnqualifiedAndSelfQualifiedRefs_NotOtherSheets()
    {
        var wb = Book("Data", "Other");
        wb.ApplyBatch([
            new("Data!A1", "=B2+Sheet1!B2+data!B2+Other!B2+Gone!B2"),
            new("Data!B2", "1"), new("Sheet1!B2", "10"), new("Other!B2", "100"),
        ]);
        wb.InsertRows("DATA", 1, 1);
        Assert.Equal("=B3+Sheet1!B2+Data!B3+Other!B2+Gone!B2", wb.GetInput("Data!A2"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("Data!A2")); // Gone! is a missing sheet: untouched, still #REF!
        wb.Undo();
        Assert.Equal("=B2+Sheet1!B2+data!B2+Other!B2+Gone!B2", wb.GetInput("Data!A1"));
    }

    // ---- All four operations with qualified ranges spanning the edit, and #REF! ----

    [Theory]
    [InlineData("InsertRows", 2, 1, "=SUM(Data!A1:A3)", "=SUM(Data!A1:A4)", 6)]
    [InlineData("DeleteRows", 2, 1, "=SUM(Data!A1:A3)", "=SUM(Data!A1:A2)", 4)]
    [InlineData("InsertColumns", 2, 1, "=SUM(Data!A1:C1)", "=SUM(Data!A1:D1)", 60)]
    [InlineData("DeleteColumns", 2, 1, "=SUM(Data!A1:C1)", "=SUM(Data!A1:B1)", 40)]
    [InlineData("DeleteRows", 1, 3, "=SUM(Data!A1:A3)", "=SUM(#REF!)", double.NaN)]
    [InlineData("DeleteColumns", 1, 3, "=SUM(Data!A1:C1)", "=SUM(#REF!)", double.NaN)]
    [InlineData("InsertRows", 1, 99_997, "=SUM(Data!A1:A3)", "=SUM(Data!A99998:A100000)", 6)]
    [InlineData("InsertRows", 4, 99_997, "=SUM(Data!A1:A5)", "=SUM(Data!A1:A100000)", 6)] // R61: the end alone is clamped
    public void QualifiedRangeFromAnotherSheet_FollowsTheEdit(string op, int start, int count, string before, string after, double value)
    {
        var wb = Book("Data");
        wb.ApplyBatch([
            new("Data!A1", "1"), new("Data!A2", "2"), new("Data!A3", "3"),
            new("Data!B1", "20"), new("Data!C1", "30"),
            new("Sheet1!D9", before),
        ]);
        wb.SetCell("Data!A1", op.EndsWith("Columns", StringComparison.Ordinal) ? "10" : "1");
        string text = Dump(wb);
        Edit(wb, op, "Data", start, count);
        Assert.Equal(after, wb.GetInput("Sheet1!D9"));
        Assert.Equal(double.IsNaN(value) ? E(ErrorCodes.Ref) : N(value), wb.GetValue("Sheet1!D9"));
        WorkbookSheetChangesTests.AssertInstalledTreesMatchInputs(wb);
        wb.Undo();
        Assert.Equal(text, Dump(wb));
        Assert.Equal(before, wb.GetInput("Sheet1!D9"));
    }

    [Theory]
    [InlineData("InsertRows", 2, "=Data!B2*2", "=Data!B3*2", 10.0)]
    [InlineData("DeleteRows", 2, "=Data!B2*2", "=#REF!*2", double.NaN)]
    [InlineData("DeleteRows", 1, "=Data!B2*2", "=Data!B1*2", 10.0)]
    [InlineData("InsertColumns", 2, "=Data!B2*2", "=Data!C2*2", 10.0)]
    [InlineData("DeleteColumns", 2, "=Data!B2*2", "=#REF!*2", double.NaN)]
    [InlineData("DeleteColumns", 1, "=Data!$B$2*2", "=Data!$A$2*2", 10.0)]
    [InlineData("InsertRows", 1, "=Data!B100000", "=#REF!", double.NaN)] // R61: pushed past the bound
    public void QualifiedCellFromAnotherSheet_FollowsTheEdit_OrBecomesRef(string op, int start, string before, string after, double value)
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Data!B2", "5"), new("Sheet1!A1", before)]);
        Edit(wb, op, "Data", start, 1);
        Assert.Equal(after, wb.GetInput("Sheet1!A1"));
        Assert.Equal(double.IsNaN(value) ? E(ErrorCodes.Ref) : N(value), wb.GetValue("Sheet1!A1"));
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public void EditOnOneSheet_LeavesUnqualifiedRefsAndCellsOfOtherSheetsAlone(string op)
    {
        var wb = Book("Data");
        wb.ApplyBatch([
            new("Sheet1!C3", "=B2+SUM(A1:C1)"), new("Sheet1!B2", "4"),
            new("Data!C3", "=B2+SUM(A1:C1)"), new("Data!B2", "4"),
        ]);
        Edit(wb, op, "Data", 2, 1);
        Assert.Equal("=B2+SUM(A1:C1)", wb.GetInput("Sheet1!C3"));
        Assert.Equal("4", wb.GetInput("Sheet1!B2"));
        Assert.Equal(N(4), wb.GetValue("Sheet1!C3"));
        WorkbookSheetChangesTests.AssertInstalledTreesMatchInputs(wb);
        Assert.NotEqual("4", wb.GetInput("Data!B2")); // Data's own cells did move
    }

    [Fact]
    public void ParseErrorsAreKeptVerbatim_OnEverySheet()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Sheet1!A1", "=Data!B2+"), new("Data!A1", "=B2+"), new("Data!A5", "=B2 +")]);
        wb.DeleteRows("Data", 1, 1);
        Assert.Equal("=Data!B2+", wb.GetInput("Sheet1!A1"));
        Assert.Equal("=B2 +", wb.GetInput("Data!A4"));
        Assert.Equal("", wb.GetInput("Data!A1"));
    }

    // ---- Names (R77, R84) ----

    [Theory]
    [InlineData("InsertRows", 2, 1, "Data!$B$3", "Data!$A$1:$A$4")]
    [InlineData("DeleteRows", 2, 1, "#REF!", "Data!$A$1:$A$2")]
    [InlineData("InsertColumns", 2, 1, "Data!$C$2", "Data!$A$1:$A$3")]
    [InlineData("DeleteColumns", 1, 1, "Data!$A$2", "#REF!")]
    public void NamesIntoTheEditedSheet_AreAdjusted_OthersUntouched(string op, int start, int count, string cell, string range)
    {
        var wb = Book("Data");
        wb.DefineName("Cell", "Data!$B$2");
        wb.DefineName("Col", "Data!$A$1:$A$3");
        wb.DefineName("Home", "$B$2");          // Sheet1!$B$2: another sheet, untouched
        wb.ApplyBatch([new("Data!B2", "5"), new("Sheet1!B2", "1"), new("Sheet1!D1", "=Home+1")]);
        var names = wb.GetNames();

        Edit(wb, op, "Data", start, count);

        Assert.Equal(
            [new KeyValuePair<string, string>("Cell", cell), new("Col", range), new("Home", "Sheet1!$B$2")],
            wb.GetNames());
        Assert.Equal(N(2), wb.GetValue("Sheet1!D1"));
        wb.Undo();
        Assert.Equal(names, wb.GetNames());
        wb.Redo();
        Assert.Equal(cell, wb.GetNames()[0].Value);
    }

    [Fact]
    public void ANameIntoTheEditedSheet_IsRecalculatedThroughItsDependents()
    {
        var wb = Book("Data");
        wb.DefineName("Rate", "Data!$A$2");
        wb.ApplyBatch([new("Data!A2", "3"), new("Data!A3", "4"), new("Sheet1!A1", "=Rate*2")]);
        wb.DeleteRows("Data", 2, 1);
        Assert.Equal("#REF!", wb.GetNames().Single().Value);
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("Sheet1!A1"));
        Assert.Equal("=Rate*2", wb.GetInput("Sheet1!A1"));
        wb.Undo();
        Assert.Equal(N(6), wb.GetValue("Sheet1!A1"));
    }

    // ---- Unqualified overloads act on the active sheet; errors (R78) ----

    [Theory]
    [MemberData(nameof(Ops))]
    public void UnqualifiedOverload_EqualsTheSheetOverloadOnTheActiveSheet(string op)
    {
        static Workbook Make()
        {
            var wb = Book("Data", "Other");
            wb.ApplyBatch([
                new("Sheet1!A1", "=Data!B2+B2"), new("Sheet1!B2", "1"),
                new("Data!A1", "=B2+Sheet1!B2"), new("Data!B2", "2"), new("Data!C3", "3"),
                new("Other!A1", "=SUM(Data!A1:C3)+data!C3"),
            ]);
            wb.DefineName("Total", "Data!$C$3");
            wb.ActiveSheet = "Data";
            return wb;
        }
        var viaActive = Make();
        var viaName = Make();
        EditActive(viaActive, op, 2, 1);
        Edit(viaName, op, "data", 2, 1);
        Assert.Equal(Dump(viaName), Dump(viaActive));
        Assert.Equal("Data", viaName.ActiveSheet);
        Assert.NotEqual(Dump(Make()), Dump(viaName));
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public void UnknownOrNullSheet_Throws_AndChangesNothing(string op)
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Data!A1", "1"), new("Sheet1!A1", "=Data!A1")]);
        wb.MarkSaved();
        string before = Dump(wb);
        var log = new EventLog(wb);
        var unknown = Assert.Throws<ArgumentException>(() => Edit(wb, op, "Nope", 1, 1));
        Assert.Equal("sheet", unknown.ParamName);
        Assert.Throws<ArgumentException>(() => Edit(wb, op, "Data!", 1, 1));
        Assert.Throws<ArgumentException>(() => Edit(wb, op, "", 1, 1));
        Assert.Throws<ArgumentNullException>(() => Edit(wb, op, null!, 1, 1));
        // R62 bounds still apply with the sheet given
        Assert.Throws<ArgumentOutOfRangeException>(() => Edit(wb, op, "Data", 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Edit(wb, op, "Data", 1, 0));
        Assert.Equal(before, Dump(wb));
        Assert.Empty(log.Events);
        Assert.False(wb.IsDirty);
        Assert.True(wb.Undo()); // the last step is still the ApplyBatch
        Assert.Equal("", wb.GetInput("Sheet1!A1"));
    }

    [Fact]
    public void UnknownSheet_IsCheckedBeforeTheBounds()
    {
        var wb = Book("Data");
        Assert.Throws<ArgumentException>(() => wb.InsertRows("Nope", 0, 0));
        Assert.Throws<ArgumentNullException>(() => wb.DeleteColumns(null!, 0, 0));
    }

    [Fact]
    public void InsertPushingInputPastTheBound_OnTheNamedSheet_Throws_AndChangesNothing()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Data!A100000", "1"), new("Sheet1!A100000", "1")]);
        string before = Dump(wb);
        Assert.Throws<InvalidOperationException>(() => wb.InsertRows("Data", 1, 1));
        Assert.Equal(before, Dump(wb));
        wb.Undo(); // the last step is still the ApplyBatch
        Assert.Equal("", wb.GetInput("Data!A100000"));
    }

    [Fact]
    public void NoOpOnTheNamedSheet_MakesNoStep()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Sheet1!A5", "=Data!A1"), new("Data!A1", "1")]);
        wb.MarkSaved();
        var log = new EventLog(wb);
        wb.InsertRows("Data", 3, 1); // nothing on Data moves; Sheet1!A5 names Data!A1, above the edit (R63)
        Assert.Empty(log.Events);
        Assert.False(wb.IsDirty);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.Undo();
        Assert.Equal("", wb.GetInput("Data!A1")); // the ApplyBatch step was the last one
    }

    // ---- Undo / redo verbatim (§9.3) ----

    [Theory]
    [MemberData(nameof(Ops))]
    public void UndoRedo_RestoreVerbatimTextOnEverySheet_AndTheNames(string op)
    {
        var wb = Book("Data", "My Sheet");
        wb.ApplyBatch([
            new("Sheet1!A1", "=  data!b2 *2"), new("Sheet1!A2", "=sum( DATA!A1:c3 )"), new("Sheet1!A3", "= b2"),
            new("Data!A1", "=  b2+'my sheet'!b2 + sheet1!B2"), new("Data!B2", "5"), new("Data!C3", "7"),
            new("'My Sheet'!A1", "=Data!$B$2 + Data!B$2"), new("'My Sheet'!B2", "1"),
        ]);
        wb.DefineName("Rate", "Data!$C$3");
        wb.DefineName("Other", "'My Sheet'!$B$2");
        string before = Dump(wb);
        Edit(wb, op, "Data", 2, 1);
        string after = Dump(wb);
        Assert.NotEqual(before, after);
        WorkbookSheetChangesTests.AssertInstalledTreesMatchInputs(wb);

        var log = new EventLog(wb);
        Assert.True(wb.Undo());
        Assert.Equal(before, Dump(wb));
        Assert.Equal("=  data!b2 *2", wb.GetInput("Sheet1!A1"));
        Assert.Equal("=  b2+'my sheet'!b2 + sheet1!B2", wb.GetInput("Data!A1"));
        WorkbookSheetChangesTests.AssertInstalledTreesMatchInputs(wb);
        Assert.True(wb.Redo());
        Assert.Equal(after, Dump(wb));
        WorkbookSheetChangesTests.AssertInstalledTreesMatchInputs(wb);
        Assert.Equal(2, log.Events.Count);
        Assert.All(log.Events, e => Assert.All(e, a => Assert.Contains('!', a))); // R81: qualified with three sheets
    }

    [Fact]
    public void StructureStep_SurvivesALaterRenameOfTheEditedSheet()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Sheet1!A1", "=Data!B2"), new("Data!B2", "5")]);
        string before = Dump(wb);
        wb.InsertRows("Data", 1, 1);
        string after = Dump(wb);
        wb.RenameSheet("Data", "Info");
        Assert.Equal("=Info!B3", wb.GetInput("Sheet1!A1"));
        wb.Undo();
        wb.Undo();
        Assert.Equal(before, Dump(wb));
        wb.Redo();
        Assert.Equal(after, Dump(wb));
    }

    // ---- MaxInputLength (D-43) and atomicity (§9.2) ----

    [Fact]
    public void RewriteOnAnotherSheetPastMaxInputLength_Throws_AndChangesNothing()
    {
        var wb = Book("Data");
        var body = new StringBuilder("=Data!A2");
        while (body.Length + 2 <= Workbook.MaxInputLength) body.Append("+1");
        if (body.Length < Workbook.MaxInputLength) body.Append('1');
        Assert.Equal(Workbook.MaxInputLength, body.Length);
        wb.ApplyBatch([new("Sheet1!A1", body.ToString()), new("Data!A2", "1")]);
        string before = Dump(wb);
        var ex = Assert.Throws<InvalidOperationException>(() => wb.InsertRows("Data", 1, 8)); // A2 → A10: one more character
        Assert.Contains("Sheet1!A1", ex.Message);
        Assert.Equal(before, Dump(wb));
        wb.InsertRows("Data", 1, 7); // A2 → A9: same length, allowed
        Assert.StartsWith("=Data!A9+1", wb.GetInput("Sheet1!A1"));
    }

    [Fact]
    public void StructureEdit_IsAtomic_WhenRecalcThrows_AndSoAreUndoAndRedo()
    {
        var functions = new ArmableResolver();
        var wb = new Workbook(functions);
        wb.AddSheet("Data");
        wb.ApplyBatch([
            new("Sheet1!A1", "=BOOM(Data!B2)"), new("Data!A1", "=B2+1"), new("Data!B2", "5"),
        ]);
        wb.DefineName("Rate", "Data!$B$2");
        wb.MarkSaved();
        string before = Dump(wb);
        var log = new EventLog(wb);

        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.InsertRows("Data", 1, 1));
        functions.Armed = false;
        Assert.Equal(before, Dump(wb));
        Assert.Empty(log.Events);
        Assert.False(wb.IsDirty);
        WorkbookSheetChangesTests.AssertInstalledTreesMatchInputs(wb);

        wb.InsertRows("Data", 1, 1);
        string after = Dump(wb);
        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.Undo());
        functions.Armed = false;
        Assert.Equal(after, Dump(wb));
        Assert.True(wb.Undo());
        Assert.Equal(before, Dump(wb));
        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.Redo());
        functions.Armed = false;
        Assert.Equal(before, Dump(wb));
        Assert.True(wb.Redo());
        Assert.Equal(after, Dump(wb));
    }

    // ---- CopyPaste across sheets (CR1.3, §9.4) ----

    [Fact]
    public void CopyPaste_Sheet1ToData_UnqualifiedPointAtDestination_QualifiedKeepTheirSheet()
    {
        var wb = Book("Data", "Other");
        wb.ApplyBatch([
            new("Sheet1!A1", "=A2+sheet1!$C$1+other!B1"),
            new("Sheet1!B1", "x"),
            new("Sheet1!A2", "2"),
            new("Sheet1!B2", "=SUM(A1:A2)*Data!A$1+SUM(Other!A1:B1)"),
            new("Sheet1!C1", "1000"),
            new("Data!C4", "100"), new("Data!D3", "old"), new("Data!D4", "keep?"),
            new("Data!C3", "10"), new("Data!E3", "outside"), new("Data!C1", "2"),
            new("Other!B1", "1"), new("Other!D3", "5"),
        ]);
        wb.MarkSaved();
        var log = new EventLog(wb);

        wb.CopyPaste("Sheet1!A1:B2", "Data!C3");

        Assert.Equal("=C4+Sheet1!$C$1+Other!D3", wb.GetInput("Data!C3"));
        Assert.Equal("x", wb.GetInput("Data!D3"));
        Assert.Equal("2", wb.GetInput("Data!C4"));
        Assert.Equal("=SUM(C3:C4)*Data!C$1+SUM(Other!C3:D3)", wb.GetInput("Data!D4"));
        Assert.Equal("outside", wb.GetInput("Data!E3"));
        // The source sheet is untouched.
        Assert.Equal("=A2+sheet1!$C$1+other!B1", wb.GetInput("Sheet1!A1"));
        Assert.Equal(N(1003), wb.GetValue("Sheet1!A1"));
        Assert.Equal("", wb.GetInput("Sheet1!C3"));
        // Unqualified references read the destination sheet (Data!C4 = 2); qualified ones keep theirs.
        Assert.Equal(N(2 + 1000 + 5), wb.GetValue("Data!C3"));
        Assert.Equal(N((1007 + 2) * 2 + 5), wb.GetValue("Data!D4"));
        Assert.Single(log.Events);
        Assert.Equal(["Data!C3", "Data!D3", "Data!C4", "Data!D4"], log.Events[0]);
        Assert.True(wb.IsDirty);
        WorkbookSheetChangesTests.AssertInstalledTreesMatchInputs(wb);
    }

    [Fact]
    public void CopyPaste_AcrossSheets_ClearsUsedDestinationCellsUnderBlankSourceCells_AndIsOneUndoStep()
    {
        var wb = Book("Data");
        wb.ApplyBatch([
            new("Sheet1!A1", "=B1*2"), new("Sheet1!B1", "3"),
            new("Data!C3", "old"), new("Data!D4", "gone"), new("Data!D3", "9"), new("Data!E9", "far"),
        ]);
        string before = Dump(wb);
        wb.CopyPaste("A1:B2", "Data!C3");
        Assert.Equal("=D3*2", wb.GetInput("Data!C3"));
        Assert.Equal("3", wb.GetInput("Data!D3"));
        Assert.Equal("", wb.GetInput("Data!D4"));  // blank source clears
        Assert.Equal("far", wb.GetInput("Data!E9"));
        Assert.Equal(N(6), wb.GetValue("Data!C3"));
        string after = Dump(wb);
        Assert.True(wb.Undo());
        Assert.Equal(before, Dump(wb));
        Assert.True(wb.Redo());
        Assert.Equal(after, Dump(wb));
        Assert.True(wb.Undo());
        Assert.True(wb.Undo()); // the ApplyBatch: one step per call
        Assert.Equal("", wb.GetInput("Data!C3"));
        Assert.Equal(["Sheet1", "Data"], wb.SheetNames);
    }

    [Fact]
    public void CopyPaste_FromAnInactiveSheet_ToTheActiveSheet_Unqualified()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Data!A1", "=B1+Data!B1"), new("Data!B1", "1"), new("Sheet1!C2", "10")]);
        wb.CopyPaste("Data!A1", "B2");
        Assert.Equal("=C2+Data!C2", wb.GetInput("Sheet1!B2"));
        Assert.Equal(N(10), wb.GetValue("Sheet1!B2"));
        Assert.Equal("=B1+Data!B1", wb.GetInput("Data!A1"));
        Assert.Equal("", wb.GetInput("Data!B2"));
    }

    [Fact]
    public void CopyPaste_AcrossSheets_OffGridAndParseErrorsAndQuotedSheets()
    {
        var wb = Book("Data", "My Sheet");
        wb.ApplyBatch([
            new("Sheet1!B2", "=A1+'my sheet'!A1+'My Sheet'!$A$1"),
            new("Sheet1!C2", "=A1+"),
            new("Sheet1!D2", "=Missing!B2"),
        ]);
        wb.CopyPaste("Sheet1!B2:D2", "'MY SHEET'!A1");
        Assert.Equal("=#REF!+#REF!+'My Sheet'!$A$1", wb.GetInput("'My Sheet'!A1"));
        Assert.Equal("=A1+", wb.GetInput("'My Sheet'!B1"));       // R39 verbatim
        Assert.Equal("=Missing!A1", wb.GetInput("'My Sheet'!C1")); // a missing sheet keeps its prefix as written
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("'My Sheet'!C1"));
        wb.AddSheet("Missing");
        Assert.Equal(N(0), wb.GetValue("'My Sheet'!C1"));
    }

    [Fact]
    public void CopyPaste_AcrossSheets_NoChange_IsANoOp()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Sheet1!A1", "=B1"), new("Data!A1", "=B1")]);
        wb.MarkSaved();
        var log = new EventLog(wb);
        wb.CopyPaste("A1", "Data!A1");
        Assert.Empty(log.Events);
        Assert.False(wb.IsDirty);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void CopyPaste_AcrossSheets_Errors_ChangeNothing()
    {
        var wb = Book("Data");
        var body = new StringBuilder("=A1");
        while (body.Length + 2 <= Workbook.MaxInputLength) body.Append("+1");
        if (body.Length < Workbook.MaxInputLength) body.Append('1');
        wb.ApplyBatch([new("Sheet1!A1", body.ToString()), new("Sheet1!B1", "1")]);
        wb.MarkSaved();
        string before = Dump(wb);
        Assert.Throws<ArgumentException>(() => wb.CopyPaste("A1", "Data!A10")); // A1 → A10 grows by one (R55)
        Assert.Throws<ArgumentException>(() => wb.CopyPaste("A1:B1", "Data!ZZ1")); // off the grid
        Assert.Throws<ArgumentException>(() => wb.CopyPaste("Nope!A1", "Data!A1"));
        Assert.Throws<ArgumentException>(() => wb.CopyPaste("A1", "Nope!A1"));
        Assert.Throws<ArgumentException>(() => wb.CopyPaste("Sheet1!A1:Data!B2", "Data!A1")); // one sheet per source
        Assert.Equal(before, Dump(wb));
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void CopyPaste_AcrossSheets_IsAtomic_WhenRecalcThrows()
    {
        var functions = new ArmableResolver();
        var wb = new Workbook(functions);
        wb.AddSheet("Data");
        wb.ApplyBatch([new("Sheet1!A1", "=BOOM(B1)"), new("Sheet1!B1", "1"), new("Data!A1", "old"), new("Data!B1", "2")]);
        wb.MarkSaved();
        string before = Dump(wb);
        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.CopyPaste("A1:B1", "Data!A1"));
        functions.Armed = false;
        Assert.Equal(before, Dump(wb));
        Assert.False(wb.IsDirty);
        wb.CopyPaste("A1:B1", "Data!A1");
        Assert.Equal(N(1), wb.GetValue("Data!A1"));
    }

    [Fact]
    public void CopyPaste_SameSheetWithQualifiedAddresses_BehavesAsUnqualified()
    {
        static Workbook Make()
        {
            var wb = Book("Data");
            wb.ApplyBatch([
                new("A1", "=B1+Data!B1+$C$1"), new("B1", "1"), new("A2", "x"), new("B3", "=A1:A2"), new("C5", "old"),
            ]);
            return wb;
        }
        var plain = Make();
        plain.CopyPaste("A1:B3", "B3");
        var qualified = Make();
        qualified.CopyPaste("sheet1!A1:B3", "SHEET1!B3");
        Assert.Equal(Dump(plain), Dump(qualified));
        Assert.Equal("=C3+Data!C3+$C$1", qualified.GetInput("B3"));
    }
}
