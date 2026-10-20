using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Addressing;
using Gridline.Core.Evaluation;
using Gridline.Core.Parsing;

namespace Gridline.Core.Tests.Evaluation;

/// <summary>In-memory cell store for evaluator tests; missing addresses are truly blank.</summary>
internal sealed class DictionaryCells : ICellValueProvider
{
    private readonly Dictionary<CellAddress, CellValue> _cells = new();
    public int Reads { get; private set; }
    public void ResetReads() => Reads = 0;

    public DictionaryCells Set(string address, CellValue value)
    {
        _cells[CellAddress.Parse(address)] = value;
        return this;
    }

    public DictionaryCells Set(string address, double n) => Set(address, CellValue.FromNumber(n));
    public DictionaryCells Set(string address, string text) => Set(address, CellValue.FromText(text));
    public DictionaryCells Set(string address, bool b) => Set(address, CellValue.FromBoolean(b));
    public void Set(CellAddress address, CellValue value) => _cells[address] = value;

    public CellValue GetValue(CellAddress address)
    {
        Reads++;
        return _cells.TryGetValue(address, out var v) ? v : CellValue.Blank;
    }
}

internal sealed class DelegateFunction(Func<IFunctionArguments, CellValue> body) : IFormulaFunction
{
    public CellValue Invoke(IFunctionArguments arguments) => body(arguments);
}

/// <summary>Fake functions used to exercise the function-call interface; real functions arrive in Tasks 8-10.</summary>
internal sealed class FakeFunctions : IFunctionResolver
{
    private readonly Dictionary<string, IFormulaFunction> _map = new(StringComparer.Ordinal);
    public int CounterCalls;
    public IFunctionArguments? Stashed;

    public FakeFunctions()
    {
        Add("ID", a => a.Evaluate(0));
        Add("ISREF", a => CellValue.FromBoolean(a.IsReference(0)));
        Add("ARGC", a => CellValue.FromNumber(a.Count));
        Add("OMITMASK", a =>
        {
            int mask = 0;
            for (int i = 0; i < a.Count; i++) if (a.IsOmitted(i)) mask |= 1 << i;
            return CellValue.FromNumber(mask);
        });
        // FSUM: references sum their numbers and ignore other kinds; direct arguments are coerced (SUM-like, §7.0).
        Add("FSUM", a =>
        {
            double total = 0;
            for (int i = 0; i < a.Count; i++)
            {
                if (a.IsReference(i))
                {
                    foreach (var cell in a.EnumerateReferenceValues(i))
                    {
                        if (cell.Value.Kind == ValueKind.Error) return cell.Value;
                        if (cell.Value.Kind == ValueKind.Number) total += cell.Value.Number;
                    }
                }
                else
                {
                    if (!Coercion.TryToNumber(a.Evaluate(i), out var n, out var err)) return err;
                    total += n;
                }
            }
            return CellValue.FromNumber(total);
        });
        Add("COUNTBLANKS", a =>
        {
            int n = 0;
            foreach (var cell in a.EnumerateReferenceValues(0)) if (cell.IsBlank) n++;
            return CellValue.FromNumber(n);
        });
        Add("ADDRS", a =>
        {
            var parts = new List<string>();
            foreach (var cell in a.EnumerateReferenceValues(0)) parts.Add(cell.Address.ToString());
            return CellValue.FromText(string.Join(",", parts));
        });
        // FIF(cond, a, b): lazy, evaluates only the chosen branch.
        Add("FIF", a =>
        {
            var c = Coercion.ToBoolean(a.Evaluate(0));
            if (c.Kind == ValueKind.Error) return c;
            return a.Evaluate(c.Boolean ? 1 : 2);
        });
        Add("COUNTER", _ => { CounterCalls++; return CellValue.FromNumber(CounterCalls); });
        Add("BIGTEXT", _ => CellValue.FromText(new string('x', 32768)));
        Add("MAXTEXT", _ => CellValue.FromText(new string('x', 32767)));
        Add("BLANKFN", _ => CellValue.Blank);
        Add("STASH", a => { Stashed = a; return CellValue.FromNumber(1); });
    }

    public void Add(string name, Func<IFunctionArguments, CellValue> body) => _map[name] = new DelegateFunction(body);

    public bool TryResolve(string upperName, [NotNullWhen(true)] out IFormulaFunction? function) =>
        _map.TryGetValue(upperName, out function);
}

internal static class Eval
{
    public static CellValue Run(string formulaText, DictionaryCells? cells = null, FakeFunctions? functions = null)
    {
        var parsed = FormulaParser.Parse(formulaText, null);
        Assert.True(parsed.Success, $"'{formulaText}' failed to parse: {parsed.ErrorMessage}");
        return FormulaEvaluator.Evaluate(parsed, cells ?? new DictionaryCells(), functions ?? new FakeFunctions());
    }

    public static CellValue N(double d) => CellValue.FromNumber(d);
    public static CellValue T(string s) => CellValue.FromText(s);
    public static CellValue B(bool b) => CellValue.FromBoolean(b);
    public static CellValue E(string code) => CellValue.FromError(code);

    /// <summary>Runs <paramref name="body"/> on a thread with the given stack size; rethrows any failure after Join.</summary>
    public static void OnSmallStack(int stackBytes, Action body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        }, stackBytes);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException("Failure on small-stack thread: " + failure);
    }
}
