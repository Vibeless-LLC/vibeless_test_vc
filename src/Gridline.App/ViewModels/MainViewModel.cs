using System.IO;
using Gridline.App.Services;
using Gridline.Core;

namespace Gridline.App.ViewModels;

/// <summary>
/// The main window's ViewModel (§11): owns the <see cref="Core.Workbook"/>, the file path, the title and the menu
/// commands. Partials: Selection (active cell and selection), Editing (§11.4), Files (§11.5 file commands and the
/// unsaved-changes prompt), Clipboard (§11.6) and Recovery (§11.6 autosave and recovery). Built-in WPF binding only.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private Workbook _workbook;
    private string? _filePath;

    /// <param name="dialogs">Modal UI.</param>
    /// <param name="clipboard">The system clipboard.</param>
    /// <param name="recovery">Where autosave writes recovery files; null disables autosave and recovery.</param>
    /// <param name="autosaveTimer">The autosave clock; null means no ticks (tests call <see cref="Autosave"/>).</param>
    public MainViewModel(IDialogService dialogs, IClipboardService clipboard, IRecoveryStore? recovery = null, IAutosaveTimer? autosaveTimer = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(clipboard);
        _dialogs = dialogs;
        _clipboard = clipboard;
        _recovery = recovery;
        _recovery?.TryClaim(_sessionId); // the session owns its guid for its lifetime (D-40); a fresh guid is always free
        _autosaveTimer = autosaveTimer;
        if (_autosaveTimer is not null) _autosaveTimer.Tick += OnAutosaveTick;
        _workbook = new Workbook();
        _workbook.CellsChanged += OnCellsChanged;

        NewCommand = new RelayCommand(() => New());
        OpenCommand = new RelayCommand(() => Open());
        SaveCommand = new RelayCommand(() => Save());
        SaveAsCommand = new RelayCommand(() => SaveAs());
        ExitCommand = new RelayCommand(Exit);
        UndoCommand = new RelayCommand(Undo, CanUndoNow); // R49: the edit box while an edit is open
        RedoCommand = new RelayCommand(Redo, CanRedoNow);
        CopyCommand = new RelayCommand(() => Copy(), CanUseClipboard);
        PasteCommand = new RelayCommand(() => Paste(), CanUseClipboard);

        OnSelectionMoved();
    }

    /// <summary>The workbook currently shown. Replaced by New and by opening a file.</summary>
    public Workbook Workbook => _workbook;

    /// <summary>Full path of the file the workbook was loaded from or saved to; null when Untitled.</summary>
    public string? FilePath => _filePath;

    /// <summary>The file name shown in the title and the prompt, or <c>Untitled</c>.</summary>
    public string DocumentName => _filePath is null ? "Untitled" : Path.GetFileName(_filePath);

    /// <summary>§11.1: <c>Gridline - &lt;file name or Untitled&gt;</c>, with <c> *</c> appended when dirty.</summary>
    public string Title => "Gridline - " + DocumentName + (IsDirty ? " *" : "");

    /// <summary><see cref="Workbook.IsDirty"/>, or true for a restored recovery file until it is saved (R50).</summary>
    public bool IsDirty => _workbook.IsDirty || _restoredUnsaved;

    public RelayCommand NewCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand PasteCommand { get; }

    /// <summary>Raised when the window should close: Exit was confirmed (the prompt, if any, has already been answered).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>R44: loads <paramref name="path"/> at startup. On failure shows an error and stays Untitled.</summary>
    /// <returns>True when the file was loaded.</returns>
    public bool OpenAtStartup(string path) => TryLoad(path);

    /// <summary>Makes <paramref name="workbook"/> the shown workbook, with <paramref name="filePath"/> (null = Untitled), and
    /// resets the selection to A1. The old workbook's recovery file is deleted: it was saved, or the user chose Don't
    /// Save (§11.5), so nothing is left to recover (D-40).</summary>
    internal void Adopt(Workbook workbook, string? filePath)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        IsEditing = false; // an open edit belongs to the old workbook
        ForgetInternalCopy(); // so is the copied range (§11.6, D-39)
        DeleteRecoveryFile();
        _restoredUnsaved = false;
        _workbook.CellsChanged -= OnCellsChanged;
        _workbook = workbook;
        _workbook.CellsChanged += OnCellsChanged;
        OnPropertyChanged(nameof(Workbook));
        SetFilePath(filePath);
        _anchor = _active = new Core.Addressing.CellAddress(1, 1);
        RefreshDocumentState();
        ContentChanged?.Invoke(this, EventArgs.Empty);
        OnSelectionMoved(); // the selection is reset to A1, so the grid scrolls there
    }

    private void Undo()
    {
        if (_isEditing) { EditTextHistory?.Undo(); RaiseUndoRedoCanExecuteChanged(); return; } // R49
        _workbook.Undo();
        RefreshDocumentState();
    }

    private void Redo()
    {
        if (_isEditing) { EditTextHistory?.Redo(); RaiseUndoRedoCanExecuteChanged(); return; } // R49
        _workbook.Redo();
        RefreshDocumentState();
    }

    private void OnCellsChanged(object? sender, CellsChangedEventArgs e)
    {
        RefreshDocumentState();
        ContentChanged?.Invoke(this, EventArgs.Empty);
        RefreshContentDerived(); // repaint only (ContentChanged); the view does not scroll
    }

    /// <summary>Title, dirty flag and Undo/Redo availability. Called after every change of the workbook's history
    /// position or saved mark (edits through CellsChanged, Undo, Redo, Save, Adopt), so the title's <c> *</c> always
    /// follows <see cref="IsDirty"/> (a Restore also refreshes it).</summary>
    private void RefreshDocumentState()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsDirty));
        RaiseUndoRedoCanExecuteChanged();
    }
}
