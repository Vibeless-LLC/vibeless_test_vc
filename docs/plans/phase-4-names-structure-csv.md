# Phase 4 — Names, Structure Edits, Lookup/Date and CSV

Source: Vibeless project test_vc, phase "Names, Structure Edits, Lookup/Date and CSV" (id 6d08ef7931ca762ea1c0f6c069be25f0).

Spec: `.claude/architecture.md` (Gridline ARCHITECTURE.md, Spec version 1.0). It is the binding authority; this plan argues from it. Vibeless Phase 4 equals spec §13 "Phase 3 — Names, structure edits, lookup/date, CSV": §7.4–§7.6, §8.5, §9.5, §9.6, the §10 Phase 3 commands and the §11 Phase 3 items. Behaviour from earlier phases MUST keep working (§13).

Task text is copied verbatim from the Vibeless tickets.

## Global Constraints

- The Global Constraints of docs/plans/phase-3-editing-gui.md still apply. Rulings R1–R56 in DECISIONS.md remain binding unless a ruling below supersedes one.
- Stack (§2): .NET 10, C# latest, Nullable enabled in every project. Core, IO, Cli target net10.0; App targets net10.0-windows with UseWPF. No packages in src/. Tests may use only xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, FsCheck.Xunit, NetArchTest.Rules, FlaUI.UIA3, coverlet.collector.
- Layout (§3.1): four src projects (Core, IO, Cli, App) and five test projects (Core, IO, Cli, App, Architecture). No other projects.
- Dependencies (§3.2, §3.3): Core references nothing; IO → Core; Cli → Core, IO; App → Core, IO, never Cli. Inside Core: Addressing depends on no other Core namespace; Parsing not on Evaluation/Functions/Dependencies/Commands; Evaluation not on Commands/Dependencies; Functions not on Parsing/Dependencies/Commands; Dependencies not on Functions/Commands. No public mutable static state in src/.
- Exact surfaces (§0): names, signatures, formats, error codes, exit codes, AutomationIds and canonical strings in §4.5, §8, §9, §10, §11.7 are implemented exactly as written. The §9.1 Phase 3 members are exactly: `void InsertRows(int row, int count)`, `void DeleteRows(int row, int count)`, `void InsertColumns(int column, int count)`, `void DeleteColumns(int column, int count)`, `void DefineName(string name, string refersTo)`, `void DeleteName(string name)`, `IReadOnlyList<KeyValuePair<string, string>> GetNames()`.
- Mutations (§9.2, §9.3): atomic on exception; synchronous recalc; CellsChanged once after recalc and not when nothing changed; exactly one undo step per mutating call (SetCell, ApplyBatch, CopyPaste, insert/delete, DefineName, DeleteName); undo restores the exact prior inputs (formula text verbatim) and the exact prior names.
- No call-stack recursion proportional to chain length or formula operator-chain length (§6.7, R10).
- Performance budgets (§12) P-1…P-7 hold in Release; existing gates must keep passing.
- Every task: dotnet build Gridline.sln -c Release with 0 warnings; dotnet test Gridline.sln -c Release --filter Category!=UI passes; UI tests tagged [Trait("Category","UI")] must pass locally when the task adds or touches them; DECISIONS.md updated with § references (next entry D-41); commit messages cite § numbers.
- No task may assume access to the hidden acceptance suite (§0).

### Controller rulings for Phase 4 (R57–R75)

