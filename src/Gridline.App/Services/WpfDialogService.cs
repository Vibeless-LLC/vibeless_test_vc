using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Gridline.App.Services;

/// <summary><see cref="IDialogService"/> over WPF: message boxes, the Win32 Open and Save As dialogs (via
/// Microsoft.Win32) and <see cref="UnsavedChangesWindow"/>, owned by the main window when it is open.</summary>
public sealed class WpfDialogService : IDialogService
{
    private const string Filter = "Gridline workbooks (*.gridline)|*.gridline";

    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

    public void ShowError(string title, string message)
    {
        if (Owner is { } owner)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public string? ShowOpenDialog()
    {
        var dialog = new OpenFileDialog { Filter = Filter, DefaultExt = ".gridline", CheckFileExists = true };
        return Show(dialog) ? dialog.FileName : null;
    }

    public string? ShowSaveDialog(string? currentPath)
    {
        var dialog = new SaveFileDialog { Filter = Filter, DefaultExt = ".gridline", AddExtension = true, OverwritePrompt = true };
        if (currentPath is not null)
        {
            dialog.FileName = Path.GetFileName(currentPath);
            dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
        }
        return Show(dialog) ? dialog.FileName : null;
    }

    private static bool Show(CommonDialog dialog) => (Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;

    public UnsavedChangesChoice PromptUnsavedChanges(string documentName)
    {
        var prompt = new UnsavedChangesWindow(documentName);
        if (Owner is { } owner) prompt.Owner = owner;
        else prompt.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        prompt.ShowDialog();
        return prompt.Choice;
    }

    /// <summary>At startup the main window is not shown yet, so the dialog is unowned, centred and in the taskbar.</summary>
    public void ShowRecovery(ViewModels.RecoveryViewModel recovery)
    {
        var dialog = new RecoveryDialog(recovery);
        if (Owner is { } owner)
        {
            dialog.Owner = owner;
            dialog.ShowInTaskbar = false;
        }
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }
}
