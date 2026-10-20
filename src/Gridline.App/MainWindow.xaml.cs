using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Gridline.App.ViewModels;

namespace Gridline.App;

/// <summary>
/// Thin code-behind (§11): sets the DataContext, closes on request, gives the grid keyboard focus at launch, and wires the
/// view-only parts of §11.4: name box and formula bar Enter/Esc, focus moves, and the edit box whose history menu
/// Undo/Redo use while an edit is open (R49). The rules themselves live in MainViewModel.Editing.cs (D-38).
/// </summary>
public partial class MainWindow : Window, IEditTextHistory
{
    private readonly MainViewModel _viewModel;
    private TextBox _lastEditBox;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _lastEditBox = SheetGrid.Editor;
        SheetGrid.Editor.GotKeyboardFocus += EditBox_GotKeyboardFocus;
        viewModel.EditTextHistory = this;
        viewModel.EditStateChanged += (_, _) =>
        {
            if (!viewModel.IsEditing) ClearHistory(FormulaBar); // the next edit's Undo never reaches this one's text
        };
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => Keyboard.Focus(SheetGrid);
    }

    // ---- R49: menu Undo/Redo act on the edit box the user last typed in ----

    bool IEditTextHistory.CanUndo => _lastEditBox.CanUndo;
    bool IEditTextHistory.CanRedo => _lastEditBox.CanRedo;
    void IEditTextHistory.Undo() => _lastEditBox.Undo();
    void IEditTextHistory.Redo() => _lastEditBox.Redo();

    /// <summary>Re-queries Undo/Redo when the Edit menu opens: the edit box's history can change without a text change
    /// the ViewModel sees (for example when the box merges typing into one undo unit).</summary>
    private void MenuEdit_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        _viewModel.UndoCommand.RaiseCanExecuteChanged();
        _viewModel.RedoCommand.RaiseCanExecuteChanged();
    }

    private void EditBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        // Entering the formula bar without an open edit: its history holds only texts the binding put there for other
        // cells, so it starts empty.
        if (ReferenceEquals(box, FormulaBar) && !_viewModel.IsEditing) ClearHistory(box);
        _lastEditBox = box;
    }

    private static void ClearHistory(TextBox box)
    {
        box.IsUndoEnabled = false;
        box.IsUndoEnabled = true;
    }

    // ---- §11.4 formula bar: Enter commits (the active cell stays), Esc reverts; both return to the grid ----

    private void FormulaBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (_viewModel.CommitPendingEdit()) SheetGrid.Focus();
                break;
            case Key.Escape:
                _viewModel.CancelEdit();
                SheetGrid.Focus();
                break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- §11.4 name box: Enter navigates, an invalid address reverts; Esc and leaving the box revert ----

    private void NameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (_viewModel.NavigateToNameBox()) SheetGrid.Focus();
                else NameBox.SelectAll(); // reverted to the active address, ready to type again
                break;
            case Key.Escape:
                _viewModel.RevertNameBox();
                SheetGrid.Focus();
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void NameBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.CommitPendingEdit();

    private void NameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.RevertNameBox();
}
