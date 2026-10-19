# Gridline decisions

Decision log for Gridline (.NET 10 Windows spreadsheet engine plus CLI). Each entry cites the spec section it implements.

## Phase 1

### D-1 Solution layout (§3.1)
`Gridline.sln` at the root with `src/Gridline.Core`, `src/Gridline.IO`, `src/Gridline.Cli` and `tests/Gridline.{Core,IO,Cli,Architecture}.Tests`. The App project is deferred to a later phase.

### D-2 Project references (§3.2)
Core references nothing. IO references Core. Cli references Core and IO. Each test project references the src projects it tests; Architecture.Tests references all three src projects.

### D-3 Build settings (§2)
Shared settings live in the root `Directory.Build.props`: `net10.0`, `LangVersion latest`, `Nullable enable`, `WarningsAsErrors=Nullable` (only nullable warnings are errors), ImplicitUsings enabled. Cli sets `OutputType Exe`, `AssemblyName gridline`, `RootNamespace Gridline.Cli`.

### D-4 Packages (§2)
No packages in `src/`. Test projects take only what they need from the §2 list, with pinned stable versions: xunit 2.9.3, xunit.runner.visualstudio 3.1.4, Microsoft.NET.Test.Sdk 17.14.1, coverlet.collector 6.0.4; Architecture.Tests also NetArchTest.Rules 1.3.2. FsCheck.Xunit is added when first needed.

### D-5 Core namespace folders (§3.3)
Core has Addressing, Parsing, Evaluation, Functions, Dependencies and Commands folders, each held by a `.gitkeep` until populated.

### D-6 Addressing grammar and bounds (§4.2, §4.3)
`Gridline.Core.Addressing` holds `GridBounds` (public consts MaxColumns 702 = A..ZZ, MaxRows 100000), `ColumnName` (letters<->number), `CellAddress` (validated, canonical uppercase no-$ `ToString`), `CellReference` (keeps `$` flags for formulas) and `CellRange` (normalised top-left:bottom-right, so `B3:A1` becomes `A1:B3`). It depends on no other Core namespace. `TryParse` reports failure; `Parse` throws `ArgumentException`; constructors throw `ArgumentOutOfRangeException` for out-of-bounds values. Parsing is case-insensitive, ASCII only, and rejects any whitespace, null, empty, a lone `$`, `A0`, `AAA1`, rows over 100000.
Choice: a row with a leading zero such as `A01` is rejected (matches Excel and keeps one canonical spelling). `CellAddress.TryParse` and `CellRange.TryParse` accept `$` markers and discard them; use `CellReference` when the flags matter.

### D-7 Value kinds and error codes (§9.1)
`Gridline.Core.ValueKind` = Blank, Number, Text, Boolean, Error. `ErrorCodes` is a static class of public consts: DivZero `#DIV/0!`, NA `#N/A`, Name `#NAME?`, Num `#NUM!`, Ref `#REF!`, Value `#VALUE!`, Circ `#CIRC!`, Parse `#PARSE!`. PROVISIONAL: §9.1's text is not available, so the set and constant names come from controller ruling R2 (Excel set minus `#NULL!`, plus `#CIRC!` and `#PARSE!`). Membership is tested with `ErrorCodes.IsKnown`, backed by a private FrozenSet (no public mutable static state, §3.3).

### D-8 CellValue (§9.1, §8.3)
`CellValue` is a readonly struct; `default` and `CellValue.Blank` are the blank value. `FromNumber` normalises -0 to 0 and turns NaN/infinity into the `#NUM!` error, so equality and hash codes stay consistent. `FromText(null)` throws ArgumentNullException; `FromError` throws ArgumentException for a code not in `ErrorCodes`. Choice: reading a payload accessor (`Number`, `Text`, `Boolean`, `Error`) on the wrong kind throws InvalidOperationException rather than returning a default, so kind bugs fail loudly. Equality compares Kind and payload: text and errors ordinally, numbers with `==`. Text longer than 32,767 code units is the caller's concern, not CellValue's.

### D-9 Canonical value strings (§8.3)
`ToCanonicalString`: Blank `BLANK`; Number `N:` + `ToString("R", InvariantCulture)` (-0 prints `N:0` via normalisation); Text `T:` + text with only backslash, TAB, LF and CR escaped (as `\\`, `\t`, `\n`, `\r`); Boolean `B:TRUE`/`B:FALSE`; Error `E:` + code. `ToString()` returns the same form.
