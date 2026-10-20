using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace Gridline.App.Controls;

/// <summary>UI Automation peer for <see cref="GridControl"/> (§11.7): a focusable DataGrid element whose AutomationId comes from
/// <see cref="AutomationProperties.AutomationIdProperty"/> (set to <c>Grid</c> in MainWindow), defaulting to <c>Grid</c>.</summary>
internal sealed class GridAutomationPeer(GridControl owner) : FrameworkElementAutomationPeer(owner)
{
    public const string DefaultAutomationId = "Grid";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataGrid;

    protected override string GetClassNameCore() => nameof(GridControl);

    protected override string GetAutomationIdCore()
    {
        string id = base.GetAutomationIdCore();
        return string.IsNullOrEmpty(id) ? DefaultAutomationId : id;
    }

    protected override string GetNameCore()
    {
        string name = base.GetNameCore();
        return string.IsNullOrEmpty(name) ? "Sheet" : name;
    }

    /// <summary>The P-6 rendered signal (§12, D-40; not normative): <c>Rendered:&lt;n&gt;</c> once the grid has drawn
    /// <c>n</c> &gt;= 1 frames of its source, empty before the first. A file argument is loaded before the window is
    /// shown (R44), so the first frame already shows the loaded workbook.</summary>
    internal static string RenderedStatus(GridControl grid) =>
        grid.Source is null || grid.RenderCount == 0 ? "" : "Rendered:" + grid.RenderCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    protected override string GetItemStatusCore() => RenderedStatus((GridControl)Owner);

    protected override bool IsKeyboardFocusableCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
