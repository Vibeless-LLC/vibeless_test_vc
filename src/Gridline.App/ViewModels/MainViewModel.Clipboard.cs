using System.Text;
using Gridline.Core;
using Gridline.Core.Addressing;
using Gridline.IO;

namespace Gridline.App.ViewModels;

/// <summary>
/// Copy and Paste (§11.6, R46, R47). Copy remembers the selection for an in-app paste (§9.4 CopyPaste) and puts the
/// displayed values on the system clipboard as tab-separated text. Paste is internal when the clipboard text still equals
/// the text this app last placed there (R47); otherwise the text is parsed with the §8.5 rules (Tab delimiter) and
/// written at the active cell as one ApplyBatch. Both act on the selection only while no edit is open (a focused text
/// box handles Ctrl+C/Ctrl+V itself). See DECISIONS D-39.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The largest selection Copy accepts (D-39): the copy text is built for the whole rectangle, so a
    /// whole-sheet selection (70.2 million cells) is refused with a message instead.</summary>
    public const int MaxCopyCells = 1_000_000;

    private CellRange? _copiedRange;
    private string? _copiedText;

    /// <summary>Copy (Ctrl+C). Returns true when the clipboard was set.</summary>
    public bool Copy()
    {
        if (_isEditing) return false;
        CellRange range = Selection;
        if (range.CellCount > MaxCopyCells)
        {
            _dialogs.ShowError("Gridline",
                $"The selection is too large to copy ({range.CellCount:N0} cells). Copy at most {MaxCopyCells:N0} cells at a time.");
            return false;
        }
        string text = BuildCopyText(_workbook, range);
        if (!_clipboard.TrySetText(text))
        {
            _dialogs.ShowError("Gridline", "Cannot copy: the clipboard is in use by another program.");
            return false;
        }
        _copiedRange = range;
        _copiedText = text;
        return true;
    }

    /// <summary>Paste (Ctrl+V) at the active cell. Returns true when the paste was applied (it may still have changed
    /// nothing, when every pasted input equals the current one). A paste that would exceed the grid, text that fails to
    /// parse or an input the workbook refuses shows a message and changes nothing.</summary>
    public bool Paste()
    {
        if (_isEditing) return false;
        if (!_clipboard.TryGetText(out string? text))
        {
            _dialogs.ShowError("Gridline", "Cannot paste: the clipboard is in use by another program.");
            return false;
        }
        if (string.IsNullOrEmpty(text)) return false;
        return IsInternalPaste(text) ? PasteInternal(_copiedRange!.Value) : PasteExternal(text);
    }

    /// <summary>R47: true when a paste of <paramref name="clipboardText"/> would be an in-app (§9.4) paste.</summary>
    internal bool IsInternalPaste(string? clipboardText) =>
        _copiedRange is not null && string.Equals(clipboardText, _copiedText, StringComparison.Ordinal);

    private bool PasteInternal(CellRange source)
    {
        try
        {
            _workbook.CopyPaste(source.ToString(), _active.ToString()); // one undo step; references shift (§9.4)
        }
        catch (ArgumentException ex) // the destination exceeds the grid, or a shifted formula is too long (R55)
        {
            _dialogs.ShowError("Gridline", $"Cannot paste at {_active}.{Environment.NewLine}{ex.Message}");
            return false;
        }
        return true;
    }

    private bool PasteExternal(string text)
    {
        try
        {
            var records = DelimitedTextParser.Parse(text, '\t');
            _workbook.ApplyBatch(DelimitedTextMapping.ToEdits(records, _active)); // one undo step
        }
        catch (Exception ex) when (ex is GridlineFormatException or ArgumentException)
        {
            _dialogs.ShowError("Gridline", $"Cannot paste at {_active}.{Environment.NewLine}{ex.Message}");
            return false;
        }
        return true;
    }

    /// <summary>A new workbook was adopted: the remembered range belongs to the old one, so the next paste of that text is
    /// external (values only).</summary>
    private void ForgetInternalCopy()
    {
        _copiedRange = null;
        _copiedText = null;
    }

    private bool CanUseClipboard() => !_isEditing;

    private void RaiseClipboardCanExecuteChanged()
    {
        CopyCommand.RaiseCanExecuteChanged();
        PasteCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// R46: the grid's display text (G10 numbers, <see cref="CellDisplayFormatter"/>) of every cell in
    /// <paramref name="range"/>, fields joined by Tab and every row ending with CRLF. Only the used cells are read (blank
    /// cells are empty fields), in row-major order. A field containing Tab, CR or LF, or starting with <c>"</c>, is quoted
    /// with its quotes doubled, so the text parses back to the same fields under §8.5 (D-39).
    /// </summary>
    internal static string BuildCopyText(Workbook workbook, CellRange range)
    {
        var used = SelectionSummary.UsedCellsIn(workbook, range); // row-major (§9.1)
        int next = 0;
        CellAddress? nextUsed = used.Count > 0 ? CellAddress.Parse(used[0]) : null;
        var sb = new StringBuilder();
        for (int row = range.TopLeft.Row; row <= range.BottomRight.Row; row++)
        {
            for (int column = range.TopLeft.Column; column <= range.BottomRight.Column; column++)
            {
                if (column > range.TopLeft.Column) sb.Append('\t');
                if (nextUsed is { } u && u.Row == row && u.Column == column)
                {
                    AppendField(sb, CellDisplayFormatter.Format(workbook.GetValue(used[next])).Text);
                    next++;
                    nextUsed = next < used.Count ? CellAddress.Parse(used[next]) : null;
                }
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string field)
    {
        bool quote = field.StartsWith('"') || field.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0;
        if (!quote) { sb.Append(field); return; }
        sb.Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }
}
