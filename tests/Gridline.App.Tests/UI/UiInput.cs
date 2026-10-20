using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using static Gridline.App.Tests.UI.AppUnderTest;

namespace Gridline.App.Tests.UI;

/// <summary>Keyboard and focus helpers shared by the FlaUI tests (D-37).</summary>
internal static class UiInput
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static readonly VirtualKeyShort[] Modifiers =
    [
        VirtualKeyShort.LSHIFT, VirtualKeyShort.RSHIFT, VirtualKeyShort.SHIFT,
        VirtualKeyShort.LCONTROL, VirtualKeyShort.RCONTROL, VirtualKeyShort.CONTROL,
        VirtualKeyShort.LMENU, VirtualKeyShort.RMENU, VirtualKeyShort.ALT,
    ];

    /// <summary>Releases any Shift, Ctrl or Alt the system still reports as down. Injected chords occasionally leave a
    /// modifier down at the OS level after a UI test (observed: Shift), which then leaks into later keyboard use on the
    /// desktop, including in-process WPF tests that read <c>Keyboard.Modifiers</c>. Called when each app is disposed.</summary>
    public static void ReleaseStuckModifiers()
    {
        foreach (var key in Modifiers)
            if (GetAsyncKeyState((int)key) < 0) Keyboard.Release(key);
    }

    /// <summary>Navigation keys are sent as extended scan codes. Sent as bare virtual keys they look like numpad keys, and
    /// with NumLock on Windows injects a Shift release before them, so Shift+arrow would arrive as a plain arrow.</summary>
    private static ushort ExtendedScanCode(VirtualKeyShort key) => key switch
    {
        VirtualKeyShort.UP => 0x48,
        VirtualKeyShort.DOWN => 0x50,
        VirtualKeyShort.LEFT => 0x4B,
        VirtualKeyShort.RIGHT => 0x4D,
        VirtualKeyShort.HOME => 0x47,
        VirtualKeyShort.END => 0x4F,
        VirtualKeyShort.DELETE => 0x53,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>Presses a navigation-cluster key (arrows, Home, End, Delete).</summary>
    public static void Press(VirtualKeyShort key) => Keyboard.TypeScanCode(ExtendedScanCode(key), isExtendedKey: true);

    public static void Chord(VirtualKeyShort modifier, VirtualKeyShort key)
    {
        Keyboard.Press(modifier);
        try { Press(key); }
        finally { Keyboard.Release(modifier); }
    }

    /// <summary>Ctrl plus a letter key (Ctrl+C, Ctrl+V, Ctrl+S).</summary>
    public static void Ctrl(VirtualKeyShort key) => Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, key);

    /// <summary>Opens a menu and clicks one of its items with the mouse. A click returns at once even when the item opens
    /// a modal dialog (a UIA Invoke could wait for the dialog).</summary>
    public static void ClickMenu(Window window, string menuId, string itemId)
    {
        RaiseToTop(window);
        var menu = Find(window, menuId).AsMenuItem();
        menu.Click();
        Find(window, itemId).AsMenuItem().Click();
    }

    /// <summary>Clicks the window's title bar so it is the foreground window and receives the injected keys (SetForeground
    /// alone is not always honoured when the test host does not own the foreground); WPF restores focus to the grid.</summary>
    public static void Activate(Window window, AutomationElement grid)
    {
        RaiseToTop(window); // so the title-bar click lands on this window, not one overlapping it
        var r = window.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(r.Left + r.Width / 2, r.Top + 12));
        Retry(() => grid.Properties.HasKeyboardFocus.Value, "grid has keyboard focus once the window is active");
    }

    public static TimeSpan TimeSpanOf(Action action)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        action();
        return sw.Elapsed;
    }

    public static void Retry(Func<bool> condition, string what)
    {
        var result = FlaUI.Core.Tools.Retry.WhileFalse(condition, UiTimeout, TimeSpan.FromMilliseconds(50), ignoreException: true);
        Assert.True(result.Result, what);
    }
}
