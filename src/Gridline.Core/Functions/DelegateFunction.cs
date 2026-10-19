using Gridline.Core.Evaluation;

namespace Gridline.Core.Functions;

/// <summary>Adapts a stateless static method or lambda to <see cref="IFormulaFunction"/>.</summary>
internal sealed class DelegateFunction(Func<IFunctionArguments, CellValue> body) : IFormulaFunction
{
    public CellValue Invoke(IFunctionArguments arguments) => body(arguments);
}
