using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using static Gridline.App.Tests.UI.AppUnderTest;
using static Gridline.App.Tests.UI.UiInput;

namespace Gridline.App.Tests.UI;

/// <summary>FlaUI acceptance for §11.5 file commands (MenuSave through the Win32 Save dialog, the §11.1 asterisk, the R45
/// prompt on the close button and Exit) and §11.6 copy/paste of a formula. Each test closes the app in Dispose.</summary>
[Collection(UiCollection.Name)]
[Trait("Category", "UI")]
public sealed class FileClipboardUiTests : IDisposable
{
    private readonly string _dir = TestSupport.NewTempDirectory();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* the app may still be closing */ }
    }

    private sealed record Ui(Window Window, AutomationElement Grid, TextBox NameBox, TextBox FormulaBar, AutomationElement SelectionValue);

    private static Ui Start(AppUnderTest app)
    {
        Window window = app.MainWindow();
        var ui = new Ui(window, Find(window, "Grid"), Find(window, "NameBox").AsTextBox(), Find(window, "FormulaBar").AsTextBox(),
            Find(window, "SelectionValue"));
        window.SetForeground();
        Retry(() => ui.Grid.Properties.HasKeyboardFocus.Value, "grid has keyboard focus at launch");
        Activate(window, ui.Grid);
        return ui;
    }

    /// <summary>Opens a menu and clicks one of its items with the mouse. A click returns at once even when the item opens
    /// a modal dialog (a UIA Invoke could wait for the dialog).</summary>
    private static void ClickMenu(Window window, string menuId, string itemId)
    {
        RaiseToTop(window);
        var menu = Find(window, menuId).AsMenuItem();
        menu.Click();
        Find(window, itemId).AsMenuItem().Click();
    }

    private static void Ctrl(VirtualKeyShort key) => Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, key);

    private static AutomationElement WaitForPrompt(AppUnderTest app)
    {
        AutomationElement? prompt = null;
        Retry(() => (prompt = app.FindPrompt()) is not null, "the unsaved-changes prompt is shown");
        return prompt!;
    }

    [Fact]
    public void MenuSave_ThroughTheSaveDialog_WritesTheFile_AndTheAsteriskFollowsTheDirtyState()
    {
        using var app = Launch();
        Ui ui = Start(app);
        Assert.Equal("Gridline - Untitled", ui.Window.Title);

        Keyboard.Type("=6*7");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Retry(() => ui.Window.Title == "Gridline - Untitled *", "the asterisk appears after an edit");

        // Save on an Untitled workbook acts as Save As: the Win32 dialog, file name box AutomationId 1001, Enter confirms.
        string path = Path.Combine(_dir, "ui-save.gridline");
        ClickMenu(ui.Window, "MenuFile", "MenuSave");
        Window? dialog = null;
        Retry(() => (dialog = app.Application.GetAllTopLevelWindows(app.Automation).FirstOrDefault(w => w.Title == "Save As")
                     ?? ui.Window.ModalWindows.FirstOrDefault()) is not null, "the Save As dialog is shown");
        var fileName = Find(dialog!, "1001").AsTextBox();
        fileName.Text = path;
        Retry(() => fileName.Text == path, "the file name is entered");
        fileName.Focus();
        Keyboard.Type(VirtualKeyShort.RETURN);

        Retry(() => ui.Window.Title == "Gridline - ui-save.gridline", "the title shows the saved file without an asterisk");
        Retry(() => File.Exists(path), "the file was written");
        Assert.Contains("\"=6*7\"", File.ReadAllText(path));

        // An edit brings the asterisk back; undoing it returns to the saved position, which is clean.
        Activate(ui.Window, ui.Grid);
        Keyboard.Type("1");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Retry(() => ui.Window.Title == "Gridline - ui-save.gridline *", "the asterisk reappears");
        ClickMenu(ui.Window, "MenuEdit", "MenuUndo");
        Retry(() => ui.Window.Title == "Gridline - ui-save.gridline", "undo to the saved position removes the asterisk");

        // Ctrl+S on a named file saves without a dialog.
        Activate(ui.Window, ui.Grid);
        Keyboard.Type("2");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Retry(() => ui.Window.Title.EndsWith(" *", StringComparison.Ordinal), "dirty again");
        Ctrl(VirtualKeyShort.KEY_S);
        Retry(() => ui.Window.Title == "Gridline - ui-save.gridline", "Ctrl+S saved");
        Assert.Contains("[\"A3\", \"2\"]", File.ReadAllText(path)); // A2 was undone; "2" went to A3
    }

    [Fact]
    public void CopyPaste_AFormula_ShiftsItsReferences()
    {
        using var app = Launch();
        Ui ui = Start(app);

        Keyboard.Type("1");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Keyboard.Type("2");
        Keyboard.Type(VirtualKeyShort.RETURN); // A1=1, A2=2; active A3
        Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
        Press(VirtualKeyShort.RIGHT);
        WaitFor(() => ui.NameBox.Text, "B1");
        Keyboard.Type("=A1*10+$A$1");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Press(VirtualKeyShort.UP);
        WaitFor(() => ui.NameBox.Text, "B1");
        WaitFor(() => ui.SelectionValue.Name, "N:11");

        Ctrl(VirtualKeyShort.KEY_C);
        Press(VirtualKeyShort.DOWN);
        WaitFor(() => ui.NameBox.Text, "B2");
        Ctrl(VirtualKeyShort.KEY_V);

        WaitFor(() => ui.FormulaBar.Text, "=A2*10+$A$1"); // relative part shifted, absolute kept (§9.4)
        WaitFor(() => ui.SelectionValue.Name, "N:21");

        // The menu items do the same.
        Press(VirtualKeyShort.RIGHT);
        WaitFor(() => ui.NameBox.Text, "C2");
        ClickMenu(ui.Window, "MenuEdit", "MenuPaste");
        WaitFor(() => ui.FormulaBar.Text, "=B2*10+$A$1");
        WaitFor(() => ui.SelectionValue.Name, "N:211");
    }

    [Fact]
    public void CloseButton_OnADirtyWorkbook_Prompts_CancelKeepsItOpen_ExitDontSaveCloses()
    {
        using var app = Launch();
        Ui ui = Start(app);
        Keyboard.Type("x");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Retry(() => ui.Window.Title == "Gridline - Untitled *", "dirty");

        using (var process = app.Process) process.CloseMainWindow(); // WM_CLOSE, as the close button sends
        var prompt = WaitForPrompt(app);
        Assert.Equal("Save", Find(prompt, "PromptSave").Name);
        Assert.Equal("Don't Save", Find(prompt, "PromptDontSave").Name);
        Assert.Equal("Cancel", Find(prompt, "PromptCancel").Name);
        Find(prompt, "PromptCancel").AsButton().Click();
        Retry(() => app.FindPrompt() is null, "the prompt closed");
        Assert.False(app.Application.HasExited);
        Assert.Equal("Gridline - Untitled *", ui.Window.Title);

        ClickMenu(ui.Window, "MenuFile", "MenuExit");
        prompt = WaitForPrompt(app);
        Find(prompt, "PromptDontSave").AsButton().Click();
        Retry(() => app.Application.HasExited, "Don't Save lets Exit close the app");
    }
}
