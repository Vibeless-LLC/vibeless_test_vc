using System.Globalization;

namespace Gridline.App.Services;

/// <summary>
/// <see cref="ISessionLock"/> over named mutexes <c>Local\Gridline.Recovery.&lt;guid D&gt;</c> (D-40). A mutex is owned by
/// the thread that took it, so acquire and release run on the same thread (the app's UI thread). The OS releases a killed
/// process's mutex as abandoned, and an abandoned mutex counts as free.
/// </summary>
public sealed class NamedMutexSessionLock : ISessionLock
{
    private readonly Dictionary<Guid, Mutex> _held = [];

    public static string MutexName(Guid sessionId) =>
        @"Local\Gridline.Recovery." + sessionId.ToString("D", CultureInfo.InvariantCulture);

    public bool TryAcquire(Guid sessionId)
    {
        if (_held.ContainsKey(sessionId)) return true;
        var mutex = new Mutex(false, MutexName(sessionId));
        if (!TryTake(mutex))
        {
            mutex.Dispose();
            return false;
        }
        _held[sessionId] = mutex;
        return true;
    }

    public void Release(Guid sessionId)
    {
        if (!_held.Remove(sessionId, out Mutex? mutex)) return;
        try { mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* not owned by this thread any more; disposing closes our handle */ }
        mutex.Dispose();
    }

    public bool IsHeldElsewhere(Guid sessionId)
    {
        if (_held.ContainsKey(sessionId)) return false;
        using var mutex = new Mutex(false, MutexName(sessionId));
        if (!TryTake(mutex)) return true;
        mutex.ReleaseMutex();
        return false;
    }

    private static bool TryTake(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; } // its owner died: now ours
    }
}
