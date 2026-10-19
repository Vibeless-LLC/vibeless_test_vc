using Gridline.Core.Evaluation;
using Gridline.Core.Functions;
using Gridline.Core.Parsing;
using Gridline.Core.Tests.Evaluation;

namespace Gridline.Core.Tests.Functions;

/// <summary>
/// End-to-end helper: parses with <see cref="FormulaParser"/> plus the real arity adapter, then evaluates with
/// <see cref="FormulaEvaluator"/> plus the real built-in table, over an in-memory provider (R16).
/// </summary>
internal static class Fx
{
    private static readonly FunctionArityAdapter Arities = new();

    public static CellValue Run(string formulaText, DictionaryCells? cells = null)
    {
        var parsed = FormulaParser.Parse(formulaText, Arities);
        return FormulaEvaluator.Evaluate(parsed, cells ?? new DictionaryCells(), BuiltInFunctionTable.Default);
    }

    public static CellValue N(double d) => CellValue.FromNumber(d);
    public static CellValue E(string code) => CellValue.FromError(code);
}
