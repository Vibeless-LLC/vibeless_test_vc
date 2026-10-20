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

    /// <summary>True when argument <paramref name="index"/> is omitted, e.g. the middle one of <c>IF(A1,,3)</c>. An omitted
    /// argument is treated as absent (§5.3, R38): the parameter's default applies, or #VALUE! where it has none.</summary>
    bool IsOmitted(int index);

    /// <summary>
    /// True when the argument is written as a cell reference, range or defined name (§7.0, ruling P2), possibly inside
    /// parentheses (ruling R12), e.g. <c>A1</c>, <c>A1:B3</c>, <c>((A1))</c>, <c>Total</c>. A name counts only while it
    /// is defined with a live target; an undefined name (#NAME?) or a deleted target (#REF!) is a direct argument.
    /// Anything else (literals, operators, calls, omitted) is a direct argument.
    /// </summary>
    bool IsReference(int index);

    /// <summary>
    /// True when the argument is written as a name (possibly in parentheses) that cannot act as a reference, with
    /// <paramref name="error"/> its own error: #NAME? for an undefined name, #REF! for a deleted target (§9.6, §5.1).
    /// Every function position that requires a reference MUST check this before answering #VALUE! for a non-reference,
    /// so such a name yields its own error, never the function's #VALUE!. Functions that never return an error (ISBLANK)
    /// need not. False for every other argument.
    /// </summary>
    bool TryGetReferenceError(int index, out CellValue error);

    /// <summary>
    /// Evaluates the argument to a scalar. A range argument gives #VALUE! even when 1x1 (§6.5, R11); a single-cell
    /// reference gives the cell's value, which is <see cref="CellValue.Blank"/> for an empty cell; an omitted argument
    /// gives #VALUE! (§5.3, R38), so a function whose parameter has a default must test <see cref="IsOmitted"/> first.
    /// Errors are returned as values, never thrown.
    /// </summary>
    CellValue Evaluate(int index);

    /// <summary>
    /// The cells of a reference argument (<see cref="IsReference"/> true) in row-major order, each paired with whether
    /// it is truly blank. Allocation-free per cell. Throws <see cref="InvalidOperationException"/> when the argument is
    /// not a reference.
    /// </summary>
    ReferenceValues EnumerateReferenceValues(int index);
}
