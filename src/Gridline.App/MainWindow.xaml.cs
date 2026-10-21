using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Gridline.App.ViewModels;

namespace Gridline.App;

/// <summary>
/// Thin code-behind (§11): sets the DataContext, closes on request (the close button runs the §11.5 Exit prompt), gives the
/// grid keyboard focus at launch, and wires the view-only parts of §11.4: name box and formula bar Enter/Esc, focus
/// moves, the edit box whose history menu Undo/Redo use while an edit is open (R49), and the sheet tab strip's mouse
/// activation and drag reordering (CR1.8, R88). The rules themselves live in MainViewModel.Editing.cs (D-38),
/// MainViewModel.Files.cs (D-39) and MainViewModel.Sheets.cs (D-59).
/// </summary>
public partial class MainWindow : Window, IEditTextHistory
{
    private readonly MainViewModel _viewModel;
    private TextBox _lastEditBox;
    private bool _wasEditing;
    private bool _closeConfirmed;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _lastEditBox = SheetGrid.Editor;
        SheetGrid.Editor.GotKeyboardFocus += EditBox_GotKeyboardFocus;
        viewModel.EditTextHistory = this;
        viewModel.EditStateChanged += OnEditStateChanged;
        viewModel.CloseRequested += (_, _) =>
        {
            _closeConfirmed = true; // Exit already ran the unsaved-changes prompt
            Close();
        };
        Closing += OnClosing;
        Closed += (_, _) => _viewModel.EndSession(); // a clean exit deletes the recovery file (§11.6, R48)
        Loaded += (_, _) => Keyboard.Focus(SheetGrid);
        // D-49: the content is disabled until startup has completed, so the grid cannot take focus at Loaded then. It is
        // focused once the content is enabled again (queued, so the IsEnabled binding and the closing RecoveryDialog's
        // own focus restore have run first).
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.IsReady) || !_viewModel.IsReady) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusGridAfterStartup);
        };
    }

    /// <summary>The queued startup focus (D-49). It may run inside another modal's nested message loop (an error message
    /// box, a dialog): then this window is not active and the focus must not be taken from the modal. In that case the
    /// grid is focused when this window is next activated, unless something in it has keyboard focus by then.</summary>
    private void FocusGridAfterStartup()
    {
        if (CanTakeStartupFocus())
        {
            Keyboard.Focus(SheetGrid);
            return;
        }
        void OnActivatedOnce(object? sender, EventArgs e)
        {
            Activated -= OnActivatedOnce;
            if (CanTakeStartupFocus() && !IsKeyboardFocusWithin) Keyboard.Focus(SheetGrid);
        }
        Activated += OnActivatedOnce;
    }

    private bool CanTakeStartupFocus() =>
        IsActive && _viewModel.IsReady && !OwnedWindows.OfType<Window>().Any(w => w.IsVisible);

    /// <summary>§11.5: the close button counts as Exit, so a dirty workbook prompts and Cancel (or a failed or cancelled
    /// Save) keeps the window open. Exit itself prompts before raising CloseRequested.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed) return;
        if (!_viewModel.ConfirmClose()) e.Cancel = true;
    }

    /// <summary>R49: the formula bar's undo history starts at the edit's start text: it is emptied whenever an edit opens
    /// and when it ends, so Undo never steps back into text from another cell or another edit. (A binding-driven Text
    /// update already resets a TextBox's history; this also covers text typed before the edit and the TryCommit retry.)
    /// The in-cell editor does the same in GridControl.</summary>
    private void OnEditStateChanged(object? sender, EventArgs e)
    {
        bool editing = _viewModel.IsEditing;
        if (editing == _wasEditing) return;
        _wasEditing = editing;
        if (!editing)
        {
            ClearHistory(FormulaBar);
            return;
        }
        // The edit may have been opened by typing in the formula bar, which is then still inside its own text change and
        // cannot drop its history yet; so the history is cleared right after the current input (Send priority runs before
        // the next queued key). The text that opened the edit is the start text.
        Dispatcher.BeginInvoke(DispatcherPriority.Send, () => { if (_viewModel.IsEditing) ClearHistory(FormulaBar); });
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

    // ---- CR1.8, R88: the sheet tab strip. The tabs are not focusable (a ListBoxItem selects itself only when it can
    // take focus), so a click or right-click activates the tab under the mouse here and the keyboard stays with the grid;
    // dragging a tab onto another tab moves it to that tab's index as one MoveSheet (D-59). ----

    private const string SheetTabDragFormat = "Gridline.SheetTabIndex";
    private Point? _tabDragStart;
    private int _tabDragIndex = -1;

    /// <summary>The index of the tab that contains <paramref name="element"/>, or -1.</summary>
    private int SheetTabIndexAt(object element) =>
        element is DependencyObject d && ItemsControl.ContainerFromElement(SheetTabStrip, d) is ListBoxItem item
            ? SheetTabStrip.ItemContainerGenerator.IndexFromContainer(item)
            : -1;

    private void SheetTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragIndex = SheetTabIndexAt(e.OriginalSource);
        _tabDragStart = null;
        if (_tabDragIndex < 0) return;
        if (_tabDragIndex != _viewModel.ActiveSheetIndex && !_viewModel.ActivateSheet(_tabDragIndex)) return; // refused edit
        _tabDragStart = e.GetPosition(SheetTabStrip);
    }

    /// <summary>The strip's selection is the active sheet: when the strip has dropped its selection (its selected tab was
    /// removed), the active tab is selected again once the items have settled.</summary>
    private void SheetTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SheetTabStrip.SelectedIndex >= 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            if (SheetTabStrip.SelectedIndex < 0 && _viewModel.ActiveSheetIndex < SheetTabStrip.Items.Count)
                SheetTabStrip.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, _viewModel.ActiveSheetIndex);
        });
    }

    private void SheetTabs_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _tabDragStart = null;

    /// <summary>A right-click activates the tab under the mouse first, so its context menu acts on that sheet.</summary>
    private void SheetTabs_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        int index = SheetTabIndexAt(e.OriginalSource);
        if (index >= 0 && index != _viewModel.ActiveSheetIndex) _viewModel.ActivateSheet(index);
    }

    private void SheetTabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        Vector moved = e.GetPosition(SheetTabStrip) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _tabDragStart = null;
        DragDrop.DoDragDrop(SheetTabStrip, new DataObject(SheetTabDragFormat, _tabDragIndex), DragDropEffects.Move);
    }

    private void SheetTabs_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(SheetTabDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SheetTabs_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(SheetTabDragFormat) is not int from) return;
        e.Handled = true;
        int to = SheetTabIndexAt(e.OriginalSource);
        if (to < 0) to = _viewModel.SheetTabs.Count - 1; // dropped past the last tab
        _viewModel.MoveSheet(from, to);
        Keyboard.Focus(SheetGrid);
    }
}
