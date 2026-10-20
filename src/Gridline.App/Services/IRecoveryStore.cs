using Gridline.Core;

namespace Gridline.App.Services;

/// <summary>A recovery file left by a session (§11.6): <c>&lt;SessionId&gt;.gridline</c> at <paramref name="Path"/>.</summary>
public sealed record RecoveryFile(Guid SessionId, string Path, DateTime LastWriteTime);

/// <summary>
/// Where autosave keeps recovery files (§11.6, R48): one <c>&lt;session-guid&gt;.gridline</c> per session. The app uses
/// <see cref="DirectoryRecoveryStore"/> over <c>%LOCALAPPDATA%\Gridline\Recovery</c>; tests point it at a temp directory.
/// </summary>
public interface IRecoveryStore
{
    /// <summary>The recovery file path of <paramref name="sessionId"/>.</summary>
    string PathFor(Guid sessionId);

    /// <summary>The recovery files present now that no live session owns (their guid's lock is not held by another
    /// process), newest first; empty when there are none or the directory cannot be read. Never throws.</summary>
    IReadOnlyList<RecoveryFile> List();

    /// <summary>Takes ownership of <paramref name="sessionId"/> for this session (D-40); false when another live session
    /// owns it, in which case its file must not be adopted.</summary>
    bool TryClaim(Guid sessionId);

    /// <summary>Gives up ownership of <paramref name="sessionId"/> (clean exit, or after adopting another guid).</summary>
    void Release(Guid sessionId);

    /// <summary>True when another live session owns <paramref name="sessionId"/>, so its file must not be discarded.</summary>
    bool IsInUse(Guid sessionId);

    /// <summary>Saves <paramref name="workbook"/> atomically as the recovery file of <paramref name="sessionId"/>,
    /// creating the directory on demand. Does not mark the workbook saved.</summary>
    /// <exception cref="Gridline.IO.GridlineIOException">The file could not be written.</exception>
    /// <exception cref="System.IO.IOException">The directory could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory could not be created.</exception>
    void Write(Workbook workbook, Guid sessionId);

    /// <summary>Deletes the recovery file of <paramref name="sessionId"/> if it exists. Never throws; returns false
    /// when a file is still there afterwards.</summary>
    bool TryDelete(Guid sessionId);
}
