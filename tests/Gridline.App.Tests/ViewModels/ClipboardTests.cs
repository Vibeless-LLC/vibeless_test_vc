using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.App.Tests.ViewModels;

/// <summary>§11.6 clipboard with a fake clipboard: R46 copy text, R47 internal vs external paste, §8.5 external paste as
/// one ApplyBatch, and the failure cases that change nothing.</summary>
public class ClipboardTests
{
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeClipboardService _clipboard = new();
    private readonly MainViewModel _vm;

    public ClipboardTests() => _vm = new MainViewModel(_dialogs, _clipboard);

    private void Select(string from, string to)
    {
        _vm.SelectCell(CellAddress.Parse(from), extend: false);
        _vm.SelectCell(CellAddress.Parse(to), extend: true);
    }

    private void Go(string cell) => _vm.SelectCell(CellAddress.Parse(cell), extend: false);

    private int UndoSteps()
    {
        int steps = 0;
        while (_vm.Workbook.CanUndo) { _vm.Workbook.Undo(); steps++; }
        for (int i = 0; i < steps; i++) _vm.Workbook.Redo();
        return steps;
    }

    // ---- Copy text (R46) ----

    [Fact]
    public void Copy_PutsDisplayTextAsTsv_TabBetweenFields_CrLfAfterEveryRow()
    {
        _vm.Workbook.ApplyBatch([
            new("A1", "1"), new("B1", "=1/3"), new("C1", "text"),
            new("A2", "TRUE"), new("C2", "=1/0"),
            new("B3", "12345678901"),
        ]);
        Select("A1", "C3");

        Assert.True(_vm.Copy());

        Assert.Equal("1\t0.3333333333\ttext\r\nTRUE\t\t#DIV/0!\r\n\t1.23456789E+10\t\r\n", _clipboard.Text);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void Copy_SingleBlankCell_IsOneEmptyRow()
    {
        Assert.True(_vm.Copy());
        Assert.Equal("\r\n", _clipboard.Text);
    }

    [Fact]
    public void Copy_SelectionCornersInAnyOrder_ReadsTheRectangle()
    {
        _vm.Workbook.ApplyBatch([new("B2", "x"), new("C3", "y")]);
        Select("C3", "B2"); // anchor bottom-right
        _vm.Copy();
        Assert.Equal("x\t\r\n\ty\r\n", _clipboard.Text);
    }

    [Fact]
    public void Copy_FieldsWithTabsLineBreaksOrALeadingQuote_AreQuoted_AndParseBack()
    {
        _vm.Workbook.ApplyBatch([new("A1", "a\tb"), new("B1", "two\nlines"), new("C1", "\"q\" x"), new("D1", "5\"")]);
        Select("A1", "D1");
        _vm.Copy();
        Assert.Equal("\"a\tb\"\t\"two\nlines\"\t\"\"\"q\"\" x\"\t5\"\r\n", _clipboard.Text);
        var parsed = Gridline.IO.DelimitedTextParser.Parse(_clipboard.Text!, '\t');
        Assert.Equal(["a\tb", "two\nlines", "\"q\" x", "5\""], parsed[0]);
    }

    [Fact]
    public void Copy_LargeSelection_ReadsOnlyUsedCells_UpToTheLimit()
    {
        _vm.Workbook.ApplyBatch([new("A1", "7"), new("J100000", "9")]);
        Select("A1", "J100000"); // exactly 1,000,000 cells
        Assert.True(_vm.Copy());
        string text = _clipboard.Text!;
        Assert.StartsWith("7\t\t\t\t\t\t\t\t\t\r\n", text);
        Assert.EndsWith("\t\t\t\t\t\t\t\t\t9\r\n", text);
        Assert.Equal(100000, text.Split("\r\n").Length - 1);
    }

    [Fact]
    public void Copy_OverTheLimit_ShowsAMessage_AndKeepsTheClipboard()
    {
        _clipboard.Text = "before";
        Select("A1", "ZZ100000"); // the whole sheet
        Assert.False(_vm.Copy());
        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("too large", error.Message);
        Assert.Equal("before", _clipboard.Text);
        Assert.Equal(0, _clipboard.Sets);
    }

    [Fact]
    public void Copy_ClipboardLocked_ShowsAnError()
    {
        _clipboard.Locked = true;
        Assert.False(_vm.Copy());
        Assert.Single(_dialogs.Errors);
    }

    // ---- Internal paste (R47, §9.4) ----

    [Fact]
    public void Paste_OfTheTextThisAppCopied_IsInternal_ShiftsReferences_OneUndoStep()
    {
        _vm.Workbook.ApplyBatch([new("A1", "1"), new("A2", "2"), new("B1", "=A1+$A$1"), new("B2", "=A2*2")]);
        Select("B1", "B2");
        _vm.Copy();
        Go("D5");
        int before = UndoSteps();

        Assert.True(_vm.IsInternalPaste(_clipboard.Text));
        Assert.True(_vm.Paste());

        Assert.Equal("=C5+$A$1", _vm.Workbook.GetInput("D5")); // the formula, not its value (§9.4)
        Assert.Equal("=C6*2", _vm.Workbook.GetInput("D6"));
        Assert.Equal(before + 1, UndoSteps());
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void Paste_Internal_BlankSourceCellsClearTheDestination()
    {
        _vm.Workbook.ApplyBatch([new("A1", "x"), new("D2", "old")]);
        Select("A1", "A2");
        _vm.Copy();
        Go("D1");
        _vm.Paste();
        Assert.Equal("x", _vm.Workbook.GetInput("D1"));
        Assert.Equal("", _vm.Workbook.GetInput("D2"));
    }

    [Fact]
    public void Paste_Internal_BeyondTheGrid_ShowsAMessage_AndChangesNothing()
    {
        _vm.Workbook.ApplyBatch([new("A1", "1"), new("B1", "2")]);
        Select("A1", "B1");
        _vm.Copy();
        Go("ZZ1");
        int before = UndoSteps();
        Assert.False(_vm.Paste());
        Assert.Single(_dialogs.Errors);
        Assert.Equal("", _vm.Workbook.GetInput("ZZ1"));
        Assert.Equal(before, UndoSteps());
    }

    [Fact]
    public void Paste_AfterTheClipboardChanged_IsExternal()
    {
        _vm.Workbook.SetCell("A1", "=1+1");
        _vm.Copy();
        _clipboard.Text = "from another app";
        Go("B1");
        Assert.False(_vm.IsInternalPaste(_clipboard.Text));
        _vm.Paste();
        Assert.Equal("from another app", _vm.Workbook.GetInput("B1"));
    }

    [Fact]
    public void Paste_OfCopiedTextAfterNew_IsExternal_ValuesOnly()
    {
        _vm.Workbook.SetCell("A1", "=1+1");
        _vm.Copy();
        _vm.Workbook.MarkSaved();
        _vm.New();
        Assert.False(_vm.IsInternalPaste(_clipboard.Text));
        _vm.Paste();
        Assert.Equal("2", _vm.Workbook.GetInput("A1")); // the displayed value, interpreted by §4.3
    }

    // ---- External paste (§8.5, Tab) ----

    [Fact]
    public void Paste_External_WritesAtTheActiveCell_AsOneApplyBatch_WithTheImportRules()
    {
        _vm.Workbook.SetCell("C3", "will be cleared");
        _clipboard.Text = "-5\t5%\ttrue\r\n=SUM(A1)\t'x\t\nplain\n";
        Go("B2");
        int before = UndoSteps();
        int changes = 0;
        _vm.Workbook.CellsChanged += (_, _) => changes++;

        Assert.True(_vm.Paste());

        Assert.Equal(1, changes);
        Assert.Equal(before + 1, UndoSteps());
        Assert.Equal("N:-5", _vm.Workbook.GetValue("B2").ToCanonicalString());
        Assert.Equal("N:0.05", _vm.Workbook.GetValue("C2").ToCanonicalString());
        Assert.Equal("B:TRUE", _vm.Workbook.GetValue("D2").ToCanonicalString());
        Assert.Equal("'=SUM(A1)", _vm.Workbook.GetInput("B3")); // no formula injection
        Assert.Equal("''x", _vm.Workbook.GetInput("C3"));
        Assert.Equal("", _vm.Workbook.GetInput("D3")); // empty field: blank
        Assert.Equal("plain", _vm.Workbook.GetInput("B4"));
        Assert.Equal("", _vm.Workbook.GetInput("B5")); // the final terminator adds no row
    }

    [Fact]
    public void Paste_External_QuotedFields()
    {
        _clipboard.Text = "\"a\tb\"\t\"say \"\"hi\"\"\"\r\n";
        _vm.Paste();
        Assert.Equal("a\tb", _vm.Workbook.GetInput("A1"));
        Assert.Equal("say \"hi\"", _vm.Workbook.GetInput("B1"));
    }

    [Fact]
    public void Paste_External_ParseError_ShowsAMessage_AndChangesNothing()
    {
        _vm.Workbook.SetCell("A1", "keep");
        _clipboard.Text = "x\t\"unterminated";
        int before = UndoSteps();
        Assert.False(_vm.Paste());
        Assert.Single(_dialogs.Errors);
        Assert.Equal("keep", _vm.Workbook.GetInput("A1"));
        Assert.Equal(before, UndoSteps());
    }

    [Fact]
    public void Paste_External_BeyondTheGrid_ShowsAMessage_AndChangesNothing()
    {
        _clipboard.Text = "a\r\nb\r\n";
        Go("A100000");
        Assert.False(_vm.Paste());
        Assert.Single(_dialogs.Errors);
        Assert.Equal("", _vm.Workbook.GetInput("A100000"));
        Assert.False(_vm.Workbook.CanUndo);
    }

    [Fact]
    public void Paste_External_InputTooLong_ShowsAMessage_AndChangesNothing()
    {
        _clipboard.Text = "ok\t" + new string('x', Workbook.MaxInputLength + 1);
        Assert.False(_vm.Paste());
        Assert.Single(_dialogs.Errors);
        Assert.Equal("", _vm.Workbook.GetInput("A1"));
    }

    [Fact]
    public void Paste_EmptyOrNoText_DoesNothing_LockedClipboardShowsAnError()
    {
        _clipboard.Text = null;
        Assert.False(_vm.Paste());
        _clipboard.Text = "";
        Assert.False(_vm.Paste());
        Assert.Empty(_dialogs.Errors);
        Assert.False(_vm.Workbook.CanUndo);

        _clipboard.Locked = true;
        Assert.False(_vm.Paste());
        Assert.Single(_dialogs.Errors);
    }

    [Fact]
    public void CopyAndPaste_WhileEditing_DoNothing()
    {
        _clipboard.Text = "x";
        _vm.BeginEdit("pending");
        Assert.False(_vm.Copy());
        Assert.False(_vm.Paste());
        Assert.Equal("x", _clipboard.Text);
        Assert.Equal("", _vm.Workbook.GetInput("A1"));
        Assert.True(_vm.IsEditing);
    }

    [Fact]
    public void Paste_MakesTheTitleDirty()
    {
        _clipboard.Text = "1";
        _vm.Paste();
        Assert.Equal("Gridline - Untitled *", _vm.Title);
    }
}
