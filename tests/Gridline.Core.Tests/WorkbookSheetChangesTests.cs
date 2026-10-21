using System.Diagnostics.CodeAnalysis;
using System.Text;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;

namespace Gridline.Core.Tests;

// Requirement IDs: CR1.3 (RenameSheet, DeleteSheet, MoveSheet), §5.5 (re-emission of rewritten formulas), §9.2 (atomicity,
// CellsChanged), §9.3 (one undo step, verbatim undo, redo), R39 (#PARSE! verbatim), R59 (#REF! names), R77 (qualified
// names), R78 (errors), R79 (no-ops), R80 (active sheet), R81 (event addresses), R82 (quoting, missing sheets), R85
// (rewriting), D-43 (MaxInputLength on rewrite).
public class WorkbookSheetChangesTests
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

    /// <summary>Sheet1 (A1 = 5), Data (A1 = 10, A2 = 20, B2 = 2), Other, with cross-sheet formulas.</summary>
    private static Workbook Referencing()
    {
        var wb = Book("Data", "Other");
        wb.ApplyBatch([
            new("Sheet1!A1", "5"),
            new("Data!A1", "10"), new("Data!A2", "20"), new("Data!B2", "2"),
            new("Sheet1!B1", "=  data!a1 *2"),
            new("Other!B1", "=SUM(Data!A1:A3)+Data!$B$2"),
            new("Data!C1", "=Data!A1+A2"),
            new("Sheet1!C1", "=  a1 + 1"),
            new("Other!C1", "=Sheet1!A1"),
            new("Other!D1", "=Data!A1+"), // #PARSE!
        ]);
        return wb;
    }

    // ---- RenameSheet (CR1.3, R85) ----

    [Fact]
    public void Rename_RewritesEveryReferencingFormula_IntoANameThatNeedsQuoting()
    {
        var wb = Referencing();
        var log = new EventLog(wb);
        wb.RenameSheet("data", "My Data");
        Assert.Equal(["Sheet1", "My Data", "Other"], wb.SheetNames);
        Assert.Equal("='My Data'!A1*2", wb.GetInput("Sheet1!B1"));
        Assert.Equal("=SUM('My Data'!A1:A3)+'My Data'!$B$2", wb.GetInput("Other!B1"));
        Assert.Equal("='My Data'!A1+A2", wb.GetInput("'My Data'!C1"));
        Assert.Equal(N(20), wb.GetValue("Sheet1!B1"));
        Assert.Equal(N(32), wb.GetValue("Other!B1"));
        Assert.Equal(N(30), wb.GetValue("'my data'!C1"));
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount); // no value changes, nothing re-evaluated
        // R81: the rewritten cells (their inputs changed), qualified, in sheet order then row-major.
        Assert.Equal([["Sheet1!B1", "'My Data'!C1", "Other!B1"]], log.Events);
    }

    [Fact]
    public void Rename_UnaffectedFormulas_AndParseErrors_KeepTheirTextVerbatim()
    {
        var wb = Referencing();
        wb.RenameSheet("Data", "Info");
        Assert.Equal("=  a1 + 1", wb.GetInput("Sheet1!C1"));
        Assert.Equal("=Sheet1!A1", wb.GetInput("Other!C1"));
        Assert.Equal("=Data!A1+", wb.GetInput("Other!D1")); // R39: #PARSE! is never rewritten
        Assert.Equal(E(ErrorCodes.Parse), wb.GetValue("Other!D1"));
    }

    [Fact]
    public void Rename_OutOfANameThatNeedsQuoting_PrintsUnquoted()
    {
        var wb = Book("My Sheet", "O'Brien");
        wb.ApplyBatch([new("'My Sheet'!A1", "3"), new("Sheet1!A1", "='my sheet'!A1+'O''Brien'!A1"), new("Sheet1!A2", "=SUM('My Sheet'!A1:B2)")]);
        wb.RenameSheet("My Sheet", "Plain");
        Assert.Equal("=Plain!A1+'O''Brien'!A1", wb.GetInput("A1"));
        Assert.Equal("=SUM(Plain!A1:B2)", wb.GetInput("A2"));
        wb.RenameSheet("o'brien", "2024");
        Assert.Equal("=Plain!A1+'2024'!A1", wb.GetInput("A1"));
        Assert.Equal(N(3), wb.GetValue("A1"));
    }

    [Fact]
    public void Rename_CasingOnly_IsAChange_ThatPrintsTheNewCasing()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Data!A1", "4"), new("A1", "=data!A1"), new("A2", "=DATA!A1+1")]);
        var log = new EventLog(wb);
        wb.RenameSheet("Data", "DATA");
        Assert.Equal(["Sheet1", "DATA"], wb.SheetNames);
        Assert.Equal("=DATA!A1", wb.GetInput("A1"));
        Assert.Equal("=DATA!A1+1", wb.GetInput("A2")); // already printed that way: kept, not an edit
        Assert.Equal([["Sheet1!A1"]], log.Events);
        Assert.Equal(N(5), wb.GetValue("A2"));
        Assert.True(wb.Undo());
        Assert.Equal(["Sheet1", "Data"], wb.SheetNames);
        Assert.Equal("=data!A1", wb.GetInput("A1"));
    }

    [Theory]
    [InlineData("Data", "Data")]
    [InlineData("data", "Data")]
    public void Rename_ToTheOrdinalIdenticalName_IsANoOp(string oldName, string newName)
    {
        var wb = Book("Data");
        wb.SetCell("A1", "=data!A1");
        wb.MarkSaved();
        var log = new EventLog(wb);
        string before = Dump(wb);
        wb.RenameSheet(oldName, newName);
        Assert.Equal(before, Dump(wb));
        Assert.Empty(log.Events);
        Assert.False(wb.IsDirty);
        Assert.True(wb.Undo()); // the SetCell, not a rename step
        Assert.Equal("", wb.GetInput("A1"));
        Assert.True(wb.Undo()); // the AddSheet
        Assert.False(wb.CanUndo);
    }

    [Theory]
    [InlineData("Nope", "X", typeof(ArgumentException))]   // unknown sheet
    [InlineData("Data", "sheet1", typeof(ArgumentException))] // duplicate, case-insensitive
    [InlineData("Data", "OTHER", typeof(ArgumentException))]
    [InlineData("Data", "a:b", typeof(ArgumentException))]   // invalid name
    [InlineData("Data", "", typeof(ArgumentException))]
    [InlineData("Data", "'Data", typeof(ArgumentException))]
    [InlineData(null, "X", typeof(ArgumentNullException))]
    [InlineData("Data", null, typeof(ArgumentNullException))]
    public void Rename_Errors_ChangeNothing(string? oldName, string? newName, Type expected)
    {
        var wb = Referencing();
        wb.MarkSaved();
        var log = new EventLog(wb);
        string before = Dump(wb);
        var ex = Assert.ThrowsAny<Exception>(() => wb.RenameSheet(oldName!, newName!));
        Assert.IsType(expected, ex);
        Assert.Equal(before, Dump(wb));
        Assert.Empty(log.Events);
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void Rename_RewritesNames_KeepingTheirBinding()
    {
        var wb = Book("Data", "Other");
        wb.SetCell("Data!B1", "7");
        wb.DefineName("Rate", "Data!$B$1");
        wb.DefineName("Span", "data!$A$1:$B$3");
        wb.DefineName("Elsewhere", "Other!$A$1");
        wb.SetCell("A1", "=Rate*2");
        wb.RenameSheet("Data", "My Data");
        Assert.Equal([new("Elsewhere", "Other!$A$1"), new("Rate", "'My Data'!$B$1"), new("Span", "'My Data'!$A$1:$B$3")], wb.GetNames());
        Assert.Equal(N(14), wb.GetValue("A1"));
        wb.SetCell("'My Data'!B1", "8");
        Assert.Equal(N(16), wb.GetValue("A1"));
        Assert.True(wb.Undo());
        Assert.True(wb.Undo());
        Assert.Equal([new("Elsewhere", "Other!$A$1"), new("Rate", "Data!$B$1"), new("Span", "Data!$A$1:$B$3")], wb.GetNames());
    }

    [Fact]
    public void Rename_ToAMissingSheetName_ResolvesThoseReferences_TextUnchanged()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Data!A1", "9"), new("A1", "=New!A1+1"), new("A2", "=Data!A1"), new("A3", "=A1*2")]);
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        var log = new EventLog(wb);
        wb.RenameSheet("Data", "New");
        Assert.Equal("=New!A1+1", wb.GetInput("A1"));
        Assert.Equal("=New!A1", wb.GetInput("A2"));
        Assert.Equal(N(10), wb.GetValue("A1"));
        Assert.Equal(N(20), wb.GetValue("A3"));
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount); // A1 and its dependent A3
        Assert.Equal([["Sheet1!A1", "Sheet1!A2", "Sheet1!A3"]], log.Events);

        Assert.True(wb.Undo());
        Assert.Equal("=New!A1+1", wb.GetInput("A1"));
        Assert.Equal("=Data!A1", wb.GetInput("A2"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A3"));
        Assert.Equal(N(9), wb.GetValue("A2"));
        Assert.True(wb.Redo());
        Assert.Equal(N(10), wb.GetValue("A1"));
    }

    [Fact]
    public void Rename_AFormulaNamingBothTheOldAndTheNewName_MergesAndRestoresVerbatim()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("Data!A1", "3"), new("A1", "=Data!A1+New!A1")]);
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        wb.RenameSheet("Data", "New");
        Assert.Equal("=New!A1+New!A1", wb.GetInput("A1"));
        Assert.Equal(N(6), wb.GetValue("A1"));
        wb.SetCell("New!A1", "4");
        Assert.Equal(N(8), wb.GetValue("A1"));
        Assert.True(wb.Undo());
        Assert.True(wb.Undo());
        Assert.Equal("=Data!A1+New!A1", wb.GetInput("A1"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        wb.AddSheet("New"); // the restored formula still depends on the missing name
        Assert.Equal(N(3), wb.GetValue("A1"));
    }

    [Fact]
    public void Rename_TheActiveSheet_KeepsItActive_AndEditsFollowTheRename()
    {
        var wb = Book("Data");
        wb.ActiveSheet = "Data";
        wb.SetCell("Sheet1!A1", "=Data!B1");
        wb.RenameSheet("Data", "Facts");
        Assert.Equal("Facts", wb.ActiveSheet);
        wb.SetCell("B1", "12"); // unqualified: the active sheet
        Assert.Equal(N(12), wb.GetValue("Sheet1!A1"));
        Assert.Equal("=Facts!B1", wb.GetInput("Sheet1!A1"));
        Assert.Throws<ArgumentException>(() => wb.GetValue("Data!B1"));
    }

    [Fact]
    public void Rename_UndoRedo_RestoresByteIdenticalText_AndTheHistoryIsOneStep()
    {
        var wb = Referencing();
        wb.MarkSaved();
        string before = Dump(wb);
        wb.RenameSheet("Data", "Info");
        Assert.True(wb.IsDirty);
        string after = Dump(wb);
        var log = new EventLog(wb);
        Assert.True(wb.Undo());
        Assert.Equal(before, Dump(wb));
        Assert.Equal("=  data!a1 *2", wb.GetInput("Sheet1!B1"));
        Assert.False(wb.IsDirty);
        Assert.True(wb.Redo());
        Assert.Equal(after, Dump(wb));
        Assert.Equal(2, log.Events.Count);
        Assert.Equal(["Sheet1!B1", "Data!C1", "Other!B1"], log.Events[0]);
        Assert.Equal(["Sheet1!B1", "Info!C1", "Other!B1"], log.Events[1]);
    }

    [Fact]
    public void Rename_ThatPushesAFormulaPastMaxInputLength_Throws_AndChangesNothing()
    {
        var wb = Book("A");
        string body = "=A!A1" + string.Concat(Enumerable.Repeat("+1", (Workbook.MaxInputLength - 5) / 2));
        Assert.True(body.Length <= Workbook.MaxInputLength && body.Length >= Workbook.MaxInputLength - 1);
        wb.SetCell("B1", body);
        wb.DefineName("Rate", "A!$A$1");
        wb.MarkSaved();
        string before = Dump(wb);
        var ex = Assert.Throws<InvalidOperationException>(() => wb.RenameSheet("A", "Longer"));
        Assert.Contains("Sheet1!B1", ex.Message);
        Assert.Equal(before, Dump(wb));
        Assert.False(wb.IsDirty);
        wb.RenameSheet("A", "B"); // same length: fine
        Assert.StartsWith("=B!A1+1", wb.GetInput("B1"));
    }

    [Fact]
    public void Rename_ThatThrowsDuringRecalc_IsAtomic_ThenRetries()
    {
        var functions = new ArmableResolver();
        var wb = new Workbook(functions);
        wb.AddSheet("Data");
        wb.ApplyBatch([new("Data!A1", "2"), new("A1", "=BOOM(New!A1)"), new("A2", "=Data!A1*3")]);
        wb.DefineName("Rate", "Data!$A$1");
        wb.MarkSaved();
        string before = Dump(wb);
        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.RenameSheet("Data", "New"));
        Assert.Equal(before, Dump(wb));
        Assert.False(wb.IsDirty);
        Assert.False(wb.CanRedo);
        functions.Armed = false;
        wb.SetCell("Data!A1", "5"); // the graph still binds Data
        Assert.Equal(N(15), wb.GetValue("A2"));
        wb.RenameSheet("Data", "New");
        Assert.Equal(N(5), wb.GetValue("A1"));
        Assert.Equal("=New!A1*3", wb.GetInput("A2"));
        wb.SetCell("New!A1", "6");
        Assert.Equal(N(18), wb.GetValue("A2"));
        Assert.Equal(N(6), wb.GetValue("A1"));
    }

    [Fact]
    public void UndoOfRename_ThatThrowsDuringRecalc_IsAtomic()
    {
        var functions = new ArmableResolver();
        var wb = new Workbook(functions);
        wb.AddSheet("Data");
        wb.ApplyBatch([new("Data!A1", "2"), new("A1", "=BOOM(New!A1)"), new("A2", "=Data!A1")]);
        wb.RenameSheet("Data", "New");
        string after = Dump(wb);
        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.Undo()); // A1 goes back to #REF! and re-evaluates
        Assert.Equal(after, Dump(wb));
        Assert.True(wb.CanUndo);
        functions.Armed = false;
        wb.SetCell("New!A1", "3");
        Assert.Equal(N(3), wb.GetValue("A1"));
        Assert.Equal(N(3), wb.GetValue("A2"));
    }

    // ---- DeleteSheet (CR1.3, R85, R80) ----

    [Fact]
    public void Delete_TurnsSingleAndRangeReferencesIntoRef_LeavingOtherMissingSheetsAlone()
    {
        var wb = Book("Data", "Other");
        wb.ApplyBatch([
            new("Data!A1", "1"), new("Other!A1", "2"),
            new("A1", "=data!A1+1"), new("A2", "=SUM(Data!A1:B2)"), new("A3", "=Data!A1+Other!A1+Gone!A1"),
            new("A4", "=Other!A1*2"), new("A5", "=A4+1"), new("Other!B1", "=COUNT(Data!$A$1:A3,1)"), new("A6", "=Data!A1+"),
        ]);
        var log = new EventLog(wb);
        wb.DeleteSheet("DATA");
        Assert.Equal(["Sheet1", "Other"], wb.SheetNames);
        Assert.Equal("=#REF!+1", wb.GetInput("A1"));
        Assert.Equal("=SUM(#REF!)", wb.GetInput("A2"));
        Assert.Equal("=#REF!+Other!A1+Gone!A1", wb.GetInput("A3"));
        Assert.Equal("=COUNT(#REF!,1)", wb.GetInput("Other!B1"));
        Assert.Equal("=Other!A1*2", wb.GetInput("A4"));
        Assert.Equal("=Data!A1+", wb.GetInput("A6")); // R39
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A2"));
        Assert.Equal(N(4), wb.GetValue("A4"));
        Assert.Throws<ArgumentException>(() => wb.GetInput("Data!A1"));
        Assert.Equal([["Sheet1!A1", "Sheet1!A2", "Sheet1!A3", "Other!B1"]], log.Events);
    }

    [Fact]
    public void Delete_SetsNamesIntoTheSheetToRef_AndTheirDependentsRecalculate()
    {
        var wb = Book("Data", "Other");
        wb.ApplyBatch([new("Data!B1", "7"), new("Other!A1", "1")]);
        wb.DefineName("Rate", "Data!$B$1");
        wb.DefineName("Keep", "Other!$A$1");
        wb.SetCell("A1", "=Rate*2");
        wb.SetCell("A2", "=Keep");
        wb.DeleteSheet("Data");
        Assert.Equal([new("Keep", "Other!$A$1"), new("Rate", "#REF!")], wb.GetNames());
        Assert.Equal("=Rate*2", wb.GetInput("A1")); // a name use is not a sheet reference: not rewritten
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("A1"));
        Assert.Equal(N(1), wb.GetValue("A2"));
        Assert.True(wb.Undo());
        Assert.Equal([new("Keep", "Other!$A$1"), new("Rate", "Data!$B$1")], wb.GetNames());
        Assert.Equal(N(14), wb.GetValue("A1"));
        wb.SetCell("Data!B1", "8");
        Assert.Equal(N(16), wb.GetValue("A1"));
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("Nope", typeof(ArgumentException))]
    public void Delete_Errors_ChangeNothing(string? name, Type expected)
    {
        var wb = Referencing();
        wb.MarkSaved();
        string before = Dump(wb);
        Assert.IsType(expected, Assert.ThrowsAny<Exception>(() => wb.DeleteSheet(name!)));
        Assert.Equal(before, Dump(wb));
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void Delete_TheLastSheet_ThrowsInvalidOperation()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.MarkSaved();
        Assert.Throws<InvalidOperationException>(() => wb.DeleteSheet("sheet1"));
        Assert.Equal(["Sheet1"], wb.SheetNames);
        Assert.Equal("1", wb.GetInput("A1"));
        Assert.False(wb.IsDirty);
        var two = Book("Data");
        two.DeleteSheet("Sheet1");
        Assert.Throws<InvalidOperationException>(() => two.DeleteSheet("Data"));
    }

    [Theory]
    [InlineData(0, "B")]  // the sheet now at the removed index
    [InlineData(1, "C")]
    [InlineData(2, "B")]  // past the end: the last sheet
    public void Delete_TheActiveSheet_MovesActivationPerR80(int index, string expected)
    {
        var wb = new Workbook();
        wb.RenameSheet("Sheet1", "A");
        wb.AddSheet("B");
        wb.AddSheet("C");
        string active = wb.SheetNames[index];
        wb.ActiveSheet = active;
        wb.DeleteSheet(active);
        Assert.Equal(expected, wb.ActiveSheet);
        Assert.True(wb.Undo());
        Assert.Equal(expected, wb.ActiveSheet); // undo never otherwise changes the active sheet (R80)
        Assert.Equal(["A", "B", "C"], wb.SheetNames);
        wb.ActiveSheet = active;
        Assert.True(wb.Redo()); // redo of a delete removes the active sheet again
        Assert.Equal(expected, wb.ActiveSheet);
    }

    [Fact]
    public void Delete_OfAnInactiveSheet_KeepsTheActiveSheet()
    {
        var wb = Book("Data", "Other");
        wb.ActiveSheet = "Other";
        wb.DeleteSheet("Sheet1");
        Assert.Equal("Other", wb.ActiveSheet);
        Assert.Equal(["Data", "Other"], wb.SheetNames);
        wb.SetCell("A1", "3");
        Assert.Equal(["Other!A1"], wb.GetUsedAddresses());
    }

    [Fact]
    public void Delete_UndoRestoresTheSheetAtItsIndex_CellsNamesAndVerbatimText_RedoReapplies()
    {
        var wb = Referencing();
        wb.DefineName("Rate", "Data!$B$2");
        wb.DefineName("Mine", "Sheet1!$A$1");
        wb.SetCell("Sheet1!D1", "=Rate+Data!A2");
        wb.MarkSaved();
        string before = Dump(wb);
        wb.DeleteSheet("Data");
        string after = Dump(wb);
        Assert.Equal("=#REF!*2", wb.GetInput("Sheet1!B1"));
        Assert.Equal("=Rate+#REF!", wb.GetInput("Sheet1!D1"));

        var log = new EventLog(wb);
        Assert.True(wb.Undo());
        Assert.Equal(before, Dump(wb));
        Assert.Equal("=  data!a1 *2", wb.GetInput("Sheet1!B1"));
        Assert.Equal(["Sheet1", "Data", "Other"], wb.SheetNames);
        Assert.False(wb.IsDirty);
        // R81: the restored sheet's cells, the restored formulas, and the values that changed.
        Assert.Equal(["Sheet1!B1", "Sheet1!D1", "Data!A1", "Data!C1", "Data!A2", "Data!B2", "Other!B1"], log.Events[0]);
        Assert.True(wb.Redo());
        Assert.Equal(after, Dump(wb));
        Assert.True(wb.Undo());
        wb.SetCell("Data!A1", "100"); // the restored sheet is live in the graph
        Assert.Equal(N(200), wb.GetValue("Sheet1!B1"));
        Assert.Equal(N(120), wb.GetValue("Data!C1"));
    }

    [Fact]
    public void Delete_ThatPushesAFormulaPastMaxInputLength_Throws_AndChangesNothing()
    {
        var wb = Book("A");
        // "A!A1" (4) becomes "#REF!" (5): the rewrite grows by one per reference.
        string body = "=" + string.Join("+", Enumerable.Repeat("A!A1", (Workbook.MaxInputLength - 1) / 5));
        body += new string(' ', Workbook.MaxInputLength - body.Length);
        wb.SetCell("B1", body);
        wb.MarkSaved();
        string before = Dump(wb);
        var ex = Assert.Throws<InvalidOperationException>(() => wb.DeleteSheet("A"));
        Assert.Contains(Workbook.MaxInputLength.ToString(), ex.Message);
        Assert.Equal(before, Dump(wb));
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void Delete_ThatThrowsDuringRecalc_IsAtomic()
    {
        var functions = new ArmableResolver();
        var wb = new Workbook(functions);
        wb.AddSheet("Data");
        wb.AddSheet("Other");
        wb.ApplyBatch([new("Data!A1", "2"), new("Data!A2", "=Data!A1*2"), new("Other!A1", "=BOOM(Data!A1)"), new("A1", "=Data!A2+1")]);
        wb.DefineName("Rate", "Data!$A$1");
        wb.ActiveSheet = "Data";
        wb.MarkSaved();
        string before = Dump(wb);
        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.DeleteSheet("Data"));
        Assert.Equal(before, Dump(wb));
        Assert.Equal("Data", wb.ActiveSheet);
        Assert.False(wb.IsDirty);
        functions.Armed = false;
        wb.SetCell("Data!A1", "3"); // the graph is intact
        Assert.Equal(N(7), wb.GetValue("Sheet1!A1"));
        Assert.Equal(N(3), wb.GetValue("Other!A1"));
        wb.DeleteSheet("Data");
        Assert.Equal("Other", wb.ActiveSheet); // R80: the sheet now at the removed index
    }

    [Fact]
    public void UndoOfDelete_ThatThrowsDuringRecalc_IsAtomic()
    {
        var functions = new ArmableResolver();
        var wb = new Workbook(functions);
        wb.AddSheet("Data");
        wb.ApplyBatch([new("Data!A1", "2"), new("A1", "=BOOM(Data!A1)")]);
        wb.DeleteSheet("Data");
        string after = Dump(wb);
        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.Undo());
        Assert.Equal(after, Dump(wb));
        functions.Armed = false;
        Assert.True(wb.Undo());
        Assert.Equal(N(2), wb.GetValue("A1"));
        Assert.Equal("=BOOM(Data!A1)", wb.GetInput("A1"));
    }

    [Fact]
    public void Delete_TheFirstSheet_LeavesEveryApiWorking()
    {
        var wb = Book("Data");
        wb.ApplyBatch([new("A1", "1"), new("Data!A1", "=Sheet1!A1+1"), new("Data!A2", "=A1*2")]);
        wb.DeleteSheet("Sheet1"); // internal sheet id 0 no longer exists
        Assert.Equal(["Data"], wb.SheetNames);
        Assert.Equal(["A1", "A2"], wb.GetUsedAddresses()); // one sheet: unqualified again (R81)
        Assert.Equal("=#REF!+1", wb.GetInput("A1"));
        wb.SetCell("A1", "5");
        Assert.Equal(N(10), wb.GetValue("A2"));
        wb.AddSheet("Sheet1"); // a new sheet gets a fresh id
        wb.SetCell("Sheet1!A1", "=Data!A1");
        Assert.Equal(N(5), wb.GetValue("Sheet1!A1"));
        wb.InsertRows(1, 1);
        Assert.Equal("=Data!A2", wb.GetInput("Sheet1!A1"));
        Assert.Equal(N(5), wb.GetValue("Sheet1!A1"));
        for (int i = 0; i < 5; i++) Assert.True(wb.Undo()); // InsertRows, SetCell, AddSheet, SetCell, DeleteSheet
        Assert.Equal(["Sheet1", "Data"], wb.SheetNames);
        Assert.Equal("1", wb.GetInput("Sheet1!A1"));
        Assert.Equal(N(2), wb.GetValue("Data!A1"));
    }

    // ---- MoveSheet (CR1.3) ----

    [Fact]
    public void Move_ChangesOnlyTheOrder_AndGetUsedAddressesFollows()
    {
        var wb = Book("Data", "Other");
        wb.ApplyBatch([new("A1", "1"), new("Data!A1", "=Sheet1!A1"), new("Other!A1", "x")]);
        wb.ActiveSheet = "Other";
        var log = new EventLog(wb);
        wb.MoveSheet("other", 0);
        Assert.Equal(["Other", "Sheet1", "Data"], wb.SheetNames);
        Assert.Equal(["Other!A1", "Sheet1!A1", "Data!A1"], wb.GetUsedAddresses());
        Assert.Equal("Other", wb.ActiveSheet);
        Assert.Empty(log.Events);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.True(wb.IsDirty);
        wb.MoveSheet("Other", 2);
        Assert.Equal(["Sheet1", "Data", "Other"], wb.SheetNames);
        wb.MoveSheet("Sheet1", 1);
        Assert.Equal(["Data", "Sheet1", "Other"], wb.SheetNames);
        Assert.True(wb.Undo());
        Assert.Equal(["Sheet1", "Data", "Other"], wb.SheetNames);
        Assert.True(wb.Undo());
        Assert.Equal(["Other", "Sheet1", "Data"], wb.SheetNames);
        Assert.True(wb.Undo());
        Assert.Equal(["Sheet1", "Data", "Other"], wb.SheetNames);
        Assert.True(wb.Redo());
        Assert.Equal(["Other", "Sheet1", "Data"], wb.SheetNames);
        Assert.Empty(log.Events);
    }

    [Fact]
    public void Move_ToTheCurrentIndex_IsANoOp()
    {
        var wb = Book("Data");
        wb.MarkSaved();
        wb.MoveSheet("data", 1);
        Assert.Equal(["Sheet1", "Data"], wb.SheetNames);
        Assert.False(wb.IsDirty);
        Assert.True(wb.Undo()); // the AddSheet, not a move
        Assert.False(wb.CanUndo);
    }

    [Theory]
    [InlineData("Data", -1, typeof(ArgumentOutOfRangeException))]
    [InlineData("Data", 3, typeof(ArgumentOutOfRangeException))]
    [InlineData("Data", int.MaxValue, typeof(ArgumentOutOfRangeException))]
    [InlineData("Nope", 0, typeof(ArgumentException))]
    [InlineData(null, 0, typeof(ArgumentNullException))]
    public void Move_Errors_ChangeNothing(string? name, int index, Type expected)
    {
        var wb = Referencing();
        wb.MarkSaved();
        string before = Dump(wb);
        Assert.IsType(expected, Assert.ThrowsAny<Exception>(() => wb.MoveSheet(name!, index)));
        Assert.Equal(before, Dump(wb));
        Assert.False(wb.IsDirty);
    }

    // ---- Mixed sequences ----

    [Fact]
    public void RenameMoveDelete_UndoAll_RestoresTheExactPriorState_RedoAllTheFinalOne()
    {
        var wb = Referencing();
        wb.DefineName("Rate", "Data!$A$2");
        wb.SetCell("Sheet1!E1", "=Rate+Other!C1");
        string initial = Dump(wb);
        var states = new List<string> { initial };
        void Step(Action a) { a(); states.Add(Dump(wb)); }
        Step(() => wb.RenameSheet("Data", "My Data"));
        Step(() => wb.MoveSheet("My Data", 0));
        Step(() => wb.RenameSheet("Other", "data"));
        Step(() => wb.SetCell("data!E5", "='My Data'!A1+Data!C1"));
        Step(() => wb.DeleteSheet("My Data"));
        Step(() => wb.AddSheet("My Data", 1));
        Step(() => wb.SetCell("'My Data'!A1", "40"));
        Step(() => wb.DeleteSheet("Sheet1"));
        Step(() => wb.RenameSheet("data", "Sheet1"));
        for (int i = states.Count - 2; i >= 0; i--)
        {
            Assert.True(wb.Undo());
            Assert.Equal(StripActive(states[i]), StripActive(Dump(wb))); // undo does not restore activation (R80)
        }
        for (int i = 1; i < states.Count; i++)
        {
            Assert.True(wb.Redo());
            Assert.Equal(StripActive(states[i]), StripActive(Dump(wb)));
        }
    }

    private static string ActiveOf(string dump) => dump[(dump.IndexOf(" active=", StringComparison.Ordinal) + 8)..dump.IndexOf('\n')];

    private static string StripActive(string dump) => dump.Replace(" active=" + ActiveOf(dump) + "\n", "\n");
}
