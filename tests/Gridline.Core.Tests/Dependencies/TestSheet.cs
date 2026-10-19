using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Addressing;
using Gridline.Core.Dependencies;
using Gridline.Core.Functions;
using Gridline.Core.Parsing;

namespace Gridline.Core.Tests.Dependencies;

/// <summary>
/// Minimal in-memory recalc target (a stand-in for the Task 12 Workbook): inputs go through
/// <see cref="CellInputInterpreter"/> and <see cref="FormulaParser"/> with the real arity adapter, formulas evaluate
/// with the real built-in table, and every edit drives <see cref="DependencyGraph"/> (§6.1, §6.7).
/// </summary>
internal sealed class TestSheet : IRecalcTarget
{
    private static readonly FunctionArityAdapter Arities = new();
    private readonly Dictionary<CellAddress, CellValue> _values = new();
    private readonly Dictionary<CellAddress, ParsedFormula> _formulas = new();

    public DependencyGraph Graph { get; } = new();
    public int ComputedWrites { get; private set; }

    /// <summary>Applies one edit and recalculates.</summary>
    public RecalcResult Set(string address, string input) => SetMany((address, input));

    /// <summary>Applies several edits, then one recalculation over all of them.</summary>
    public RecalcResult SetMany(params (string Address, string Input)[] edits)
    {
        var changed = new List<CellAddress>();
        foreach (var (address, input) in edits) changed.Add(Apply(address, input));
        return Graph.Recalculate(changed, this, BuiltInFunctionTable.Default);
    }

    /// <summary>Stores inputs without recalculating, then performs one full recalc (as after a file load).</summary>
    public RecalcResult Load(params (string Address, string Input)[] cells)
    {
        foreach (var (address, input) in cells) Apply(address, input);
        return Graph.RecalculateAll(this, BuiltInFunctionTable.Default);
    }

    public CellValue this[string address] => GetValue(CellAddress.Parse(address));

    /// <summary>Stores the input; a formula keeps the cell's previous value until recalc writes the new one.</summary>
    public CellAddress Apply(string address, string input)
    {
        var a = CellAddress.Parse(address);
        var r = CellInputInterpreter.Interpret(input);
        if (r.Kind == CellInputKind.Formula)
        {
            var parsed = FormulaParser.Parse(r.FormulaText, Arities);
            _formulas[a] = parsed;
            Graph.SetFormula(a, parsed);
        }
        else
        {
            _formulas.Remove(a);
            Graph.SetFormula(a, null);
            if (r.Kind == CellInputKind.Blank) _values.Remove(a);
            else _values[a] = r.Value;
        }
        return a;
    }

    public CellValue GetValue(CellAddress address) =>
        _values.TryGetValue(address, out var v) ? v : CellValue.Blank;

    public bool TryGetFormula(CellAddress address, [NotNullWhen(true)] out ParsedFormula? formula) =>
        _formulas.TryGetValue(address, out formula);

    public void SetComputedValue(CellAddress address, CellValue value)
    {
        ComputedWrites++;
        _values[address] = value;
    }

    public static CellValue N(double d) => CellValue.FromNumber(d);
    public static CellValue E(string code) => CellValue.FromError(code);
    public static CellAddress A(string address) => CellAddress.Parse(address);
}
