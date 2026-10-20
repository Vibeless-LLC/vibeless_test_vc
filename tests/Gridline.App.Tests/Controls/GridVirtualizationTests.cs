using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Gridline.App.Controls;
using Gridline.Core;
using Gridline.Core.Addressing;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.Controls;

/// <summary>§11.3: the grid realizes only the visible cells, at any scroll position, and repaints on CellsChanged.</summary>
public class GridVirtualizationTests
{
    /// <summary>A source that records every cell the grid asks for.</summary>
    private sealed class RecordingSource : IGridDataSource
    {
        public List<(int Column, int Row)> Requested { get; } = [];
        public CellAddress ActiveCell { get; set; } = new(1, 1);
        public CellRange Selection => new(ActiveCell, ActiveCell);
        public Func<int, int, CellDisplay> Display { get; init; } = (c, r) => new CellDisplay(c + ":" + r, CellAlignment.Left);
        public CellDisplay GetDisplay(int column, int row)
        {
            Requested.Add((column, row));
            return Display(column, row);
        }
        public void MoveActiveCell(int columnDelta, int rowDelta, bool extend) { }
        public void SelectCell(CellAddress cell, bool extend) { ActiveCell = cell; SelectionChanged?.Invoke(this, EventArgs.Empty); }
        public CellAddress GetLastUsedCell() => new(1, 1);
        public event EventHandler? ContentChanged;
        public event EventHandler? SelectionChanged;
        public void RaiseContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private static GridControl Layout(IGridDataSource source, double width = 800, double height = 600)
    {
        var grid = new GridControl { Source = source, ModifierProvider = () => System.Windows.Input.ModifierKeys.None };
        grid.Measure(new Size(width, height));
        grid.Arrange(new Rect(0, 0, width, height));
        return grid;
    }

    /// <summary>Draws one frame directly. Arrange already drew one through OnRender, so a recording source is cleared first.</summary>
    private static void RenderFrame(GridControl grid)
    {
        (grid.Source as RecordingSource)?.Requested.Clear();
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen()) grid.Render(dc);
    }

    [Fact]
    public void AtOrigin_RequestsExactlyTheVisibleCells() => RunSta(() =>
    {
        var source = new RecordingSource();
        var grid = Layout(source);
        RenderFrame(grid);

        // 800 x 600 minus headers (60 x 22): ceil(740 / 80) = 10 columns, ceil(578 / 22) = 27 rows.
        Assert.Equal(new VisibleCells(1, 10, 1, 27), grid.LastRenderedRange);
        Assert.Equal(270, source.Requested.Count);
        Assert.Equal(270, grid.LastRenderedCellCount);
        Assert.Equal(270, source.Requested.Distinct().Count());
        Assert.All(source.Requested, c => Assert.True(c.Column is >= 1 and <= 10 && c.Row is >= 1 and <= 27, c.ToString()));
    });

    [Fact]
    public void AtTheFarCorner_RequestsOnlyVisibleCellsEndingAtZZ100000() => RunSta(() =>
    {
        var source = new RecordingSource();
        var grid = Layout(source);
        grid.SetHorizontalOffset(double.MaxValue);
        grid.SetVerticalOffset(double.MaxValue);
        RenderFrame(grid);

        // Full cells: floor(740 / 80) = 9 columns, floor(578 / 22) = 26 rows; the last page ends exactly at ZZ100000.
        Assert.Equal(GridBounds.MaxColumns - 9, grid.HorizontalOffset);
        Assert.Equal(GridBounds.MaxRows - 26, grid.VerticalOffset);
        Assert.Equal(new VisibleCells(694, 702, 99975, 100000), grid.LastRenderedRange);
        Assert.Equal(9 * 26, source.Requested.Count);
        Assert.Contains((702, 100000), source.Requested);
        Assert.Equal("ZZ", ColumnName.FromNumber(grid.LastRenderedRange.LastColumn));
    });

    [Fact]
    public void Extent_IsTheWholeGrid_InCells() => RunSta(() =>
    {
        var grid = Layout(new RecordingSource());
        Assert.Equal(702, grid.ExtentWidth);
        Assert.Equal(100000, grid.ExtentHeight);
        Assert.Equal(9, grid.ViewportWidth);
        Assert.Equal(26, grid.ViewportHeight);
        grid.SetVerticalOffset(-5);
        Assert.Equal(0, grid.VerticalOffset);
        grid.SetVerticalOffset(double.NaN);
        Assert.Equal(0, grid.VerticalOffset);
    });

