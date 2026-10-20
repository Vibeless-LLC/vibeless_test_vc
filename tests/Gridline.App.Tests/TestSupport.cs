using System.Runtime.ExceptionServices;
using Gridline.App.Services;
using Gridline.App.ViewModels;

namespace Gridline.App.Tests;

/// <summary>Records dialog calls instead of showing UI.</summary>
internal sealed class FakeDialogService : IDialogService
{
    public List<(string Title, string Message)> Errors { get; } = [];

    public void ShowError(string title, string message) => Errors.Add((title, message));
}

internal static class TestSupport
{
    public static MainViewModel NewViewModel() => new(new FakeDialogService());

    /// <summary>Runs <paramref name="action"/> on a fresh STA thread (WPF elements need one) and rethrows its failure.</summary>
    public static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>A unique temp directory for one test, deleted by the caller.</summary>
    public static string NewTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridline-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
