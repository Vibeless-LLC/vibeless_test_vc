using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;

namespace Gridline.Core.Tests;

// Requirement IDs: CR1.4 (workbook-scoped names, sheet-qualified absolute refersTo, unqualified qualified with the active
// sheet, GetNames qualified), CR1.10, R77 (always qualified, canonical form, unknown sheet), R58, R59, R60 (no-op on the
// canonical form), R82 (prefix rules), R84 (structure edits adjust names pointing into the edited sheet), §6.7
// (cross-sheet name cycles), §9.2 (atomicity), §9.3 (one undo step, exact names restored), §9.6.
public class WorkbookQualifiedNamesTests
{
    private static CellValue N(double d) => CellValue.FromNumber(d);
    private static CellValue E(string code) => CellValue.FromError(code);
    private static KeyValuePair<string, string> P(string a, string b) => new(a, b);

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

    // ---- Unqualified refersTo: qualified with the active sheet (CR1.4, R77) ----

    [Fact]
    public void Unqualified_OnASingleSheet_IsQualified()
    {
        var wb = new Workbook();
        wb.DefineName("Rate", "$B$1");
        Assert.Equal([P("Rate", "Sheet1!$B$1")], wb.GetNames());
    }

    [Fact]
    public void Unqualified_IsQualifiedWithTheSheetActiveAtDefinition()
    {
        var wb = Book("Data", "My Sheet", "O'Brien", "2024");
        wb.DefineName("OnSheet1", "$a$1");
        wb.ActiveSheet = "data";
        wb.DefineName("OnData", "$C$10:$A$1");
        wb.ActiveSheet = "My Sheet";
        wb.DefineName("OnMy", "$B$2");
        wb.ActiveSheet = "O'Brien";
        wb.DefineName("OnBrien", "$C$3");
        wb.ActiveSheet = "2024";
        wb.DefineName("OnYear", "$D$4:$D$4");
        Assert.Equal(
            [P("OnBrien", "'O''Brien'!$C$3"), P("OnData", "Data!$A$1:$C$10"), P("OnMy", "'My Sheet'!$B$2"),
                P("OnSheet1", "Sheet1!$A$1"), P("OnYear", "'2024'!$D$4:$D$4")],
            wb.GetNames());
    }

    // ---- Qualified refersTo (CR1.4, R77, R58, R82) ----

    [Theory]
    [InlineData("Data!$A$1:$A$10", "Data!$A$1:$A$10")]
    [InlineData("data!$b$2", "Data!$B$2")]                  // sheet in its defined casing, cell part uppercase
    [InlineData("DATA!$C$10:$A$1", "Data!$A$1:$C$10")]      // corners normalised (R58)
    [InlineData("'Data'!$B$2", "Data!$B$2")]                // quotes that are not required are dropped
    [InlineData("'My Sheet'!$B$2", "'My Sheet'!$B$2")]
    [InlineData("'my sheet'!$b$2:$b$2", "'My Sheet'!$B$2:$B$2")]
    [InlineData("'O''Brien'!$A$1", "'O''Brien'!$A$1")]
    [InlineData("'2024'!$A$1", "'2024'!$A$1")]
    [InlineData("Sheet1!$ZZ$100000", "Sheet1!$ZZ$100000")]
    public void Qualified_IsStoredCanonically(string refersTo, string stored)
    {
        var wb = Book("Data", "My Sheet", "O'Brien", "2024");
        wb.ActiveSheet = "My Sheet"; // a qualified refersTo ignores the active sheet
        wb.DefineName("x", refersTo);
        Assert.Equal([P("x", stored)], wb.GetNames());
    }

