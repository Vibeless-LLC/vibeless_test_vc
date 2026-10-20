using System.Windows.Input;

namespace Gridline.App.ViewModels;

/// <summary>An <see cref="ICommand"/> over delegates. CanExecuteChanged is raised explicitly by the ViewModel when the
/// state it depends on changes (no CommandManager polling).</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
