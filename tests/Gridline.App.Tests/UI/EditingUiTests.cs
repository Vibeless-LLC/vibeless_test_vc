using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using static Gridline.App.Tests.UI.AppUnderTest;
using static Gridline.App.Tests.UI.UiInput;

namespace Gridline.App.Tests.UI;

/// <summary>FlaUI acceptance for §11.4 editing, the name box, the formula bar and R49. Each test closes the app in Dispose.</summary>
[Collection(UiCollection.Name)]
[Trait("Category", "UI")]
public class EditingUiTests
{
    private sealed record Ui(Window Window, AutomationElement Grid, TextBox NameBox, TextBox FormulaBar, AutomationElement SelectionValue, AutomationElement SelectionSum);

    private static Ui Start(AppUnderTest app)
    {
        Window window = app.MainWindow();
        var ui = new Ui(window, Find(window, "Grid"), Find(window, "NameBox").AsTextBox(), Find(window, "FormulaBar").AsTextBox(),
            Find(window, "SelectionValue"), Find(window, "SelectionSum"));
        window.SetForeground();
        Retry(() => ui.Grid.Properties.HasKeyboardFocus.Value, "grid has keyboard focus at launch");
        Activate(window, ui.Grid);
        return ui;
    }

    private static void Key(VirtualKeyShort key) => Keyboard.Type(key);

    private static TextBox Editor(Ui ui) => Find(ui.Grid, "CellEditor").AsTextBox();

    private static void InvokeMenu(Window window, string menuId, string itemId)
    {
        var menu = Find(window, menuId).AsMenuItem();
        menu.Expand();
        Find(menu, itemId).AsMenuItem().Invoke();
    }

    [Fact]
    public void TypeEnterTab_F2_Esc_CommitAndCancelThroughTheGrid()
    {
        using var app = Launch();
        Ui ui = Start(app);

        // Typing starts an edit with that character; Enter commits and moves down.
        Keyboard.Type("42");
        WaitFor(() => ui.FormulaBar.Text, "42"); // the formula bar shows the pending text while editing
        Assert.Equal("BLANK", ui.SelectionValue.Name); // not committed yet
        Key(VirtualKeyShort.RETURN);
        WaitFor(() => ui.NameBox.Text, "A2");
        Assert.Equal("", ui.FormulaBar.Text);
        Press(VirtualKeyShort.UP);
        WaitFor(() => ui.NameBox.Text, "A1");
        WaitFor(() => ui.SelectionValue.Name, "N:42");
        Assert.Equal("42", ui.FormulaBar.Text);

        // Tab commits and moves right.
        Press(VirtualKeyShort.RIGHT);
        WaitFor(() => ui.NameBox.Text, "B1");
        Keyboard.Type("=A1*2");
        Key(VirtualKeyShort.TAB);
        WaitFor(() => ui.NameBox.Text, "C1");
        Press(VirtualKeyShort.LEFT);
        WaitFor(() => ui.NameBox.Text, "B1");
        WaitFor(() => ui.FormulaBar.Text, "=A1*2");
        WaitFor(() => ui.SelectionValue.Name, "N:84");

        // F2 edits the existing input (caret at the end); Esc cancels.
        Key(VirtualKeyShort.F2);
        WaitFor(() => Editor(ui).Text, "=A1*2");
        Keyboard.Type("+1");
        WaitFor(() => ui.FormulaBar.Text, "=A1*2+1");
        Key(VirtualKeyShort.ESCAPE);
        WaitFor(() => ui.FormulaBar.Text, "=A1*2");
        Assert.Equal("N:84", ui.SelectionValue.Name);
        Assert.Equal("B1", ui.NameBox.Text);

        // F2 and Enter commit the change.
        Key(VirtualKeyShort.F2);
        WaitFor(() => Editor(ui).Text, "=A1*2");
        Keyboard.Type("+1");
        Key(VirtualKeyShort.RETURN);
        WaitFor(() => ui.NameBox.Text, "B2");
        Press(VirtualKeyShort.UP);
        WaitFor(() => ui.SelectionValue.Name, "N:85");
        Assert.Equal("=A1*2+1", ui.FormulaBar.Text);
        Assert.Equal("Gridline - Untitled *", ui.Window.Title);
    }

