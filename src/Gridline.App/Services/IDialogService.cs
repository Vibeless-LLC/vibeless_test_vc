namespace Gridline.App.Services;

/// <summary>The answer to the unsaved-changes prompt (§11.5, R45).</summary>
public enum UnsavedChangesChoice
{
    Save,
    DontSave,
    Cancel,
}

/// <summary>
/// The seam between ViewModels and modal UI, so ViewModel tests run without windows (a fake records the calls): error
/// messages, the Win32 Open and Save As file dialogs with a <c>*.gridline</c> filter, the same two with a <c>*.csv</c>
/// filter for Import CSV and Export CSV (§11.5, R74), the Save / Don't Save / Cancel prompt (§11.5, R45) and the
/// RecoveryDialog (§11.6, R50).
/// </summary>
public interface IDialogService
{
    /// <summary>Shows a modal error message and returns when it is dismissed.</summary>
    void ShowError(string title, string message);

    /// <summary>The Open dialog; returns the chosen path, or null when cancelled.</summary>
    string? ShowOpenDialog();

    /// <summary>The Save As dialog, starting at <paramref name="currentPath"/> when there is one; returns the chosen path,
    /// or null when cancelled.</summary>
    string? ShowSaveDialog(string? currentPath);

    /// <summary>Import CSV's Open dialog (§11.5, R74), <c>*.csv</c> filter; returns the chosen path, or null when cancelled.</summary>
    string? ShowOpenCsvDialog();

    /// <summary>Export CSV's Save dialog (§11.5, R74), <c>*.csv</c> filter, proposing <paramref name="currentPath"/>'s
    /// folder and name with a <c>.csv</c> extension when there is one; returns the chosen path, or null when cancelled.</summary>
    string? ShowSaveCsvDialog(string? currentPath);

    /// <summary>The R45 prompt for a dirty workbook named <paramref name="documentName"/> (file name or Untitled).</summary>
    UnsavedChangesChoice PromptUnsavedChanges(string documentName);

    /// <summary>The RecoveryDialog (§11.6, R50) over <paramref name="recovery"/>; returns when it closes.</summary>
    void ShowRecovery(ViewModels.RecoveryViewModel recovery);
}
