using System.Windows.Threading;

namespace Gridline.App.Services;

/// <summary><see cref="IAutosaveTimer"/> over a <see cref="DispatcherTimer"/>, so ticks run on the UI thread, where the
/// workbook lives (§11.6).</summary>
public sealed class DispatcherAutosaveTimer : IAutosaveTimer
{
    private readonly DispatcherTimer _timer;

    public DispatcherAutosaveTimer(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        _timer.Tick += (_, e) => Tick?.Invoke(this, e);
    }

    public event EventHandler? Tick;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();
}
