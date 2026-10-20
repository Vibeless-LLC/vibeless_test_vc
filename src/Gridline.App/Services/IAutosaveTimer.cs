namespace Gridline.App.Services;

/// <summary>The autosave clock (§11.6): raises <see cref="Tick"/> every interval once started. The app uses
/// <see cref="DispatcherAutosaveTimer"/>; ViewModel tests fire ticks by hand.</summary>
public interface IAutosaveTimer
{
    /// <summary>Raised on the UI thread each time the interval elapses while the timer runs.</summary>
    event EventHandler? Tick;

    void Start();

    void Stop();
}
