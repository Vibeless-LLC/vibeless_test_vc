using System.Windows;
using Gridline.App.Services;
using Gridline.App.ViewModels;

namespace Gridline.App;

/// <summary>Startup (§11, §11.6, §11.7, R44, R50, D-40): <c>Gridline.App.exe [path]</c>. With no recovery files the given
/// file is opened (or it starts Untitled) before the window is first shown (P-6). With recovery files the main window is
/// shown first and the RecoveryDialog is then shown as its owned modal, once the window has rendered; unless a file is
/// restored, the given file is opened after the dialog closes. Autosave runs every 60 s (R51:
/// <c>GRIDLINE_AUTOSAVE_INTERVAL_MS</c> overrides it).</summary>
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
        MainWindow = window;
        if (viewModel.BeginStartup(e.Args.Length > 0 ? e.Args[0] : null)) // load errors were shown; it stays Untitled
        {
            // Recovery is pending: the dialog needs a shown, rendered owner (§11.7), so it opens after the first frame.
            void OnRendered(object? sender, EventArgs args)
            {
                window.ContentRendered -= OnRendered;
                window.Dispatcher.BeginInvoke(() => viewModel.CompleteStartup());
            }
            window.ContentRendered += OnRendered;
        }
        window.Show();
    }
}
