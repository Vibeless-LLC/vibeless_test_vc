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

### D-10 Cell input interpretation (§4.3)
`CellInputInterpreter.Interpret` (root namespace `Gridline.Core`, no Parsing/Evaluation dependency) applies the rules in order, first match wins: empty -> Blank; leading `=` -> Formula (`FormulaText` is the rest; a lone `=` is a Formula with empty text, which the parser later turns into `#PARSE!`); leading `'` -> Text minus that one apostrophe; case-insensitive whole-string TRUE/FALSE -> Boolean (no trimming, so `TRUE ` is text); number grammar -> Number; otherwise verbatim Text (no trimming, so ` 5` is text). Result is `CellInputResult(Kind, Value, FormulaText)`.
Choice: a lone `'` yields Text `""` (not Blank), so the cell stays a text cell. `MaxInputLength = 32767` UTF-16 units lives on `CellInputInterpreter`; longer input throws ArgumentException, null throws ArgumentNullException.

### D-11 Number grammar (§4.3, §6.2)
`NumberGrammar.TryParse(ReadOnlySpan<char>, out double)` is a hand-written span matcher for `^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?%?$`, no allocation or regex. Choice: `\d` means ASCII 0-9 only (non-ASCII digits are text), since the engine is culture-invariant. Value parsed with `double.TryParse(NumberStyles.Float, InvariantCulture)`, trailing `%` divides by 100, -0 normalised to 0, and a non-finite result (`1e999`) is not a number so the input becomes Text. Only one `%` is allowed (`5%%` is text). No trimming here; §6.2 coercion callers trim U+0020 first.

### D-12 Formula tokenizer (§5.1)
`Gridline.Core.Parsing.Tokenizer.Tokenize(formulaText)` (input is the text after the leading `=`) is one linear pass, no recursion, no Regex. It returns a `TokenizeResult` (tokens ending in `End`, or `ErrorMessage` + 0-based `ErrorPosition`); user-input errors never throw (a failed tokenize becomes a `#PARSE!` formula later). `Token` is a readonly record struct: Kind, Position, Lexeme (exact source text, needed for §5.5), plus payload (Number, Text, Boolean, Reference). Depends only on Addressing and root Core types.
R6 (numbers): literal forms are `digits ['.' digits*] [exponent]` and `'.' digits [exponent]`, exponent `[eE][+-]?digits`; no sign and no `%` (those are Minus/Plus and Percent tokens). Value is `double.Parse(lexeme, NumberStyles.Float, InvariantCulture)`; a non-finite value (`1e999`) is a tokenizer error, i.e. `#PARSE!`. An exponent marker not followed by digits is not consumed (`1e` is Number then Name).
R7 (error literals): `#` plus letters/digits/`/`/`!`/`?` is matched case-insensitively against the eight ErrorCodes and normalised to the canonical code (`#n/a` -> `#N/A`); anything else starting with `#` (`#FOO!`, `#NULL!`, lone `#`, `#N/AX`) is a tokenizer error. The token keeps the original lexeme.
Words: `[$]?[A-Za-z][A-Za-z0-9_.]*` where `$` may also appear inside the word (so `$B$7` is one word). Classification order: any word containing `$` must be a valid in-bounds `CellReference` else tokenizer error (`$foo`, `A$`, `$A$100001`, `AAA$1`; this also applies before the `(` check, so `$A$1(` is an error); otherwise followed immediately by `(` -> Function (name upper-cased); valid in-bounds reference -> CellRef; TRUE/FALSE case-insensitive -> Boolean; else Name (so `A100001`, `AAA1` are Names). `TRUE(` is a Function; `A1(` is a Function.
Choices: whitespace is space and tab only, skipped between tokens (so `SUM (` is Name, LParen, and `< =` is Less, Equal); `<=`, `>=`, `<>` use longest match; strings use `""` for an embedded quote and an unterminated string is an error at the opening quote; Name tokens keep the case as written; any other character (`@`, `{`, `!`, a lone `.`) is an error at its position. Adjacent oddities such as `1.5.3` or `5abc` tokenize without error and are left for the parser to reject.
