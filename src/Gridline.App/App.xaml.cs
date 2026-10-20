using System.Windows;
using Gridline.App.Services;
using Gridline.App.ViewModels;

namespace Gridline.App;

/// <summary>Startup (§11, R44): <c>Gridline.App.exe [path]</c> opens the given file, or starts Untitled.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var viewModel = new MainViewModel(new WpfDialogService(), new WpfClipboardService());
        if (e.Args.Length > 0) viewModel.OpenAtStartup(e.Args[0]); // on failure an error was shown and it stays Untitled
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }
}
