using System.Globalization;

namespace Gridline.App.Services;

/// <summary>The autosave interval (§11.6): 60 s, unless the test seam <c>GRIDLINE_AUTOSAVE_INTERVAL_MS</c> (R51) holds a
/// positive whole number of milliseconds. Any other value of the variable is ignored.</summary>
public static class AutosaveSettings
{
    public const string IntervalVariable = "GRIDLINE_AUTOSAVE_INTERVAL_MS";

    public static TimeSpan DefaultInterval => TimeSpan.FromSeconds(60);

    /// <summary>The interval for this process, read from the environment.</summary>
    public static TimeSpan IntervalFromEnvironment() => IntervalFrom(Environment.GetEnvironmentVariable(IntervalVariable));

    /// <summary>The interval a value of <c>GRIDLINE_AUTOSAVE_INTERVAL_MS</c> selects (null or invalid: the default).</summary>
    public static TimeSpan IntervalFrom(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : DefaultInterval;
}
