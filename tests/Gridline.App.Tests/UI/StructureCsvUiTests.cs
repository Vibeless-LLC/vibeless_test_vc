using System.Text;
using Xunit.Abstractions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using static Gridline.App.Tests.UI.AppUnderTest;
using static Gridline.App.Tests.UI.UiInput;

namespace Gridline.App.Tests.UI;

/// <summary>FlaUI acceptance for Phase 3's GUI additions (§11.5, §11.3, §11.7, R74, R75, D-49): File > Import CSV and
/// Export CSV through the Win32 dialogs, and the header context menu's Insert (with Ctrl+Z) and Delete.</summary>
[Collection(UiCollection.Name)]
[Trait("Category", "UI")]
public sealed class StructureCsvUiTests(ITestOutputHelper output) : IDisposable
{
    private readonly ITestOutputHelper _output = UiLog.Attach(output);

    private readonly string _dir = TestSupport.NewTempDirectory();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* the app may still be closing */ }
    }

    private sealed record Ui(Window Window, AutomationElement Grid, TextBox NameBox, AutomationElement SelectionValue, AutomationElement SelectionSum);

    private static Ui Start(AppUnderTest app)
    {
        Window window = app.MainWindow();
        var ui = new Ui(window, Find(window, "Grid"), Find(window, "NameBox").AsTextBox(), Find(window, "SelectionValue"),
            Find(window, "SelectionSum"));
        window.SetForeground();
        Retry(() => ui.Grid.Properties.HasKeyboardFocus.Value, "grid has keyboard focus at launch");
        Activate(window, ui.Grid);
        return ui;
    }

    /// <summary>A Win32 file dialog opened from the File menu (an owned modal, or a top-level window with that title).</summary>
    private static Window FileDialog(AppUnderTest app, Ui ui, string menuItem, string title)
    {
        ClickMenu(ui.Window, "MenuFile", menuItem);
        Window? dialog = null;
        Retry(() => (dialog = app.Application.GetAllTopLevelWindows(app.Automation).FirstOrDefault(w => w.Title == title)
                     ?? ui.Window.ModalWindows.FirstOrDefault()) is not null, $"the {title} dialog is shown");
        return dialog!;
    }

    private static void EnterFileName(AutomationElement dialog, string fileNameBoxId, string path)
    {
        var fileName = (dialog.FindFirstDescendant(cf => cf.ByAutomationId(fileNameBoxId).And(cf.ByControlType(ControlType.Edit)))
                        ?? throw new InvalidOperationException("file name box not found")).AsTextBox();
        fileName.Text = path;
        Retry(() => fileName.Text == path, "the file name is entered");
        fileName.Focus();
        Keyboard.Type(VirtualKeyShort.RETURN);
    }

    private static void TypeCell(string text, VirtualKeyShort commitKey = VirtualKeyShort.RETURN)
    {
        Keyboard.Type(text);
        Keyboard.Type(commitKey);
    }

    /// <summary>Right-clicks a row or column header and invokes a context menu item (R75).</summary>
    private static void HeaderMenu(AppUnderTest app, Ui ui, System.Drawing.Point header, string itemId)
    {
        RaiseToTop(ui.Window);
        Mouse.RightClick(header);
        AutomationElement? item = null;
        Retry(() => (item = FindInProcess(app, itemId)) is not null, $"the context menu shows {itemId}");
        item!.AsMenuItem().Invoke();
        Retry(() => FindInProcess(app, itemId) is null, "the context menu closed");
    }

    [Fact]
    public void MenuImportCsv_ImportsThroughTheOpenDialog_AsAnUntitledCleanWorkbook()
    {
        string path = Path.Combine(_dir, "data.csv");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("10,20\r\nhello,TRUE\r\n"));
        using var app = Launch();
        Ui ui = Start(app);

        EnterFileName(FileDialog(app, ui, "MenuImportCsv", "Open"), "1148", path);

        WaitFor(() => ui.SelectionValue.Name, "N:10");
        Assert.Equal("Gridline - Untitled", ui.Window.Title); // R74: Untitled, not dirty
        Activate(ui.Window, ui.Grid);
        Press(VirtualKeyShort.RIGHT);
        WaitFor(() => ui.NameBox.Text, "B1");
        WaitFor(() => ui.SelectionValue.Name, "N:20");
        Press(VirtualKeyShort.DOWN);
        WaitFor(() => ui.SelectionValue.Name, "B:TRUE");
        Press(VirtualKeyShort.LEFT);
        WaitFor(() => ui.SelectionValue.Name, "T:hello");
        Assert.Equal("Gridline - Untitled", ui.Window.Title);
    }

    [Fact]
    public void MenuExportCsv_WritesTheCsvBytes_AndKeepsTheTitleAndDirtyState()
    {
        using var app = Launch();
        Ui ui = Start(app);
        TypeCell("1", VirtualKeyShort.TAB);
        TypeCell("a,b");          // A1=1, B1=a,b; Enter moves down from B1
        TypeCell("=A1*3");        // B2
        Retry(() => ui.Window.Title == "Gridline - Untitled *", "dirty after the edits");

        string path = Path.Combine(_dir, "out.csv");
        EnterFileName(FileDialog(app, ui, "MenuExportCsv", "Save As"), "1001", path);

        Retry(() => File.Exists(path), "the CSV was written");
        byte[] expected = [.. Encoding.UTF8.Preamble, .. "1,\"a,b\"\r\n,3\r\n"u8]; // §8.5 values, BOM, CRLF
        Retry(() => File.ReadAllBytes(path).AsSpan().SequenceEqual(expected), "the CSV bytes are the §8.5 export");
        Assert.Equal("Gridline - Untitled *", ui.Window.Title); // R74: no path, title or dirty change
    }

    [Fact]
    public void RowHeaderInsert_ShiftsDataDown_AndCtrlZRestores()
    {
        using var app = Launch();
        Ui ui = Start(app);
        TypeCell("1");
        TypeCell("2");
        TypeCell("3"); // A1..A3

        HeaderMenu(app, ui, RowHeaderPoint(ui, 2), "ContextInsert");

        WaitFor(() => ui.NameBox.Text, "A2");
        WaitFor(() => ui.SelectionValue.Name, "BLANK"); // the inserted row
        WaitFor(() => ui.SelectionSum.Name, "0");
        Activate(ui.Window, ui.Grid);
        Press(VirtualKeyShort.DOWN);
        WaitFor(() => ui.SelectionValue.Name, "N:2"); // A3 now holds what A2 held
        Press(VirtualKeyShort.DOWN);
        WaitFor(() => ui.SelectionValue.Name, "N:3");

        Ctrl(VirtualKeyShort.KEY_Z);
        WaitFor(() => ui.SelectionValue.Name, "BLANK"); // A4 is empty again
        Press(VirtualKeyShort.UP);
        WaitFor(() => ui.SelectionValue.Name, "N:3");
        Press(VirtualKeyShort.UP);
        WaitFor(() => ui.SelectionValue.Name, "N:2");

        // A left-click on the header selects the whole row; SelectionSum covers it (R75).
        RaiseToTop(ui.Window);
        Mouse.Click(RowHeaderPoint(ui, 3));
        WaitFor(() => ui.NameBox.Text, "A3");
        WaitFor(() => ui.SelectionSum.Name, "3");
    }

    [Fact]
    public void ColumnHeaderDelete_RemovesTheColumn()
    {
        using var app = Launch();
        Ui ui = Start(app);
        TypeCell("1", VirtualKeyShort.TAB);
        TypeCell("2", VirtualKeyShort.TAB);
        TypeCell("=B1*10"); // A1=1, B1=2, C1=20

        HeaderMenu(app, ui, ColumnHeaderPoint(ui, 2), "ContextDelete");

        WaitFor(() => ui.NameBox.Text, "B1");
        WaitFor(() => ui.SelectionValue.Name, "E:#REF!"); // C1's formula moved to B1 and lost its target
        Activate(ui.Window, ui.Grid);
        Press(VirtualKeyShort.LEFT);
        WaitFor(() => ui.SelectionValue.Name, "N:1");
        Press(VirtualKeyShort.RIGHT);
        Press(VirtualKeyShort.RIGHT);
        WaitFor(() => ui.NameBox.Text, "C1");
        WaitFor(() => ui.SelectionValue.Name, "BLANK");
        Assert.Equal("Gridline - Untitled *", ui.Window.Title);
    }

    // ---- geometry (GridMetrics: 60-DIP row headers, 22-DIP column header and rows, 80-DIP columns; no scrolling) ----

    private static System.Drawing.Point RowHeaderPoint(Ui ui, int row) => GridPoint(ui, 30, 22 + (row - 1) * 22 + 11);

    private static System.Drawing.Point ColumnHeaderPoint(Ui ui, int column) => GridPoint(ui, 60 + (column - 1) * 80 + 40, 11);

    private static System.Drawing.Point GridPoint(Ui ui, double dipX, double dipY)
    {
        double scale = GetDpiForWindow(ui.Window.Properties.NativeWindowHandle.Value) / 96.0;
        var r = ui.Grid.BoundingRectangle; // physical pixels
        return new System.Drawing.Point(r.Left + (int)Math.Round(dipX * scale), r.Top + (int)Math.Round(dipY * scale));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>An element of the app's process anywhere on the desktop: a context menu is its own popup window.</summary>
    private static AutomationElement? FindInProcess(AppUnderTest app, string automationId)
    {
        var desktop = app.Automation.GetDesktop();
        foreach (var top in desktop.FindAllChildren(cf => cf.ByProcessId(app.Application.ProcessId)))
            if (top.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is { } found) return found;
        return null;
    }
}
