using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Gridline.Core;
using Gridline.IO;
using static Gridline.App.Tests.UI.AppUnderTest;

namespace Gridline.App.Tests.UI;

/// <summary>FlaUI acceptance for the app shell (§11.1-§11.3, §11.7, R44). Each test closes the app in Dispose (finally).</summary>
[Collection(UiCollection.Name)]
[Trait("Category", "UI")]
public class ShellUiTests
{
    private static readonly string[] TopLevelIds = ["NameBox", "FormulaBar", "Grid", "SelectionValue", "SelectionSum", "MenuFile", "MenuEdit"];

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
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private static void Press(VirtualKeyShort key) => Keyboard.TypeScanCode(ExtendedScanCode(key), isExtendedKey: true);

    private static void Chord(VirtualKeyShort modifier, VirtualKeyShort key)
    {
        Keyboard.Press(modifier);
        try { Press(key); }
        finally { Keyboard.Release(modifier); }
    }

    [Fact]
    public void Launch_HasTitleAndEveryAutomationId_AndArrowsMoveTheActiveCell()
    {
        using var app = Launch();
        Window window = app.MainWindow();
        Assert.Equal("Gridline - Untitled", window.Title);

        foreach (string id in TopLevelIds) Assert.NotNull(Find(window, id));

        AutomationElement grid = Find(window, "Grid");
        Assert.Equal(ControlType.DataGrid, grid.ControlType);
        Assert.True(grid.Properties.IsKeyboardFocusable.Value);
        window.SetForeground();
        Retry(() => grid.Properties.HasKeyboardFocus.Value, "grid has keyboard focus at launch");
        Activate(window, grid);

        TextBox nameBox = Find(window, "NameBox").AsTextBox();
        AutomationElement selectionValue = Find(window, "SelectionValue");
        AutomationElement selectionSum = Find(window, "SelectionSum");
        Assert.Equal("A1", nameBox.Text);
        Assert.Equal("BLANK", selectionValue.Name);
        Assert.Equal("0", selectionSum.Name);

        Press(VirtualKeyShort.RIGHT);
        WaitFor(() => nameBox.Text, "B1");
        Press(VirtualKeyShort.DOWN);
        WaitFor(() => nameBox.Text, "B2");
        Press(VirtualKeyShort.LEFT);
        WaitFor(() => nameBox.Text, "A2");
        Press(VirtualKeyShort.UP);
        Press(VirtualKeyShort.UP); // clamped at row 1
        WaitFor(() => nameBox.Text, "A1");
        // Submenu items exist in the UIA tree once their menu is expanded (checked last: menus take keyboard focus).
        AssertSubmenuItems(window, "MenuFile", "MenuOpen", "MenuSave");
        AssertSubmenuItems(window, "MenuEdit", "MenuUndo", "MenuRedo");
        Assert.Equal("Gridline - Untitled", window.Title);
    }

