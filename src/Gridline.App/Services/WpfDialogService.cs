using System.Windows;

namespace Gridline.App.Services;

/// <summary><see cref="IDialogService"/> over WPF message boxes, owned by the main window when it is open.</summary>
public sealed class WpfDialogService : IDialogService
{
    public void ShowError(string title, string message)
    {
        Window? owner = Application.Current?.MainWindow;
        if (owner is { IsLoaded: true })
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
