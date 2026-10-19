# Phase 1 — Foundation: Model, Parser and Evaluator

Source: Vibeless project test_vc, phase "Foundation: Model, Parser and Evaluator" (id 0043b2701db0c398329f1b36b7935dd1).
Task text below is copied verbatim from the Vibeless tickets. The full ARCHITECTURE.md text is not stored in
Vibeless; the ticket specs, project constraints and decisions are the binding spec. Where a ticket cites a spec
section whose literal content it does not reproduce, the implementer chooses the Excel-compatible behaviour,
documents the choice in DECISIONS.md citing the section, and flags it in its report.

## Global Constraints

- Gridline.Core MUST NOT reference any other Gridline project, System.Windows*, System.IO.File, System.IO.Directory, System.IO.FileStream, or System.Console (§3.2).
- Gridline.IO may reference only Core and MUST NOT depend on App, Cli, System.Windows*, or System.Console. Gridline.Cli may reference Core and IO and MUST NOT depend on App or System.Windows*. Gridline.App may reference Core and IO and MUST NOT depend on Cli (§3.2).
- Inside Core, namespace dependencies MUST obey §3.3: Addressing depends on no other Core namespace; Parsing MUST NOT depend on Evaluation, Functions, Dependencies, or Commands; Evaluation MUST NOT depend on Commands or Dependencies; Functions MUST NOT depend on Parsing, Dependencies, or Commands; Dependencies MUST NOT depend on Functions or Commands.
- No public mutable static state anywhere in src/ (§3.3).
- src/ projects MUST use only the .NET base class library (including System.Text.Json and System.Text.Encoding.CodePages); no third-party packages. Test projects are limited to xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, FsCheck.Xunit, NetArchTest.Rules, FlaUI.UIA3, coverlet.collector (§2).
- Every project MUST target .NET 10 with C# latest and <Nullable>enable</Nullable>; Core, IO, Cli target net10.0, App targets net10.0-windows with <UseWPF>true</UseWPF>; Cli assembly name is gridline (§2).
- Recalculation, dependency traversal and cycle detection MUST NOT use call-stack recursion proportional to chain length; a 100,000-cell dependency chain MUST evaluate without stack overflow, and P-2 requires SetCell on A1 in <= 250 ms with EvaluatedFormulaCount = 99,999 (§6.7, §12).
- Release performance budgets (median of 5 runs after warm-up unless stated): P-1 load 100k cells <= 1.5 s; P-2 chain edit <= 250 ms; P-3 SetCell with no dependents median <= 1 ms over 1,000 edits; P-4 SUM over 100k numbers edit <= 50 ms; P-5 save 100k cells <= 1 s; P-6 GUI open 100k-cell file to rendered grid <= 3 s; P-7 load <= 5 s and private memory <= 600 MB (§12).
- Canonical .gridline serialization MUST be byte-exact: UTF-8 without BOM, LF line endings, trailing newline, two-space indent, key order format/version/names/cells, and the exact string escaping rules in §8.1. SaveToPath MUST write a temp file in the same directory, Flush(flushToDisk: true), then replace the target in one step (§8.1, §8.4).
- Any API method that throws MUST leave the workbook completely unchanged, and a failed load MUST load nothing (no partial state) (§9.2, §8.2).
- Every mutating Workbook call recalculates synchronously before returning, raises CellsChanged once after recalc (not if nothing changed), and is exactly one undo step; workbook use is single-threaded only (§6.7, §9.2, §9.3).
- Names, signatures, formats, error codes, exit codes, AutomationIds, regexes and canonical value strings defined in §4.5, §8, §9, §10 and §11.7 MUST be implemented exactly as written; the CLI MUST emit UTF-8 without BOM with LF line endings and exit codes 0-5 as defined in §10.1.
- Every phase MUST end with dotnet build Gridline.sln -c Release and dotnet test Gridline.sln -c Release (excluding Category=UI) passing, DECISIONS.md updated with section references, and the work committed; behavior from earlier phases MUST keep working (§2, §13).
- Requirement traceability: commits, tests and decision records MUST cite spec section numbers (e.g. §6.3) as requirement IDs, and no task may assume access to the hidden acceptance suite (§0).
- Function library is fixed at 70 deterministic built-ins with no volatile functions and no text-to-date parsing (§6.8, §6.10, §7).
- Use System.Text.Json for loading and a custom byte-exact writer for saving .gridline files (§2, §8.1).
- Plain .NET console app with hand-rolled command and script parsing for Gridline.Cli (no System.CommandLine) (§2).