    [Fact]
    public void FileArgument_ShowsValues_AndNavigatingToZZ100000StaysResponsive()
    {
        string dir = TestSupport.NewTempDirectory();
        try
        {
            string path = Path.Combine(dir, "far.gridline");
            GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "5"), new("A2", "=A1*2"), new("ZZ100000", "42"), new("ZZ99999", "8")]), path);

            using var app = Launch(path); // R44
            Window window = app.MainWindow();
            Assert.Equal("Gridline - far.gridline", window.Title);
            TextBox nameBox = Find(window, "NameBox").AsTextBox();
            AutomationElement selectionValue = Find(window, "SelectionValue");
            AutomationElement selectionSum = Find(window, "SelectionSum");
            Assert.Equal("N:5", selectionValue.Name);
            window.SetForeground();
            Retry(() => Find(window, "Grid").Properties.HasKeyboardFocus.Value, "grid has keyboard focus at launch");
            Activate(window, Find(window, "Grid"));

            // Ctrl+End jumps to the bottom-right of the used area: ZZ100000, scrolling the grid there.
            var jump = TimeSpanOf(() =>
            {
                Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
                WaitFor(() => nameBox.Text, "ZZ100000");
            });
            Assert.Equal("N:42", selectionValue.Name);
            Assert.True(jump < TimeSpan.FromSeconds(2), $"Ctrl+End took {jump.TotalMilliseconds} ms");

            // Keep moving around the far corner; every step must round-trip quickly.
            var slowest = TimeSpan.Zero;
            string[] expected = ["ZZ99999", "ZY99999", "ZY100000", "ZZ100000"];
            VirtualKeyShort[] keys = [VirtualKeyShort.UP, VirtualKeyShort.LEFT, VirtualKeyShort.DOWN, VirtualKeyShort.RIGHT];
            for (int round = 0; round < 3; round++)
                for (int i = 0; i < keys.Length; i++)
                {
                    var t = TimeSpanOf(() => { Press(keys[i]); WaitFor(() => nameBox.Text, expected[i]); });
                    if (t > slowest) slowest = t;
                }
            Assert.True(slowest < TimeSpan.FromSeconds(1), $"slowest arrow step {slowest.TotalMilliseconds} ms");

            // Shift+Up selects ZZ99999:ZZ100000: R43 sum of 8 and 42.
            Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.UP);
            WaitFor(() => selectionSum.Name, "50");

            // Scrolling the viewport to the end through UIA (ScrollViewer + IScrollInfo) also stays responsive.
            var scroller = Find(window, "GridScroller");
            var scroll = scroller.Patterns.Scroll.Pattern;
            var scrollTime = TimeSpanOf(() =>
            {
                scroll.SetScrollPercent(0, 0);
                WaitFor(() => scroll.VerticalScrollPercent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "0");
                scroll.SetScrollPercent(100, 100);
                WaitFor(() => scroll.VerticalScrollPercent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "100");
            });
            Assert.True(scrollTime < TimeSpan.FromSeconds(2), $"scrolling took {scrollTime.TotalMilliseconds} ms");

            Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
            WaitFor(() => nameBox.Text, "A1");
            Assert.Equal("N:5", selectionValue.Name);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void UnreadableFileArgument_ShowsErrorThenStartsUntitled()
    {
        string missing = Path.Combine(Path.GetTempPath(), "gridline-app-tests", Guid.NewGuid().ToString("N"), "missing.gridline");
        using var app = Launch(missing);
        Window box = app.WaitForWindow(t => t == "Gridline");
        Assert.Contains(box.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)), e => (e.Name ?? "").Contains("missing.gridline"));
        (box.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("OK"))) ?? throw new InvalidOperationException("OK button not found")).AsButton().Invoke();

        Window window = app.MainWindow();
        Assert.Equal("Gridline - Untitled", window.Title);
    }

    private static void AssertSubmenuItems(Window window, string menuId, params string[] itemIds)
    {
        var menu = Find(window, menuId).AsMenuItem();
        menu.Expand();
        try
        {
            foreach (string id in itemIds) Assert.NotNull(Find(menu, id));
        }
        finally { menu.Collapse(); }
    }

    /// <summary>Clicks the window's title bar so it is the foreground window and receives the injected keys (SetForeground
    /// alone is not always honoured when the test host does not own the foreground); WPF restores focus to the grid.</summary>
    private static void Activate(Window window, AutomationElement grid)
    {
        AppUnderTest.RaiseToTop(window); // so the title-bar click lands on this window, not one overlapping it
        var r = window.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(r.Left + r.Width / 2, r.Top + 12));
        Retry(() => grid.Properties.HasKeyboardFocus.Value, "grid has keyboard focus once the window is active");
    }

    private static TimeSpan TimeSpanOf(Action action)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        action();
        return sw.Elapsed;
    }

    private static void Retry(Func<bool> condition, string what)
    {
        var result = FlaUI.Core.Tools.Retry.WhileFalse(condition, UiTimeout, TimeSpan.FromMilliseconds(50), ignoreException: true);
        Assert.True(result.Result, what);
    }
}
