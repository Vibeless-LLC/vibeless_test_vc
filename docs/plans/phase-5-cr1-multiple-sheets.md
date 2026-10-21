# Phase 5 — Change Request CR-1: Multiple Sheets

Source: Vibeless project test_vc, phase "Change Request CR-1: Multiple Sheets (Phase 5)" (id fe8e38af8b5b4ad5379234ddc75f8bdb).

Spec: `.claude/architecture.md` (Gridline ARCHITECTURE.md, Spec version 1.0), amended by CR-1. The CR-1 text is the Vibeless phase description, copied to `docs/plans/cr-1.md`. CR1.x section numbers are requirement IDs. Everything CR-1 does not change stays in force. Every Phase 1–3 behaviour MUST still hold for single-sheet workbooks (CR1.10).

Task text is copied from the Vibeless tickets.

## Global Constraints

- The Global Constraints of docs/plans/phase-4-names-structure-csv.md still apply. Rulings R1–R75 in DECISIONS.md remain binding unless a ruling below supersedes one.
- Stack (§2): .NET 10, C# latest, Nullable enabled. No packages in src/. Test packages only from the §2 list.
- Layout (§3.1): four src projects and five test projects. Dependencies (§3.2, §3.3) as enforced by Gridline.Architecture.Tests: Core references nothing; IO → Core; Cli → Core, IO; App → Core, IO. Inside Core:
  - Addressing depends on no other Core namespace.
  - Parsing does not depend on Evaluation, Functions, Dependencies or Commands.
  - Evaluation does not depend on Commands or Dependencies.
  - Functions does not depend on Parsing, Dependencies or Commands.
  - Dependencies does not depend on Functions or Commands.
  - No public mutable static state.
- Exact surfaces: the CR1.3 member signatures, the CR1.5 file layout, the CR1.7 command table and output, and the CR1.8 AutomationIds (`SheetTabs`, `SheetTab_<index>`) are implemented exactly as written.
- Mutations (§9.2, §9.3):
  - atomic on exception;
  - synchronous recalc;
  - CellsChanged once after recalc, and not when nothing changed;
  - exactly one undo step per mutating call (all CR1.3 members except `ActiveSheet`, plus `CsvFormat.ImportInto`);
  - undo restores exact prior inputs (formula text verbatim), names and the sheet list.
- No call-stack recursion proportional to chain length or operator-chain length (§6.7, R10).
- Performance budgets (§12) P-1…P-7 hold in Release. P-8 (CR1.9, verbatim): 10 sheets × 10,000 formulas, each referencing other sheets. RenameSheet on the most-referenced sheet takes ≤ 500 ms.
- Single-sheet compatibility (CR1.10): every Phase 1–4 test keeps passing without edits. The exceptions are tests whose expectation CR-1 explicitly changes (R77 qualified names; CR1.5 save writes version 2). Each such edit is listed in the task's DECISIONS entry with its CR section.
- Every task has these gates:
  - `dotnet build Gridline.sln -c Release` with 0 warnings;
  - `dotnet test Gridline.sln -c Release --filter Category!=UI` passes;
  - UI tests `[Trait("Category","UI")]` must pass locally when the task adds or touches them;
  - DECISIONS.md is updated with § and CR references (next entry D-51);
  - commit messages cite CR/§ numbers.
- No task may assume access to the hidden acceptance suite (§0).

### Controller rulings for Phase 5 (R76–R88)

- R76 (CR1.6, R70) — operator decision. CR1.6's `Csv` is the existing `Gridline.IO.CsvFormat`.
  - Add `public static void ImportInto(Workbook wb, string sheetName, Stream source, CsvOptions? options = null)`.
  - Add `public sealed class CsvOptions { public char Delimiter { get; init; } = ','; }`.
  - `Export` and `ExportToPath` gain a trailing `string? sheet = null` parameter (null = active sheet; unknown → ArgumentException).
  - `Import` and `ImportFromPath` are unchanged and create Sheet1.
  - ImportInto validates everything before mutating: the sheet name, the delimiter (R72 rules → ArgumentException), and the whole stream parsed per §8.5 (format error → GridlineFormatException).
  - ImportInto appends the sheet and does not change ActiveSheet. One recalc runs; formulas that referenced the missing sheet resolve. It is one undo step, the workbook becomes dirty, and CellsChanged is raised per R81.