    [Theory]
    [InlineData("Nope!$A$1")]           // unknown sheet (R77)
    [InlineData("'No Such'!$A$1")]
    [InlineData("Data2!$A$1:$B$2")]
    [InlineData("My Sheet!$B$2")]       // a prefix that needs quotes must be quoted (R82)
    [InlineData("2024!$A$1")]
    [InlineData("'My Sheet!$B$2")]      // unterminated quote
    [InlineData("'My Sheet' !$B$2")]    // whitespace before !
    [InlineData("Data !$B$2")]
    [InlineData("Data! $B$2")]
    [InlineData("Data!B2")]             // still fully absolute (R58)
    [InlineData("Data!$B2")]
    [InlineData("Data!")]
    [InlineData("!$A$1")]
    [InlineData("Data!Data!$A$1")]      // a second prefix
    [InlineData("Data!$A$1:Data!$B$2")]
    [InlineData("$A$1:Data!$B$2")]      // a prefix on the second corner only
    [InlineData("Data!#REF!")]
    [InlineData("#REF!")]               // R59: only the loader may store it
    [InlineData("Data!Rate")]
    [InlineData("''!$A$1")]
    [InlineData("'Bad[Name'!$A$1")]     // not a valid sheet name
    public void InvalidQualified_ThrowsArgumentException_AndChangesNothing(string refersTo)
    {
        var wb = Book("Data", "My Sheet", "2024");
        wb.DefineName("Rate", "$A$1");
        var log = new EventLog(wb);
        var ex = Assert.Throws<ArgumentException>(() => wb.DefineName("Rate", refersTo));
        Assert.Equal("refersTo", ex.ParamName);
        Assert.Throws<ArgumentException>(() => wb.DefineName("Other", refersTo));
        Assert.Equal([P("Rate", "Sheet1!$A$1")], wb.GetNames());
        Assert.Empty(log.Events);
        Assert.True(wb.Undo());
        Assert.Empty(wb.GetNames()); // the last step was the original definition: the failed calls recorded none
    }

    [Fact]
    public void UnknownSheet_MessageNamesTheSheet()
    {
        var wb = new Workbook();
        var ex = Assert.Throws<ArgumentException>(() => wb.DefineName("Rate", "Data!$A$1"));
        Assert.Contains("Data", ex.Message, StringComparison.Ordinal);
    }

    // ---- GetNames (§9.1, CR1.4) ----

    [Fact]
    public void GetNames_IsSortedOrdinalIgnoreCase_WithQualifiedValues()
    {
        var wb = Book("Data", "My Sheet");
        wb.DefineName("beta", "Data!$A$1");
        wb.DefineName("_z", "$A$2");
        wb.ActiveSheet = "My Sheet";
        wb.DefineName("Alpha", "$A$3:$B$4");
        wb.DefineName("ALPHA2", "sheet1!$A$5");
        wb.DefineName("a.b", "'my sheet'!$a$6");
        Assert.Equal(
            [P("a.b", "'My Sheet'!$A$6"), P("Alpha", "'My Sheet'!$A$3:$B$4"), P("ALPHA2", "Sheet1!$A$5"),
                P("beta", "Data!$A$1"), P("_z", "Sheet1!$A$2")],
            wb.GetNames());
    }

    // ---- Evaluation across sheets (CR1.4, CR1.2, §6.7) ----

    [Fact]
    public void SumOfANameIntoAnotherSheet_RecalculatesWhenADataCellIsEdited()
    {
        var wb = Book("Data");
        wb.ApplyBatch([P("Data!A1", "1"), P("Data!A2", "2"), P("Data!A3", "3"), P("A2", "100")]);
        wb.DefineName("Total", "Data!$A$1:$A$3");
        wb.SetCell("A1", "=SUM(Total)");
        Assert.Equal(N(6), wb.GetValue("A1"));

        var log = new EventLog(wb);
        wb.SetCell("Data!A2", "20");
        Assert.Equal(N(24), wb.GetValue("Sheet1!A1"));
        Assert.Equal(["Sheet1!A1", "Data!A2"], Assert.Single(log.Events)); // sheet order (R81)
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);