- R57 (§5.1, §9.6): §9.6 allows names starting with `_` but the §5.1 word token starts with a letter. A word token may also start with `_`; such a token can only classify as a name reference. Cost if wrong: `=_x` with `_x` undefined is #NAME? instead of #PARSE!.
- R58 (§9.6, §4.2): `refersTo` is accepted case-insensitively and every row and column component must carry `$`. Range corners are normalized top-left:bottom-right (§4.2). It is stored uppercase, for example `$a$1` → `$A$1` and `$C$10:$A$1` → `$A$1:$C$10`. A single-cell range `$A$1:$A$1` stays a range.
- R59 (§9.6, §8.1, §8.2): `DefineName` rejects `#REF!` with ArgumentException. The loader accepts the exact string `#REF!` as a `refersTo`, so a saved workbook whose name target was deleted reloads and re-saves byte-exact.
- R60 (§9.3, §9.2): `DefineName` with the identical casing and `refersTo` is a no-op, with no undo step and no CellsChanged. A casing-only change is a change. Name operations raise CellsChanged with every address whose value changed. Formula inputs are not rewritten by DefineName or DeleteName.
- R61 (§9.5): on insert, a single reference past `MaxRows`/`MaxColumns` → #REF! (spec). A range whose start would pass the bound → #REF!. A range whose end alone would pass it is clamped to the bound.
- R62 (§9.5): `count < 1` → ArgumentOutOfRangeException for all four methods. InsertColumns needs `1 ≤ column ≤ MaxColumns`. DeleteColumns needs `1 ≤ column` and `column + count - 1 ≤ MaxColumns`.
- R63 (§9.3, §9.2): a structure edit that moves no input, rewrites no formula and changes no name is a no-op, with no undo step and no CellsChanged. Otherwise every formula cell is re-evaluated, and `LastRecalc.EvaluatedFormulaCount` is the number of formula evaluations actually performed.
- R64 (§9.2): CellsChanged for a structure edit carries every address whose input or value differs, comparing the same address before and after.
- R65 (§7.4, §7.1): where a lookup function expects a range (INDEX, MATCH, VLOOKUP/HLOOKUP table, ROWS, COLUMNS), a direct scalar argument is treated as a 1×1 range, as SUMPRODUCT does. ROW/COLUMN with a direct, non-reference argument → #VALUE!. INDEX with a negative `r` or `c` → #VALUE!. MATCH `type` is truncated (§6.9): any positive value acts as 1 and any negative value as -1.
- R66 (§7.4, §6.3): exact-mode lookup equality: a number matches a number of the same value, a boolean the same boolean, and text matches text case-insensitively with wildcards `* ? ~` (as SEARCH, keeping the Phase 3 ruling that `~` escapes any next character). Blank keys never match in exact mode. A blank lookup value is looked up as number 0 (§6.6). Approximate mode compares with §6.3 ordering.
- R67 (§7.4, §6.5): CHOOSE treats its chosen argument exactly as IF treats its chosen branch, including a range.
- R68 (§7.5, §6.1): a boolean criteria operand matches Boolean cells only, compared with its operator (FALSE < TRUE). A blank criteria value behaves as `""`. An error criteria propagates. A direct, non-reference `range`, `sum_range` or `avg_range` → #VALUE!. Resized `sum_range`/`avg_range` positions beyond the grid are ignored. Dependencies: when both `range` and the third argument of SUMIF/AVERAGEIF are references, the resized rectangle is a static dependency. It is computed from the formula text by function name in Dependencies or Parsing (never Functions), so editing any cell of it recalculates the formula.
- R69 (§7.6, §6.2, §6.9): DATE computes on Excel's serial calendar, in which February 1900 has 29 days: `DATE(1900,2,29)` = 60 and `DATE(1900,3,0)` = 60. Serial and integer arguments are coerced per §6.2 (numeric text coerces; date-looking text → #VALUE!), then truncated toward zero (§6.9). DAYS uses INT (floor) and has no range check.
- R70 (§8.5, §8.2, §8.4): CSV lives in `Gridline.IO.CsvFormat` (public static):
  - `Workbook Import(ReadOnlySpan<byte> bytes, char delimiter = ',')`
  - `Workbook ImportFromPath(string path, char delimiter = ',')`
  - `byte[] Export(Workbook workbook, char delimiter = ',')`
  - `void ExportToPath(Workbook workbook, string path, char delimiter = ',')`, atomic like §8.4

  An invalid delimiter → ArgumentException before anything is read. An imported workbook is like a load: full recalc, not dirty, empty history, no names. A stored input longer than 32,767 → GridlineFormatException. The existing DelimitedTextParser/DelimitedTextMapping are reused, and GUI clipboard paste behaviour is unchanged.
- R71 (§14, §8.5): the CSV round-trip property: for any grid of field strings F (including delimiters, quotes, CR, LF, leading/trailing spaces, leading `=`/`'`, numeric, boolean and empty fields), let W1 = Import(Encode(F)) and W2 = Import(Export(W1)). Every cell of W1 and W2 then has an equal value, and Export(W2) is byte-equal to Export(W1).
- R72 (§10.1, §10.2): `--delimiter <c>` takes exactly one UTF-16 character other than `"`, CR or LF, else exit 1.
  - import-csv: missing CSV → 2; CSV format error → 3; output I/O error → 2.
  - export-csv: missing input → 2; `.gridline` format error → 3; output I/O error → 2.
  - Both write atomically and print nothing on success.
