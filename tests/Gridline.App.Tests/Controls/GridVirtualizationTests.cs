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
        public CellDisplay GetDisplay(int column, int row)
        {
            Requested.Add((column, row));
            return new CellDisplay(column + ":" + row, CellAlignment.Left);
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
        var grid = new GridControl { Source = source };
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
        Assert.True(grid.IsArrangeValid);
        vm.Workbook.SetCell("A1", "1"); // Workbook.CellsChanged -> ContentChanged -> InvalidateVisual (invalidates arrange)
        Assert.False(grid.IsArrangeValid);
        grid.UpdateLayout();
        vm.MoveActiveCell(1, 0, extend: false); // SelectionChanged repaints too
        Assert.False(grid.IsArrangeValid);
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
}