        wb.SetCell("A2", "5"); // Sheet1!A2 is not part of the target
        Assert.Equal(N(24), wb.GetValue("A1"));
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void ANameBehavesLikeItsQualifiedReference_FromAnySheet()
    {
        var wb = Book("Data", "Other");
        wb.ApplyBatch([P("Data!B1", "7"), P("B1", "1"), P("Other!B1", "1000")]);
        wb.DefineName("Rate", "Data!$B$1");
        wb.SetCell("Sheet1!A1", "=Rate*2");
        wb.SetCell("Data!A1", "=Rate+1");
        wb.SetCell("Other!A1", "=SUM(Rate)");
        wb.SetCell("Other!A2", "=Data!$B$1*2");
        Assert.Equal(N(14), wb.GetValue("Sheet1!A1"));
        Assert.Equal(N(8), wb.GetValue("Data!A1"));
        Assert.Equal(N(7), wb.GetValue("Other!A1"));
        Assert.Equal(wb.GetValue("Other!A2"), wb.GetValue("Sheet1!A1"));

        wb.SetCell("Data!B1", "10");
        Assert.Equal(N(20), wb.GetValue("Sheet1!A1"));
        Assert.Equal(N(11), wb.GetValue("Data!A1"));
        Assert.Equal(N(10), wb.GetValue("Other!A1"));
    }

    [Fact]
    public void RedefiningANameOntoAnotherSheet_MovesTheDependency()
    {
        var wb = Book("Data");
        wb.ApplyBatch([P("Data!B1", "7"), P("B1", "1"), P("A1", "=Rate*2")]);
        wb.DefineName("Rate", "$B$1");
        Assert.Equal(N(2), wb.GetValue("A1"));
        wb.DefineName("Rate", "Data!$B$1");
        Assert.Equal(N(14), wb.GetValue("A1"));
        wb.SetCell("B1", "50");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("Data!B1", "8");
        Assert.Equal(N(16), wb.GetValue("A1"));
    }