- R77 (CR1.4 vs CR1.10) — operator decision. Names are always sheet-qualified, even with one sheet: `DefineName("Rate","$B$1")` on Sheet1 → `GetNames()` returns `Sheet1!$B$1`.
  - An unqualified refersTo is qualified with the active sheet at definition time.
  - A qualified refersTo naming an unknown sheet → ArgumentException.
  - Stored canonical form: sheet in defined casing, quoted only when required, cell part per R58.
  - R59 and R60 still hold, compared on the canonical form.
- R78 (CR1.1, CR1.3) — sheet operation errors.
  - A null argument → ArgumentNullException.
  - An invalid sheet name, or a name equal case-insensitively to another existing sheet → ArgumentException.
  - An unknown sheet in any parameter → ArgumentException. Lookup is case-insensitive.
  - AddSheet when 255 sheets exist → InvalidOperationException.
  - AddSheet index outside `0 ≤ index ≤ SheetNames.Count` → ArgumentOutOfRangeException. MoveSheet newIndex outside `0 ≤ newIndex < SheetNames.Count` → ArgumentOutOfRangeException.
  - DeleteSheet on the only sheet → InvalidOperationException.
  - All errors leave the workbook unchanged.
- R79 (CR1.3, §9.3) — no-ops. RenameSheet to an ordinal-identical name, and MoveSheet to the current index, are no-ops: no undo step, no CellsChanged. A casing-only rename is a change: it rewrites the referencing formulas so they print the new casing.
- R80 (CR1.1, CR1.3) — active sheet.
  - The `ActiveSheet` setter looks up case-insensitively and stores the defined casing. It makes no undo step, raises no CellsChanged, and leaves IsDirty unchanged.
  - AddSheet and ImportInto do not change the active sheet. RenameSheet of the active sheet keeps it active under the new name.
  - When the active sheet stops existing (DeleteSheet, undo of AddSheet/ImportInto, redo of DeleteSheet), the active sheet becomes the sheet now at the removed index, or the last sheet if that index is past the end.
  - Undo and redo never otherwise change ActiveSheet.
- R81 (CR1.3, §9.2) — CellsChanged addresses follow the GetUsedAddresses rule, evaluated after the call. They are unqualified when the workbook then has exactly one sheet; otherwise they are qualified, in sheet order then row-major.
  - Cells of a sheet that no longer exists after the call are not reported.
  - Cells of a sheet restored or created by the call are reported if non-empty.
  - A sheet operation that changes no cell input or value raises no CellsChanged, but it is still an undo step.
  - No new public event is added. Hosts refresh sheet lists after their own commands, including undo and redo.
- R82 (CR1.2) — qualified reference syntax.
  - An unquoted sheet prefix is accepted only if it is a valid sheet name that does not require quoting. Otherwise, for example `My Sheet!A1`, `2024!A1`, `A1!B2` or `TRUE!A1`, the formula is #PARSE!.
  - A quoted prefix `'…'` uses `''` for `'` and must contain a valid CR1.1 sheet name, else #PARSE!. Quoting a name that needs no quotes is accepted and printed unquoted.
  - No whitespace between the prefix, `!` and the reference.
  - After `!` comes a cell reference, optionally `:` and a second, unqualified cell reference. A name, a function call, or a second prefix → #PARSE!. A prefix on the second corner only (`A1:Data!B2`) → #PARSE!.
  - Sheet names match case-insensitively. Printing uses the sheet's defined casing when the sheet exists, else the casing as written.
  - A qualified reference stays qualified when printed, even when it names the formula's own sheet. The `$` flags follow the `!` (`Data!$A$1`).
  - A reference to a missing sheet evaluates to #REF!. Its formula depends on that sheet name, so creating the sheet (AddSheet, RenameSheet, ImportInto) recalculates it.
  - #PARSE! inputs are kept verbatim by every rewrite (R39).
- R83 (CR1.3) — API address strings. Qualified addresses are accepted in SetCell, GetInput, GetValue, ApplyBatch, CopyPaste (source and destination), DefineName refersTo, and in the qualified forms of CLI and script addresses.
  - The same quoting rules as R82 apply. Address parsing is case-insensitive.
  - An unqualified address means the active sheet. An unknown sheet → ArgumentException.
  - ApplyBatch duplicate detection compares the sheet and canonical address, so `A1` and `Sheet1!A1` on active Sheet1 are duplicates.
  - A CopyPaste source range has one sheet.
