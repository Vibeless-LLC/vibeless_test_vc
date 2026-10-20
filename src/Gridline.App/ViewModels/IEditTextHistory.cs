namespace Gridline.App.ViewModels;

/// <summary>
/// The undo history of the text box an open edit is typed in (ruling R49): while an edit is open, menu Undo/Redo act on
/// this instead of the workbook. Implemented by the view (the in-cell editor or the formula bar, whichever was focused
/// last), so the ViewModel never touches a WPF control.
/// </summary>
public interface IEditTextHistory
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Undo();
    void Redo();
}
