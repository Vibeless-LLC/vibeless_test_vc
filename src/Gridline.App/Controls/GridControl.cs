using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Gridline.App.Controls;

/// <summary>
/// The virtualized spreadsheet grid (§11.3): a custom-drawn element that renders only the visible rectangle, pulls
/// display values from its <see cref="Source"/> on demand and repaints when the source reports a change. Scrolling is
/// logical (one unit = one column or one row) through <see cref="System.Windows.Controls.Primitives.IScrollInfo"/>, so
/// row 100,000 and column ZZ cost the same as A1. See DECISIONS D-37.
/// </summary>
public sealed partial class GridControl : FrameworkElement
{
    /// <summary>Identifies the <see cref="Source"/> dependency property (R42: an immutable registration).</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(IGridDataSource), typeof(GridControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public GridControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        InitializeEditor();
    }

    /// <summary>The data and selection the grid shows.</summary>
    public IGridDataSource? Source
    {
        get => (IGridDataSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    // ---- test hooks (internal, InternalsVisibleTo Gridline.App.Tests) ----

    /// <summary>Cells read from the source by the most recent frame.</summary>
    internal int LastRenderedCellCount { get; private set; }

    /// <summary>The cell rectangle of the most recent frame.</summary>
    internal VisibleCells LastRenderedRange { get; private set; }

    /// <summary>Number of frames drawn.</summary>
    internal int RenderCount { get; private set; }

    /// <summary>The cells the current size and scroll position show, including partially visible ones.</summary>
    internal VisibleCells VisibleRange => GridMetrics.Visible(_firstColumn, _firstRow, RenderSize);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (GridControl)d;
        if (e.OldValue is IGridDataSource old)
        {
            old.ContentChanged -= grid.OnContentChanged;
            old.SelectionChanged -= grid.OnSelectionChanged;
            if (old is IGridEditSource oldEdit) oldEdit.EditStateChanged -= grid.OnEditStateChanged;
        }
        if (e.NewValue is IGridDataSource src)
        {
            src.ContentChanged += grid.OnContentChanged;
            src.SelectionChanged += grid.OnSelectionChanged;
            if (src is IGridEditSource edit) edit.EditStateChanged += grid.OnEditStateChanged;
            grid.ScrollIntoView(src.ActiveCell);
        }
        grid.SyncEditor();
    }

    private void OnContentChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (Source is { } src) ScrollIntoView(src.ActiveCell);
        InvalidateArrange(); // the in-cell editor follows the active cell
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GridAutomationPeer(this);

    protected override Size MeasureOverride(Size availableSize)
    {
        _editor.Measure(new Size(double.PositiveInfinity, GridMetrics.RowHeight - 2)); // desired width = text width
        return new(
            double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateViewport(finalSize);
        ArrangeEditor(finalSize);
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext) => Render(drawingContext);

    /// <summary>Draws the current frame into <paramref name="dc"/> (OnRender, and directly from tests).</summary>
    internal void Render(DrawingContext dc)
    {
        VisibleCells v = VisibleRange;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        LastRenderedCellCount = GridRenderer.Draw(dc, RenderSize, v, Source, pixelsPerDip);
        LastRenderedRange = v;
        RenderCount++;
    }
}
