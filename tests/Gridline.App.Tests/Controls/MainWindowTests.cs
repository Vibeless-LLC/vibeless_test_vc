using System.Windows;
using Gridline.App.Services;
using Gridline.App.ViewModels;
using Gridline.Core.Addressing;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.Controls;

/// <summary>The main window's view-only wiring (STA): the close button as Exit (§11.5) and the R49 formula bar history
/// starting at the edit's start text.</summary>
public class MainWindowTests
{
    private static CellAddress A(string s) => CellAddress.Parse(s);

    /// <summary>A shown main window placed off screen and not activated, so it never covers the desktop.</summary>
    private static MainWindow Offscreen(MainViewModel vm)
    {
        var window = new MainWindow(vm)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
        };
        window.Show();
        Pump(window); // bindings and layout settle
        return window;
    }

    private static void Pump(Window window) =>
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>Types <paramref name="text"/> into <paramref name="box"/> through the TextInput route, as the keyboard does,
    /// so the box records an undo unit (programmatic Text sets on a bound box do not).</summary>
    private static void TypeInto(System.Windows.Controls.TextBox box, string text)
    {
        box.CaretIndex = box.Text.Length;
        box.RaiseEvent(new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, box, text))
        {
            RoutedEvent = System.Windows.Input.TextCompositionManager.TextInputEvent,
        });
    }

    [Fact]
    public void R49_BothEditBoxes_StartEveryEditWithAnEmptyHistory_SoUndoCannotReachAnotherEditsText() => RunSta(() =>
    {
        var vm = NewViewModel(new FakeDialogService { DefaultPromptAnswer = UnsavedChangesChoice.DontSave });
        var window = Offscreen(vm);
        var fb = window.FormulaBar;
        var editor = window.SheetGrid.Editor;

        // Edit 1 at A1, typed in the formula bar and then in the cell: both boxes collect history for it.
        TypeInto(fb, "a");
        Pump(window); // the editor appears (and gets its template); the formula bar's history restarts at "a"
        TypeInto(fb, "b");
        TypeInto(fb, "c");
        Assert.True(fb.CanUndo);
        fb.Undo(); fb.Undo();
        Assert.Equal("a", fb.Text);
        Assert.False(fb.CanUndo); // not back past the text that opened the edit
        TypeInto(editor, "d");
        Assert.Equal("ad", fb.Text);
        Assert.True(editor.CanUndo);
        Assert.True(vm.CommitPendingEdit());

        // Edit 2 at B1, typed on the grid: neither box can undo into edit 1's text.
        vm.SelectCell(A("B1"), extend: false);
        vm.BeginEdit("x");
        Pump(window);
        Assert.Equal("x", fb.Text);
        Assert.Equal("x", editor.Text);
        Assert.False(fb.CanUndo);
        Assert.False(editor.CanUndo);
        Assert.False(vm.UndoCommand.CanExecute(null));
        TypeInto(editor, "y");
        Assert.True(editor.CanUndo);
        editor.Undo();
        Assert.Equal("x", editor.Text);
        Assert.False(editor.CanUndo); // stops at the edit's start text
        vm.CancelEdit();

        // Edit 3 at B1, opened from the formula bar: the editor, which still holds edit 2's history, starts empty too.
        TypeInto(fb, "q");
        Pump(window);
        Assert.True(vm.IsEditing);
        Assert.Equal("q", editor.Text);
        Assert.False(editor.CanUndo);
        Assert.False(fb.CanUndo);
        vm.CancelEdit();
        window.Close();
    });

    [Fact]
    public void CloseButton_CountsAsExit_CancelKeepsTheWindowOpen_DontSaveCloses() => RunSta(() =>
    {
        var dialogs = new FakeDialogService();
        var vm = NewViewModel(dialogs);
        var window = Offscreen(vm);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        vm.Workbook.SetCell("A1", "1");

        dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Cancel);
        window.Close();
        Assert.False(closed);
        Assert.Equal(["Untitled"], dialogs.Prompts);

        dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.DontSave);
        window.Close();
        Assert.True(closed);
        Assert.Equal(2, dialogs.Prompts.Count);
    });

    [Fact]
    public void Exit_PromptsOnce_ThenTheWindowCloses() => RunSta(() =>
    {
        var dialogs = new FakeDialogService { DefaultPromptAnswer = UnsavedChangesChoice.DontSave };
        var vm = NewViewModel(dialogs);
        var window = Offscreen(vm);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        vm.Workbook.SetCell("A1", "1");

        vm.ExitCommand.Execute(null);

        Assert.True(closed);
        Assert.Single(dialogs.Prompts); // the close that Exit requests does not prompt again
    });
}
