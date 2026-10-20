using System.IO;
using Gridline.App.Services;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.App.ViewModels;

/// <summary>
/// The main window's ViewModel (§11): owns the <see cref="Core.Workbook"/>, the file path, the title, the menu commands
/// and (in MainViewModel.Selection.cs) the active cell and selection that the grid, name box, formula bar and status bar
/// show. Built-in WPF binding only.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private Workbook _workbook;
    private string? _filePath;

    public MainViewModel(IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        _dialogs = dialogs;
        _workbook = new Workbook();
        _workbook.CellsChanged += OnCellsChanged;

        NewCommand = new RelayCommand(New);
        // Task 8 (§11.5, §8.4, R45): Open, Save and Save As with file dialogs and the unsaved-changes prompt.
        OpenCommand = new RelayCommand(NotYetAvailable, () => false);
        SaveCommand = new RelayCommand(NotYetAvailable, () => false);
        SaveAsCommand = new RelayCommand(NotYetAvailable, () => false);
        ExitCommand = new RelayCommand(Exit);
        UndoCommand = new RelayCommand(Undo, () => _workbook.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _workbook.CanRedo);
        // Task 8 (§11.6, §9.4, R46, R47): Copy and Paste through the clipboard.
        CopyCommand = new RelayCommand(NotYetAvailable, () => false);
        PasteCommand = new RelayCommand(NotYetAvailable, () => false);

        RefreshSelectionDerived();
    }

    /// <summary>The workbook currently shown. Replaced by New and by opening a file.</summary>
    public Workbook Workbook => _workbook;

    /// <summary>Full path of the file the workbook was loaded from or saved to; null when Untitled.</summary>
    public string? FilePath => _filePath;

    /// <summary>§11.1: <c>Gridline - &lt;file name or Untitled&gt;</c>, with <c> *</c> appended when dirty.</summary>
    public string Title => "Gridline - " + (_filePath is null ? "Untitled" : Path.GetFileName(_filePath)) + (_workbook.IsDirty ? " *" : "");

    public bool IsDirty => _workbook.IsDirty;

    public RelayCommand NewCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand PasteCommand { get; }

    /// <summary>Raised when the window should close (Exit).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>R44: loads <paramref name="path"/> at startup. On failure shows an error and stays Untitled.</summary>
    /// <returns>True when the file was loaded.</returns>
    public bool OpenAtStartup(string path)
    {
        Workbook loaded;
        string fullPath;
        try
        {
            loaded = GridlineFile.LoadFromPath(path);
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is GridlineFormatException or GridlineIOException or ArgumentException or NotSupportedException or IOException)
        {
            _dialogs.ShowError("Gridline", $"Cannot open '{path}'.{Environment.NewLine}{ex.Message}");
            return false;
        }
        Adopt(loaded, fullPath);
        return true;
    }

    /// <summary>Makes <paramref name="workbook"/> the shown workbook, with <paramref name="filePath"/> (null = Untitled), and
    /// resets the selection to A1.</summary>
    internal void Adopt(Workbook workbook, string? filePath)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        _workbook.CellsChanged -= OnCellsChanged;
        _workbook = workbook;
        _filePath = filePath;
        _workbook.CellsChanged += OnCellsChanged;
        OnPropertyChanged(nameof(Workbook));
        OnPropertyChanged(nameof(FilePath));
        _anchor = _active = new Core.Addressing.CellAddress(1, 1);
        RefreshDocumentState();
        ContentChanged?.Invoke(this, EventArgs.Empty);
        RefreshSelectionDerived();
    }

    // Task 8 (§11.5): New on a dirty workbook prompts Save / Don't Save / Cancel first (R45).
    private void New() => Adopt(new Workbook(), null);

    // Task 8 (§11.5): Exit on a dirty workbook prompts first; closing the window counts as Exit.
    private void Exit() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Undo()
    {
        _workbook.Undo();
        RefreshDocumentState();
    }

    private void Redo()
    {
        _workbook.Redo();
        RefreshDocumentState();
    }

    private static void NotYetAvailable() { }

    private void OnCellsChanged(object? sender, CellsChangedEventArgs e)
    {
        RefreshDocumentState();
        ContentChanged?.Invoke(this, EventArgs.Empty);
        RefreshSelectionDerived();
    }

    /// <summary>Title, dirty flag and Undo/Redo availability.</summary>
    private void RefreshDocumentState()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsDirty));
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
    }
}
