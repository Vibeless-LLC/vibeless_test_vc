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

    /// <summary>The <c>LOCALAPPDATA</c> the app was started with; its recovery files are in
    /// <see cref="RecoveryDirectory"/> (§11.6, R51).</summary>
    public string LocalAppData { get; }

    public string RecoveryDirectory => Path.Combine(LocalAppData, "Gridline", "Recovery");

    private readonly string? _ownedLocalAppData;

    private AppUnderTest(Application application, string localAppData, string? owned)
    {
        Application = application;
        LocalAppData = localAppData;
        _ownedLocalAppData = owned;
    }

    /// <summary>Starts the app with <paramref name="args"/> and a fresh, empty <c>LOCALAPPDATA</c> of its own (deleted in
    /// Dispose), so no test sees or leaves recovery files in the real profile and no RecoveryDialog appears.</summary>
    public static AppUnderTest Launch(params string[] args) => LaunchWith(null, null, args);

    /// <summary>Starts the app with <c>LOCALAPPDATA</c> = <paramref name="localAppData"/> (null: a fresh owned temp
    /// directory) and, when given, <c>GRIDLINE_AUTOSAVE_INTERVAL_MS</c> = <paramref name="autosaveIntervalMs"/> (R51).</summary>
    public static AppUnderTest LaunchWith(string? localAppData, int? autosaveIntervalMs, params string[] args)
    {
        string? owned = null;
        if (localAppData is null)
        {
            owned = localAppData = Path.Combine(Path.GetTempPath(), "gridline-app-tests", "lad-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(owned);
        }
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["LOCALAPPDATA"] = localAppData;
        if (autosaveIntervalMs is { } ms) psi.Environment["GRIDLINE_AUTOSAVE_INTERVAL_MS"] = ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else psi.Environment.Remove("GRIDLINE_AUTOSAVE_INTERVAL_MS");
        return new AppUnderTest(Application.Launch(psi), localAppData, owned);
    }

    /// <summary>Ends the process at once, as a crash would: no prompt, no clean exit, recovery files stay.</summary>
    public void Kill()
    {
        using var process = Process;
        process.Kill();
        process.WaitForExit(10_000);
    }

    /// <summary>The RecoveryDialog window (§11.7) if it is open, else null.</summary>
    public AutomationElement? FindRecoveryDialog()
    {
        foreach (var w in Application.GetAllTopLevelWindows(Automation))
        {
            if (w.Properties.AutomationId.ValueOrDefault == "RecoveryDialog") return w;
            if (w.FindFirstChild(cf => cf.ByAutomationId("RecoveryDialog")) is { } child) return child;
        }
        return null;
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

    /// <summary>The app's process (for WM_CLOSE through <see cref="Process.CloseMainWindow"/> and exit checks).</summary>
    public Process Process => Process.GetProcessById(Application.ProcessId);

    /// <summary>The unsaved-changes prompt window (R45) if it is open, else null. It is owned by the main window, so UIA
    /// may list it as a top-level window or as the main window's child.</summary>
    public AutomationElement? FindPrompt()
    {
        foreach (var w in Application.GetAllTopLevelWindows(Automation))
        {
            if (w.Properties.AutomationId.ValueOrDefault == "UnsavedChangesPrompt") return w;
            if (w.FindFirstChild(cf => cf.ByAutomationId("UnsavedChangesPrompt")) is { } child) return child;
        }
        return null;
    }

    /// <summary>Closes the app: WM_CLOSE to the main window; a dirty workbook's prompt (§11.5) is answered Don't Save;
    /// anything still running after that is killed.</summary>
    public void Dispose()
    {
        try
        {
            if (!Application.HasExited)
            {
                using var process = Process;
                process.CloseMainWindow(); // posted, like the close button
                if (!process.WaitForExit(2000))
                {
                    try
                    {
                        if (FindPrompt()?.FindFirstDescendant(cf => cf.ByAutomationId("PromptDontSave"))?.AsButton() is { } dontSave)
                            dontSave.Invoke();
                    }
                    catch (Exception) { /* fall through to the kill */ }
                    if (!process.WaitForExit(3000)) process.Kill();
                }
            }
        }
        catch (Exception)
        {
            try { if (!Application.HasExited) Application.Kill(); } catch (Exception) { /* already gone */ }
        }
        finally
        {
            Application.Dispose();
            Automation.Dispose();
            if (_ownedLocalAppData is not null)
            {
                try { Directory.Delete(_ownedLocalAppData, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            }
        }
    }
}