## Task 1: Solution scaffolding, project references and build settings

Vibeless task id: 6fbbc0f466b51e3f1b63216338f518e1

**Spec:** Create Gridline.sln with src projects Gridline.Core, Gridline.IO, Gridline.Cli (assembly name gridline) and test projects Gridline.Core.Tests, Gridline.IO.Tests, Gridline.Cli.Tests, Gridline.Architecture.Tests per §3.1. Set net10.0, C# latest, Nullable enable (nullable warnings as errors only), no packages in src/. Test projects reference only the §2 package list. Add project references per §3.2 (Core none; IO->Core; Cli->Core,IO). Create DECISIONS.md and empty namespace folders from §3.3. Do NOT create the App project yet and do not implement any behavior.

**Task constraints:** No third-party packages in src/. Cli assembly name must be gridline.

**Acceptance Criteria:** dotnet build Gridline.sln -c Release and dotnet test -c Release succeed with a placeholder test in each project; project references match §3.2.

## Task 2: Addressing: cell addresses, ranges and grid bounds

Vibeless task id: 60dd6588512885dd0c45472a2c0919b7

**Spec:** In Gridline.Core.Addressing implement parsing and formatting of [$]COLUMN[$]ROW addresses (case-insensitive, A-ZZ, rows 1-100000), canonical uppercase no-$ output, and ranges ADDR:ADDR normalized to top-left:bottom-right (B3:A1 -> A1:B3). Keep $ flags available for formula references. Invalid addresses are reported (TryParse) and API-level callers throw ArgumentException. Expose column letter<->number helpers. Namespace must depend on no other Core namespace. No formula code here.

**Acceptance Criteria:** Tests cover a1, $B$7, zz100000, B3:A1->A1:B3, and rejection of A0, AAA1, A100001, empty and malformed strings.

## Task 3: Public value types: ValueKind, ErrorCodes, CellValue

Vibeless task id: 1df1da7aa189a8ffc11576b2940348aa

