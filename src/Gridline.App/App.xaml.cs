using System.Windows;
using Gridline.App.Services;
using Gridline.App.ViewModels;

namespace Gridline.App;

/// <summary>Startup (§11, §11.6, R44, R50): <c>Gridline.App.exe [path]</c>. Recovery files, if any, are offered first;
/// then, unless one was restored, the given file is opened (or it starts Untitled); then the window is shown and
/// autosave runs every 60 s (R51: <c>GRIDLINE_AUTOSAVE_INTERVAL_MS</c> overrides it).</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var viewModel = new MainViewModel(
            new WpfDialogService(),
            new WpfClipboardService(),
            new DirectoryRecoveryStore(DirectoryRecoveryStore.DefaultDirectory()),
            new DispatcherAutosaveTimer(AutosaveSettings.IntervalFromEnvironment()));
        var window = new MainWindow(viewModel);
        MainWindow = window; // before the RecoveryDialog, so closing that dialog does not end the app
        viewModel.Startup(e.Args.Length > 0 ? e.Args[0] : null); // load errors were shown; it stays Untitled
        window.Show();
    }
}