- R73 (§10.3): INSERTROWS/DELETEROWS take `<row>` and `<n>` as `^[0-9]+$` decimal integers. INSERTCOLS/DELETECOLS take `<col>` as case-insensitive letters A–ZZ. Any other shape, an extra or missing argument, or an API exception → `ERR\t<message>`. NAME takes exactly two arguments. NAMES prints nothing when there are no names. All of them inside BEGIN…END → ERR (R40).
- R74 (§11.5, §8.5): File > Import CSV… (AutomationId `MenuImportCsv`) acts like Open: unsaved-changes prompt first, comma delimiter. The result is Untitled, not dirty, with an empty history. File > Export CSV… (AutomationId `MenuExportCsv`) writes the current workbook with a comma and changes no file path, title or dirty state. A failure shows a message box and leaves the workbook unchanged.
- R75 (§11.5, §11.3): a left-click on a row or column header selects the whole row (A–ZZ) or column (1–100000); Shift+click extends it. A right-click on a header outside the current whole-row/column selection selects that header first. The context menu offers Insert (`ContextInsert`) and Delete (`ContextDelete`), acting on as many rows/columns as are selected, at the first selected one. A failure shows a message box with no change. SelectionSum stays responsive on whole-row/column selections.

## Task 1: Named ranges in Core (§9.6, §5.1, §5.5, §6.1, §6.7, §9.3)
Vibeless task id: 9b410d64ccc7d69e20f52d113ab07ce7

**Spec:** Implement the §9.1 Phase 3 name members on `Workbook`: `DefineName(string name, string refersTo)`, `DeleteName(string name)`, `GetNames()` (sorted `StringComparer.OrdinalIgnoreCase`, each name in its defined casing, value = stored refersTo).

Name syntax (§9.6, verbatim): matches `^[A-Za-z_][A-Za-z0-9_.]{0,254}$`; must not be `TRUE` or `FALSE`; must not match `^[A-Za-z]{1,3}[0-9]+$`; names are unique case-insensitively; an invalid name → `ArgumentException`. `refersTo` must be a fully absolute cell or range such as `$B$1` or `$A$1:$C$10`, stored in canonical uppercase; anything else → `ArgumentException` (R58, R59). `DefineName` on an existing name replaces its definition, keeping the new casing. `DeleteName` on an unknown name → `ArgumentException`. Name matching is case-insensitive.