**Spec:** In namespace Gridline.Core add ValueKind, ErrorCodes (const strings exactly as §9.1) and readonly struct CellValue : IEquatable<CellValue> with Kind, Number, Text, Boolean, Error, Blank, FromNumber (-0 -> 0; NaN/infinity -> Error #NUM!), FromText, FromBoolean, FromError (ArgumentException if code not in ErrorCodes) and ToCanonicalString per §8.3 (BLANK, N: using ToString("R", InvariantCulture) with -0 as 0, T: with \\ \t \n \r escaping, B:TRUE/FALSE, E:code). Implement Equals/GetHashCode. Text over 32,767 code units is handled by callers, not here.

**Acceptance Criteria:** Tests verify each canonical string form including E:#DIV/0!, escaped text, -0 printing as N:0, FromNumber(NaN) yielding error, and FromError("bad") throwing.

## Task 4: Cell input interpretation (§4.3)

Vibeless task id: 6a9abc9d0016be0c68306bfdc1c93d24

**Spec:** Implement a classifier that applies the §4.3 rules in order, first match wins: empty -> blank; '=' prefix -> formula (lone '=' is a formula with parse error); leading apostrophe -> text minus the apostrophe; TRUE/FALSE case-insensitive -> boolean; regex ^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?%?$ -> number (trailing % divides by 100; non-finite result becomes text); otherwise verbatim text with no trimming (' 5' is text). Use invariant culture. Enforce 32,767 UTF-16 unit input max with ArgumentException at API level. Place under Gridline.Core without depending on Parsing/Evaluation.

**Acceptance Criteria:** Tests cover 5%, 1e999 (text), ' 5' (text), '=' formula, 'true' boolean, "'=x" text, '.5', '5.' and empty input.

## Task 5: Formula tokenizer (§5.1)

Vibeless task id: aa086b7f57315be6affc70a53012ec6e

**Spec:** In Gridline.Core.Parsing implement the tokenizer per §5.1: ignore space/tab between tokens (not inside strings, not between function name and '('); number literals (no sign) using the two literal forms; string literals with doubled-quote escape; error literals (exact §4.5 codes); word tokens [$]?[A-Za-z][A-Za-z0-9_.]* with optional $ before row digits, classified as function name (followed by '('), cell reference (in bounds), boolean literal, else name reference; token with $ that is not a valid reference is a parse error; operators : + - * / ^ & % = <> < > <= >=; punctuation ( ) ,. Preserve each number's exact original lexeme for §5.5. Parsing namespace MUST NOT depend on Evaluation, Functions, Dependencies, Commands.

**Acceptance Criteria:** Tests cover classification of SUM( vs SUM (, a1 vs foo, TRUE, #N/A literal, $B$7, bad $ tokens, "a""b" strings and lexeme retention of 1.50.

## Task 6: Formula parser, AST and parse errors (§5.2-5.4)

Vibeless task id: a9570ac1e45b7dbbda5947806edc5b1f

**Spec:** Implement the recursive-descent parser for the §5.3 EBNF with the §5.2 precedence: ':' tightest (only between two cell references), unary +/-, postfix %, '^' left-associative, * /, + -, &, comparisons. AST nodes must retain explicit parenthesized groups and number lexemes. Support omitted arguments (IF(A1,,3)). Syntax errors, known-function arity violations (use an arity table provided by a Functions-agnostic registry interface in Parsing, populated later), and misuse of ':' yield a stored formula that evaluates to #PARSE!. Unknown functions parse successfully. Required results to hold at parse level: -2^2 groups as (-2)^2, 2^3^2 as (2^3)^2.

**Acceptance Criteria:** Tests assert AST shapes for =-2^2, =2^3^2, =1+2&3, ="a"&1=1, =50%%, parse-error cases (=, =1+, =A1:5, wrong arity) and omitted args.

## Task 7: Evaluator core: coercion, operators, errors, ranges (§6)

Vibeless task id: ec1640e771f7fb6cea8080d0fd4b514f

**Spec:** In Gridline.Core.Evaluation implement scalar evaluation of the AST over an abstract cell-value provider (so Evaluation does not depend on Dependencies/Commands). Implement §6.2 coercions (number trims U+0020 and uses the §4.3 number grammar; text via G15 invariant; boolean), §6.3 operators (arithmetic, unary, %, power edge cases 0^0 -> #NUM!, 0^neg -> #DIV/0!, neg base with fractional exponent -> #NUM!; concatenation; comparison with blank adaptation and Number<Text<Boolean ordering, OrdinalIgnoreCase via ToUpperInvariant), §6.4 left-then-right first-error propagation, §6.5 range in scalar context -> #VALUE!, §6.6 blank reference -> 0, #NAME? for unknown functions/names, text over 32,767 -> #VALUE!, non-finite -> #NUM!. Define the function-call interface (lazy argument access, reference vs direct argument).

**Acceptance Criteria:** Tests prove -2^2=4, 2^3^2=64, 1+2&3='33', "a"&1=1 FALSE, 50%%=0.005, comparison ordering, =A1:B2 -> #VALUE!, blank ref -> 0, and first-error order.

## Task 8: Math function library (§7.1) with aggregation rules

Vibeless task id: 70e2dce5ce58b097cfc6a4059057f5c3

**Spec:** In Gridline.Core.Functions implement the 20 math functions: SUM, AVERAGE, MIN, MAX, PRODUCT, COUNT, COUNTA, COUNTBLANK, SUMPRODUCT, ABS, SIGN, INT, MOD, POWER, SQRT, PI, ROUND, ROUNDUP, ROUNDDOWN, SUMSQ. Implement the §7.0 aggregation table (reference args: numbers only, errors propagate; direct args: bool/text coercion) and §6.9 integer truncation. ROUND family operates half-away-from-zero on the shortest round-trip decimal (use decimal/string-based logic, not binary floats). Register arity limits (variadic max 255) with the parser's registry. Functions MUST NOT depend on Parsing, Dependencies, Commands.

**Task constraints:** ROUND must use shortest round-trip decimal representation. Functions MUST NOT depend on Parsing, Dependencies or Commands.

**Acceptance Criteria:** Tests prove ROUND(2.675,2)=2.68, ROUND(0.285,2)=0.29, ROUND(-2.5,0)=-3, ROUND(1250,-2)=1300, MOD(-3,2)=1, MOD(3,-2)=-1, INT(-2.5)=-3, empty AVERAGE -> #DIV/0!.

## Task 9: Logic and information functions (§7.2)

Vibeless task id: 549fc989fd41cc01c002d2dba6a5b0ce

**Spec:** Implement IF (2-3 args, lazy, omitted f -> FALSE, omitted t -> 0), AND, OR, XOR (reference args: booleans count, numbers coerced, text/blank ignored; direct coerced; no logical values -> #VALUE!), NOT, IFERROR and IFNA (lazy alt), ISBLANK (TRUE only for reference to truly blank cell; '' formula result is not blank), ISNUMBER, ISTEXT, ISLOGICAL, ISERROR, ISNA. Type tests never return an error. Register arities. Scope limit: no lookup functions.

**Acceptance Criteria:** Tests cover lazy branch evaluation, IF(A1,,3), AND over range with text ignored, no-logical -> #VALUE!, IFNA passing non-#N/A errors, ISBLANK on '' formula result.

## Task 10: Text functions (§7.3)

Vibeless task id: 4b75e6077de50c6b332fc8cc7353f727

**Spec:** Implement LEN, LEFT, RIGHT, MID, UPPER, LOWER, PROPER, TRIM, CONCAT, TEXTJOIN, FIND, SEARCH, SUBSTITUTE, REPLACE, REPT, EXACT, VALUE with 1-based UTF-16 positions. Follow the edge rules: LEFT/RIGHT n<0 -> #VALUE!; MID s<1 or n<0 -> #VALUE!; PROPER per char.IsLetter rule ("o'neil 2nd" -> "O'Neil 2Nd"); TRIM only U+0020 and collapses runs; FIND case-sensitive, FIND("",t,s)=s; SEARCH wildcards * ? ~ case-insensitive; SUBSTITUTE with optional instance; REPT and CONCAT results over 32,767 -> #VALUE!. Ranges read row-major. Register arities.

**Acceptance Criteria:** Tests cover PROPER example, SEARCH with *, ?, ~ escape, FIND not found -> #VALUE!, SUBSTITUTE instance i, REPT overflow -> #VALUE!, TEXTJOIN ignore_empty.

## Task 11: Dependency graph, iterative recalc and cycle detection (§6.1, §6.7)

Vibeless task id: 8b70c81a52a8899e105eec5e27e9666a

**Spec:** In Gridline.Core.Dependencies build a static dependency graph (every reference/range/name in formula text, including untaken branches). Implement iterative (non-recursive over chain length) topological scheduling to recompute a changed cell plus transitive dependents, each formula evaluated at most once per recalc; iterative cycle detection marking every cell in a cycle (self-reference, via ranges and names) as #CIRC! so dependents see it as an ordinary error and IFERROR works; breaking a cycle restores values. Range edits (including blank cells inside a range) trigger dependents. Track EvaluatedFormulaCount. Dependencies MUST NOT depend on Functions or Commands.

**Acceptance Criteria:** Tests: 100,000-cell A2=A1+1 chain evaluates without overflow; self-reference and 3-cell cycle give #CIRC!; =IFERROR(A1,0) over a cycle works; count equals formula cells in {X} plus dependents.

## Task 12: Workbook class: Phase 1 API (§9.1, §9.2)

Vibeless task id: 48203a0a7e65bb327a7c76c855ff3440

**Spec:** Implement Gridline.Core.Workbook with constants MaxRows, MaxColumns, MaxInputLength, UndoLimit, SetCell, GetInput, GetValue, GetUsedAddresses (row-major), LastRecalc (RecalcStats record struct) and CellsChanged event with CellsChangedEventArgs (canonical addresses). Compose address parsing, input interpretation, parser, evaluator, functions and dependency recalc. SetCell is synchronous, atomic (any exception leaves state unchanged), raises CellsChanged once after recalc only if input or value changed. Invalid address/range or oversized input -> ArgumentException. Include the Phase 2/3 members only as signatures needed later; do not implement them yet. Single-threaded only.

**Acceptance Criteria:** Tests: SetCell/GetValue round trips, atomicity after ArgumentException, CellsChanged raised once with all changed addresses and not raised for no-op, EvaluatedFormulaCount semantics, GetUsedAddresses order.

## Task 13: Canonical .gridline writer and strict loader (§8.1-8.3)

Vibeless task id: 301e3ad83dc08412f83caa28e46bb1d7

**Spec:** In Gridline.IO implement a byte-exact writer: UTF-8 no BOM, LF, trailing newline, two-space indent, key order format/version/names/cells, empty collections as `"names": {},` and `"cells": []`, names sorted OrdinalIgnoreCase, cells row-major as [address,input] one per line, with the exact §8.1 string escaping (only \" \\ \b \f \n \r \t and \u00xx lowercase hex for other controls; everything else literal). Implement loader using System.Text.Json accepting any valid whitespace/key order, ignoring unknown top-level keys, throwing GridlineFormatException (message contains 'unsupported version' for version>1) for every §8.2 failure case with no partial state. After load: full recalc, not dirty-ready state, empty undo history. IO MUST NOT depend on Console or System.Windows*.

**Acceptance Criteria:** Tests: byte-for-byte save of a sample matches the §8.1 layout; load-then-save of canonical file is identical; each §8.2 malformed case throws GridlineFormatException; FsCheck file round-trip property.

## Task 14: Atomic SaveToPath and LoadFromPath (§8.4)

Vibeless task id: e198cdebc897ad2ebd37436351a9401a

**Spec:** Add SaveToPath and LoadFromPath to Gridline.IO. Save writes a temp file in the same directory, flushes with Flush(flushToDisk: true), then replaces the target using File.Move(tmp, path, overwrite: true) or File.Replace. A failure at any step must leave the target as complete old or complete new content. Map missing file / I/O errors to distinct exception types the CLI can translate to exit code 2 and format errors to exit code 3. Leftover temp files are acceptable. Do not call MarkSaved in Phase 1 API surface beyond what exists.

**Task constraints:** Temp file MUST be in the same directory as the target.

**Acceptance Criteria:** Tests: save replaces existing file atomically; simulated failure before replace leaves original intact; load of missing file raises I/O error type; saved bytes equal the canonical writer output.

## Task 15: CLI: eval, set, exit codes and output rules (§10.1, §10.2)

Vibeless task id: 6fd46b3efff7b43216ec0cece0254ad7

**Spec:** In Gridline.Cli (assembly gridline) implement hand-rolled argument parsing for `gridline eval <file> <addr>...`, `gridline eval <file> --all`, `gridline set <file> <addr> <input> [--out <path>]`. Eval prints `ADDR\t<canonical value>` per address in the order given, or all used cells row-major. Set sets the cell and saves atomically in place or to --out. All stdout is UTF-8 without BOM with LF line endings; errors go to stderr as a single line. Exit codes: 0 success, 1 usage error, 2 file not found/I/O, 3 format error, 5 invalid address. Cli MUST NOT depend on App or System.Windows*.

**Acceptance Criteria:** Cli tests (in-process or process) verify eval output lines and order, --all row-major, set in place and --out, and exit codes 1, 2, 3, 5 with single-line stderr.

## Task 16: CLI: script mode, Phase 1 commands (§10.3)

Vibeless task id: 3a75866af3272ef2a1082996ecf4fffe

**Spec:** Implement `gridline script [--file <path>]` reading stdin against a new or loaded workbook. One command per line (LF or CRLF); blank lines and '#' lines ignored; command word case-insensitive; args separated by single spaces. Commands: SET (input is everything after the second space verbatim; `SET A1` clears), SETX (unescape \\ \t \n \r), GET (`ADDR\t<canonical>`), INPUT (`ADDR\t<input escaped per §8.3>`), DUMP (all used cells row-major), STATS (`EVALUATED\t<count>`), SAVE <path> (atomic), LOAD <path>. On failure print `ERR\t<message>` and continue; exit code 4 if any ERR printed. Commands from later phases print `ERR\tnot implemented`.

**Acceptance Criteria:** Tests drive scripts through stdin: SET with spaces, SETX escapes, GET/INPUT/DUMP/STATS outputs, ERR continuation with exit code 4, CRLF scripts, UNDO printing ERR\tnot implemented.

## Task 17: Architecture tests and Phase 1 performance tests

Vibeless task id: f0b85beed3665da97d36aed74db917e9

**Spec:** In Gridline.Architecture.Tests encode every §3.2 project dependency rule and every §3.3 namespace rule (including Core must not use System.IO.File/Directory/FileStream/System.Console, no System.Windows*, and no public mutable static state in src/) as NetArchTest tests. Add Release performance tests P-1 (load 100k-cell file <= 1.5 s), P-2 (100k chain SetCell A1 <= 250 ms, EvaluatedFormulaCount = 99,999), P-3 (median <= 1 ms over 1,000 edits), P-4 (SUM edit <= 50 ms), P-5 (save <= 1 s) and P-7 (load <= 5 s, memory <= 600 MB) using warm-up and median of 5. Optimize hot paths if a budget fails; do not change budgets. Update DECISIONS.md.

**Acceptance Criteria:** All architecture tests pass; P-1 to P-5 and P-7 pass in Release; dotnet build and dotnet test -c Release pass; DECISIONS.md updated and work committed.
