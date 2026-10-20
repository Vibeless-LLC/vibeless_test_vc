using System.Globalization;
using Gridline.App.Controls;
using Gridline.Core;

namespace Gridline.App.ViewModels;

/// <summary>Grid display formatting (§11.3): numbers G10 invariant, right-aligned; text left-aligned; booleans and errors
/// centred; blank cells empty.</summary>
public static class CellDisplayFormatter
{
    public static CellDisplay Format(CellValue value) => value.Kind switch
    {
        ValueKind.Number => new CellDisplay(value.Number.ToString("G10", CultureInfo.InvariantCulture), CellAlignment.Right),
        ValueKind.Text => new CellDisplay(value.Text, CellAlignment.Left),
        ValueKind.Boolean => new CellDisplay(value.Boolean ? "TRUE" : "FALSE", CellAlignment.Center),
        ValueKind.Error => new CellDisplay(value.Error, CellAlignment.Center),
        _ => CellDisplay.Empty,
    };
}
