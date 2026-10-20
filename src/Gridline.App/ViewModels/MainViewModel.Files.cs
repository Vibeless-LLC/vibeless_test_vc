using System.IO;
using Gridline.App.Services;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.App.ViewModels;

/// <summary>
/// File commands (§11.5, §8.4): New, Open, Save, Save As and Exit, with the Save / Don't Save / Cancel prompt (R45) on a
/// dirty workbook. Loading and saving go through <see cref="GridlineFile"/> (saves are atomic); a failure shows an error
/// and leaves the workbook, its path and its dirty state unchanged. See DECISIONS D-39.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>New (Ctrl+N): after the unsaved-changes prompt, an empty Untitled workbook. Returns false when cancelled.</summary>
    public bool New()
    {
        if (!ConfirmUnsavedChanges()) return false;
        Adopt(new Workbook(), null);
        return true;
    }

    /// <summary>Open (Ctrl+O): after the prompt, the Open dialog, then the chosen file. Returns true when a file was loaded.</summary>
    public bool Open()
    {
        if (!ConfirmUnsavedChanges()) return false;
        string? path = _dialogs.ShowOpenDialog();
        return path is not null && TryLoad(path);
    }

    /// <summary>Save (Ctrl+S): to the current file, or Save As when Untitled. Returns true when the workbook was saved.</summary>
    public bool Save()
    {
        if (!CommitPendingEdit()) return false;
        return _filePath is null ? SaveAs() : SaveTo(_filePath);
    }

    /// <summary>Save As (Ctrl+Shift+S): the Save dialog, then a save to the chosen path, which becomes the file path (and
    /// the title's file name). Returns true when the workbook was saved.</summary>
    public bool SaveAs()
    {
        if (!CommitPendingEdit()) return false;
        string? path = _dialogs.ShowSaveDialog(_filePath);
        return path is not null && SaveTo(path);
    }

    /// <summary>Exit: after the prompt, asks the window to close (<see cref="CloseRequested"/>).</summary>
    private void Exit()
    {
        if (ConfirmUnsavedChanges()) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window's close button counts as Exit (§11.5): returns false when the close must be cancelled.</summary>
    public bool ConfirmClose() => ConfirmUnsavedChanges();

    /// <summary>
    /// Before New, Open and Exit (§11.5): commits an open edit (so typed text counts as a change; a refused commit aborts),
    /// then, when the workbook is dirty, asks Save / Don't Save / Cancel (R45). Save that fails or is cancelled aborts the
    /// action. Returns true when the action may go ahead.
    /// </summary>
    private bool ConfirmUnsavedChanges()
    {
        if (!CommitPendingEdit()) return false;
        if (!IsDirty) return true;
        return _dialogs.PromptUnsavedChanges(DocumentName) switch
        {
            UnsavedChangesChoice.Save => Save(),
            UnsavedChangesChoice.DontSave => true,
            _ => false,
        };
    }

    /// <summary>Loads <paramref name="path"/> and adopts it; on failure shows an error and changes nothing.</summary>
    private bool TryLoad(string path)
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

    /// <summary>Saves atomically to <paramref name="path"/>, then marks the workbook saved and makes the path current. On
    /// failure shows an error; the workbook stays dirty and the path unchanged.</summary>
    private bool SaveTo(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            GridlineFile.SaveToPath(_workbook, fullPath);
        }
        catch (Exception ex) when (ex is GridlineIOException or ArgumentException or NotSupportedException or IOException)
        {
            _dialogs.ShowError("Gridline", $"Cannot save '{path}'.{Environment.NewLine}{ex.Message}");
            return false;
        }
        _workbook.MarkSaved(); // SaveToPath does not (§8.4); the title loses its " *"
        _restoredUnsaved = false;
        DeleteRecoveryFile(); // §11.6: a successful Save deletes the recovery file
        SetFilePath(fullPath);
        RefreshDocumentState();
        return true;
    }

    private void SetFilePath(string? filePath)
    {
        if (_filePath == filePath) return;
        _filePath = filePath;
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(Title));
    }
}
