using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Gridline.Core;
using Gridline.IO;
using static Gridline.App.Tests.UI.AppUnderTest;
using static Gridline.App.Tests.UI.UiInput;

namespace Gridline.App.Tests.UI;

/// <summary>
/// Phase 3 acceptance (§11.6, §11.7, §13): the phase story end to end on a 100k-cell file (edit, paste, MenuUndo,
/// MenuRedo, MenuSave, the title asterisk), then an edit, an autosave, a killed process, a restart with the RecoveryDialog
/// and the restored edit; and MenuOpen through the Win32 Open dialog. With the other UI classes every §11.7 AutomationId is
/// exercised. Autosave runs every 500 ms here (R51) and LOCALAPPDATA is a temp directory.
/// </summary>
[Collection(UiCollection.Name)]
[Trait("Category", "UI")]
public sealed class PhaseStoryUiTests : IDisposable
{
    private const int AutosaveMs = 500;
    private readonly string _dir = TestSupport.NewTempDirectory();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the app may still be closing */ }
    }

    private sealed record Ui(Window Window, AutomationElement Grid, TextBox NameBox, TextBox FormulaBar, AutomationElement SelectionValue, AutomationElement SelectionSum);

    private static Ui Start(AppUnderTest app, string expectedTitle)
    {
        Window window = app.WaitForWindow(t => t == expectedTitle);
        var ui = new Ui(window, Find(window, "Grid"), Find(window, "NameBox").AsTextBox(), Find(window, "FormulaBar").AsTextBox(),
            Find(window, "SelectionValue"), Find(window, "SelectionSum"));
        window.SetForeground();
        Retry(() => ui.Grid.Properties.HasKeyboardFocus.Value, "grid has keyboard focus at launch");
        Activate(window, ui.Grid);
        return ui;
    }

    private static void GoTo(Ui ui, string address)
    {
        ui.NameBox.Focus();
        ui.NameBox.Text = address;
        Keyboard.Type(VirtualKeyShort.RETURN);
        WaitFor(() => ui.NameBox.Text, address);
        Retry(() => ui.Grid.Properties.HasKeyboardFocus.Value, "grid has focus after the name box");
    }

    /// <summary>The session guids of recovery files whose C2 holds <paramref name="input"/>.</summary>
    private static List<Guid> RecoveryFilesWith(string recoveryDir, string address, string input)
    {
        var found = new List<Guid>();
        if (!Directory.Exists(recoveryDir)) return found;
        foreach (string path in Directory.GetFiles(recoveryDir, "*.gridline"))
        {
            try
            {
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out Guid id)
                    && GridlineFile.LoadFromPath(path).GetInput(address) == input)
                    found.Add(id);
            }
            catch (Exception ex) when (ex is GridlineIOException or GridlineFormatException or IOException) { /* being replaced */ }
        }
        return found;
    }

    private static string[] RecoveryFiles(string recoveryDir) =>
        Directory.Exists(recoveryDir) ? Directory.GetFiles(recoveryDir, "*.gridline") : [];

    [Fact]
    public void PhaseStory_EditPasteUndoRedoSave_ThenAutosaveKillRestartRestore()
    {
        string path = Path.Combine(_dir, "story.gridline");
        Fixtures.Write100kCells(path);
        string localAppData = Path.Combine(_dir, "LocalAppData");
        Directory.CreateDirectory(localAppData);
        Guid session;

        using (var app = LaunchWith(localAppData, AutosaveMs, path))
        {
            Ui ui = Start(app, "Gridline - story.gridline");
            WaitFor(() => ui.SelectionValue.Name, "N:1");

            // Edit by typing: B1 = A1*10 + $A$1 = 11. The title gains its asterisk.
            Press(VirtualKeyShort.RIGHT);
            WaitFor(() => ui.NameBox.Text, "B1");
            Keyboard.Type("=A1*10+$A$1");
            Keyboard.Type(VirtualKeyShort.RETURN);
            Retry(() => ui.Window.Title == "Gridline - story.gridline *", "the asterisk appears after the edit");
            Press(VirtualKeyShort.UP);
            WaitFor(() => ui.SelectionValue.Name, "N:11");

            // Paste: Ctrl+C on B1, Ctrl+V on B2 shifts the relative reference (§9.4).
            Ctrl(VirtualKeyShort.KEY_C);
            Press(VirtualKeyShort.DOWN);
            WaitFor(() => ui.NameBox.Text, "B2");
            Ctrl(VirtualKeyShort.KEY_V);
            WaitFor(() => ui.FormulaBar.Text, "=A2*10+$A$1");
            WaitFor(() => ui.SelectionValue.Name, "N:21");
            Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.UP); // B1:B2
            WaitFor(() => ui.SelectionSum.Name, "32");
            Press(VirtualKeyShort.DOWN);
            WaitFor(() => ui.NameBox.Text, "B2");

            // MenuUndo removes the paste, MenuRedo brings it back.
            ClickMenu(ui.Window, "MenuEdit", "MenuUndo");
            WaitFor(() => ui.SelectionValue.Name, "BLANK");
            WaitFor(() => ui.FormulaBar.Text, "");
            ClickMenu(ui.Window, "MenuEdit", "MenuRedo");
            WaitFor(() => ui.FormulaBar.Text, "=A2*10+$A$1");
            WaitFor(() => ui.SelectionValue.Name, "N:21");
            Assert.Equal("Gridline - story.gridline *", ui.Window.Title);

            // MenuSave writes the named file; the asterisk disappears and a successful Save leaves no recovery file.
            ClickMenu(ui.Window, "MenuFile", "MenuSave");
            Retry(() => ui.Window.Title == "Gridline - story.gridline", "the asterisk disappears after MenuSave");
            Workbook saved = GridlineFile.LoadFromPath(path);
            Assert.Equal("=A2*10+$A$1", saved.GetInput("B2"));
            Assert.Equal("=A1*10+$A$1", saved.GetInput("B1"));
            Assert.Equal("=A50000*2", saved.GetInput("A100000"));
            Retry(() => RecoveryFiles(app.RecoveryDirectory).Length == 0, "no recovery file once saved and clean");

            // An edit makes it dirty again; the autosave writes it to the recovery file.
            Activate(ui.Window, ui.Grid);
            Press(VirtualKeyShort.RIGHT);
            WaitFor(() => ui.NameBox.Text, "C2");
            Keyboard.Type("recover me");
            Keyboard.Type(VirtualKeyShort.RETURN);
            Retry(() => ui.Window.Title == "Gridline - story.gridline *", "dirty again");
            List<Guid> sessions = [];
            Retry(() => (sessions = RecoveryFilesWith(app.RecoveryDirectory, "C2", "recover me")).Count == 1, "an autosave wrote the edit");
            session = sessions[0];
            Assert.Equal("Gridline - story.gridline *", ui.Window.Title); // autosave never marks saved (R48)

            app.Kill(); // a crash: no prompt, no clean exit
            Assert.True(app.Application.HasExited);
        }
        string recoveryFile = Path.Combine(localAppData, "Gridline", "Recovery", session.ToString("D") + ".gridline");
        Assert.True(File.Exists(recoveryFile), "the recovery file survives the killed process");

        // Restart with the same environment and file argument: the RecoveryDialog comes first (D-40).
        using (var app = LaunchWith(localAppData, AutosaveMs, path))
        {
            AutomationElement? dialog = null;
            Retry(() => (dialog = app.FindRecoveryDialog()) is not null, "the RecoveryDialog is shown at startup");
            Assert.NotNull(Find(dialog!, "RecoveryList"));
            Assert.NotNull(Find(dialog!, "RecoveryDiscard_" + session.ToString("N")));
            Assert.NotNull(Find(dialog!, "RecoveryClose"));
            Assert.Contains(dialog!.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)),
                e => (e.Name ?? "").Contains(session.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Find(dialog!, "RecoveryRestore_" + session.ToString("N")).AsButton().Invoke();

            // Restored as an Untitled, dirty workbook with the edit (R50).
            Ui ui = Start(app, "Gridline - Untitled *");
            Assert.Null(app.FindRecoveryDialog());
            GoTo(ui, "C2");
            WaitFor(() => ui.FormulaBar.Text, "recover me");
            WaitFor(() => ui.SelectionValue.Name, "T:recover me");
            GoTo(ui, "B2");
            WaitFor(() => ui.FormulaBar.Text, "=A2*10+$A$1");
            WaitFor(() => ui.SelectionValue.Name, "N:21");
            GoTo(ui, "A100000");
            WaitFor(() => ui.SelectionValue.Name, "N:100000");
            Assert.True(File.Exists(recoveryFile), "the restored session keeps the adopted recovery file");
        } // Dispose: WM_CLOSE, Don't Save, a clean exit
        Retry(() => !File.Exists(recoveryFile), "a clean exit deletes the adopted recovery file");
    }

    [Fact]
    public void MenuOpen_CancelChangesNothing_ThenOpensAFileThroughTheOpenDialog()
    {
        string path = Path.Combine(_dir, "opened.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "=40+2")]), path);
        using var app = Launch();
        Ui ui = Start(app, "Gridline - Untitled");

        Window OpenDialog()
        {
            ClickMenu(ui.Window, "MenuFile", "MenuOpen");
            Window? dialog = null;
            Retry(() => (dialog = app.Application.GetAllTopLevelWindows(app.Automation).FirstOrDefault(w => w.Title == "Open")
                         ?? ui.Window.ModalWindows.FirstOrDefault()) is not null, "the Open dialog is shown");
            return dialog!;
        }

        // Cancel: nothing changes.
        Window first = OpenDialog();
        (first.FindFirstChild(cf => cf.ByAutomationId("2").And(cf.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException("Cancel button not found")).AsButton().Invoke(); // the dialog's Cancel
        Retry(() => ui.Window.ModalWindows.Length == 0 && !app.Application.GetAllTopLevelWindows(app.Automation).Any(w => w.Title == "Open"),
            "the Open dialog closed");
        Assert.Equal("Gridline - Untitled", ui.Window.Title);

        // Open: the file name box (AutomationId 1148) and Enter.
        Window second = OpenDialog();
        var fileName = (second.FindFirstDescendant(cf => cf.ByAutomationId("1148").And(cf.ByControlType(ControlType.Edit)))
                        ?? throw new InvalidOperationException("file name box not found")).AsTextBox();
        fileName.Text = path;
        Retry(() => fileName.Text == path, "the file name is entered");
        fileName.Focus();
        Keyboard.Type(VirtualKeyShort.RETURN);
        Retry(() => ui.Window.Title == "Gridline - opened.gridline", "the opened file's name is in the title");
        WaitFor(() => ui.SelectionValue.Name, "N:42");
        WaitFor(() => ui.FormulaBar.Text, "=40+2");
    }
}