Evaluation: a name that refers to a cell behaves like that cell reference; a name that refers to a range behaves like that range (a reference argument for §7.0 aggregation rules; a range outside a range-accepting argument → #VALUE! per §6.5). Formulas depend on the name itself: defining, redefining or deleting it recalculates dependents. A formula using an undefined name shows `#NAME?` and resolves automatically once the name is defined. Cycles through names are cycles (§6.7, `#CIRC!`). Lexer: R57 (`_`-initial word tokens are names).

Undo (§9.3): DefineName and DeleteName are each one undo step; undo restores the exact prior names and redo reapplies; no-op rules per R60; dirty tracking unchanged in shape. Extend `UndoStep` with a names before/after payload as D-34 anticipated, keeping `UndoHistory` opaque. Canonical printing (§5.5) already resolves name casing through `INameCasingResolver`; wire it to the workbook's names so later rewrites (paste, structure edits) print names in their definition casing.

Respect §3.3: name resolution for evaluation must not make Evaluation depend on Dependencies or Commands. Keep the incremental recalc rules (§6.7): only the name's dependents re-evaluate on a name change.

**Acceptance Criteria:** Tests cover: every name-syntax rule including `_x`, `a.b`, 255-char limit, `TRUE`/`true`, `A1`/`ABC123`/`abcd1` (last valid), duplicates case-insensitive; refersTo validation and R58 normalization; `GetNames` sort and casing; `=Rate*2` and `=SUM(Total)` evaluation; `#NAME?` resolving on define and returning on delete; redefinition recalc; a cycle through a name gives `#CIRC!`; one undo step per call restoring exact names and inputs; R60 no-op; CellsChanged contents; atomicity on every exception. Build 0 warnings; non-UI tests pass; DECISIONS.md entry D-41.

## Task 2: Names in the .gridline file format (§8.1, §8.2, §14)
Vibeless task id: 2c7eb0cd7e9def38509e478b4058024a

**Spec:** Persist names through `.gridline` save and load.

Save (§8.1): `names` sorted by `StringComparer.OrdinalIgnoreCase`, each name in its defined casing, written with the existing byte-exact writer (two-space indent, the §8.1 escaping rules, `"names": {},` when empty). The public `GridlineSerializer.Serialize` and `GridlineFile` save path now write the workbook's names (remove the internal Phase-1-only overload if it becomes redundant, keeping tests meaningful).

Load (§8.2): accept a non-empty `names` object; each entry is name → refersTo string. An invalid name or refersTo (§9.6), a duplicate name (case-insensitive) or a non-string value → `GridlineFormatException`, loading nothing. `#REF!` is accepted as a stored refersTo (R59) and evaluates to `#REF!`. After a load: full recalc (formulas using names resolve), not dirty, empty undo history (§8.2). Keep R54 leniency (missing `names` allowed).

Extend the FsCheck file round-trip property (§14) to generate names (including `#REF!` targets via deleted-target scenarios or direct loader input), asserting save → load → save is byte-identical and names/inputs/values are equal.

**Acceptance Criteria:** The §8.1 example file (names Rate and Total) loads, evaluates `=SUM(B1:B3)*Rate`, and re-saves byte-for-byte. Tests for each new §8.2 rejection, the `#REF!` acceptance, sort order with mixed casing, escaping inside names section is unaffected, and the round-trip property with names. P-1, P-5, P-7 gates still pass. Build 0 warnings; non-UI tests pass; DECISIONS.md updated.

## Task 3: Insert and delete rows and columns (§9.5, §9.6, §9.3, §5.5)
Vibeless task id: 66884966d729eaae84a602cc138a072e

**Spec:** Implement `InsertRows(int row, int count)`, `DeleteRows(int row, int count)`, `InsertColumns(int column, int count)` (1-based column number) and `DeleteColumns(int column, int count)`. Rows are described; columns are symmetric (§9.5, verbatim):

- InsertRows requires `1 ≤ row ≤ MaxRows` and `count ≥ 1`, else `ArgumentOutOfRangeException`. If any cell with input would move past `MaxRows` → `InvalidOperationException`, and nothing changes. Cells at rows ≥ `row` move down by `count`.
- DeleteRows requires `1 ≤ row` and `row + count - 1 ≤ MaxRows`. Rows `[row, row+count-1]` are removed. Cells below them move up.
- Reference adjustment applies to every reference in every formula and in every name definition. Relative and absolute references are treated the same way. References point at positions, so moving a formula does not change this.
- Single cell at row `x`: insert at `r`, `n` rows: `x ≥ r` → `x + n`; if the result exceeds `MaxRows` → `#REF!`. Delete `[r, r+n-1]`: `x` inside the deleted span → `#REF!`; `x` below it → `x − n`.
- Range: insert: each endpoint independently `≥ r` → `+n`. Example: insert at 3 makes `A1:A5` → `A1:A7`; insert at 1 makes it `A3:A7`. Delete: keep the surviving rows; if none survive → `#REF!`; otherwise the new endpoints are the first and last surviving rows after the shift. Examples: delete 3–4 makes `A1:A10` → `A1:A8`; delete 1–2 makes it `A1:A8`.
- Formulas with at least one changed reference are re-emitted per §5.5 (an invalid reference prints `#REF!`; if either corner of a range became invalid the whole range prints `#REF!`; names in definition casing). Formulas with no changed references keep their text verbatim. `#PARSE!` formulas are kept verbatim (as R39).
- Names adjust under §9.5; a name whose target is fully deleted gets `refersTo` = `#REF!` and evaluates to `#REF!` (§9.6).
- The operation is one undo step; undo restores the exact prior inputs (verbatim) and names, redo reapplies (§9.3).

Rulings R61–R64 apply. Atomicity (§9.2): any exception leaves the workbook unchanged. Reuse the Phase 3 reference-rewriting machinery (`FormulaPrinter`, `IReferenceRewriter`, `ReferenceShifter`) where it fits; respect §3.3. No recursion proportional to chain length. Add a Performance-category sanity gate (internal, not a §12 budget): InsertRows(1,1) and DeleteRows(1,1) on the P-1 100k-cell fixture each complete in ≤ 2 s Release, undo included.

Extend the FsCheck undo property (§14) so random sequences include insert/delete rows/columns and DefineName/DeleteName, and undo-all returns the initial inputs and names.

**Acceptance Criteria:** Tests cover every table row and example above for rows and for columns, including `#REF!` creation, R61 clamping, mixed `$` references, ranges spanning the edit, formulas moved but unchanged keeping verbatim text, name adjustment and `#REF!` names, `InvalidOperationException` with nothing changed, every ArgumentOutOfRange case, R63 no-op, R64 CellsChanged contents, LastRecalc, one undo step with exact restore, and the extended undo property. Sanity gate passes. Build 0 warnings; non-UI tests pass; DECISIONS.md updated.

## Task 4: Lookup and reference functions (§7.4)
Vibeless task id: 627e2c897f1b0ea9839bd2e65174c267

**Spec:** Add the 9 §7.4 functions to the built-in table with arity enforced at parse time (§5.4, §7.0). Semantics (verbatim):

| Function | Semantics |
|---|---|
| `VLOOKUP(v,table,col,[approx=TRUE])` | **Approximate**: binary search over the first column, assumed ascending; returns the last row whose key ≤ `v` under §6.3 ordering. If `v` is less than the first key → `#N/A`. Results on unsorted or mixed-type columns are unspecified. **Exact**: first match; text matching is case-insensitive with wildcards `* ? ~`. Not found → `#N/A`. `col < 1` → `#VALUE!`. `col` greater than the table width → `#REF!`. |
| `HLOOKUP(v,table,row,[approx=TRUE])` | VLOOKUP transposed. |
| `INDEX(range,r,[c])` | Returns a value, never a reference. For a single-row or single-column range, one index selects the element. Out of bounds → `#REF!`. `r` or `c` equal to `0` → `#VALUE!`. A 2-D range with `c` omitted → `#VALUE!`. |
| `MATCH(v,range,[type=1])` | `range` must be a single row or column, else `#N/A`. `1`: largest value ≤ `v` (ascending). `0`: exact match, with wildcards. `-1`: smallest value ≥ `v` (descending). Returns a 1-based position. Not found → `#N/A`. |
| `CHOOSE(i,v1,…)` | Evaluates only the chosen value. `i < 1` or `i > count` → `#VALUE!`. |
| `ROW([ref])` / `COLUMN([ref])` | Row or column number of the reference's top-left cell. Omitted → the formula's own cell. |
| `ROWS(range)` / `COLUMNS(range)` | Dimensions. |

Integer parameters are truncated toward zero (§6.9); `approx` is coerced to boolean. Errors in arguments propagate in §6.4 order. Names count as reference arguments. Rulings R65–R67 apply. Reuse the existing SEARCH wildcard matcher rather than duplicating it. Dependencies stay static (§6.1): CHOOSE/IF branches not taken are still dependencies. Respect §3.3 (Functions must not depend on Parsing/Dependencies/Commands). ROW()/COLUMN() with no argument need the evaluating cell's position; supply it through the existing evaluation context without breaking §3.3.

**Acceptance Criteria:** Per-function tests for every rule and error above, approximate and exact modes, wildcards and `~` escapes, case-insensitivity, single-row/column INDEX with one index, 2-D INDEX, `#REF!` vs `#VALUE!` boundaries, MATCH -1/0/1, CHOOSE laziness (an error in an unchosen branch does not surface) and its static dependency, ROW()/COLUMN() in their own cell and after CopyPaste, ROWS/COLUMNS on cells, ranges and names, and arity `#PARSE!` cases. Build 0 warnings; non-UI tests pass; DECISIONS.md updated.

## Task 5: Conditional aggregation functions (§7.5)
Vibeless task id: 5edabf4ebb4e9bdf7334b55c808c5792

**Spec:** Add `SUMIF(range,criteria,[sum_range])`, `COUNTIF(range,criteria)` and `AVERAGEIF(range,criteria,[avg_range])` (§7.5, verbatim):

**Criteria as text.** A criteria string is an optional operator (`=`, `<>`, `<`, `>`, `<=`, `>=`) followed by an operand. The operand is: a number, if it matches the §4.3 rule 5 grammar; a boolean, if it is `TRUE` or `FALSE`; text otherwise.

| Operand | Operator | Matches |
|---|---|---|
| Number | any | Numeric cells only, compared with that operator |
| Text | `=` or none | Text cells, case-insensitive, wildcards `* ? ~` |
| Text | `<>` | Every cell that does not match, including blanks |
| Text | `<` `>` `<=` `>=` | Text cells only, case-insensitive ordering |
| Empty (`""` or `"="`) | — | Blank cells and `""` text |
| `"<>"` alone | — | Non-blank cells |

**Criteria as a value.** A criteria that is a number or boolean value, not text, means equality.

Other rules: `sum_range` and `avg_range` are anchored at their top-left cell and resized to `range`'s dimensions; only numbers are aggregated. Error cells in `range` never match. An error in `sum_range` at a matched position propagates. AVERAGEIF with no matches → `#DIV/0!`.

Ruling R68 applies, including the static dependency on the resized rectangle so incremental recalc stays correct (§6.7: editing any cell inside a referenced range triggers dependents). Reuse the shared wildcard matcher and the existing number grammar (`NumberGrammar`) and §6.3 comparison helpers. Respect §3.3.

**Acceptance Criteria:** Tests for every table row; numeric operators against numeric, text and blank cells; `5%` and `1e3` operands; boolean operands (R68); wildcard and `~` escapes; value criteria; criteria from a cell reference (including blank and error); sum/avg_range resizing including a single-cell anchor and positions beyond the grid; error-in-range never matching; matched-position error propagation; AVERAGEIF `#DIV/0!`; ranges given by names; and an incremental-recalc test proving an edit inside the resized sum_range (outside its textual extent) updates the result. Build 0 warnings; non-UI tests pass; DECISIONS.md updated.

## Task 6: Date functions (§7.6)
Vibeless task id: 00b59066d744591038bfe28bbae36368

**Spec:** Add `DATE`, `YEAR`, `MONTH`, `DAY`, `WEEKDAY`, `EDATE`, `EOMONTH`, `DAYS` (§7.6, verbatim):

**Serial system.** The 1900 system matches Excel exactly, including the fictitious 1900-02-29: serial 1 = 1900-01-01, 59 = 1900-02-28, 60 = 1900-02-29 (fictitious), 61 = 1900-03-01, 0 = "1900-01-00". Valid serials are `0`–`2958465`, i.e. up to 9999-12-31. Arguments are numbers truncated per §6.9. Text is never parsed as a date.

| Function | Semantics |
|---|---|
| `DATE(y,m,d)` | `0 ≤ y ≤ 1899` → `y+1900`. `y < 0` or `y ≥ 10000` → `#NUM!`. Month and day overflow roll over: `DATE(2024,14,1)` = 2025-02-01, `DATE(2024,1,0)` = 2023-12-31. A result outside the valid serial range → `#NUM!`. |
| `YEAR(s)` / `MONTH(s)` / `DAY(s)` | Serial outside the valid range → `#NUM!`. Serial `60` → 1900, 2, 29. Serial `0` → 1900, 1, 0. |
| `WEEKDAY(s,[type=1])` | Type 1: `((s-1) floormod 7)+1`, with Sunday = 1 and serial 1 a Sunday. Type 2: Monday = 1 … Sunday = 7. Type 3: Monday = 0 … Sunday = 6. Any other type → `#NUM!`. |
| `EDATE(s,months)` | Same day-of-month, `months` later, clamped to the end of the target month. Result out of range → `#NUM!`. Behavior for `s < 61` is unspecified. |
| `EOMONTH(s,months)` | Last day of the month `months` away. Same range rules as EDATE. |
| `DAYS(end,start)` | `INT(end) - INT(start)`. |

Ruling R69 applies. Deterministic (§6.8): no use of the current date or time zone. Implement the serial conversion without depending on culture.

**Acceptance Criteria:** Tests for every serial in the table, the 9999-12-31 bound (2958465) and 2958466 → `#NUM!`, DATE roll-over examples, `y` mapping and `#NUM!` bounds, `DATE(1900,2,29)`=60 and `DATE(1900,3,1)`=61, YEAR/MONTH/DAY on 0 and 60, WEEKDAY types 1–3 across a full week plus invalid types, EDATE/EOMONTH clamping (Jan 31 + 1 month, leap years 2024/2100/2000), negative months, out-of-range `#NUM!`, DAYS with fractions and negatives, text arguments (numeric text coerces, date text → `#VALUE!`), and fractional truncation. A function-library test asserts all 70 §7 functions are registered with their arities. Build 0 warnings; non-UI tests pass; DECISIONS.md updated.

## Task 7: CSV import and export (§8.5, §14)
Vibeless task id: 1daad43c60afdfa649d442d6fa605c16

**Spec:** Implement CSV in Gridline.IO per §8.5 (verbatim) through the R70 API.

**Import.** Encoding, detected in this order: 1. UTF-8 BOM → UTF-8; 2. UTF-16 LE BOM; 3. UTF-16 BE BOM; 4. no BOM and the bytes are valid UTF-8 → UTF-8; 5. otherwise Windows-1252 (System.Text.Encoding.CodePages). Records are separated by CRLF, LF or CR, in any mix. A final terminator does not create an empty row. Fields follow RFC 4180 using the configured delimiter: quoted fields may contain the delimiter, `""` and line breaks; a `"` inside an unquoted field is a literal character; an unterminated quoted field at EOF → `GridlineFormatException`. Mapping: field *j* of record *i* goes to the cell at row *i*, column *j*; an empty field → blank; a field starting with `=` or `'` is stored as input `"'" + field`; every other field is stored verbatim, so §4.3 interprets it (`-5` becomes a number, `5%` becomes 0.05, `true` becomes a boolean). More than 100,000 records or 702 fields in a record → `GridlineFormatException`.

**Export.** Extent: rectangle from A1 to the maximum used row and column. Cell values are exported as computed values, not inputs: blank → empty field; number → `ToString("R", InvariantCulture)`; boolean → `TRUE` or `FALSE`; text → as is; error → its code. Quoting: a field is quoted if and only if it contains the delimiter, `"`, CR or LF, or begins or ends with U+0020; quotes inside are doubled. Output: CRLF after every record, including the last; UTF-8 **with** BOM; an empty workbook produces only the BOM. Delimiter: any character except `"`, CR and LF; otherwise `ArgumentException`.

Reuse `DelimitedTextParser` and `DelimitedTextMapping` (D-39). `ExportToPath`/`ImportFromPath` use the §8.4 atomic temp-file-and-replace path and the existing I/O exception mapping. Add the FsCheck CSV round-trip property defined by R71 (§14). Respect §3.2 (IO references Core only).

**Acceptance Criteria:** Tests for each encoding branch (including invalid UTF-8 falling back to Windows-1252 bytes like 0x80 → €, and UTF-16 BE), mixed terminators, final terminator, quoted delimiters/quotes/newlines, literal mid-field quotes, unterminated quote, `=`/`'` injection guard, §4.3 interpretation examples, the 100,000-record and 702-field limits (exact boundary passes, one over fails), over-length inputs, export extent with gaps, each value kind, every quoting trigger and a non-trigger, CRLF on the last record, BOM-only empty export, custom delimiters (`;`, Tab) and invalid delimiters, formulas exported as values, and the R71 property. A Performance-category gate: importing and exporting the 100k-cell fixture each complete in ≤ 1.5 s Release (internal gate). Build 0 warnings; non-UI tests pass; DECISIONS.md updated.

## Task 8: CLI CSV commands and Phase 3 script commands (§10.1, §10.2, §10.3)
Vibeless task id: 5ca587ed8ca12762d965e9184c4ad115

**Spec:** Add the §10.2 Phase 3 commands (verbatim):

| Command | Behavior |
|---|---|
| `gridline import-csv <csv> <out.gridline> [--delimiter <c>]` | CSV → `.gridline` |
| `gridline export-csv <file> <out.csv> [--delimiter <c>]` | `.gridline` → CSV |

And the §10.3 Phase 3 script commands, replacing their `ERR\tnot implemented` behaviour:

| Command | Output |
|---|---|
| `INSERTROWS <row> <n>` / `DELETEROWS <row> <n>` | None |
| `INSERTCOLS <col> <n>` / `DELETECOLS <col> <n>` | None. `col` is given as letters, e.g. `C` |
| `NAME <name> <refersTo>` / `UNNAME <name>` | None |
| `NAMES` | `NAME\t<name>\t<refersTo>` per name, sorted |

§10.1 applies: stdout UTF-8 without BOM, LF endings; errors on stderr as a single line; exit codes 0 success, 1 usage, 2 file not found or I/O, 3 format (`.gridline` or CSV), 4 script ERR, 5 invalid address. Rulings R72 and R73 apply. Script failures print `ERR\t<message>` and continue; exit 4 if any ERR. `SAVE` in a script now persists names (Task 2). Update the CLI usage text.

**Acceptance Criteria:** End-to-end tests (running the built `gridline` entry point as existing Cli tests do) for import-csv and export-csv success, round trip through both, `--delimiter` with `;` and Tab, every exit code path in R72, stdout empty on success, stderr single line on failure; script tests for each new command including success, bad arguments, API exceptions (ArgumentOutOfRange, InvalidOperation, invalid name), NAMES sort and empty output, commands inside BEGIN…END giving ERR, and UNDO after each new command restoring state. Build 0 warnings; non-UI tests pass; DECISIONS.md updated.

## Task 9: GUI CSV menu items and header insert/delete (§11.5, §11.3, §11.7)
Vibeless task id: 211521ee309bbdc339a07ea6c9547e36

**Spec:** In Gridline.App (MVVM, existing services and seams):

- File menu (§11.5): add Import CSV and Export CSV between Save As and Exit, per R74, with file dialogs through the existing `IDialogService`.
- Header context menu (§11.5, Phase 3): right-clicking a row or column header offers Insert and Delete for the selected rows or columns, per R75. Insert calls InsertRows/InsertColumns with the selected count at the first selected index; Delete calls DeleteRows/DeleteColumns. Each is one undo step (Ctrl+Z undoes it). Header click selection per R75.
- The grid stays virtualized and responsive (§11.3); the title, dirty marker, SelectionValue and SelectionSum update after these operations; an open edit is committed first (as D-39 does for New/Open/Exit).
- Fix the parked Phase 3 startup gap: no user command can run between the main window becoming visible and recovery startup completing (BeginStartup/CompleteStartup, D-40).

AutomationIds `MenuImportCsv`, `MenuExportCsv`, `ContextInsert`, `ContextDelete` are added; all §11.7 AutomationIds stay exactly as they are.

**Acceptance Criteria:** ViewModel unit tests for import/export (success, cancel, failure message, dirty-prompt paths, title/dirty state) and for insert/delete (counts, index, undo, failure message, header selection logic and SelectionSum on whole-row/column selections). FlaUI UI tests (Category=UI): import a CSV via the menu and read cell values; export and verify file bytes; right-click a row header → Insert shifts data down and Ctrl+Z restores; right-click a column header → Delete removes the column. All UI tests (old and new) pass locally; P-6 still passes. Build 0 warnings; non-UI tests pass; DECISIONS.md updated.

## Task 10: Phase 4 exit: regression, gates and records (§13, §12, §14)
Vibeless task id: 59de939f2ae8b616041b289be39021b3

**Spec:** Close the phase per §13: verify that `dotnet build Gridline.sln -c Release` and `dotnet test Gridline.sln -c Release --filter Category!=UI` pass, that all Performance-category gates (P-1…P-7 and the Phase 4 internal gates) pass in Release, and that the full UI suite passes locally. Confirm the §14 property tests exist (undo with structure/name operations, file round-trip with names, CSV round-trip). Fix the parked doubled "(c) R53:" label in the DECISIONS.md D-32 §5.1 row. Add a "## Phase 4" index to DECISIONS.md listing D-41 onward and rulings R57–R75 with their sections, and record the Phase 4 exit counts (tests per project, gate timings).

**Acceptance Criteria:** The commands above pass with recorded counts and gate timings in DECISIONS.md; the D-32 label is fixed; the Phase 4 index exists. No production code changes unless a regression is found (any such fix is listed with its §).
