using System.Reflection;
using System.Runtime.CompilerServices;

namespace Gridline.Core.Tests;

// Requirement IDs: §9.1 (public API, Phase 1 members), §0 (names and signatures exactly as written).
// Pins the member shapes the hidden acceptance suite compiles against: name, static vs instance, property vs method vs
// field vs event, declared type and parameter types. Extra public members may exist (FromCells, EscapeText,
// ErrorCodes.IsKnown); these tests only require that every §9.1 member is present with the listed shape.
public class PublicApiShapeTests
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly NullabilityInfoContext Nullability = new();

    private static void AssertInstanceGetOnly(Type type, string name, Type propertyType)
    {
        var p = type.GetProperty(name, PublicInstance);
        Assert.True(p is not null, $"{type.Name}.{name} must be a public instance property");
        Assert.Equal(propertyType, p.PropertyType);
        Assert.NotNull(p.GetMethod);
        Assert.True(p.SetMethod is null || !p.SetMethod.IsPublic || IsInitOnly(p.SetMethod),
            $"{type.Name}.{name} must not have a public setter");
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

    private static void AssertMethod(Type type, string name, bool isStatic, Type returnType, params Type[] parameters)
    {
        var m = type.GetMethod(name, isStatic ? PublicStatic : PublicInstance, parameters);
        Assert.True(m is not null, $"{type.Name}.{name}({string.Join(", ", parameters.Select(t => t.Name))}) must be a public {(isStatic ? "static" : "instance")} method");
        Assert.Equal(returnType, m.ReturnType);
    }

    private static void AssertConstInt(Type type, string name, int value)
    {
        var f = type.GetField(name, PublicStatic);
        Assert.True(f is not null && f.IsLiteral, $"{type.Name}.{name} must be a public const");
        Assert.Equal(typeof(int), f.FieldType);
        Assert.Equal(value, f.GetRawConstantValue());
    }

    [Fact]
    public void ValueKind_IsEnumWithExactMembersInOrder()
    {
        Assert.True(typeof(ValueKind).IsEnum);
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(ValueKind)));
        Assert.Equal(["Blank", "Number", "Text", "Boolean", "Error"], Enum.GetNames<ValueKind>());
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<ValueKind>().Select(k => (int)k));
    }

    [Fact]
    public void ErrorCodes_IsStaticClassWithExactlyTheEightConstants()
    {
        var t = typeof(ErrorCodes);
        Assert.True(t.IsClass && t.IsAbstract && t.IsSealed, "ErrorCodes must be a static class");
        var expected = new Dictionary<string, string>
        {
            ["Div0"] = "#DIV/0!", ["Value"] = "#VALUE!", ["Ref"] = "#REF!", ["Name"] = "#NAME?",
            ["NA"] = "#N/A", ["Num"] = "#NUM!", ["Circ"] = "#CIRC!", ["Parse"] = "#PARSE!",
        };
        var fields = t.GetFields(PublicStatic);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), fields.Select(f => f.Name).Order(StringComparer.Ordinal));
        foreach (var f in fields)
        {
            Assert.True(f.IsLiteral, $"ErrorCodes.{f.Name} must be const");
            Assert.Equal(typeof(string), f.FieldType);
            Assert.Equal(expected[f.Name], f.GetRawConstantValue());
        }
        Assert.Null(t.GetMember("DivZero", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SingleOrDefault());
    }

    [Fact]
    public void CellValue_IsReadOnlyStructWithSpecMembers()
    {
        var t = typeof(CellValue);
        Assert.True(t.IsValueType);
        Assert.True(t.IsDefined(typeof(IsReadOnlyAttribute)), "CellValue must be a readonly struct");
        Assert.Contains(typeof(IEquatable<CellValue>), t.GetInterfaces());

        AssertInstanceGetOnly(t, "Kind", typeof(ValueKind));
        AssertInstanceGetOnly(t, "Number", typeof(double));
        AssertInstanceGetOnly(t, "Text", typeof(string));
        AssertInstanceGetOnly(t, "Boolean", typeof(bool));
        AssertInstanceGetOnly(t, "Error", typeof(string));
        Assert.Equal(NullabilityState.NotNull, Nullability.Create(t.GetProperty("Text")!).ReadState);
        Assert.Equal(NullabilityState.NotNull, Nullability.Create(t.GetProperty("Error")!).ReadState);

        // `public static CellValue Blank { get; }`: a static get-only property, not a field.
        Assert.Null(t.GetField("Blank", PublicStatic));
        var blank = t.GetProperty("Blank", PublicStatic);
        Assert.NotNull(blank);
        Assert.Equal(t, blank.PropertyType);
        Assert.NotNull(blank.GetMethod);
        Assert.Null(blank.SetMethod);

        AssertMethod(t, "FromNumber", isStatic: true, t, typeof(double));
        AssertMethod(t, "FromText", isStatic: true, t, typeof(string));
        AssertMethod(t, "FromBoolean", isStatic: true, t, typeof(bool));
        AssertMethod(t, "FromError", isStatic: true, t, typeof(string));
        AssertMethod(t, "ToCanonicalString", isStatic: false, typeof(string));
        AssertMethod(t, "Equals", isStatic: false, typeof(bool), t);
    }

    [Fact]
    public void RecalcStats_IsReadOnlyRecordStructOfEvaluatedFormulaCount()
    {
        var t = typeof(RecalcStats);
        Assert.True(t.IsValueType);
        Assert.True(t.IsDefined(typeof(IsReadOnlyAttribute)), "RecalcStats must be a readonly record struct");
        Assert.Contains(typeof(IEquatable<RecalcStats>), t.GetInterfaces());
        var ctor = t.GetConstructor([typeof(int)]);
        Assert.NotNull(ctor);
        Assert.Equal("EvaluatedFormulaCount", ctor.GetParameters()[0].Name);
        AssertInstanceGetOnly(t, "EvaluatedFormulaCount", typeof(int));
        Assert.Equal(3, new RecalcStats(3).EvaluatedFormulaCount);
    }

    [Fact]
    public void CellsChangedEventArgs_IsSealedEventArgsWithReadOnlyCollectionOfAddresses()
    {
        var t = typeof(CellsChangedEventArgs);
        Assert.True(t.IsClass && t.IsSealed);
        Assert.Equal(typeof(EventArgs), t.BaseType);
        AssertInstanceGetOnly(t, "Addresses", typeof(IReadOnlyCollection<string>));
    }

    [Fact]
    public void Workbook_HasThePhase1Surface()
    {
        var t = typeof(Workbook);
        Assert.True(t.IsClass && t.IsSealed && !t.IsAbstract);

        AssertConstInt(t, "MaxRows", 100_000);
        AssertConstInt(t, "MaxColumns", 702);
        AssertConstInt(t, "MaxInputLength", 32_767);
        AssertConstInt(t, "UndoLimit", 100);

        Assert.NotNull(t.GetConstructor(Type.EmptyTypes));

        AssertMethod(t, "SetCell", isStatic: false, typeof(void), typeof(string), typeof(string));
        AssertMethod(t, "GetInput", isStatic: false, typeof(string), typeof(string));
        AssertMethod(t, "GetValue", isStatic: false, typeof(CellValue), typeof(string));
        AssertMethod(t, "GetUsedAddresses", isStatic: false, typeof(IReadOnlyList<string>));
        AssertInstanceGetOnly(t, "LastRecalc", typeof(RecalcStats));

        var e = t.GetEvent("CellsChanged", PublicInstance);
        Assert.NotNull(e);
        Assert.Equal(typeof(EventHandler<CellsChangedEventArgs>), e.EventHandlerType);
        Assert.True(e.AddMethod!.IsPublic && !e.AddMethod.IsStatic);
    }

    // §9.1 Phase 2 (undo and dirty tracking, §9.3; CopyPaste, §9.4).
    [Fact]
    public void Workbook_HasThePhase2UndoSurface()
    {
        var t = typeof(Workbook);
        AssertMethod(t, "ApplyBatch", isStatic: false, typeof(void), typeof(IReadOnlyList<KeyValuePair<string, string>>));
        AssertInstanceGetOnly(t, "CanUndo", typeof(bool));
        AssertInstanceGetOnly(t, "CanRedo", typeof(bool));
        AssertMethod(t, "Undo", isStatic: false, typeof(bool));
        AssertMethod(t, "Redo", isStatic: false, typeof(bool));
        AssertInstanceGetOnly(t, "IsDirty", typeof(bool));
        AssertMethod(t, "MarkSaved", isStatic: false, typeof(void));
        AssertMethod(t, "CopyPaste", isStatic: false, typeof(void), typeof(string), typeof(string));
    }

    // §9.1 Phase 3 names (§9.6); the insert/delete members land with structure edits.
    [Fact]
    public void Workbook_HasThePhase3NameSurface()
    {
        var t = typeof(Workbook);
        AssertMethod(t, "DefineName", isStatic: false, typeof(void), typeof(string), typeof(string));
        AssertMethod(t, "DeleteName", isStatic: false, typeof(void), typeof(string));
        AssertMethod(t, "GetNames", isStatic: false, typeof(IReadOnlyList<KeyValuePair<string, string>>));
    }

    [Fact]
    public void Workbook_HasThePhase3StructureSurface()
    {
        var t = typeof(Workbook);
        AssertMethod(t, "InsertRows", isStatic: false, typeof(void), typeof(int), typeof(int));
        AssertMethod(t, "DeleteRows", isStatic: false, typeof(void), typeof(int), typeof(int));
        AssertMethod(t, "InsertColumns", isStatic: false, typeof(void), typeof(int), typeof(int));
        AssertMethod(t, "DeleteColumns", isStatic: false, typeof(void), typeof(int), typeof(int));
        Assert.Equal(["row", "count"], t.GetMethod("InsertRows")!.GetParameters().Select(p => p.Name));
        Assert.Equal(["column", "count"], t.GetMethod("DeleteColumns")!.GetParameters().Select(p => p.Name));
    }

    // D-53: the multi-sheet plumbing of evaluation (CR1.2, CR1.4) stays internal, so the public surface is §9.1 + CR1.3.
    [Fact]
    public void SheetBindingOfEvaluation_IsNotPublic()
    {
        Assert.Null(typeof(Gridline.Core.Evaluation.NameTarget).GetProperty("Sheet", PublicInstance));
        Assert.Null(typeof(Gridline.Core.Evaluation.ICellValueProvider).GetMethod("ResolveSheet", PublicInstance));
        Assert.NotNull(typeof(Gridline.Core.Evaluation.NameTarget).GetProperty("Sheet", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(typeof(Gridline.Core.Evaluation.ICellValueProvider).GetMethod("ResolveSheet", BindingFlags.NonPublic | BindingFlags.Instance));
    }
}
