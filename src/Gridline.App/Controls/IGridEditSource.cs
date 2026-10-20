namespace Gridline.App.Controls;

/// <summary>
/// The editing side of the grid's source (§11.4). A <see cref="GridControl"/> whose <see cref="GridControl.Source"/> also
/// implements this interface hosts an in-cell editor over the active cell and maps the §11.4 keys onto these members. The
/// pending text is shared with the formula bar, so the two always show the same text while an edit is open (D-38).
/// </summary>
public interface IGridEditSource
{
    /// <summary>True while an edit of the active cell is open.</summary>
    bool IsEditing { get; }

    /// <summary>The pending text of the open edit (the active cell's input when none is open). Setting a different text
    /// while no edit is open opens one.</summary>
    string EditText { get; set; }

    /// <summary>Opens an edit of the active cell: with <paramref name="replacement"/> (a typed character) the edit starts
    /// from that text and replaces the input; with null (F2) it starts from the existing input. Ignored while editing.</summary>
    void BeginEdit(string? replacement);

    /// <summary>Commits the open edit through Workbook.SetCell (one undo step), then moves the active cell by the deltas,
    /// clamped to the grid. Without an open edit it only moves.</summary>
    void CommitEdit(int columnDelta, int rowDelta);

    /// <summary>Closes the open edit without changing the workbook.</summary>
    void CancelEdit();

    /// <summary>Delete (§11.4): clears the used cells of the selection as one ApplyBatch; nothing when there are none.</summary>
    void ClearSelection();

    /// <summary>Raised when <see cref="IsEditing"/> or <see cref="EditText"/> changed.</summary>
    event EventHandler? EditStateChanged;
}
