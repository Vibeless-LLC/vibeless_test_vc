using System.IO;
using Gridline.App.Services;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.App.ViewModels;

/// <summary>
/// Autosave and recovery (§11.6, R48, R50, R51): while dirty, every timer tick saves the workbook atomically as this
/// session's recovery file (<see cref="IRecoveryStore"/>) without marking it saved; a successful Save, adopting another
/// workbook and a clean exit delete it. At startup recovery files are offered in the RecoveryDialog (owned by the shown main window)
/// before the file argument is opened. See DECISIONS D-40.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly IRecoveryStore? _recovery;
    private readonly IAutosaveTimer? _autosaveTimer;
    private Guid _sessionId = Guid.NewGuid();
    private bool _restoredUnsaved; // a restored workbook is dirty until saved (R50), with no undo step behind it
    private bool _sessionEnded;
    private bool _startupPending;

    /// <summary>
    /// False from a <see cref="BeginStartup"/> that left recovery pending until <see cref="CompleteStartup"/> has finished
    /// (D-40's window between the main window becoming visible and the RecoveryDialog; closed by D-49). While false every
    /// command is unavailable, selection, editing and header input are ignored, and the window's content is disabled, so
    /// no user action can reach the workbook before recovery has been offered.
    /// </summary>
    public bool IsReady
    {
        get => !_startupPending;
        private set
        {
            if (_startupPending == !value) return;
            _startupPending = !value;
            OnPropertyChanged();
            foreach (RelayCommand command in AllCommands) command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The session guid that names this session's recovery file. A restore adopts the restored file's guid.</summary>
    public Guid SessionId => _sessionId;

    /// <summary>This session's recovery file path, or null when there is no recovery store.</summary>
    public string? RecoveryPath => _recovery?.PathFor(_sessionId);

    private IReadOnlyList<RecoveryFile>? _pendingRecovery;
    private string? _pendingFileArgument;

    /// <summary>
    /// Startup in one call (§11.6, R44, R50): <see cref="BeginStartup"/> and, when recovery files are pending,
    /// <see cref="CompleteStartup"/>. The app itself calls the two halves around showing the main window (D-40).
    /// </summary>
    /// <returns>True when a recovery file was restored.</returns>
    public bool Startup(string? fileArgument) => BeginStartup(fileArgument) && CompleteStartup();

    /// <summary>
    /// First half of startup (§11.6, §11.7, D-40). With no recovery files (the usual case, and P-6's): opens
    /// <paramref name="fileArgument"/> (on failure an error is shown and it stays Untitled), starts autosave and returns
    /// false, so the window is shown with the file already loaded. With recovery files: does nothing yet and returns
    /// true; the caller shows the main window and then calls <see cref="CompleteStartup"/>, which shows the
    /// RecoveryDialog as the main window's owned modal.
    /// </summary>
    /// <returns>True when recovery is pending and <see cref="CompleteStartup"/> must be called.</returns>
    public bool BeginStartup(string? fileArgument)
    {
        IReadOnlyList<RecoveryFile> files = _recovery?.List() ?? []; // without live sessions' files (D-40)
        if (files.Count == 0)
        {
            if (fileArgument is not null) TryLoad(fileArgument);
            _autosaveTimer?.Start();
            return false;
        }
        _pendingRecovery = files;
        _pendingFileArgument = fileArgument;
        IsReady = false; // until CompleteStartup (D-49)
        return true;
    }

    /// <summary>Second half of startup (§11.6, D-40): offers the pending recovery files in the RecoveryDialog; a restore
    /// skips the file argument, otherwise the file argument is opened after the dialog has closed; then autosave starts.
    /// Does nothing when <see cref="BeginStartup"/> found no recovery files.</summary>
    /// <returns>True when a recovery file was restored.</returns>
    public bool CompleteStartup()
    {
        if (_pendingRecovery is not { } files) return false;
        _pendingRecovery = null;
        string? fileArgument = _pendingFileArgument;
        _pendingFileArgument = null;
        if (_sessionEnded) return false; // the window was closed before the dialog could be shown
        try
        {
            bool restored = OfferRecovery(files);
            if (!restored && fileArgument is not null) TryLoad(fileArgument);
            return restored;
        }
        finally
        {
            // Even when the dialog or a restore throws, the window must not stay disabled for good (D-49).
            _autosaveTimer?.Start();
            IsReady = true;
        }
    }

    /// <summary>One autosave tick (R48): while dirty, writes the recovery file; a failure is ignored and the next tick
    /// tries again. While clean, a recovery file left from an earlier dirty state is deleted (nothing to recover).</summary>
    /// <returns>True when a recovery file was written.</returns>
    internal bool Autosave()
    {
        if (_recovery is null || _sessionEnded) return false;
        if (!IsDirty)
        {
            _recovery.TryDelete(_sessionId);
            return false;
        }
        try
        {
            _recovery.Write(_workbook, _sessionId);
            return true;
        }
        catch (Exception ex) when (ex is GridlineIOException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false; // R48: retried on the next tick
        }
    }

    /// <summary>A clean exit (§11.6, R48; the window has closed, also after Don't Save): stops autosave, deletes the
    /// recovery file and releases the session's guid (D-40). A killed process never gets here, so its file stays for the
    /// next startup, and the OS releases its guid lock.</summary>
    public void EndSession()
    {
        if (_sessionEnded) return;
        _sessionEnded = true;
        _autosaveTimer?.Stop();
        DeleteRecoveryFile();
        _recovery?.Release(_sessionId);
    }

    private void OnAutosaveTick(object? sender, EventArgs e) => Autosave();

    private void DeleteRecoveryFile() => _recovery?.TryDelete(_sessionId);

    private bool OfferRecovery(IReadOnlyList<RecoveryFile> files)
    {
        if (_recovery is null) return false;
        var recovery = new RecoveryViewModel(files, Restore,
            file => !_recovery.IsInUse(file.SessionId) && _recovery.TryDelete(file.SessionId));
        _dialogs.ShowRecovery(recovery);
        return recovery.Restored is not null;
    }

    /// <summary>R50: the restored workbook becomes the window's Untitled, dirty workbook, and the session adopts the
    /// restored file's guid, so the next autosave replaces that file and a Save or clean exit deletes it. The restored
    /// guid's lock is taken first (D-40); when another live session holds it, nothing is adopted.</summary>
    /// <returns>False when the file is in use by another session.</returns>
    private bool Restore(RecoveryFile file, Workbook workbook)
    {
        if (_recovery is not null && !_recovery.TryClaim(file.SessionId)) return false;
        Adopt(workbook, null); // deletes the current session's own recovery file, if any
        _recovery?.Release(_sessionId);
        _sessionId = file.SessionId;
        _restoredUnsaved = true;
        RefreshDocumentState();
        return true;
    }
}