- R84 (CR1.3, §9.5) — sheet structure overloads: `InsertRows(string sheet, int row, int count)`, `DeleteRows(string sheet, int row, int count)`, `InsertColumns(string sheet, int column, int count)`, `DeleteColumns(string sheet, int column, int count)`. The unqualified overloads act on the active sheet.
  - An edit adjusts qualified references to that sheet from every sheet, unqualified references on that sheet, and names pointing into that sheet.
  - R61–R64 apply per edit across all sheets.
- R85 (CR1.3) — DeleteSheet.
  - Every reference, single or range, whose prefix names the deleted sheet prints `#REF!`, and the formula is re-emitted.
  - Names into it get refersTo `#REF!`.
  - References to other missing sheets are untouched.
  - Undo restores the sheet at its index, its cells, names, and the verbatim text of every rewritten formula.
  - RenameSheet rewrites prefixes matching the old name, case-insensitively, to the new name with canonical quoting.
  - A rewrite over MaxInputLength → InvalidOperationException with no change (the D-43 execution ruling).
- R86 (CR1.5, §8.2) — file v2 loading.
  - `format`, `version`, `activeSheet` and `sheets` are required for version 2. A missing `names` stays lenient (R54).
  - Unknown keys are treated as the v1 strict loader treats them.
  - `activeSheet` must match a sheet name case-insensitively.
  - Each v2 name value must be a qualified, absolute refersTo naming an existing sheet, or exactly `#REF!`.
  - Cells inside a sheet use unqualified addresses. A qualified cell address → GridlineFormatException.
  - Duplicate cell addresses within a sheet are rejected as in v1.
  - A version 1 file containing `activeSheet` or `sheets` → GridlineFormatException.
  - Version is an integer (integral 2.0 accepted, as R54 does for 1.0). A version > 2 → message contains `unsupported version`.
- R87 (CR1.7) — CLI and script.
  - `import-csv … --sheet <name>` names the only sheet of the produced file. `export-csv … --sheet <name>` selects the sheet to export. An invalid or unknown sheet, or a missing value, → exit 1.
  - Address echo: the sheet in its defined casing, quoted only when required; an unqualified address given stays unqualified.
  - Index arguments are `^[0-9]+$`.
  - The script splitter honours single-quoted tokens (with `''`) for sheet names and qualified addresses, including `SET 'My Sheet'!A1 <input>` (the input stays the rest of the line).
  - API exceptions → `ERR\t<message>`.
  - All new commands and ACTIVATE inside BEGIN…END → ERR (R40). SET and SETX with qualified addresses still batch.
  - NAMES prints qualified values (R77).
- R88 (CR1.8) — GUI.
  - Context menu AutomationIds (not normative): `ContextAddSheet`, `ContextRenameSheet`, `ContextDeleteSheet`, `ContextMoveSheetLeft`, `ContextMoveSheetRight`.
  - Add appends the first free `Sheet<n>` (smallest n ≥ 1, case-insensitive) and activates it.
  - Rename edits in place or in a small dialog (`RenameSheetBox`). An invalid or duplicate name → message box, no change.
  - Delete is disabled on the last sheet. Move Left is disabled on the first tab, Move Right on the last.
  - Dragging calls MoveSheet as one undo step.
  - Activating a sheet selects A1. Tabs refresh from SheetNames and ActiveSheet after every command, undo and redo.
  - The name box shows unqualified addresses. Typing a qualified address activates that sheet and selects the cell. An unknown sheet → message box, no change.
  - Activation does not change dirty state or title.

## Task 1: Sheet names and qualified reference syntax: tokenize, parse, print (CR1.1, CR1.2, §5.1-§5.5)
Vibeless task id: 64413f18c30b01ad1893aef2594c587a

**Spec:** Pure Addressing/Parsing work. No Workbook changes yet, and every existing formula must parse and print unchanged.

Sheet names (CR1.1, verbatim):
- 1–31 characters.
- Must not contain `: \ / ? * [ ]`.
- Must not begin or end with `'`.
- Unique case-insensitively.
- An invalid name → ArgumentException.

Add a sheet-name validator in Gridline.Core.Addressing (or Gridline.Core). Uniqueness is the Workbook's job (Task 2). Also add a "requires quoting" predicate. Quote the sheet name, as in `'My Sheet'!A1`, when it is any of the following:
- contains a character outside `[A-Za-z0-9_.]`;
- starts with a digit;
- looks like a cell reference (`^[A-Za-z]{1,3}[0-9]+$`);
- is TRUE or FALSE.

