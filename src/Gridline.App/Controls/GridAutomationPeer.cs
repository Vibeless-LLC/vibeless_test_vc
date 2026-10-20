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

    protected override bool IsKeyboardFocusableCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