    [Fact]
    public void CrossSheetCycleThroughNames_IsCirc_AndBreaks()
    {
        var wb = Book("Data");
        wb.DefineName("Back", "Sheet1!$A$1");
        wb.DefineName("Fwd", "Data!$A$1");
        wb.SetCell("Sheet1!A1", "=Fwd+1");
        wb.SetCell("Data!A1", "=Back*2");
        wb.SetCell("Sheet1!B1", "=A1");
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("Sheet1!A1"));
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("Data!A1"));
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("Sheet1!B1"));

        wb.DefineName("Back", "Sheet1!$C$1"); // retarget: the cycle is broken
        Assert.Equal(N(1), wb.GetValue("Sheet1!A1"));
        Assert.Equal(N(0), wb.GetValue("Data!A1"));
        Assert.Equal(N(1), wb.GetValue("Sheet1!B1"));

        wb.Undo();
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("Sheet1!A1"));
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("Data!A1"));
    }

    // ---- R60 no-op on the canonical form; undo and redo (§9.3) ----

    [Fact]
    public void Redefinition_WithTheSameCanonicalForm_IsANoOp()
    {
        var wb = Book("Data");
        wb.ApplyBatch([P("B1", "4"), P("A1", "=Rate")]);
        wb.DefineName("Rate", "$B$1");
        wb.DefineName("Other", "Data!$A$1");
        wb.Undo();
        Assert.True(wb.CanRedo);
        var log = new EventLog(wb);

        wb.DefineName("Rate", "sheet1!$b$1");     // explicit qualifier, other casing: the same canonical form
        wb.DefineName("Rate", "'Sheet1'!$B$1");
        wb.ActiveSheet = "Data";
        wb.DefineName("Rate", "Sheet1!$B$1");     // qualified explicitly, so the active sheet does not matter
        Assert.Empty(log.Events);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.True(wb.CanRedo);                  // no step was recorded

        wb.DefineName("Rate", "$B$1");            // now Data!$B$1: a real change
        Assert.Equal([P("Rate", "Data!$B$1")], wb.GetNames());
        Assert.False(wb.CanRedo);
        Assert.Equal(N(0), wb.GetValue("Sheet1!A1"));
    }

    [Fact]
    public void UndoAndRedo_RestoreTheExactQualifiedNames()
    {
        var wb = Book("Data", "My Sheet");
        wb.ApplyBatch([P("Data!B1", "3"), P("'My Sheet'!B1", "5"), P("A1", "=rate*10")]);
        var s0 = wb.GetNames();
        wb.DefineName("rate", "data!$b$1");
        var s1 = wb.GetNames();
        wb.ActiveSheet = "My Sheet";
        wb.DefineName("RATE", "$B$1");
        var s2 = wb.GetNames();
        wb.DefineName("Total", "Data!$A$1:$B$2");
        var s3 = wb.GetNames();
        wb.DeleteName("Rate");
        var s4 = wb.GetNames();
        Assert.Equal([P("rate", "Data!$B$1")], s1);
        Assert.Equal([P("RATE", "'My Sheet'!$B$1")], s2);
        Assert.Equal([P("RATE", "'My Sheet'!$B$1"), P("Total", "Data!$A$1:$B$2")], s3);
        Assert.Equal([P("Total", "Data!$A$1:$B$2")], s4);
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("Sheet1!A1"));

        Assert.True(wb.Undo());
        Assert.Equal(s3, wb.GetNames());
        Assert.Equal(N(50), wb.GetValue("Sheet1!A1"));
        Assert.True(wb.Undo());
        Assert.Equal(s2, wb.GetNames());
        Assert.True(wb.Undo());
        Assert.Equal(s1, wb.GetNames());
        Assert.Equal(N(30), wb.GetValue("Sheet1!A1"));
        Assert.True(wb.Undo());
        Assert.Equal(s0, wb.GetNames());
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("Sheet1!A1"));
        Assert.Equal("My Sheet", wb.ActiveSheet); // undo of a name never changes the active sheet (R80)

        Assert.True(wb.Redo());
        Assert.Equal(s1, wb.GetNames());
        Assert.Equal(N(30), wb.GetValue("Sheet1!A1"));
        Assert.True(wb.Redo());
        Assert.Equal(s2, wb.GetNames());
        Assert.Equal(N(50), wb.GetValue("Sheet1!A1"));
        Assert.True(wb.Redo());
        Assert.True(wb.Redo());
        Assert.Equal(s4, wb.GetNames());
        Assert.Equal("=rate*10", wb.GetInput("Sheet1!A1")); // name operations never rewrite inputs (R60)
    }

    // ---- Atomicity (§9.2) ----

    [Fact]
    public void ThrowingEvaluation_DuringDefineNameIntoAnotherSheet_LeavesEverythingUnchanged()
    {
        var functions = new ArmableResolver();
        var wb = Workbook.FromCells([P("B1", "5"), P("A1", "=BOOM(Rate)"), P("A2", "=Rate*2")], functions);
        wb.AddSheet("Data");
        wb.SetCell("Data!B1", "9");
        wb.DefineName("Rate", "$B$1");
        var log = new EventLog(wb);

        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.DefineName("Rate", "Data!$B$1"));
        Assert.Throws<InvalidOperationException>(() => wb.Undo());
        functions.Armed = false;

        Assert.Equal([P("Rate", "Sheet1!$B$1")], wb.GetNames());
        Assert.Equal(N(5), wb.GetValue("A1"));
        Assert.Equal(N(10), wb.GetValue("A2"));
        Assert.Empty(log.Events);
        // The graph's binding rolled back too: Sheet1!B1 drives the dependents, Data!B1 does not.
        wb.SetCell("Data!B1", "8");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("B1", "6");
        Assert.Equal(N(12), wb.GetValue("A2"));
    }

    // ---- Structure edits adjust names pointing into the edited sheet (§9.5, R84) ----

    [Fact]
    public void DeleteRows_OnTheActiveSheet_AdjustsOnlyItsNames_AndUndoRestoresThem()
    {
        var wb = Book("Data");
        wb.DefineName("Low", "Data!$A$5:$B$6");
        wb.DefineName("Gone", "Data!$A$2");
        wb.DefineName("Here", "Sheet1!$A$5");
        wb.SetCell("A1", "=Gone");
        var before = wb.GetNames();
        wb.ActiveSheet = "Data";
        wb.DeleteRows(2, 2);
        Assert.Equal([P("Gone", "#REF!"), P("Here", "Sheet1!$A$5"), P("Low", "Data!$A$3:$B$4")], wb.GetNames());
        Assert.Equal(E(ErrorCodes.Ref), wb.GetValue("Sheet1!A1"));
        Assert.True(wb.Undo());
        Assert.Equal(before, wb.GetNames());
        Assert.Equal(N(0), wb.GetValue("Sheet1!A1"));
        Assert.True(wb.Redo());
        Assert.Equal([P("Gone", "#REF!"), P("Here", "Sheet1!$A$5"), P("Low", "Data!$A$3:$B$4")], wb.GetNames());
    }

    [Fact]
    public void InsertColumns_KeepsQuotedPrefixes()
    {
        var wb = Book("My Sheet");
        wb.ActiveSheet = "My Sheet";
        wb.DefineName("Rate", "$B$1:$C$2");
        wb.InsertColumns(1, 2);
        Assert.Equal([P("Rate", "'My Sheet'!$D$1:$E$2")], wb.GetNames());
    }

    // ---- Loader entry (CR1.5 migration, R77) ----

    [Fact]
    public void FromSheets_QualifiesUnqualifiedNamesWithTheFirstSheet_AndChecksQualifiedOnes()
    {
        var wb = Workbook.FromSheets(
            [new LoadedSheet("Data", [P("B1", "4")]), new LoadedSheet("My Sheet", [P("A1", "=Rate*2"), P("A2", "=Far")])],
            "My Sheet",
            [P("Rate", "$b$1"), P("Far", "'my sheet'!$c$10:$a$1"), P("Gone", "#REF!")]);
        Assert.Equal([P("Far", "'My Sheet'!$A$1:$C$10"), P("Gone", "#REF!"), P("Rate", "Data!$B$1")], wb.GetNames());
        Assert.Equal(N(8), wb.GetValue("'My Sheet'!A1"));
        Assert.False(wb.CanUndo);

        Assert.Throws<ArgumentException>(() => Workbook.FromSheets(
            [new LoadedSheet("Data", [])], "Data", [P("Rate", "Nope!$A$1")]));
        Assert.Throws<ArgumentException>(() => Workbook.FromSheets(
            [new LoadedSheet("Data", [])], "Data", [P("Rate", "Data!A1")]));
        Assert.Throws<ArgumentException>(() => Workbook.FromSheets(
            [new LoadedSheet("Data", [])], "Data", [P("Rate", "#ref!")]));
    }

    /// <summary>CR1.5, R86, D-56: a version 1 file holds only unqualified targets and a version 2 file only qualified
    /// ones; the exact #REF! is accepted in both, and a malformed value is still reported as invalid.</summary>
    [Theory]
    [InlineData("Unqualified", "$B$1", true)]
    [InlineData("Unqualified", "$A$1:$B$2", true)]
    [InlineData("Unqualified", "#REF!", true)]
    [InlineData("Unqualified", "Data!$B$1", false)]
    [InlineData("Unqualified", "'Data'!$B$1", false)]
    [InlineData("Unqualified", "My Sheet!$B$1", false)]
    [InlineData("Qualified", "Data!$B$1", true)]
    [InlineData("Qualified", "data!$b$1", true)]
    [InlineData("Qualified", "#REF!", true)]
    [InlineData("Qualified", "$B$1", false)]
    [InlineData("Qualified", "$A$1:$B$2", false)]
    [InlineData("Qualified", "Nope!$B$1", false)]
    [InlineData("Qualified", "Data!B1", false)]
    [InlineData("Any", "$B$1", true)]
    [InlineData("Any", "Data!$B$1", true)]
    public void FromSheets_NameForm_RestrictsTheWrittenForm(string form, string refersTo, bool accepted)
    {
        Workbook Build() => Workbook.FromSheets([new LoadedSheet("Data", [P("B1", "4")])], "Data", [P("Rate", refersTo)], null, Enum.Parse<LoadedNameForm>(form));
        if (accepted)
        {
            var wb = Build();
            Assert.Equal(refersTo == "#REF!" ? "#REF!" : "Data!" + (refersTo.Contains(':') ? "$A$1:$B$2" : "$B$1"), wb.GetNames()[0].Value);
        }
        else
        {
            var ex = Assert.Throws<ArgumentException>(Build);
            Assert.Equal("names", ex.ParamName);
        }
    }

    [Fact]
    public void FromSheets_NullRefersTo_MessageSaysNull()
    {
        var ex = Assert.Throws<ArgumentException>(() => Workbook.FromSheets(
            [new LoadedSheet("Data", [])], "Data", [new KeyValuePair<string, string>("Rate", null!)]));
        Assert.Equal("The refersTo of name 'Rate' is null.", ex.Message.Split(" (Parameter")[0]);
    }
}
