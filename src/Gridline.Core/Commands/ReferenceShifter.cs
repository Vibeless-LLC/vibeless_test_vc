using Gridline.Core.Addressing;
using Gridline.Core.Parsing;

namespace Gridline.Core.Commands;

/// <summary>
/// The §9.4 CopyPaste reference rewriter: shifts each relative component by the paste offset, leaves $-absolute
/// components alone and keeps every component's $ flag. A reference whose row leaves 1..100000 or whose column leaves
/// 1..702 becomes invalid (null, printed #REF!, §5.5); a range uses the default per-corner rule, so one invalid corner
/// makes the whole range #REF!.
/// </summary>
internal sealed class ReferenceShifter(int rowOffset, int columnOffset) : IReferenceRewriter
{
    public CellReference? Rewrite(CellReference reference)
    {
        int row = reference.RowAbsolute ? reference.Address.Row : reference.Address.Row + rowOffset;
        int column = reference.ColumnAbsolute ? reference.Address.Column : reference.Address.Column + columnOffset;
        if (row < 1 || row > GridBounds.MaxRows || column < 1 || column > GridBounds.MaxColumns) return null;
        return reference with { Address = new CellAddress(column, row) };
    }
}
