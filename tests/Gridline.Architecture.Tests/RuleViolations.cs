// Known-violation fixtures for the §3.3 namespace rules. Bad.<Owner> holds types that depend on every namespace the
// rule forbids (one type per forbidden namespace); Good.<Owner> holds types that use only dependencies the rule allows.
// RuleSanityTests runs the production SiblingRule helper over these namespaces.
using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.Core.Commands
{
    /// <summary>Stand-in for the still-empty Core Commands namespace, so the Commands side of the rules has a type
    /// to be detected. It lives in the test assembly, never in Gridline.Core.</summary>
    public static class CommandsMarker { public static int Id() => 1; }
}

namespace Gridline.Architecture.Tests.RuleViolations.Bad.Addressing
{
    public class UsesParsing { public object P() => Gridline.Core.Parsing.FormulaParser.Parse("=1", null); }
    public class UsesEvaluation { public object P() => Gridline.Core.Evaluation.Coercion.ToText(CellValue.Blank); }
    public class UsesFunctions { public object P() => Gridline.Core.Functions.BuiltInFunctionTable.Default; }
    public class UsesDependencies { public object P() => new Gridline.Core.Dependencies.DependencyGraph(); }
    public class UsesCommands { public int P() => Gridline.Core.Commands.CommandsMarker.Id(); }
    public class UsesRootType { public object P() => new Workbook(); }
}

namespace Gridline.Architecture.Tests.RuleViolations.Bad.Parsing
{
    public class UsesEvaluation { public object P() => Gridline.Core.Evaluation.Coercion.ToText(CellValue.Blank); }
    public class UsesFunctions { public object P() => Gridline.Core.Functions.BuiltInFunctionTable.Default; }
    public class UsesDependencies { public object P() => new Gridline.Core.Dependencies.DependencyGraph(); }
    public class UsesCommands { public int P() => Gridline.Core.Commands.CommandsMarker.Id(); }
}

namespace Gridline.Architecture.Tests.RuleViolations.Bad.Evaluation
{
    public class UsesDependencies { public object P() => new Gridline.Core.Dependencies.DependencyGraph(); }
    public class UsesCommands { public int P() => Gridline.Core.Commands.CommandsMarker.Id(); }
}

namespace Gridline.Architecture.Tests.RuleViolations.Bad.Functions
{
    public class UsesParsing { public object P() => Gridline.Core.Parsing.FormulaParser.Parse("=1", null); }
    public class UsesDependencies { public object P() => new Gridline.Core.Dependencies.DependencyGraph(); }
    public class UsesCommands { public int P() => Gridline.Core.Commands.CommandsMarker.Id(); }
}

namespace Gridline.Architecture.Tests.RuleViolations.Bad.Dependencies
{
    public class UsesFunctions { public object P() => Gridline.Core.Functions.BuiltInFunctionTable.Default; }
    public class UsesCommands { public int P() => Gridline.Core.Commands.CommandsMarker.Id(); }
}

// ---- allowed dependencies: must NOT be flagged (negative controls) ----

namespace Gridline.Architecture.Tests.RuleViolations.Good.Addressing
{
    // Addressing may depend on nothing in Core; BCL only.
    public class UsesOnlyBcl { public string P() => string.Concat("A", 1.ToString()); }
}

namespace Gridline.Architecture.Tests.RuleViolations.Good.Parsing
{
    public class UsesAddressing { public object P() => CellAddress.Parse("A1"); }
    public class UsesRootType { public object P() => CellValue.Blank; }
}

namespace Gridline.Architecture.Tests.RuleViolations.Good.Evaluation
{
    public class UsesParsing { public object P() => Gridline.Core.Parsing.FormulaParser.Parse("=1", null); }
    public class UsesFunctions { public object P() => Gridline.Core.Functions.BuiltInFunctionTable.Default; }
    public class UsesAddressing { public object P() => CellAddress.Parse("A1"); }
}

namespace Gridline.Architecture.Tests.RuleViolations.Good.Functions
{
    public class UsesEvaluation { public object P() => Gridline.Core.Evaluation.Coercion.ToText(CellValue.Blank); }
    public class UsesAddressing { public object P() => CellAddress.Parse("A1"); }
}

namespace Gridline.Architecture.Tests.RuleViolations.Good.Dependencies
{
    public class UsesParsing { public object P() => Gridline.Core.Parsing.FormulaParser.Parse("=1", null); }
    public class UsesEvaluation { public object P() => Gridline.Core.Evaluation.Coercion.ToText(CellValue.Blank); }
    public class UsesAddressing { public object P() => CellAddress.Parse("A1"); }
}
