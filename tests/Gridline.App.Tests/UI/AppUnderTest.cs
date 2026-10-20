using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace Gridline.App.Tests.UI;

/// <summary>Non-parallel collection for FlaUI tests: they share the desktop, keyboard and focus.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiCollection
{
    public const string Name = "UI";
}

/// <summary>
/// Launches the built Gridline.App.exe that the project reference copies beside this test assembly, and always closes it
/// (Dispose: close, then kill if it does not exit).
/// </summary>
internal sealed class AppUnderTest : IDisposable
{
    public static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    public static string ExePath
    {
        get
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Gridline.App.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("Gridline.App.exe is not beside the test assembly; build tests/Gridline.App.Tests.", path);
            return path;
        }
    }

    public Application Application { get; }
    public UIA3Automation Automation { get; } = new();

    private AppUnderTest(Application application) => Application = application;

    public static AppUnderTest Launch(params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return new AppUnderTest(Application.Launch(psi));
    }

    /// <summary>The top-level window whose title satisfies <paramref name="predicate"/>, waiting up to <see cref="UiTimeout"/>.</summary>
    public Window WaitForWindow(Func<string, bool> predicate)
    {
        Window? found = null;
        Retry.WhileNull(() =>
        {
            found = Application.GetAllTopLevelWindows(Automation).FirstOrDefault(w => predicate(w.Title ?? ""));
            return found;
        }, UiTimeout, TimeSpan.FromMilliseconds(100), ignoreException: true);
        return found ?? throw new TimeoutException("No matching window appeared.");
    }

    public Window MainWindow() => WaitForWindow(t => t.StartsWith("Gridline - ", StringComparison.Ordinal));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Moves <paramref name="window"/> above every other non-topmost window without activating it (topmost on,
    /// then off). Mouse clicks are then delivered to it even when another application overlaps it.</summary>
    public static void RaiseToTop(Window window)
    {
        const uint NoSizeNoMoveNoActivate = 0x0001 | 0x0002 | 0x0010;
        IntPtr hwnd = window.Properties.NativeWindowHandle.Value;
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, NoSizeNoMoveNoActivate); // HWND_TOPMOST
        SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, NoSizeNoMoveNoActivate); // HWND_NOTOPMOST, stays on top of the rest
    }

    public static AutomationElement Find(AutomationElement root, string automationId)
    {
        AutomationElement? e = null;
        Retry.WhileNull(() => e = root.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), UiTimeout, TimeSpan.FromMilliseconds(100), ignoreException: true);
        return e ?? throw new InvalidOperationException($"AutomationId '{automationId}' not found.");
    }

    /// <summary>Polls until <paramref name="read"/> returns <paramref name="expected"/>; returns the elapsed time.</summary>
    public static TimeSpan WaitFor(Func<string?> read, string expected, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? UiTimeout;
        string? last = null;
        while (sw.Elapsed < limit)
        {
            try { last = read(); } catch (Exception) { last = null; }
            if (last == expected) return sw.Elapsed;
            Thread.Sleep(10);
        }
        throw new TimeoutException($"Expected '{expected}', last value '{last}' after {limit.TotalSeconds} s.");
    }

    public void Dispose()
    {
        try
        {
            if (!Application.HasExited) Application.Close(killIfCloseFails: true);
        }
        catch (Exception)
        {
            try { if (!Application.HasExited) Application.Kill(); } catch (Exception) { /* already gone */ }
        }
        finally
        {
            Application.Dispose();
            Automation.Dispose();
        }
    }
}
