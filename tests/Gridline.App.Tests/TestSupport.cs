using System.Runtime.ExceptionServices;
using Gridline.App.Services;
using Gridline.App.ViewModels;

namespace Gridline.App.Tests;

/// <summary>Records dialog calls instead of showing UI, and answers them from queued or default replies.</summary>
internal sealed class FakeDialogService : IDialogService
{
    public List<(string Title, string Message)> Errors { get; } = [];

    /// <summary>Document names the unsaved-changes prompt was shown for, in order.</summary>
    public List<string> Prompts { get; } = [];

    public int OpenDialogs { get; private set; }

    /// <summary>The current path passed to each Save dialog.</summary>
    public List<string?> SaveDialogs { get; } = [];

    /// <summary>Answers for the next prompts; when empty, <see cref="DefaultPromptAnswer"/>.</summary>
    public Queue<UnsavedChangesChoice> PromptAnswers { get; } = new();

    public UnsavedChangesChoice DefaultPromptAnswer { get; set; } = UnsavedChangesChoice.Cancel;

    /// <summary>The path the Open dialog returns (null = cancelled).</summary>
    public string? OpenPath { get; set; }

    /// <summary>The path the Save dialog returns (null = cancelled).</summary>
    public string? SavePath { get; set; }

    public void ShowError(string title, string message) => Errors.Add((title, message));

    public string? ShowOpenDialog()
    {
        OpenDialogs++;
        return OpenPath;
    }

    public string? ShowSaveDialog(string? currentPath)
    {
        SaveDialogs.Add(currentPath);
        return SavePath;
    }

    public UnsavedChangesChoice PromptUnsavedChanges(string documentName)
    {
        Prompts.Add(documentName);
        return PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : DefaultPromptAnswer;
    }
}

/// <summary>An in-memory clipboard; <see cref="Locked"/> makes every call fail as if another process held it.</summary>
internal sealed class FakeClipboardService : IClipboardService
{
    public string? Text { get; set; }
    public bool Locked { get; set; }
    public int Sets { get; private set; }

    public bool TrySetText(string text)
    {
        if (Locked) return false;
        Text = text;
        Sets++;
        return true;
    }

    public bool TryGetText(out string? text)
    {
        text = Locked ? null : Text;
        return !Locked;
    }
}

internal static class TestSupport
{
    public static MainViewModel NewViewModel() => new(new FakeDialogService(), new FakeClipboardService());

    public static MainViewModel NewViewModel(FakeDialogService dialogs) => new(dialogs, new FakeClipboardService());

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
