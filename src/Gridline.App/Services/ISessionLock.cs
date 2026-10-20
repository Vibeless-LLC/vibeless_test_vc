namespace Gridline.App.Services;

/// <summary>
/// Ownership of a session guid across processes (§11.6, R50, D-40): a session holds its guid's lock for its lifetime, so
/// another Gridline process neither offers nor adopts a live session's recovery file. A killed process loses its locks.
/// </summary>
public interface ISessionLock
{
    /// <summary>Takes the lock of <paramref name="sessionId"/> for this process; true when it is ours now (also when it
    /// already was), false when another process holds it.</summary>
    bool TryAcquire(Guid sessionId);

    /// <summary>Releases a lock this process holds; nothing happens when it holds none.</summary>
    void Release(Guid sessionId);

    /// <summary>True when another process (or another thread) holds the lock of <paramref name="sessionId"/>. An abandoned
    /// lock is free.</summary>
    bool IsHeldElsewhere(Guid sessionId);
}
