namespace Gridline.App.Services;

/// <summary>
/// The seam between ViewModels and modal UI, so ViewModel tests run without windows. Task 6 needs only error reporting;
/// the file dialogs and the Save / Don't Save / Cancel prompt (§11.5, R45) are added here by the file-commands task.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows a modal error message and returns when it is dismissed.</summary>
    void ShowError(string title, string message);
}