    [Fact]
    public void RealizedCount_DependsOnViewportNotWorkbookSize() => RunSta(() =>
    {
        // A real ViewModel over 20,000 used cells: a frame still reads only the visible rectangle.
        var cells = new List<KeyValuePair<string, string>>();
        for (int row = 1; row <= 2000; row++)
            for (int column = 1; column <= 10; column++)
                cells.Add(new(new CellAddress(column, row).ToString(), (row * column).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var vm = NewViewModel();
        vm.Adopt(Workbook.FromCells(cells), null);
        var grid = Layout(vm, 400, 300);
        RenderFrame(grid);
        var v = grid.LastRenderedRange;
        Assert.Equal(v.CellCount, grid.LastRenderedCellCount);
        Assert.True(grid.LastRenderedCellCount <= 5 * 13, $"realized {grid.LastRenderedCellCount}");
    });

    [Fact]
    public void SelectingAFarCell_ScrollsItIntoView() => RunSta(() =>
    {
        var vm = NewViewModel();
        var grid = Layout(vm);
        vm.SelectCell(CellAddress.Parse("ZZ100000"), extend: false);
        RenderFrame(grid);
        var v = grid.LastRenderedRange;
        Assert.Equal(702, v.LastColumn);
        Assert.Equal(100000, v.LastRow);

        vm.SelectCell(CellAddress.Parse("M500"), extend: false); // back up: becomes the first visible row/column
        RenderFrame(grid);
        Assert.Equal(13, grid.LastRenderedRange.FirstColumn);
        Assert.Equal(500, grid.LastRenderedRange.FirstRow);
    });

    [Fact]
    public void ContentChanged_InvalidatesTheGrid() => RunSta(() =>
    {
        var vm = NewViewModel();
        var grid = Layout(vm);
        grid.UpdateLayout();
        int frames = grid.RenderCount;
        Assert.True(frames >= 1); // Arrange drew the first frame through OnRender

        grid.UpdateLayout();
        Assert.Equal(frames, grid.RenderCount); // nothing changed: no new frame

        vm.Workbook.SetCell("A1", "1"); // Workbook.CellsChanged -> ContentChanged -> InvalidateVisual
        grid.UpdateLayout();
        Assert.Equal(frames + 1, grid.RenderCount);

        vm.MoveActiveCell(1, 0, extend: false); // SelectionChanged repaints too
        grid.UpdateLayout();
        Assert.Equal(frames + 2, grid.RenderCount);
    });

    [Fact]
    public void ContentChange_DoesNotScrollBackToTheActiveCell() => RunSta(() =>
    {
        var vm = NewViewModel();
        var grid = Layout(vm);
        int selectionEvents = 0;
        vm.SelectionChanged += (_, _) => selectionEvents++;
        grid.SetVerticalOffset(5000); // scroll away; A1 stays active
        grid.SetHorizontalOffset(300);

        vm.Workbook.SetCell("A1", "1");
        vm.UndoCommand.Execute(null);
        vm.RedoCommand.Execute(null);

        Assert.Equal(5000, grid.VerticalOffset);
        Assert.Equal(300, grid.HorizontalOffset);
        Assert.Equal(0, selectionEvents);
        Assert.Equal("N:1", vm.SelectionValue); // the status bar still follows the content
    });

    // §11.3 display: the full G10 string is drawn right-aligned and clipped to the cell, even when wider than the column.
    [Theory]
    [InlineData("-1.234567891E+100")]
    [InlineData("1.23456789E+10")]
    [InlineData("-0.3333333333")]
    [InlineData("5")]
    public void Number_IsDrawnAsTheFullG10String_RightAlignedAndClippedToTheCell(string g10) => RunSta(() =>
    {
        var source = new RecordingSource { Display = (c, r) => c == 1 && r == 1 ? new CellDisplay(g10, CellAlignment.Right) : CellDisplay.Empty };
        var grid = Layout(source);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen()) grid.Render(dc);

        var cell = new Rect(GridMetricsLeft, GridMetricsTop, 80, 22); // A1: after the 60 px row header and 22 px column header
        var runs = GlyphRuns(visual.Drawing, null).Where(x => x.Clip == cell).ToList();
        Assert.NotEmpty(runs);
        Assert.Equal(g10, string.Concat(runs.Select(x => new string([.. x.Run.Characters]))));
        Assert.DoesNotContain(GlyphRuns(visual.Drawing, null), x => new string([.. x.Run.Characters]).Contains('#'));

        // Right-aligned: the text ends at the cell's right edge minus the 3 px padding.
        var last = runs[^1].Run;
        double right = last.BaselineOrigin.X + last.AdvanceWidths.Sum();
        Assert.InRange(right, cell.Right - 3 - 0.5, cell.Right - 3 + 0.5);
        if (g10.Length > 14) Assert.True(runs[0].Run.BaselineOrigin.X < cell.Left, "the wide value overflows to the left and is clipped");
    });

    private const double GridMetricsLeft = 60, GridMetricsTop = 22;

    /// <summary>Every glyph run in a drawing, with the bounds of the innermost clip that applies to it.</summary>
    private static IEnumerable<(GlyphRun Run, Rect? Clip)> GlyphRuns(Drawing? drawing, Rect? clip)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                Rect? inner = group.ClipGeometry?.Bounds ?? clip;
                foreach (var child in group.Children)
                    foreach (var r in GlyphRuns(child, inner)) yield return r;
                break;
            case GlyphRunDrawing g:
                yield return (g.GlyphRun, clip);
                break;
        }
    }

    [Fact]
    public void NavigationKeys_AreHandledByTheGrid_SoTheScrollViewerNeverScrollsAlone() => RunSta(() =>
    {
        var vm = NewViewModel();
        var grid = new GridControl { Source = vm, ModifierProvider = () => System.Windows.Input.ModifierKeys.None };
        var scroller = new System.Windows.Controls.ScrollViewer { CanContentScroll = true, Content = grid, Width = 800, Height = 600 };
        using var host = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("grid-test") { Width = 800, Height = 600 })
        {
            RootVisual = scroller,
        };
        scroller.UpdateLayout();

        foreach (var key in new[] { System.Windows.Input.Key.End, System.Windows.Input.Key.Home, System.Windows.Input.Key.Right, System.Windows.Input.Key.PageDown })
        {
            var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, host, 0, key)
            {
                RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent,
            };
            grid.RaiseEvent(args);
            scroller.UpdateLayout(); // ScrollViewer applies queued scroll commands during layout
            Assert.True(args.Handled, key + " was not handled");
            if (key == System.Windows.Input.Key.End)
            {
                // Unhandled, End would bubble to the ScrollViewer, which scrolls to the right edge.
                Assert.Equal(0, grid.HorizontalOffset);
                Assert.Equal(0, grid.VerticalOffset);
                Assert.Equal("A1", vm.ActiveAddress);
            }
        }
        // End alone did nothing: Home went back to column A, Right to B, PageDown a page down; the view follows the cell.
        Assert.Equal(2, vm.ActiveCell.Column);
        Assert.True(vm.ActiveCell.Row > 1);
        Assert.Equal(0, grid.HorizontalOffset);
    });

    [Fact]
    public void AutomationPeer_IsFocusableDataGridWithIdGrid() => RunSta(() =>
    {
        var grid = Layout(new RecordingSource());
        AutomationProperties.SetAutomationId(grid, "Grid");
        var peer = UIElementAutomationPeer.CreatePeerForElement(grid);
        Assert.Equal(AutomationControlType.DataGrid, peer.GetAutomationControlType());
        Assert.Equal("Grid", peer.GetAutomationId());
        Assert.True(peer.IsKeyboardFocusable());
        Assert.True(grid.Focusable);

        var unnamed = UIElementAutomationPeer.CreatePeerForElement(Layout(new RecordingSource()));
        Assert.Equal("Grid", unnamed.GetAutomationId());
    });

    [Fact]
    public void AutomationPeer_ItemStatus_ReportsRenderedFramesOfTheSource_TheP6Signal() => RunSta(() =>
    {
        var empty = new GridControl();
        Assert.Equal("", UIElementAutomationPeer.CreatePeerForElement(empty).GetItemStatus()); // no source, no frame

        var grid = new GridControl { Source = new RecordingSource(), ModifierProvider = () => System.Windows.Input.ModifierKeys.None };
        var peer = UIElementAutomationPeer.CreatePeerForElement(grid);
        Assert.Equal(0, grid.RenderCount);
        Assert.Equal("", peer.GetItemStatus());
        RenderFrame(grid);
        Assert.Equal("Rendered:1", peer.GetItemStatus());
        RenderFrame(grid);
        Assert.Equal("Rendered:2", peer.GetItemStatus());
    });
}
