using Gridline.Core.Evaluation;

namespace Gridline.Core.Functions;

/// <summary>
/// One built-in function (§6.8, §7): its upper-case name, the allowed argument count range (omitted arguments count,
/// variadic maximum <see cref="BuiltInFunctionTable.MaxVariadicArguments"/>) and its implementation (ruling R16).
/// </summary>
public sealed record FunctionDefinition(string Name, int MinArguments, int MaxArguments, IFormulaFunction Implementation);