Inside quotes, `''` stands for `'`. Add the canonical formatter: defined casing, quoted only when required.

Qualified references (CR1.2): `Sheet!A1` or `Sheet!A1:B3`. A range has a single sheet prefix; `Sheet2!A1:Sheet2!B3` is #PARSE!.
- Extend the tokenizer, parser and AST: CellRefNode and RangeNode gain an optional sheet name, kept as written.
- Ruling R82 fixes the details.

Canonical printing (§5.5 amended by CR1.2): the sheet name appears in its defined casing, quoted only when required.
- The printer receives a sheet-casing resolver, as names do through INameCasingResolver. An unknown sheet prints in its casing as written, quoted only when required.
- A redundantly quoted name prints unquoted.
- The reference rewriter (IReferenceRewriter) must see each reference's sheet, so later tasks can rewrite or #REF! qualified references.

Dependency extraction (PrecedentExtractor) must expose each precedent's sheet name, so Task 2 can bind precedents to sheets. Keep the current behaviour for unqualified references.

§3.3 holds. No recursion proportional to operator-chain length (R10).

**Acceptance Criteria:** Tests cover the following.
- Every CR1.1 name rule: 31 vs 32 characters, each forbidden character, leading and trailing `'`, and an empty name.
- Every quoting trigger: `My Sheet`, `2024`, `A1`, `abc123`, `TRUE`, `true`, `O'Brien` → `'O''Brien'`. Names that need no quotes print bare: `Data`, `Sheet_1`, `a.b`.
- Parse and print round trips:
  - `=Data!B2*2`
  - `='My Sheet'!A1:B3`
  - `='O''Brien'!$A$1`
  - `=data!a1` prints with the resolver's defined casing (`Data!A1`)
  - `='Data'!A1` prints `=Data!A1`
- #PARSE! for each of: `Sheet2!A1:Sheet2!B3`, `My Sheet!A1`, `2024!A1`, `A1!B2`, `TRUE!A1`, `Data!Rate`, `Data!SUM(A1)`, `Data !A1`, `Data! A1`, `A1:Data!B2`, `'a[b'!A1`, `''!A1`, and an unterminated quote.
- Every existing parser, printer and property test passes unchanged.
- Architecture tests pass. Build has 0 warnings; the non-UI Release suite passes; DECISIONS.md D-51 (CR1.1, CR1.2, §5.5, R82).

## Task 2: Multi-sheet workbook model, evaluation and AddSheet/ActiveSheet (CR1.1-CR1.3, §6.7, §9.1-§9.3)
Vibeless task id: 51fb86afb4ff52d2f18cc68fc1d75bac

**Spec:** Make Gridline.Core.Workbook hold an ordered list of 1 to 255 sheets (CR1.1). A new workbook has one sheet, Sheet1. Exactly one sheet is active.

Add these members (CR1.3, verbatim signatures):
```csharp
public IReadOnlyList<string> SheetNames { get; }          // in order
public string ActiveSheet { get; set; }                    // ArgumentException if unknown; not undoable
public void AddSheet(string name, int? index = null);     // append if null
```
RenameSheet, DeleteSheet and MoveSheet come in Task 4. Errors and bounds follow R78. ActiveSheet follows R80. CellsChanged follows R81. Address strings follow R83.

Address strings: every address parameter (SetCell, GetInput, GetValue, ApplyBatch, CopyPaste source and destination) accepts qualified or unqualified addresses.
- An unqualified address refers to the active sheet.
- An unknown sheet → ArgumentException, with no change.
- In this task CopyPaste only needs to work when the source and destination are on the same sheet. A cross-sheet call may throw NotSupportedException until Task 5.

