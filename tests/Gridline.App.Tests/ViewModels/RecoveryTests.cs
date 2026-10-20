using Gridline.App.Services;
using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.App.Tests.ViewModels;

/// <summary>Autosave and recovery (§11.6, R48, R50, R51): autosave only while dirty, the file location, deletion on Save,
/// on adopting another workbook and on a clean exit, failures retried, and the RecoveryDialog's restore adoption, discard,
/// unreadable files and startup order. A real <see cref="DirectoryRecoveryStore"/> over a temp directory.</summary>
public sealed class RecoveryTests : IDisposable
{
    private readonly string _root = TestSupport.NewTempDirectory();
    private readonly string _recoveryDir;
    private readonly DirectoryRecoveryStore _store;
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeAutosaveTimer _timer = new();

    public RecoveryTests()
    {
        _recoveryDir = Path.Combine(_root, "Gridline", "Recovery"); // not created: autosave creates it on demand (R48)
        _store = new DirectoryRecoveryStore(_recoveryDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private MainViewModel NewViewModel() => new(_dialogs, new FakeClipboardService(), _store, _timer);

    private string[] RecoveryFiles() => Directory.Exists(_recoveryDir) ? Directory.GetFiles(_recoveryDir) : [];

    /// <summary>Writes a recovery file for a fresh session guid with one cell, dated <paramref name="age"/> ago.</summary>
    private RecoveryFile PlantRecovery(string address, string input, TimeSpan age)
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(_recoveryDir);
        string path = _store.PathFor(id);
        GridlineFile.SaveToPath(Workbook.FromCells([new(address, input)]), path);
        DateTime when = DateTime.Now - age;
        File.SetLastWriteTime(path, when);
        return new RecoveryFile(id, path, when);
    }

    // ---- location and settings ----

    [Fact]
    public void RecoveryPath_IsSessionGuidDotGridline_InTheStoreDirectory()
    {
        var vm = NewViewModel();
        Assert.Equal(Path.Combine(_recoveryDir, vm.SessionId.ToString("D") + ".gridline"), vm.RecoveryPath);
        Assert.NotEqual(vm.SessionId, NewViewModel().SessionId);
    }

    [Fact]
    public void DefaultDirectory_IsLocalAppDataGridlineRecovery()
    {
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA")!;
        Assert.Equal(Path.Combine(local, "Gridline", "Recovery"), DirectoryRecoveryStore.DefaultDirectory());
    }

    [Theory]
    [InlineData(null, 60_000)]
    [InlineData("500", 500)]
    [InlineData("1", 1)]
    [InlineData("0", 60_000)]
    [InlineData("-5", 60_000)]
    [InlineData("abc", 60_000)]
    [InlineData(" 5", 60_000)]
    [InlineData("1.5", 60_000)]
    [InlineData("99999999999", 60_000)]
    public void AutosaveInterval_Is60s_UnlessTheVariableHoldsPositiveMilliseconds(string? value, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), AutosaveSettings.IntervalFrom(value));

    // ---- autosave only while dirty ----

    [Fact]
    public void Autosave_WhileClean_WritesNothing()
    {
        var vm = NewViewModel();
        vm.Startup(null);
        _timer.Fire();
        Assert.False(Directory.Exists(_recoveryDir));
    }

    [Fact]
    public void Autosave_WhileDirty_WritesTheWorkbookAtomically_WithoutMarkingItSaved()
    {
        var vm = NewViewModel();
        vm.Startup(null);
        Assert.True(_timer.Running);
        vm.Workbook.SetCell("B2", "=6*7");
        _timer.Fire();

        Assert.Equal([vm.RecoveryPath!], RecoveryFiles());
        Assert.Equal("=6*7", GridlineFile.LoadFromPath(vm.RecoveryPath!).GetInput("B2"));
        Assert.True(vm.IsDirty); // R48: never MarkSaved, the title keeps its asterisk
        Assert.Equal("Gridline - Untitled *", vm.Title);
        Assert.Null(vm.FilePath);

        vm.Workbook.SetCell("B3", "x"); // the next tick replaces the same file
        _timer.Fire();
        Assert.Equal([vm.RecoveryPath!], RecoveryFiles());
        Assert.Equal("x", GridlineFile.LoadFromPath(vm.RecoveryPath!).GetInput("B3"));
    }

