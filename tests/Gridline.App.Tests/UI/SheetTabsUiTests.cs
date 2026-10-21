using Xunit.Abstractions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using static Gridline.App.Tests.UI.AppUnderTest;
using static Gridline.App.Tests.UI.UiInput;

namespace Gridline.App.Tests.UI;

/// <summary>FlaUI acceptance for the sheet tab strip and the qualified name box (CR1.8, §11.7, R88, D-59): SheetTabs with
/// SheetTab_0 and, after Add, SheetTab_1; a click activating a tab; the tab context menu's Add, Rename (RenameSheetBox)
/// and Delete; and the name box's <c>Data!B2</c> switching sheets.</summary>
[Collection(UiCollection.Name)]
[Trait("Category", "UI")]
public sealed class SheetTabsUiTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = UiLog.Attach(output);

    private sealed record Ui(Window Window, AutomationElement Grid, TextBox NameBox, AutomationElement SelectionValue, AutomationElement Tabs);

    private static Ui Start(AppUnderTest app)
    {
        Window window = app.MainWindow();
        var ui = new Ui(window, Find(window, "Grid"), Find(window, "NameBox").AsTextBox(), Find(window, "SelectionValue"),
            Find(window, "SheetTabs"));
        window.SetForeground();
        Retry(() => ui.Grid.Properties.HasKeyboardFocus.Value, "grid has keyboard focus at launch");
        Activate(window, ui.Grid);
        return ui;
    }

    private static AutomationElement? Tab(Ui ui, int index) =>
        ui.Tabs.FindFirstDescendant(cf => cf.ByAutomationId("SheetTab_" + index));

    private static AutomationElement WaitTab(Ui ui, int index)
    {
        AutomationElement? tab = null;
        Retry(() => (tab = Tab(ui, index)) is not null, $"SheetTab_{index} exists");
        return tab!;
    }

    private static bool IsSelected(AutomationElement tab) => tab.AsListBoxItem().IsSelected;

    /// <summary>Right-clicks <paramref name="tab"/> and clicks a context menu item (a click, not a UIA Invoke, so an item
    /// that opens a modal dialog does not block the test).</summary>
    private static void TabMenu(AppUnderTest app, Ui ui, AutomationElement tab, string itemId)
    {
        RaiseToTop(ui.Window);
        Mouse.RightClick(tab.GetClickablePoint());
        AutomationElement? item = null;
        Retry(() => (item = FindInProcess(app, itemId)) is not null, $"the tab context menu shows {itemId}");
        Retry(() => item!.IsEnabled, $"{itemId} is enabled");
        item!.Click();
        Retry(() => FindInProcess(app, itemId) is null, "the context menu closed");
    }

    private static void TypeCell(string text)
    {
        Keyboard.Type(text);
        Keyboard.Type(VirtualKeyShort.RETURN);
    }

    [Fact]
    public void SheetTabs_ShowsSheetTab0_AddMakesSheetTab1_AndAClickActivatesATab()
    {
        using var app = Launch();
        Ui ui = Start(app);
        TypeCell("11"); // Sheet1!A1
        Retry(() => ui.Window.Title == "Gridline - Untitled *", "dirty after the edit");

        AutomationElement tab0 = WaitTab(ui, 0);
        Assert.Equal("Sheet1", tab0.Name);
        Assert.True(IsSelected(tab0));
        Assert.Null(Tab(ui, 1));

        TabMenu(app, ui, tab0, "ContextAddSheet");

        AutomationElement tab1 = WaitTab(ui, 1);
        Assert.Equal("Sheet2", tab1.Name);
        Retry(() => IsSelected(tab1), "the added sheet is active");
        WaitFor(() => ui.NameBox.Text, "A1");
        WaitFor(() => ui.SelectionValue.Name, "BLANK");

        Activate(ui.Window, ui.Grid);
        TypeCell("22"); // Sheet2!A1

        // A click activates Sheet1: A1 is selected and shows Sheet1's value; activation keeps the title.
        string title = ui.Window.Title;
        RaiseToTop(ui.Window);
        Mouse.Click(tab0.GetClickablePoint());
        Retry(() => IsSelected(tab0), "Sheet1 is active after the click");
        WaitFor(() => ui.NameBox.Text, "A1");
        WaitFor(() => ui.SelectionValue.Name, "N:11");
        Assert.Equal(title, ui.Window.Title);
        Retry(() => ui.Grid.Properties.HasKeyboardFocus.Value, "the grid has the keyboard again after the click");

        Mouse.Click(tab1.GetClickablePoint());
        Retry(() => IsSelected(tab1), "Sheet2 is active after the click");
        WaitFor(() => ui.SelectionValue.Name, "N:22");
    }

    [Fact]
    public void ContextMenu_AddRenameDelete()
    {
        using var app = Launch();
        Ui ui = Start(app);

        TabMenu(app, ui, WaitTab(ui, 0), "ContextAddSheet");
        AutomationElement tab1 = WaitTab(ui, 1);

        // Rename: the dialog's RenameSheetBox holds the current name; typing a new one and OK renames the tab.
        TabMenu(app, ui, tab1, "ContextRenameSheet");
        AutomationElement? box = null;
        Retry(() => (box = FindInProcess(app, "RenameSheetBox")) is not null, "the Rename dialog is shown");
        TextBox nameBox = box!.AsTextBox();
        Assert.Equal("Sheet2", nameBox.Text);
        nameBox.Text = "Data";
        Retry(() => nameBox.Text == "Data", "the new name is entered");
        FindInProcess(app, "RenameSheetOk")!.AsButton().Click();
        Retry(() => FindInProcess(app, "RenameSheetBox") is null, "the Rename dialog closed");
        Retry(() => Tab(ui, 1)?.Name == "Data", "the tab shows the new name");
        Retry(() => IsSelected(Tab(ui, 1)!), "the renamed sheet stays active");

        // Delete: the active tab goes; Sheet1 becomes active again.
        TabMenu(app, ui, Tab(ui, 1)!, "ContextDeleteSheet");
        Retry(() => Tab(ui, 1) is null, "SheetTab_1 is gone");
        Retry(() => IsSelected(Tab(ui, 0)!), "Sheet1 is active");
        Assert.Equal("Sheet1", Tab(ui, 0)!.Name);

        // Delete is disabled on the last sheet (R88).
        RaiseToTop(ui.Window);
        Mouse.RightClick(Tab(ui, 0)!.GetClickablePoint());
        AutomationElement? delete = null;
        Retry(() => (delete = FindInProcess(app, "ContextDeleteSheet")) is not null, "the tab context menu is shown");
        Retry(() => !delete!.IsEnabled, "Delete is disabled on the only sheet");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Retry(() => FindInProcess(app, "ContextDeleteSheet") is null, "the context menu closed");
    }

    [Fact]
    public void NameBox_QualifiedAddress_SwitchesSheets()
    {
        using var app = Launch();
        Ui ui = Start(app);

        TabMenu(app, ui, WaitTab(ui, 0), "ContextAddSheet");
        TabMenu(app, ui, WaitTab(ui, 1), "ContextRenameSheet");
        AutomationElement? box = null;
        Retry(() => (box = FindInProcess(app, "RenameSheetBox")) is not null, "the Rename dialog is shown");
        box!.AsTextBox().Text = "Data";
        Retry(() => box!.AsTextBox().Text == "Data", "the new name is entered");
        FindInProcess(app, "RenameSheetOk")!.AsButton().Click();
        Retry(() => Tab(ui, 1)?.Name == "Data", "renamed to Data");

        Activate(ui.Window, ui.Grid);
        Press(VirtualKeyShort.RIGHT);
        Press(VirtualKeyShort.DOWN);
        WaitFor(() => ui.NameBox.Text, "B2");
        TypeCell("99"); // Data!B2

        RaiseToTop(ui.Window);
        Mouse.Click(Tab(ui, 0)!.GetClickablePoint());
        Retry(() => IsSelected(Tab(ui, 0)!), "Sheet1 is active");
        WaitFor(() => ui.SelectionValue.Name, "BLANK");

        ui.NameBox.Focus();
        ui.NameBox.Text = "Data!B2";
        Keyboard.Type(VirtualKeyShort.RETURN);

        Retry(() => IsSelected(Tab(ui, 1)!), "the name box activated Data");
        WaitFor(() => ui.NameBox.Text, "B2"); // unqualified (R88)
        WaitFor(() => ui.SelectionValue.Name, "N:99");
        Retry(() => ui.Grid.Properties.HasKeyboardFocus.Value, "grid has focus after the name box");
        _output.WriteLine("tabs: " + string.Join(", ", ui.Tabs.FindAllChildren().Select(t => t.Name)));
    }

    [Fact]
    public void DraggingATab_MovesTheSheet()
    {
        using var app = Launch();
        Ui ui = Start(app);
        TabMenu(app, ui, WaitTab(ui, 0), "ContextAddSheet");
        AutomationElement tab0 = WaitTab(ui, 0), tab1 = WaitTab(ui, 1);
        Retry(() => IsSelected(tab1), "Sheet2 is active");

        RaiseToTop(ui.Window);
        Mouse.Drag(tab0.GetClickablePoint(), tab1.GetClickablePoint());

        Retry(() => Tab(ui, 0)?.Name == "Sheet2" && Tab(ui, 1)?.Name == "Sheet1", "Sheet1 was dragged after Sheet2");
        Retry(() => IsSelected(Tab(ui, 1)!), "the dragged sheet is active");
        Retry(() => ui.Window.Title == "Gridline - Untitled *", "the move is a change");
    }

    /// <summary>An element of the app's process anywhere on the desktop: a context menu is its own popup window, and the
    /// Rename dialog an owned window.</summary>
    private static AutomationElement? FindInProcess(AppUnderTest app, string automationId)
    {
        var desktop = app.Automation.GetDesktop();
        foreach (var top in desktop.FindAllChildren(cf => cf.ByProcessId(app.Application.ProcessId)))
            if (top.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is { } found) return found;
        return null;
    }
}