    [Fact]
    public void Delete_ThenMenuUndo_RestoresTheSelection_AndMenuUndoWhileEditingActsOnTheEditor()
    {
        using var app = Launch();
        Ui ui = Start(app);

        Keyboard.Type("1");
        Key(VirtualKeyShort.RETURN);
        Keyboard.Type("2");
        Key(VirtualKeyShort.RETURN);
        WaitFor(() => ui.NameBox.Text, "A3");
        Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
        Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.DOWN);
        WaitFor(() => ui.SelectionSum.Name, "3");

        Press(VirtualKeyShort.DELETE);
        WaitFor(() => ui.SelectionSum.Name, "0");
        Assert.Equal("BLANK", ui.SelectionValue.Name);

        InvokeMenu(ui.Window, "MenuEdit", "MenuUndo"); // the whole Delete is one undo step
        WaitFor(() => ui.SelectionSum.Name, "3");
        WaitFor(() => ui.SelectionValue.Name, "N:2"); // A2 is the active end of A1:A2

        // R49: while an edit is open, menu Undo undoes typing in the editor, not the workbook.
        Activate(ui.Window, ui.Grid);
        Press(VirtualKeyShort.DOWN); // A3, collapsing the selection
        WaitFor(() => ui.NameBox.Text, "A3");
        Keyboard.Type("ab");
        WaitFor(() => ui.FormulaBar.Text, "ab");
        InvokeMenu(ui.Window, "MenuEdit", "MenuUndo");
        WaitFor(() => ui.FormulaBar.Text, "a");
        Assert.Equal("a", Editor(ui).Text);
        Assert.Equal("BLANK", ui.SelectionValue.Name);
        Activate(ui.Window, Find(ui.Grid, "CellEditor")); // the menu gave focus back to the editor
        Press(VirtualKeyShort.UP); // still editing: the editor keeps the key, nothing moves
        Assert.Equal("A3", ui.NameBox.Text);
        Key(VirtualKeyShort.ESCAPE);
        WaitFor(() => ui.FormulaBar.Text, "");
        // Once the edit is closed, menu Undo acts on the workbook again: the next step back is the "2" typed into A2.
        InvokeMenu(ui.Window, "MenuEdit", "MenuUndo");
        Activate(ui.Window, ui.Grid);
        Press(VirtualKeyShort.UP);
        WaitFor(() => ui.NameBox.Text, "A2");
        WaitFor(() => ui.SelectionValue.Name, "BLANK");
    }

    [Fact]
    public void NameBox_NavigatesAndReverts_FormulaBar_CommitsAndReverts()
    {
        using var app = Launch();
        Ui ui = Start(app);
        var scroll = Find(ui.Window, "GridScroller").Patterns.Scroll.Pattern;

        // $ markers and lower case are accepted; the box shows the canonical address and the grid scrolls there.
        ui.NameBox.Focus();
        ui.NameBox.Text = "$c$5000";
        Key(VirtualKeyShort.RETURN);
        WaitFor(() => ui.NameBox.Text, "C5000");
        Retry(() => ui.Grid.Properties.HasKeyboardFocus.Value, "grid has focus after navigating");
        Retry(() => scroll.VerticalScrollPercent.Value > 0, "the grid scrolled to row 5000");
        Keyboard.Type("5");
        Key(VirtualKeyShort.RETURN);
        WaitFor(() => ui.NameBox.Text, "C5001");

        // An invalid address reverts the box.
        ui.NameBox.Focus();
        ui.NameBox.Text = "nope";
        Key(VirtualKeyShort.RETURN);
        WaitFor(() => ui.NameBox.Text, "C5001");

        ui.NameBox.Text = "c5000";
        Key(VirtualKeyShort.RETURN);
        WaitFor(() => ui.NameBox.Text, "C5000");
        WaitFor(() => ui.FormulaBar.Text, "5");
        Assert.Equal("N:5", ui.SelectionValue.Name);

        // Formula bar: Enter commits (the active cell stays), Esc reverts.
        ui.FormulaBar.Focus();
        ui.FormulaBar.Text = "hello";
        Key(VirtualKeyShort.RETURN);
        WaitFor(() => ui.SelectionValue.Name, "T:hello");
        Assert.Equal("C5000", ui.NameBox.Text);
        ui.FormulaBar.Focus();
        ui.FormulaBar.Text = "discard me";
        Key(VirtualKeyShort.ESCAPE);
        WaitFor(() => ui.FormulaBar.Text, "hello");
        Assert.Equal("T:hello", ui.SelectionValue.Name);
    }
}