    [Fact]
    public void Autosave_AfterUndoBackToClean_DeletesTheStaleRecoveryFile()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "1");
        Assert.True(vm.Autosave());
        vm.UndoCommand.Execute(null);
        Assert.False(vm.IsDirty);
        Assert.False(vm.Autosave());
        Assert.Empty(RecoveryFiles());
    }

    [Fact]
    public void Autosave_Failure_IsIgnored_AndTheNextTickRetries()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_recoveryDir)!);
        File.WriteAllText(_recoveryDir, "a file where the directory should be"); // CreateDirectory fails
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "1");

        Assert.False(vm.Autosave()); // no exception, no dialog
        Assert.Empty(_dialogs.Errors);
        Assert.True(vm.IsDirty);

        File.Delete(_recoveryDir);
        Assert.True(vm.Autosave());
        Assert.True(File.Exists(vm.RecoveryPath));
    }

    [Fact]
    public void Autosave_WithoutAStore_DoesNothing()
    {
        var vm = TestSupport.NewViewModel();
        vm.Workbook.SetCell("A1", "1");
        Assert.False(vm.Autosave());
        Assert.Null(vm.RecoveryPath);
        Assert.False(vm.Startup(null));
        vm.EndSession();
    }

    // ---- deletion: Save, adopting another workbook, clean exit; not otherwise ----

    [Fact]
    public void SuccessfulSave_DeletesTheRecoveryFile_AFailedSaveKeepsIt()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "1");
        vm.Autosave();
        Assert.True(File.Exists(vm.RecoveryPath));

        _dialogs.SavePath = Path.Combine(_root, "missing-dir", "x.gridline"); // fails: the folder does not exist
        Assert.False(vm.Save());
        Assert.True(File.Exists(vm.RecoveryPath));

        _dialogs.SavePath = Path.Combine(_root, "saved.gridline");
        Assert.True(vm.Save());
        Assert.False(File.Exists(vm.RecoveryPath));
        Assert.Empty(RecoveryFiles());
    }

    [Fact]
    public void CleanExit_DeletesTheRecoveryFile_AndStopsAutosave()
    {
        var vm = NewViewModel();
        vm.Startup(null);
        vm.Workbook.SetCell("A1", "1");
        _timer.Fire();
        Assert.True(File.Exists(vm.RecoveryPath));

        vm.EndSession();
        Assert.False(_timer.Running);
        Assert.Empty(RecoveryFiles());
        _timer.Fire(); // a late tick writes nothing
        Assert.Empty(RecoveryFiles());
    }

    [Fact]
    public void ExitAfterDontSave_IsACleanExit_TheRecoveryFileIsDeleted()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "1");
        vm.Autosave();
        bool closed = false;
        vm.CloseRequested += (_, _) => { closed = true; vm.EndSession(); }; // what MainWindow does when it has closed
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.DontSave);
        vm.ExitCommand.Execute(null);
        Assert.True(closed);
        Assert.Empty(RecoveryFiles());
    }

    [Fact]
    public void ExitCancelled_KeepsTheRecoveryFile()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "1");
        vm.Autosave();
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Cancel);
        vm.ExitCommand.Execute(null);
        Assert.True(File.Exists(vm.RecoveryPath));
    }

    [Fact]
    public void NewAfterDontSave_DeletesTheRecoveryFile_ForTheSameSession()
    {
        var vm = NewViewModel();
        Guid session = vm.SessionId;
        vm.Workbook.SetCell("A1", "1");
        vm.Autosave();
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.DontSave);
        Assert.True(vm.New());
        Assert.Empty(RecoveryFiles());
        Assert.Equal(session, vm.SessionId);
        vm.Workbook.SetCell("A1", "2");
        vm.Autosave();
        Assert.Equal("2", GridlineFile.LoadFromPath(vm.RecoveryPath!).GetInput("A1"));
    }

    // ---- startup and the RecoveryDialog (R50) ----

    [Fact]
    public void Startup_WithoutRecoveryFiles_ShowsNoDialog_AndOpensTheFileArgument()
    {
        string file = Path.Combine(_root, "arg.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "7")]), file);
        var vm = NewViewModel();
        Assert.False(vm.Startup(file));
        Assert.Empty(_dialogs.Recoveries);
        Assert.Equal("Gridline - arg.gridline", vm.Title);
    }

    [Fact]
    public void BeginStartup_WithoutRecoveryFiles_OpensTheFileArgument_BeforeTheWindowIsShown()
    {
        // §11.6, D-40, P-6: no recovery files keeps the old order: the file is loaded before the first show.
        string file = Path.Combine(_root, "arg.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "7")]), file);
        var vm = NewViewModel();
        Assert.False(vm.BeginStartup(file)); // nothing pending
        Assert.Equal("Gridline - arg.gridline", vm.Title);
        Assert.True(_timer.Running);
        Assert.False(vm.CompleteStartup()); // a no-op
        Assert.Empty(_dialogs.Recoveries);
    }

    [Fact]
    public void BeginStartup_WithRecoveryFiles_DefersEverything_UntilCompleteStartup_ThenOpensTheArgumentAfterTheDialog()
    {
        // §11.7, D-40: the main window is shown first; the dialog (its owned modal) and the file argument come after.
        var planted = PlantRecovery("A1", "1", TimeSpan.Zero);
        string file = Path.Combine(_root, "arg.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "7")]), file);
        var vm = NewViewModel();
        string? titleWhileDialogOpen = null;
        _dialogs.OnRecovery = _ => titleWhileDialogOpen = vm.Title;

        Assert.True(vm.BeginStartup(file)); // recovery pending
        Assert.Empty(_dialogs.Recoveries);
        Assert.Equal("Gridline - Untitled", vm.Title);
        Assert.False(_timer.Running);

        Assert.False(vm.CompleteStartup()); // the fake closes the dialog without restoring
        var dialog = Assert.Single(_dialogs.Recoveries);
        Assert.Equal([planted.SessionId], dialog.Items.Select(i => i.File.SessionId));
        Assert.Equal("Gridline - Untitled", titleWhileDialogOpen); // the argument opens after the dialog closes
        Assert.Equal("Gridline - arg.gridline", vm.Title);
        Assert.True(_timer.Running);

        Assert.False(vm.CompleteStartup()); // only once
        Assert.Single(_dialogs.Recoveries);
    }

    [Fact]
    public void CompleteStartup_Restore_SkipsTheFileArgument()
    {
        var planted = PlantRecovery("B2", "restored", TimeSpan.Zero);
        string file = Path.Combine(_root, "arg.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "7")]), file);
        _dialogs.OnRecovery = d => d.Items[0].RestoreCommand.Execute(null);
        var vm = NewViewModel();

        Assert.True(vm.BeginStartup(file));
        Assert.True(vm.CompleteStartup());
        Assert.Equal("restored", vm.Workbook.GetInput("B2"));
        Assert.Equal("", vm.Workbook.GetInput("A1"));
        Assert.Equal("Gridline - Untitled *", vm.Title);
        Assert.Equal(planted.SessionId, vm.SessionId);
    }

    [Fact]
    public void Startup_ListsEveryRecoveryFile_NewestFirst_WithItsLastWriteTime_IgnoringOtherFiles()
    {
        var older = PlantRecovery("A1", "old", TimeSpan.FromHours(2));
        var newer = PlantRecovery("A1", "new", TimeSpan.FromMinutes(1));
        File.WriteAllText(Path.Combine(_recoveryDir, "notes.gridline"), "{}"); // not named by a guid
        File.WriteAllText(Path.Combine(_recoveryDir, ".gl-" + Guid.NewGuid().ToString("N") + ".tmp"), "x"); // a save's temp file

        var vm = NewViewModel();
        vm.Startup(null);

        var dialog = Assert.Single(_dialogs.Recoveries);
        Assert.Equal([newer.SessionId, older.SessionId], dialog.Items.Select(i => i.File.SessionId));
        Assert.All(dialog.Items, i => Assert.True(i.IsReadable));
        Assert.Equal(newer.SessionId.ToString("D") + ".gridline", dialog.Items[0].FileName);
        Assert.Equal(File.GetLastWriteTime(newer.Path).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), dialog.Items[0].LastWriteText);
        Assert.Equal("RecoveryRestore_" + newer.SessionId.ToString("N"), dialog.Items[0].RestoreAutomationId);
        Assert.Equal("RecoveryDiscard_" + newer.SessionId.ToString("N"), dialog.Items[0].DiscardAutomationId);
        Assert.True(_timer.Running); // autosave starts after the dialog
    }

    [Fact]
    public void Restore_AdoptsTheFile_AsUntitledAndDirty_AndTheSessionTakesItsGuid()
    {
        var planted = PlantRecovery("C3", "=2+3", TimeSpan.FromMinutes(5));
        var other = PlantRecovery("A1", "other", TimeSpan.FromMinutes(9));
        string file = Path.Combine(_root, "arg.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "from the argument")]), file);
        bool closed = false;
        _dialogs.OnRecovery = d =>
        {
            d.CloseRequested += (_, _) => closed = true;
            d.Items.Single(i => i.File.SessionId == planted.SessionId).RestoreCommand.Execute(null);
        };

        var vm = NewViewModel();
        Assert.True(vm.Startup(file));

        Assert.True(closed); // Restore closes the dialog
        Assert.Equal("=2+3", vm.Workbook.GetInput("C3"));
        Assert.Equal("", vm.Workbook.GetInput("A1")); // the file argument was not opened (D-40)
        Assert.Null(vm.FilePath);
        Assert.True(vm.IsDirty);
        Assert.Equal("Gridline - Untitled *", vm.Title);
        Assert.False(vm.UndoCommand.CanExecute(null)); // restored content is not an undo step
        Assert.Equal(planted.SessionId, vm.SessionId);
        Assert.Equal(planted.Path, vm.RecoveryPath, ignoreCase: true);
        Assert.True(File.Exists(other.Path)); // not acted on: offered again next time

        // The next autosave replaces the adopted file; it is not a second file.
        vm.Workbook.SetCell("D4", "more");
        _timer.Fire();
        Assert.Equal(2, RecoveryFiles().Length);
        Assert.Equal("more", GridlineFile.LoadFromPath(planted.Path).GetInput("D4"));

        // A successful Save deletes it and the workbook is clean.
        _dialogs.SavePath = Path.Combine(_root, "restored.gridline");
        Assert.True(vm.Save());
        Assert.False(File.Exists(planted.Path));
        Assert.False(vm.IsDirty);
        Assert.Equal("Gridline - restored.gridline", vm.Title);
    }

    [Fact]
    public void RestoredSession_CleanExit_DeletesTheAdoptedFile()
    {
        var planted = PlantRecovery("A1", "1", TimeSpan.Zero);
        _dialogs.OnRecovery = d => d.Items[0].RestoreCommand.Execute(null);
        var vm = NewViewModel();
        vm.Startup(null);
        vm.EndSession();
        Assert.False(File.Exists(planted.Path));
    }

    [Fact]
    public void RestoredSession_StaysDirtyAcrossTicks_AndPromptsOnNew()
    {
        var planted = PlantRecovery("A1", "1", TimeSpan.Zero);
        _dialogs.OnRecovery = d => d.Items[0].RestoreCommand.Execute(null);
        var vm = NewViewModel();
        vm.Startup(null);
        _timer.Fire();
        Assert.True(File.Exists(planted.Path)); // still dirty: not deleted as stale
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Cancel);
        Assert.False(vm.New());
        Assert.Equal(["Untitled"], _dialogs.Prompts);
    }

    [Fact]
    public void Close_WithoutActing_KeepsTheFiles_AndOpensTheFileArgument()
    {
        var planted = PlantRecovery("A1", "1", TimeSpan.Zero);
        string file = Path.Combine(_root, "arg.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "7")]), file);
        var vm = NewViewModel();

        Assert.False(vm.Startup(file)); // the fake closes the dialog at once
        Assert.Single(_dialogs.Recoveries);
        Assert.Equal("Gridline - arg.gridline", vm.Title);
        Assert.True(File.Exists(planted.Path));
        Assert.NotEqual(planted.SessionId, vm.SessionId);

        vm.EndSession(); // our clean exit deletes only our own file
        Assert.True(File.Exists(planted.Path));
    }

    [Fact]
    public void Discard_DeletesTheFile_AndRemovesTheRow_TheLastDiscardClosesTheDialog()
    {
        var a = PlantRecovery("A1", "a", TimeSpan.FromMinutes(1));
        var b = PlantRecovery("A1", "b", TimeSpan.FromMinutes(2));
        int closes = 0;
        _dialogs.OnRecovery = d =>
        {
            d.CloseRequested += (_, _) => closes++;
            d.Items[0].DiscardCommand.Execute(null);
            Assert.False(File.Exists(a.Path));
            Assert.True(File.Exists(b.Path));
            Assert.Equal(b.SessionId, Assert.Single(d.Items).File.SessionId);
            Assert.Equal(0, closes);
            d.Items[0].DiscardCommand.Execute(null);
            Assert.Empty(d.Items);
            Assert.Equal(1, closes);
        };
        var vm = NewViewModel();
        Assert.False(vm.Startup(null));
        Assert.Empty(RecoveryFiles());
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void UnreadableFile_IsListedAsUnreadable_AndCanOnlyBeDiscarded()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(_recoveryDir);
        string path = _store.PathFor(id);
        File.WriteAllText(path, "not json");
        _dialogs.OnRecovery = d =>
        {
            var item = Assert.Single(d.Items);
            Assert.False(item.IsReadable);
            Assert.StartsWith("Unreadable: ", item.Status, StringComparison.Ordinal);
            Assert.False(item.RestoreCommand.CanExecute(null));
            item.RestoreCommand.Execute(null); // ignored
            Assert.Null(d.Restored);
            Assert.True(item.DiscardCommand.CanExecute(null));
            item.DiscardCommand.Execute(null);
        };
        var vm = NewViewModel();
        Assert.False(vm.Startup(null));
        Assert.False(File.Exists(path));
        Assert.Equal("Gridline - Untitled", vm.Title);
    }

    [Fact]
    public void Discard_ThatCannotDelete_KeepsTheRowWithAStatus()
    {
        var planted = PlantRecovery("A1", "1", TimeSpan.Zero);
        _dialogs.OnRecovery = d =>
        {
            using (new FileStream(planted.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) // locked against delete
                d.Items[0].DiscardCommand.Execute(null);
            Assert.Single(d.Items);
            Assert.Equal("Could not delete this file.", d.Items[0].Status);
        };
        NewViewModel().Startup(null);
        Assert.True(File.Exists(planted.Path));
    }

    // ---- live sessions: the per-guid named mutex (D-40) ----

    /// <summary>Holds <c>Local\Gridline.Recovery.&lt;guid&gt;</c> on its own thread, as another live Gridline process would;
    /// <see cref="Dispose"/> releases it, <see cref="Abandon"/> ends the thread without releasing (a killed process).</summary>
    private sealed class OtherSession : IDisposable
    {
        private readonly ManualResetEventSlim _taken = new();
        private readonly ManualResetEventSlim _finish = new();
        private readonly Thread _thread;
        private bool _release = true;

        public OtherSession(Guid sessionId)
        {
            _thread = new Thread(() =>
            {
                var mutex = new Mutex(false, NamedMutexSessionLock.MutexName(sessionId));
                mutex.WaitOne();
                _taken.Set();
                _finish.Wait();
                if (_release) mutex.ReleaseMutex();
                // not disposed when abandoning: the thread just ends while owning it
            }) { IsBackground = true };
            _thread.Start();
            _taken.Wait();
        }

        public void Abandon()
        {
            _release = false;
            _finish.Set();
            _thread.Join();
        }

        public void Dispose()
        {
            _finish.Set();
            _thread.Join();
        }
    }

    [Fact]
    public void ALiveSessionsFile_IsNotOffered()
    {
        var live = PlantRecovery("A1", "live", TimeSpan.Zero);
        var free = PlantRecovery("A1", "free", TimeSpan.FromMinutes(1));
        using var other = new OtherSession(live.SessionId);

        Assert.Equal([free.SessionId], _store.List().Select(f => f.SessionId));
        var vm = NewViewModel();
        vm.Startup(null);
        var dialog = Assert.Single(_dialogs.Recoveries);
        Assert.Equal(free.SessionId, Assert.Single(dialog.Items).File.SessionId);
        Assert.True(File.Exists(live.Path));
    }

    [Fact]
    public void AFileWhoseSessionBecomesLive_CannotBeAdoptedOrDiscarded()
    {
        var planted = PlantRecovery("A1", "taken", TimeSpan.Zero);
        OtherSession? other = null;
        bool closed = false;
        _dialogs.OnRecovery = d =>
        {
            d.CloseRequested += (_, _) => closed = true;
            other = new OtherSession(planted.SessionId); // another instance adopted it after the dialog was built
            var item = d.Items[0];
            item.RestoreCommand.Execute(null);
            Assert.Null(d.Restored);
            Assert.Equal("This file is in use by another Gridline window.", item.Status);
            item.DiscardCommand.Execute(null);
            Assert.Equal("Could not delete this file.", item.Status);
            Assert.Single(d.Items);
        };
        try
        {
            var vm = NewViewModel();
            Assert.False(vm.Startup(null));
            Assert.False(closed);
            Assert.Equal("Gridline - Untitled", vm.Title);
            Assert.NotEqual(planted.SessionId, vm.SessionId);
            Assert.True(File.Exists(planted.Path));
            Assert.Equal("", vm.Workbook.GetInput("A1"));
        }
        finally { other?.Dispose(); }
    }

    [Fact]
    public void AnAbandonedOrReleasedSessionsFile_IsOffered_AndCanBeRestored()
    {
        var abandoned = PlantRecovery("A1", "abandoned", TimeSpan.Zero);
        var released = PlantRecovery("A1", "released", TimeSpan.FromMinutes(1));
        new OtherSession(abandoned.SessionId).Abandon(); // its owner died holding it: counts as free
        new OtherSession(released.SessionId).Dispose();

        Assert.Equal([abandoned.SessionId, released.SessionId], _store.List().Select(f => f.SessionId));
        _dialogs.OnRecovery = d => d.Items[0].RestoreCommand.Execute(null);
        var vm = NewViewModel();
        Assert.True(vm.Startup(null));
        Assert.Equal(abandoned.SessionId, vm.SessionId);
        Assert.Equal("abandoned", vm.Workbook.GetInput("A1"));
    }

    [Fact]
    public void ASession_HoldsItsGuidUntilACleanExit_AndARestoreMovesTheLockToTheAdoptedGuid()
    {
        var planted = PlantRecovery("A1", "1", TimeSpan.Zero);
        _dialogs.OnRecovery = d => d.Items[0].RestoreCommand.Execute(null);
        var vm = NewViewModel();
        Guid original = vm.SessionId;
        Assert.True(HeldOnAnotherThread(original)); // claimed at session start

        vm.Startup(null); // restores the planted file
        Assert.Equal(planted.SessionId, vm.SessionId);
        Assert.True(HeldOnAnotherThread(planted.SessionId));
        Assert.False(HeldOnAnotherThread(original)); // released after adopting

        vm.EndSession();
        Assert.False(HeldOnAnotherThread(planted.SessionId));
    }

    private static bool HeldOnAnotherThread(Guid sessionId)
    {
        bool held = false;
        var t = new Thread(() => held = new NamedMutexSessionLock().IsHeldElsewhere(sessionId));
        t.Start();
        t.Join();
        return held;
    }
}
