namespace Gridline.Core.Evaluation;

/// <summary>
/// Lazy access to a function call's arguments (§6.8, §7.0). Nothing is evaluated until asked, and each
/// <see cref="Evaluate"/> call evaluates the argument again (evaluation is deterministic, so cache it yourself if you
/// need it twice). Valid only during <see cref="IFormulaFunction.Invoke"/>; use afterwards throws
/// <see cref="InvalidOperationException"/>. Indices are 0-based; an out-of-range index throws
/// <see cref="ArgumentOutOfRangeException"/>.
/// </summary>
public interface IFunctionArguments
{
    /// <summary>Number of arguments as written, omitted ones included (<c>F(1,)</c> has 2, <c>F()</c> has 0).</summary>
    int Count { get; }

    /// <summary>True when argument <paramref name="index"/> is omitted, e.g. the middle one of <c>IF(A1,,3)</c>.</summary>
    bool IsOmitted(int index);

    /// <summary>
    /// True when the argument is written as a cell reference or range, possibly inside parentheses (ruling R12),
    /// e.g. <c>A1</c>, <c>A1:B3</c>, <c>((A1))</c>. Anything else (literals, operators, calls, omitted) is a direct
    /// argument.
    /// </summary>
    bool IsReference(int index);

    /// <summary>
    /// Evaluates the argument to a scalar. A range argument gives #VALUE! even when 1x1 (§6.5, R11); a single-cell
    /// reference gives the cell's value, which is <see cref="CellValue.Blank"/> for an empty cell; an omitted argument
    /// gives Blank. Errors are returned as values, never thrown.
    /// </summary>
    CellValue Evaluate(int index);

    /// <summary>
    /// The cells of a reference argument (<see cref="IsReference"/> true) in row-major order, each paired with whether
    /// it is truly blank. Allocation-free per cell. Throws <see cref="InvalidOperationException"/> when the argument is
    /// not a reference.
    /// </summary>
    ReferenceValues EnumerateReferenceValues(int index);
}
