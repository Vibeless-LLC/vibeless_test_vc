using System.IO;
using Gridline.App.Services;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.App.ViewModels;

/// <summary>
/// Autosave and recovery (§11.6, R48, R50, R51): while dirty, every timer tick saves the workbook atomically as this
/// session's recovery file (<see cref="IRecoveryStore"/>) without marking it saved; a successful Save, adopting another
/// workbook and a clean exit delete it. At startup recovery files are offered in the RecoveryDialog before the file
/// argument is opened. See DECISIONS D-40.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly IRecoveryStore? _recovery;
    private readonly IAutosaveTimer? _autosaveTimer;
    private Guid _sessionId = Guid.NewGuid();
    private bool _restoredUnsaved; // a restored workbook is dirty until saved (R50), with no undo step behind it
    private bool _sessionEnded;

    /// <summary>The session guid that names this session's recovery file. A restore adopts the restored file's guid.</summary>
    public Guid SessionId => _sessionId;

    /// <summary>This session's recovery file path, or null when there is no recovery store.</summary>
    public string? RecoveryPath => _recovery?.PathFor(_sessionId);

    /// <summary>
    /// Startup (§11.6, R44, R50): offers the recovery files, if any, in the RecoveryDialog; then, unless one was restored,
    /// opens <paramref name="fileArgument"/> (on failure an error is shown and it stays Untitled); then starts autosave.
    /// </summary>
    /// <returns>True when a recovery file was restored.</returns>
    public bool Startup(string? fileArgument)
    {
        bool restored = OfferRecovery();
        if (!restored && fileArgument is not null) TryLoad(fileArgument);
        _autosaveTimer?.Start();
        return restored;
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

    /// <summary>A clean exit (§11.6, R48; the window has closed, also after Don't Save): stops autosave and deletes the
    /// recovery file. A killed process never gets here, so its file stays for the next startup.</summary>
    public void EndSession()
    {
        if (_sessionEnded) return;
        _sessionEnded = true;
        _autosaveTimer?.Stop();
        DeleteRecoveryFile();
    }

    private void OnAutosaveTick(object? sender, EventArgs e) => Autosave();

    private void DeleteRecoveryFile() => _recovery?.TryDelete(_sessionId);

    private bool OfferRecovery()
    {
        if (_recovery is null) return false;
        IReadOnlyList<RecoveryFile> files = _recovery.List();
        if (files.Count == 0) return false;
        var recovery = new RecoveryViewModel(files, Restore, file => _recovery.TryDelete(file.SessionId));
        _dialogs.ShowRecovery(recovery);
        return recovery.Restored is not null;
    }

    /// <summary>R50: the restored workbook becomes the window's Untitled, dirty workbook, and the session adopts the
    /// restored file's guid, so the next autosave replaces that file and a Save or clean exit deletes it.</summary>
    private void Restore(RecoveryFile file, Workbook workbook)
    {
        Adopt(workbook, null); // deletes the current session's own recovery file, if any
        _sessionId = file.SessionId;
        _restoredUnsaved = true;
        RefreshDocumentState();
    }
}
