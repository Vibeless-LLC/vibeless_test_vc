using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Gridline.Core.Addressing;

namespace Gridline.App.Controls;

/// <summary>
/// Draws the headers, gridlines, selection and the visible cells of a <see cref="GridControl"/> (§11.3). Only the
/// cells inside <see cref="VisibleCells"/> are read from the source, so the cost of a frame is proportional to the
/// visible rectangle and independent of the sheet size and scroll position.
/// </summary>
internal static class GridRenderer
{
    private static readonly Brush Background = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush HeaderBackground = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));
    private static readonly Brush HeaderSelectedBackground = Frozen(new SolidColorBrush(Color.FromRgb(0xD3, 0xDE, 0xEE)));
    private static readonly Brush SelectionFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x33, 0x77, 0xCC)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Colors.Black));
    private static readonly Brush HeaderTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
    private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xDA, 0xDC, 0xE0))), 1));
    private static readonly Pen HeaderPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0))), 1));
    private static readonly Pen ActivePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x5C, 0xC0))), 2));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private const double FontSize = 12;

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    /// <summary>Draws one frame and returns the number of cells read from <paramref name="source"/>.</summary>
    public static int Draw(DrawingContext dc, Size size, VisibleCells v, IGridDataSource? source, double pixelsPerDip)
    {
        dc.DrawRectangle(Background, null, new Rect(size));
        CellRange? selection = source?.Selection;
        int realized = 0;

        if (source != null)
        {
            if (selection is { } sel) DrawSelectionFill(dc, v, sel);
            for (int row = v.FirstRow; row <= v.LastRow; row++)
            {
                double top = GridMetrics.RowTop(row, v.FirstRow);
                for (int column = v.FirstColumn; column <= v.LastColumn; column++)
                {
                    CellDisplay d = source.GetDisplay(column, row);
                    realized++;
                    if (d.Text.Length == 0) continue;
                    var cell = new Rect(GridMetrics.ColumnLeft(column, v.FirstColumn), top, GridMetrics.ColumnWidth, GridMetrics.RowHeight);
                    if (d.Alignment == CellAlignment.Right)
                        DrawNumber(dc, d.Text, cell, pixelsPerDip); // §11.3: always the full G10 string
                    else
                        DrawText(dc, d.Text, cell, ToTextAlignment(d.Alignment), TextBrush, pixelsPerDip);
                }
            }
        }

        DrawGridLines(dc, size, v);
        if (source != null) DrawActiveCell(dc, v, source.ActiveCell);
        DrawHeaders(dc, size, v, selection, pixelsPerDip);
        return realized;
    }

    private static TextAlignment ToTextAlignment(CellAlignment a) => a switch
    {
        CellAlignment.Right => TextAlignment.Right,
        CellAlignment.Center => TextAlignment.Center,
        _ => TextAlignment.Left,
    };

    /// <summary>Draws the complete G10 string right-aligned inside the cell, clipped to the cell rectangle (§11.3): a number
    /// wider than the column loses its leading characters at the left edge, never its text.</summary>
    private static void DrawNumber(DrawingContext dc, string text, Rect cell, double pixelsPerDip)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSize, TextBrush, pixelsPerDip);
        double x = cell.Right - GridMetrics.TextPadding - ft.WidthIncludingTrailingWhitespace;
        double y = cell.Top + Math.Max(0, (cell.Height - ft.Height) / 2);
        dc.PushClip(new RectangleGeometry(cell));
        dc.DrawText(ft, new Point(x, y));
        dc.Pop();
    }

    private static void DrawText(DrawingContext dc, string text, Rect cell, TextAlignment alignment, Brush brush, double pixelsPerDip)
    {
        double width = cell.Width - 2 * GridMetrics.TextPadding;
        if (width <= 0) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSize, brush, pixelsPerDip)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };
        double y = cell.Top + Math.Max(0, (cell.Height - ft.Height) / 2);
        dc.DrawText(ft, new Point(cell.Left + GridMetrics.TextPadding, y));
    }

    /// <summary>The on-screen rectangle of the part of <paramref name="r"/> that is visible, or null.</summary>
    private static Rect? VisibleRect(VisibleCells v, CellRange r)
    {
        int c1 = Math.Max(r.TopLeft.Column, v.FirstColumn), c2 = Math.Min(r.BottomRight.Column, v.LastColumn);
        int r1 = Math.Max(r.TopLeft.Row, v.FirstRow), r2 = Math.Min(r.BottomRight.Row, v.LastRow);
        if (c1 > c2 || r1 > r2) return null;
        double left = GridMetrics.ColumnLeft(c1, v.FirstColumn), top = GridMetrics.RowTop(r1, v.FirstRow);
        return new Rect(left, top, (c2 - c1 + 1) * GridMetrics.ColumnWidth, (r2 - r1 + 1) * GridMetrics.RowHeight);
    }

    private static void DrawSelectionFill(DrawingContext dc, VisibleCells v, CellRange selection)
    {
        if (selection.CellCount <= 1) return; // a single active cell is shown by its border only
        if (VisibleRect(v, selection) is { } rect) dc.DrawRectangle(SelectionFill, null, rect);
    }

    private static void DrawActiveCell(DrawingContext dc, VisibleCells v, CellAddress active)
    {
        if (VisibleRect(v, new CellRange(active, active)) is { } rect)
        {
            rect.Inflate(-1, -1);
            dc.DrawRectangle(null, ActivePen, rect);
        }
    }

    private static void DrawGridLines(DrawingContext dc, Size size, VisibleCells v)
    {
        double right = Math.Min(GridMetrics.ColumnLeft(v.LastColumn + 1, v.FirstColumn), size.Width);
        double bottom = Math.Min(GridMetrics.RowTop(v.LastRow + 1, v.FirstRow), size.Height);
        for (int column = v.FirstColumn; column <= v.LastColumn + 1; column++)
        {
            double x = GridMetrics.ColumnLeft(column, v.FirstColumn) - 0.5;
            dc.DrawLine(GridPen, new Point(x, GridMetrics.ColumnHeaderHeight), new Point(x, bottom));
        }
        for (int row = v.FirstRow; row <= v.LastRow + 1; row++)
        {
            double y = GridMetrics.RowTop(row, v.FirstRow) - 0.5;
            dc.DrawLine(GridPen, new Point(GridMetrics.RowHeaderWidth, y), new Point(right, y));
        }
    }

    private static void DrawHeaders(DrawingContext dc, Size size, VisibleCells v, CellRange? selection, double pixelsPerDip)
    {
        dc.DrawRectangle(HeaderBackground, null, new Rect(0, 0, size.Width, GridMetrics.ColumnHeaderHeight));
        dc.DrawRectangle(HeaderBackground, null, new Rect(0, 0, GridMetrics.RowHeaderWidth, size.Height));

        for (int column = v.FirstColumn; column <= v.LastColumn; column++)
        {
            var rect = new Rect(GridMetrics.ColumnLeft(column, v.FirstColumn), 0, GridMetrics.ColumnWidth, GridMetrics.ColumnHeaderHeight);
            if (selection is { } s && column >= s.TopLeft.Column && column <= s.BottomRight.Column)
                dc.DrawRectangle(HeaderSelectedBackground, null, rect);
            dc.DrawLine(HeaderPen, new Point(rect.Right - 0.5, 0), new Point(rect.Right - 0.5, rect.Bottom));
            DrawText(dc, ColumnName.FromNumber(column), rect, TextAlignment.Center, HeaderTextBrush, pixelsPerDip);
        }
        for (int row = v.FirstRow; row <= v.LastRow; row++)
        {
            var rect = new Rect(0, GridMetrics.RowTop(row, v.FirstRow), GridMetrics.RowHeaderWidth, GridMetrics.RowHeight);
            if (selection is { } s && row >= s.TopLeft.Row && row <= s.BottomRight.Row)
                dc.DrawRectangle(HeaderSelectedBackground, null, rect);
            dc.DrawLine(HeaderPen, new Point(0, rect.Bottom - 0.5), new Point(rect.Right, rect.Bottom - 0.5));
            DrawText(dc, row.ToString(CultureInfo.InvariantCulture), rect, TextAlignment.Center, HeaderTextBrush, pixelsPerDip);
        }
        // Corner box and the header borders on top of everything.
        dc.DrawRectangle(HeaderBackground, null, new Rect(0, 0, GridMetrics.RowHeaderWidth, GridMetrics.ColumnHeaderHeight));
        dc.DrawLine(HeaderPen, new Point(0, GridMetrics.ColumnHeaderHeight - 0.5), new Point(size.Width, GridMetrics.ColumnHeaderHeight - 0.5));
        dc.DrawLine(HeaderPen, new Point(GridMetrics.RowHeaderWidth - 0.5, 0), new Point(GridMetrics.RowHeaderWidth - 0.5, size.Height));
    }
}
