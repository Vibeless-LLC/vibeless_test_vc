namespace Gridline.App.ViewModels;

/// <summary>One tab of the sheet tab strip (CR1.8, R88): its position and the sheet name shown there. The strip updates its
/// tabs in place, so a tab keeps its <see cref="Index"/> (and its AutomationId <c>SheetTab_&lt;index&gt;</c>) while the
/// name at that position may change (rename, move, delete, undo).</summary>
public sealed class SheetTabViewModel(int index, string name) : ObservableObject
{
    private string _name = name;

    /// <summary>The tab's 0-based position, the index in <see cref="Core.Workbook.SheetNames"/>.</summary>
    public int Index { get; } = index;

    /// <summary>CR1.8: <c>SheetTab_&lt;index&gt;</c>.</summary>
    public string AutomationId => "SheetTab_" + Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The sheet name in its defined casing, unquoted.</summary>
    public string Name
    {
        get => _name;
        internal set => SetProperty(ref _name, value);
    }
}
