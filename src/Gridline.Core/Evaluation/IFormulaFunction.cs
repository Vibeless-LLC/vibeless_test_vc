using System.Diagnostics.CodeAnalysis;

namespace Gridline.Core.Evaluation;

/// <summary>
/// A built-in function as seen by the evaluator (§6.8, §7). Implementations receive lazily evaluated arguments and
/// return a value. The evaluator passes the result through unchanged, except that text longer than
/// <see cref="Coercion.MaxTextLength"/> becomes #VALUE! (§6.3), and a Blank result becomes 0 when it is the final
/// formula result (§6.6). Argument errors are NOT propagated automatically: each function decides, so that
/// IFERROR/ISERROR-style functions can inspect them. Implementations must be stateless and deterministic.
/// </summary>
public interface IFormulaFunction
{
    /// <param name="arguments">Valid only for the duration of this call.</param>
    CellValue Invoke(IFunctionArguments arguments);
}

/// <summary>Looks up functions by upper-cased name (§6.8). An unresolved name evaluates to #NAME?.</summary>
public interface IFunctionResolver
{
    bool TryResolve(string upperName, [NotNullWhen(true)] out IFormulaFunction? function);
}