Evaluation:
- Unqualified references in a formula refer to the formula's own sheet.
- A reference to a sheet that does not exist parses and evaluates to #REF!.
- Formulas depend on sheet existence. When AddSheet (later also RenameSheet and ImportInto) creates a referenced sheet name, the dependents recalculate.
- Cycles across sheets are detected per §6.7 (#CIRC!), with no recursion.
- Incremental recalc stays incremental: an edit re-evaluates only its transitive dependents, on any sheet.

GetUsedAddresses():
- With exactly one sheet, it returns unqualified addresses, unchanged from v1.
- With more than one sheet, it returns qualified addresses for all sheets, in sheet order then row-major.

Design guidance:
- Key cells by a stable internal sheet id, not by index, so a move or rename does not rekey cells.
- Use one dependency graph across sheets.
- Keep SetCell O(1) when nothing depends on the cell (P-3). Keep P-1…P-5 and P-7 green.
- The AddSheet undo step records the sheet. Undo removes it (R80 if it was active); redo re-adds it at the same index.
- `FromCells` keeps its signature and builds a single-sheet Sheet1 workbook. The internal loader entry may take a sheet list (Task 6 uses it).

Single-sheet workbooks behave exactly as in Phases 1–4 (CR1.10). Every existing Core test passes without edits, except tests whose expectations CR-1 explicitly changes; list them in DECISIONS. §3.3 holds.

**Acceptance Criteria:** Tests cover the following.
- New workbook: SheetNames == ["Sheet1"] and ActiveSheet == "Sheet1".
- AddSheet append and insert at an index. Every R78 error, each leaving the workbook unchanged.
- The ActiveSheet setter per R80.
- SetCell and GetValue with qualified addresses (including `'My Sheet'!A1`), and unqualified addresses after switching the active sheet.
- `=Data!B2*2` evaluates. A missing sheet gives #REF!, which resolves after AddSheet("Data") with a CellsChanged.
- A cross-sheet cycle gives #CIRC! on both cells. Breaking it restores the values.
- Incremental LastRecalc counts across sheets.
- GetUsedAddresses with one sheet and with three sheets in non-alphabetical order.
- CellsChanged address format per R81.
- AddSheet undo and redo, including undo of an active added sheet.
- A 100,000-cell chain spanning two sheets evaluates without a stack overflow.
- P-1…P-5 and P-7 pass.
- Build 0 warnings; the non-UI suite passes; DECISIONS.md D-52.

## Task 3: Workbook-scoped, sheet-qualified names (CR1.4, §9.6)
Vibeless task id: 8276e43a4705ab2c266a5f2be2931a35

**Spec:** Names are workbook-scoped (CR1.4). refersTo MUST be sheet-qualified and absolute, e.g. `Data!$A$1:$A$10`. An unqualified refersTo is qualified with the active sheet when it is defined. `GetNames()` returns qualified forms.

Ruling R77 applies: names are always qualified, even with one sheet. Phase 4 tests asserting an unqualified refersTo are updated to the qualified form; list them in DECISIONS.

Storage and canonical form:
- The sheet appears in its defined casing, quoted only when required.
- The cell part follows R58.
- A qualified refersTo naming an unknown sheet → ArgumentException.
- R59 holds. R60 holds on the canonical form.

Evaluation:
- A name referring into another sheet behaves like that qualified reference, from any sheet.
- Name dependents recalculate when the name or its target cells change. Cross-sheet cycles through names give #CIRC!.

Existing structure edits (§9.5) must still adjust names that point into the edited sheet. That is the active sheet until Task 5. §3.3 holds: name resolution must not make Evaluation depend on Dependencies or Commands.

**Acceptance Criteria:** Tests cover the following.
- An unqualified refersTo while different sheets are active.
- Qualified refersTo: `Data!$A$1:$A$10`, `'My Sheet'!$B$2`, `data!$b$2` → `Data!$B$2`, and an unknown sheet → ArgumentException.
- GetNames sort order and qualified values.
- `=SUM(Total)` across sheets recalculates when a Data cell is edited.
- A cross-sheet name cycle gives #CIRC!.
- R60 no-op. Undo and redo restore exact names. Atomicity.
- The updated Phase 4 tests pass.
- Build 0 warnings; the non-UI suite passes; DECISIONS.md D-53 (CR1.4, CR1.10, R77).

## Task 4: RenameSheet, DeleteSheet, MoveSheet with formula/name rewriting and undo; P-8 gate (CR1.3, CR1.9)
Vibeless task id: 4440d9efe7679dc9a46284f19d0d123a

**Spec:** Add (CR1.3, verbatim):
```csharp
public void RenameSheet(string oldName, string newName);
public void DeleteSheet(string name);                      // InvalidOperationException if last sheet
public void MoveSheet(string name, int newIndex);
```
Each is one undo step. Errors follow R78, no-ops R79, ActiveSheet R80, CellsChanged R81, rewriting R85.

RenameSheet:
- Rewrites every formula and name that refers to the old name. Rewritten formulas are re-emitted per §5.5.
- Formulas with no changed reference keep their text verbatim. #PARSE! inputs are kept verbatim.
- A formula that referred to a missing sheet whose name equals the new name now resolves on recalc, with its text unchanged.

DeleteSheet:
- References to the deleted sheet become #REF! and are re-emitted.
- Names pointing into it get refersTo = #REF!.
- Undo restores the sheet's cells and the verbatim original text of every rewritten formula.

MoveSheet changes the order only. GetUsedAddresses follows the new order.

P-8 (CR1.9, verbatim): 10 sheets × 10,000 formulas, each referencing other sheets. RenameSheet on the most-referenced sheet takes ≤ 500 ms.
- Add it to the Performance gates in Release, using the existing median-of-5-after-warm-up harness.
- The rewrite must touch only formulas that reference the sheet, found through the dependency graph or an index, not a scan of every cell's text.

**Acceptance Criteria:** Tests cover the following.
- RenameSheet rewriting across sheets, including into and out of names that need quoting. Casing-only rename.
- The R79 no-op. Duplicate and unknown names.
- Names rewritten. Unaffected formulas kept verbatim.
- A missing-sheet reference resolves after a rename.
- DeleteSheet produces #REF! for single references and ranges, and names become #REF!.
- Deleting the last sheet → InvalidOperationException. Deleting the active sheet moves activation per R80.
- Undo restores byte-identical formula text, cells, names and the index. Redo reapplies.
- MoveSheet ordering, bounds and the no-op case.
- Atomicity, including the MaxInputLength overflow on rename.
- An FsCheck undo property with random sheet operations: undo of any sequence restores the exact prior inputs, names and sheet list.
- P-8 passes in Release, and P-1…P-5 and P-7 still pass.
- Build 0 warnings; the non-UI suite passes; DECISIONS.md D-54.

## Task 5: Per-sheet insert/delete rows and columns and cross-sheet CopyPaste (CR1.3, §9.4, §9.5)
Vibeless task id: 88c0746b99ea1464f911513c30496141

**Spec:** Structure edits (CR1.3, R84). Add the sheet overloads `InsertRows(string sheet, int row, int count)`, `DeleteRows(string sheet, int row, int count)`, `InsertColumns(string sheet, int column, int count)` and `DeleteColumns(string sheet, int column, int count)`. The existing unqualified overloads act on the active sheet.

Insert and delete on a sheet adjust qualified references to that sheet from every sheet. They also adjust unqualified references on that same sheet. Unqualified references on other sheets are unaffected.
- Names pointing into that sheet are adjusted.
- R61–R64 apply, with a formula now meaning a formula on any sheet.
- Rewritten formulas are re-emitted per §5.5. #PARSE! inputs are kept verbatim.
- One undo step, which restores the verbatim formulas on every sheet and the names.
- An unknown sheet → ArgumentException.

CopyPaste across sheets (CR1.3): the source range and the destination top-left may each be qualified, and may name different sheets.
- Unqualified references stay unqualified and therefore point at the destination sheet. Qualified references keep their sheet.
- Relative and mixed shifting follows §9.4 for both kinds.
- The work is proportional to used cells (D-35). It is one undo step and atomic.
- Remove any interim NotSupportedException from Task 2.

**Acceptance Criteria:** Tests cover the following.
- `InsertRows("Data", 2, 1)` adjusts `=Data!B2` on Sheet1 to `=Data!B3`, adjusts `=B2` on Data, and leaves `=B2` on Sheet1 unchanged.
- All four operations with qualified ranges that span the edit, and a delete giving #REF!.
- Names into the edited sheet are adjusted; others are untouched.
- The unqualified overloads act on the active sheet. An unknown sheet → ArgumentException.
- Undo and redo restore verbatim text.
- CopyPaste `Sheet1!A1:B2` → `Data!C3` with unqualified and qualified references shifting correctly.
- Same-sheet CopyPaste with qualified addresses behaves as before.
- Atomicity. Existing structure and paste tests pass.
- Build 0 warnings; the non-UI suite passes; DECISIONS.md D-55.

## Task 6: .gridline file format version 2 with v1 migration (CR1.5, §8.1-§8.4, §14)
Vibeless task id: 9b5eb9b6f07131502c274df6b3457aca

**Spec:** File format version 2 (CR1.5, verbatim layout):
```json
{
  "format": "gridline",
  "version": 2,
  "activeSheet": "Sheet1",
  "names": {
    "Rate": "Data!$B$1"
  },
  "sheets": [
    {
      "name": "Sheet1",
      "cells": [
        ["A1", "=Data!B2*Rate"]
      ]
    },
    {
      "name": "Data",
      "cells": []
    }
  ]
}
```
Byte-exact canonical rules follow §8.1:
- two-space indent per nesting level;
- top-level key order format, version, activeSheet, names, sheets;
- sheet-object key order name, cells;
- sheets in workbook order.

Everything else from §8.1 is kept: UTF-8 without BOM, LF, a trailing newline, the escaping rules, names sorted OrdinalIgnoreCase, cells row-major with unqualified addresses inside each sheet, and the empty forms `"names": {}` and `"cells": []`. Saving always writes version 2.

Loading:
- Version 1 migrates to a single sheet Sheet1. Each name `$X$n` becomes `Sheet1!$X$n`. Formula texts are unchanged.
- Version > 2 → GridlineFormatException containing `unsupported version`.
- A duplicate sheet name, an invalid sheet name, zero sheets, or an unknown activeSheet → GridlineFormatException.
- R86 fixes the rest.
- A failed load loads nothing. A successful load does a full recalc, is not dirty, and has an empty history.
- Atomic SaveToPath is unchanged.

Extend the §14 FsCheck file round-trip property to multi-sheet workbooks: sheet names that need quoting, cross-sheet formulas, qualified names, and a non-first active sheet. Assert save → load → save is byte-identical and the state is equal. Update the v1 byte-exact writer tests (save now writes v2). The v1 example files must still load and evaluate identically.

**Acceptance Criteria:** Tests cover the following.
- The CR1.5 example loads, evaluates `=Data!B2*Rate`, and re-saves byte-for-byte.
- A new workbook saves as v2 with activeSheet Sheet1.
- The §8.1 v1 example migrates its names and keeps its formulas.
- Each CR1.5 and R86 rejection, including version 3 (`unsupported version`).
- No partial state after a failed load.
- The round-trip property with sheets.
- P-1, P-5 and P-7 pass. Recovery and autosave still work.
- Build 0 warnings; the non-UI suite passes; DECISIONS.md D-56.

## Task 7: CSV ImportInto and per-sheet Export (CR1.6, §8.5)
Vibeless task id: 196ba2bc4ef1fd5f7148d71157a134e6

**Spec:** CR1.6 on the existing `Gridline.IO.CsvFormat` (R76).
- `Csv.Import` is unchanged and creates Sheet1, so `CsvFormat.Import` and `ImportFromPath` are unchanged.
- New: `public static void ImportInto(Workbook wb, string sheetName, Stream source, CsvOptions? options = null)`. It creates a new sheet, appended. An existing name → ArgumentException. It is one undo step.
- New: `public sealed class CsvOptions` with `public char Delimiter { get; init; } = ',';`.
- `Export` and `ExportToPath` gain an optional `string? sheet = null` as the last parameter. null means the active sheet; an unknown sheet → ArgumentException.

R76 fixes validation, atomicity, recalc, dirty state and ActiveSheet. Export of a non-active sheet writes that sheet exactly as §8.5 exports today. §3.2: IO references only Core; any Workbook entry the IO layer needs is internal, through the existing InternalsVisibleTo (P1).

**Acceptance Criteria:** Tests cover the following.
- ImportInto appends a sheet with the §8.5 type rules and encodings.
- `=Data!A1` written before the import resolves after it.
- An existing name, an invalid name, a bad delimiter, or a CSV format error leaves the workbook unchanged.
- One undo step removes the sheet. The workbook is dirty after the import.
- A semicolon delimiter through CsvOptions.
- Export with null versus a named sheet; an unknown sheet → ArgumentException.
- Import still creates Sheet1. Existing CSV tests and the R71 property pass.
- Build 0 warnings; the non-UI suite passes; DECISIONS.md D-57.

## Task 8: CLI --sheet options, qualified addresses and sheet script commands (CR1.7, §10.1-§10.3)
Vibeless task id: 1d18ce069ad9159dc2238b4fbf192d96

**Spec:** CR1.7 (verbatim):
- import-csv and export-csv accept `--sheet <name>`.
- eval, GET, INPUT, SET, SETX and PASTE accept qualified addresses. Output echoes each address in canonical form, qualified exactly as given.
- DUMP follows the GetUsedAddresses rule from CR1.3.
- New script commands. Sheet-name arguments use the CR1.2 quoting rules, so quoted names may contain spaces.

| Command | Output |
|---|---|
| `ADDSHEET <name> [index]` | None |
| `RENAMESHEET <old> <new>` | None |
| `DELETESHEET <name>` | None |
| `MOVESHEET <name> <index>` | None |
| `ACTIVATE <name>` | None |
| `SHEETS` | `SHEET\t<index>\t<name>` per sheet, 0-based, then `ACTIVE\t<name>` |
| `INSERTROWS [<sheet>!]<row> <n>` (and DELETEROWS, INSERTCOLS, DELETECOLS) | None. Example: `INSERTCOLS 'My Data'!C 2` |

R87 fixes the details. Exit codes and UTF-8/LF output rules (§10.1) are unchanged.

**Acceptance Criteria:** Cli tests, driving both the in-process interpreter and the gridline process, cover the following.
- eval with qualified, lowercase and quoted addresses echoes canonical forms.
- eval --all and DUMP on single-sheet versus multi-sheet files.
- `import-csv --sheet Data` then `eval Data!A1`. `export-csv --sheet Data`.
- Each new script command, including quoted names with spaces and `''`.
- SHEETS output, byte-exact.
- `INSERTCOLS 'My Data'!C 2`.
- GET, INPUT, SET, SETX and PASTE across sheets.
- ERR cases: bad index, unknown sheet, last-sheet delete, the new commands inside BEGIN.
- Existing CLI tests pass.
- Build 0 warnings; the non-UI suite passes; DECISIONS.md D-58.

## Task 9: GUI sheet tab strip and qualified name box (CR1.8, §11.3, §11.4, §11.7)
Vibeless task id: a0b0a1447a9307468951d88416770616

**Spec:** CR1.8 (verbatim):
- A sheet tab strip below the grid (AutomationId `SheetTabs`). Each tab's AutomationId is `SheetTab_<index>`.
- Clicking a tab activates it.
- The context menu offers Add, Rename, Delete and Move Left/Right.
- Dragging reorders tabs.
- The name box accepts qualified addresses and switches sheets.
- SelectionValue behavior is unchanged.

R88 fixes the details. The grid, formula bar, SelectionValue and SelectionSum show the active sheet. The grid stays virtualized and P-6 holds. Use MVVM with the existing services and seams; the App does not reference Cli (§3.2).

**Acceptance Criteria:** ViewModel tests cover:
- tab activation;
- add, rename, delete and move, including disabled states and the invalid-rename message;
- the name box with a qualified address;
- undo of a sheet delete refreshing the tabs;
- dirty state and title.

FlaUI UI tests `[Trait("Category","UI")]` cover:
- SheetTabs and SheetTab_0, SheetTab_1 after Add;
- a click activating a tab;
- context menu add, rename and delete;
- the name box `Data!B2` switching sheets.

The existing UI suite passes locally, and P-6 passes. Build 0 warnings; the non-UI suite passes; DECISIONS.md D-59.

## Task 10: CR-1 exit: single-sheet regression, gates and records (CR1.9, CR1.10, §13)
Vibeless task id: 06c584681746d29880f550d6a8ae2a54

**Spec:** Close CR-1 per CR1.10 and §13:
- `dotnet build Gridline.sln -c Release` has 0 warnings.
- `dotnet test Gridline.sln -c Release --filter Category!=UI` passes.
- The UI suite passes locally.
- All Performance gates pass in Release (P-1…P-8).
- Add a single-sheet regression check: the §9.1 public API shape test extended with the CR1.3 members, plus a scan confirming that no test was weakened. Every test whose expectation CR-1 changed is listed with its CR section.
- DECISIONS.md gets a "Phase 5 (CR-1)" index of D-51 onward, rulings R76–R88 by section, execution rulings and gate timings.
- Note that the Vibeless non-goal "Multi-sheet workbooks" is superseded by CR-1 (an operator action).
- Commit the work.

No production code changes unless a regression is found; any such fix is listed with its section.

**Acceptance Criteria:**
- The commands pass, with their output quoted.
- The P-8 median is recorded.
- The DECISIONS Phase 5 index and rulings are complete.
- The regression list of changed expectations is complete.
- The work is committed.
