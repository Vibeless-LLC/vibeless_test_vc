# Phase 3 — Editing History and Desktop GUI

Source: Vibeless project test_vc, phase "Editing History and Desktop GUI" (id 4e45ebecbe982b94778c0e15a421b039).

Spec: `.claude/architecture.md` (Gridline ARCHITECTURE.md, Spec version 1.0). It is the binding authority; this plan argues from it. Vibeless Phase 3 equals spec §13 "Phase 2 — Undo, paste, GUI" (§5.5, §9.3, §9.4, §10 Phase 2 commands, §11 except items marked Phase 3, §12 P-6). Vibeless Phases 1–2 together equal spec Phase 1. Spec "Phase 3" items (names, structure edits, lookup/date functions, CSV, header context menus) belong to Vibeless Phase 4 and are out of scope.

Task text is copied verbatim from the Vibeless tickets.

## Global Constraints

- The Global Constraints of docs/plans/phase-1-foundation.md still apply, with spec 1.0 taking precedence wherever they differ. Rulings R1–R37 in DECISIONS.md remain binding unless Task 1 records that spec 1.0 supersedes one.
- Stack (§2): .NET 10, C# latest, Nullable enabled in every project. Core, IO, Cli target net10.0; App targets net10.0-windows with UseWPF. No packages in src/. Tests may use only xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, FsCheck.Xunit, NetArchTest.Rules, FlaUI.UIA3, coverlet.collector.
- Layout (§3.1): four src projects (Core, IO, Cli, App) and five test projects (Core, IO, Cli, App, Architecture). No other projects.
- Dependencies (§3.2, §3.3): Core references nothing; IO → Core; Cli → Core, IO; App → Core, IO, never Cli. Inside Core: Addressing depends on no other Core namespace; Parsing not on Evaluation/Functions/Dependencies/Commands; Evaluation not on Commands/Dependencies; Functions not on Parsing/Dependencies/Commands; Dependencies not on Functions/Commands. Undo machinery lives in Gridline.Core.Commands. No public mutable static state in src/.
- Exact surfaces (§0): names, signatures, formats, error codes, exit codes, AutomationIds and canonical strings in §4.5, §8, §9, §10, §11.7 are implemented exactly as written.
- Mutations (§9.2, §9.3): atomic on exception; synchronous recalc; CellsChanged once after recalc and not when nothing changed; exactly one undo step per mutating call.
- No call-stack recursion proportional to chain length or formula operator-chain length (§6.7, D-13).
- Performance budgets (§12) P-1…P-7 hold in Release; existing gates must keep passing.
- Every task: dotnet build Gridline.sln -c Release with 0 warnings; dotnet test Gridline.sln -c Release --filter Category!=UI passes; UI tests tagged [Trait("Category","UI")] must pass locally when the task adds or touches them; DECISIONS.md updated with § references; commit messages cite § numbers.
- No task may assume access to the hidden acceptance suite (§0).

### Controller rulings for Phase 3 (R38–R51)

- R38 (§5.3, supersedes R34): an omitted argument takes the function's default; with no default it evaluates to #VALUE! in argument order. COUNT and COUNTA ignore omitted arguments.
- R39 (§9.4): a #PARSE! formula is copied verbatim by CopyPaste, unshifted.
- R40 (§10.3): BEGIN/END details as stated in Task 5.
- R41 (§3.1, §3.2): Architecture.Tests retargets to net10.0-windows so it can reference App.
- R42 (§3.3): public static readonly DependencyProperty, RoutedEvent and RoutedUICommand identifier fields are allowed.
- R43 (§11.7): SelectionSum is the sum of Number values in the selection, ToString("R", InvariantCulture), `0` when none.
- R44 (§11): `Gridline.App.exe <path>` opens that file at startup.
- R45 (§11.5): unsaved-changes prompt buttons "Save", "Don't Save", "Cancel".
- R46 (§11.6): copy text uses grid display text, Tab between fields, CRLF after each row.
- R47 (§11.6): a paste is internal when the clipboard text equals the text the app last placed there.
- R48 (§11.6): autosave uses SaveToPath, never MarkSaved; failures are retried next tick.
- R49 (§11.4): while an edit is open, Undo/Redo act on the edit text box.
- R50 (§11.6): RecoveryDialog with Restore/Discard per file; restore adopts the recovery file.
- R51 (§11.6): GRIDLINE_AUTOSAVE_INTERVAL_MS overrides the 60 s interval as a test seam.

## Task 1: Spec 1.0 conformance pass for the shipped engine, IO and CLI (§5.3, §7, §9.1, §10)

Vibeless task id: 1f2cee92c6426005e774dc4e92f1b012

**Spec:** The full specification is now available (ARCHITECTURE.md, Spec version 1.0, at .claude/architecture.md). Phases 1-2 were built from ticket text plus controller rulings R1-R37 while the full text was unavailable. Bring the shipped Core, IO and CLI into exact conformance before Phase 3 builds on them. The hidden acceptance suite compiles against §9 and drives §8/§10, so names and member shapes matter.

1. §9.1 ErrorCodes: the constants are exactly `Div0, Value, Ref, Name, NA, Num, Circ, Parse`. Rename `DivZero` to `Div0` everywhere (no alias left behind). Remove the PROVISIONAL note from DECISIONS.md D-7.
2. §9.1 CellValue payload accessors return defaults on the wrong kind instead of throwing: `Number` is 0 unless Kind == Number, `Text` is "" unless Text, `Boolean` is false unless Boolean, `Error` is "" unless Error. Update D-8 and every caller/test that relied on the throw.
3. §9.1 member shapes: compare every public member of ValueKind, ErrorCodes, CellValue, RecalcStats, CellsChangedEventArgs and Workbook (Phase 1 members) with the §9.1 listing — name, static vs instance, property vs method, declared type. In particular `CellValue.Blank` is a static get-only property and `CellsChangedEventArgs.Addresses` is declared as `IReadOnlyCollection<string>`. Extra public members that already exist (Workbook.FromCells, CellValue.EscapeText, ErrorCodes helpers) may stay.
4. §5.3 omitted arguments (ruling R38, supersedes R34 and the omitted-argument choices in D-19, D-21, D-22, D-31): an omitted argument is treated as absent, so the function's default applies; where the position has no default the argument evaluates to #VALUE! and takes its place in §6.4 argument order. Defaults: IF true branch 0 and false branch FALSE (so `IF(FALSE,1,)` is FALSE); LEFT/RIGHT n = 1 (`LEFT("abc",)` is "a"); FIND/SEARCH start = 1; SUBSTITUTE instance absent = all occurrences. Every other omitted position is #VALUE!, including any argument of SUM, AVERAGE, MIN, MAX, PRODUCT, SUMSQ, SUMPRODUCT, AND, OR, XOR, NOT, CONCAT, TEXTJOIN and the IF condition. Exceptions: COUNT and COUNTA never error (§7.1) and simply ignore an omitted argument, so `COUNT(1,,2)` = 2 and `COUNTA(1,,2)` = 2. IFERROR(,x) catches its #VALUE!.
5. Audit the rest of §4, §5.1-§5.4, §6 (including §6.10), §7.0-§7.3, §8.1-§8.4, §9.2 and the §10 Phase 1 commands against the code and DECISIONS.md. Fix any divergence where the spec is explicit. Do not change behaviour the spec leaves open. List every divergence found and fixed (or "none") per section in the report and in DECISIONS.md.
6. DECISIONS.md: add a "Spec 1.0 reconciliation" entry recording, for each ruling R1-R37, whether spec 1.0 confirms it, supersedes it (with the new behaviour) or leaves it as an implementer choice. Note that §6.10.4 confirms exact IEEE equality (`=0.1+0.2=0.3` is FALSE).

**Acceptance Criteria:** ErrorCodes.Div0 exists and DivZero does not; CellValue accessors return the defaults with tests per kind; a reflection test pins the §9.1 member shapes for the Phase 1 types; R38 omitted-argument behaviour has tests for every default listed and for SUM(1,,2), AND(TRUE,), CONCAT("a",,"b"), IF(,1,2) giving #VALUE!, COUNT(1,,2)=2 and COUNTA(1,,2)=2; the audit result is recorded per section in DECISIONS.md with the R1-R37 reconciliation table. dotnet build Gridline.sln -c Release has 0 warnings and dotnet test -c Release --filter Category!=UI passes.

## Task 2: Canonical formula printer with reference rewriting (§5.5)

Vibeless task id: b947e993b5136321e221a0b93f1ecd74

**Spec:** Add a canonical formula printer in Gridline.Core.Parsing. It must not depend on Evaluation, Functions, Dependencies or Commands (§3.3).

Printing rules (§5.5, verbatim):
- `=` prefix. No whitespace anywhere except inside string literals.
- Function names, `TRUE`/`FALSE`, error literals and cell references are uppercase. Each reference keeps its own `$` markers.
- Name references use the casing of the name's definition, or the casing as written if the name is undefined.
- Number literals keep their exact original lexeme: `1.50` stays `1.50`.
- String literals are re-emitted with `""` escaping.
- Parentheses are preserved exactly as written. None are added or removed, so the AST MUST retain explicit parenthesized groups.
- A reference that became invalid is printed as `#REF!`. If either corner of a range became invalid, the whole range is printed as `#REF!`.
Example: `= sum( a1 , $b$2:C3 ) * (2.50+ x)` with `x` undefined, after a no-op rewrite, becomes `=SUM(A1,$B$2:C3)*(2.50+x)`.

API (internal shape is the implementer's choice, record it in DECISIONS.md): print a successfully parsed formula's AST, taking (a) an optional reference rewriter that maps each CellReference, with its $ flags, to a new CellReference or to "invalid", applied to single references and to each range corner; and (b) an optional name-casing resolver (Phase 4 names plug in here; with no resolver names print as written). Omitted arguments print as empty (`IF(A1,,3)`), unary and postfix operators print without spaces, error literals print in canonical form. A formula that failed to parse has no AST; callers keep such input verbatim.

The printer must not recurse along operator chains: D-13 notes a left spine can be about 16,000 nodes deep in a 32,767-character formula. Use an explicit stack like the evaluator and PrecedentExtractor (§6.7 spirit, R9).

**Acceptance Criteria:** Tests cover every §5.5 bullet, the §5.5 worked example exactly, number lexemes (1.50, 1e5, .5), string re-escaping, nested and redundant parentheses kept, $ markers kept per corner, an invalid single reference and an invalid range corner printing #REF!, omitted arguments, and a 16,000-term `1+1+...` chain printed on a 1 MB thread. An FsCheck property shows that for generated formulas Print(Parse(f)) parses again and printing that result is idempotent. Architecture tests still pass. Build has 0 warnings; tests pass with --filter Category!=UI.

## Task 3: Undo/redo, ApplyBatch and dirty tracking in Gridline.Core.Commands (§9.1, §9.3, §14)

Vibeless task id: cc9d38c9c4eb0a5763c3d780a7301a0a

**Spec:** Add the §9.1 Phase 2 Workbook members with these exact signatures: `void ApplyBatch(IReadOnlyList<KeyValuePair<string, string>> edits)`, `bool CanUndo { get; }`, `bool CanRedo { get; }`, `bool Undo()` (false if nothing to undo), `bool Redo()`, `bool IsDirty { get; }`, `void MarkSaved()`. CopyPaste is the next ticket.

The undo machinery lives in Gridline.Core.Commands (currently an empty namespace with a .gitkeep; §3.3 lets Commands depend on any Core namespace, and nothing else in Core except the root Workbook may depend on it).

§9.3 semantics (verbatim):
- Each mutating API call is one undo step: SetCell, ApplyBatch, CopyPaste, insert/delete, DefineName and DeleteName.
- Calls that change nothing (for example, setting the same input) create no step.
- Undo restores the exact prior inputs, including formula text verbatim, and the exact prior names. Redo reapplies the step.
- A new mutation after an undo clears the redo stack.
- At most UndoLimit steps are kept. When a 101st is added, the oldest is dropped.
- A new or freshly loaded workbook is not dirty. MarkSaved() records the current history position as "saved". IsDirty is true exactly when the current position differs from the saved position, so undoing back to the saved position makes the workbook clean again. If the saved position has been dropped from history, the workbook is dirty until the next MarkSaved().
- ApplyBatch: if an address appears more than once, the last edit wins. An empty batch is a no-op and creates no undo step. Any invalid address → ArgumentException, and nothing is applied.

Further rules:
- ApplyBatch validates everything first (null list or entry, invalid address, input over MaxInputLength → ArgumentException, nothing applied), drops edits equal to the current input, and if nothing remains it is a no-op with no step and no event. Otherwise it applies all edits, runs one recalc with every edited cell in the changed set, raises CellsChanged once and records one step. It is atomic like SetCell (§9.2).
- Undo and Redo are mutations: they recalculate synchronously, set LastRecalc, raise CellsChanged once when something changed, and are atomic on exceptions. They never create steps. When there is nothing to undo or redo they return false and change nothing, including LastRecalc.
- A step stores the exact prior and new input of every cell it touched. Design the step type so CopyPaste (next ticket) and the Phase 4 structure and name edits can record steps without redesign, but add no Phase 4 API now.
- Workbook.FromCells and every load produce a clean workbook with empty history (§8.2).
- Performance must hold: P-2 (≤ 250 ms, EvaluatedFormulaCount 99,999) and P-3 (median ≤ 1 ms) gates still pass with step recording on, and SetCell's step allocation is O(1).

Tests (§14): an FsCheck property generates random sequences of SetCell and ApplyBatch over a small address pool, with inputs drawn from numbers, text, booleans, formulas referencing the pool, self- and mutual cycles and unparseable formulas, interleaved with Undo/Redo/MarkSaved. Undo-all must return exactly the initial inputs, with values equal to a fresh FromCells of those inputs. Redo-all must return the final inputs. IsDirty must equal (position != saved position) at every step, and the UndoLimit drop must be covered.

**Acceptance Criteria:** The seven members exist with exact §9.1 signatures; unit tests cover every §9.3 bullet including the 101st-step drop, dirty after the saved position is dropped, redo cleared by a new mutation, no-op calls creating no step, ApplyBatch last-edit-wins, empty batch and atomic rejection; Undo/Redo raise CellsChanged once and set LastRecalc; the FsCheck undo-all property passes; Commands namespace rules in Architecture.Tests now have real targets; P-2 and P-3 gates pass in Release. Build has 0 warnings; tests pass with --filter Category!=UI.

## Task 4: CopyPaste with relative, absolute and mixed reference shifting (§9.4)

Vibeless task id: 846b88145624ef07d347a8b59aa93a11

**Spec:** Implement `public void CopyPaste(string sourceRange, string destinationTopLeft)` on Workbook (§9.1), recorded as one undo step through the Commands machinery.

§9.4 (verbatim):
- The source range is copied to the same-sized rectangle whose top-left is the destination. The source is snapshotted first, so overlapping source and destination work correctly.
- If the destination rectangle exceeds the grid → ArgumentException.
- Non-formula inputs are copied verbatim. Blank source cells clear their destination cells.
- Formula inputs: each reference is shifted by the offset (Δrow, Δcol). Absolute components ($) do not shift; mixed references shift only their relative part. A reference that shifts outside the grid becomes #REF! (§5.5). Every pasted formula is re-emitted in canonical form (§5.5), even when the offset is zero.
- The paste is one undo step.

Further rules:
- sourceRange is a range or a single cell address (§4.2, corners in any order, $ markers allowed); destinationTopLeft is a single address. Invalid → ArgumentException, nothing changes.
- Work in proportion to used cells, not rectangle area: pasting A1:ZZ100000 must snapshot only the used cells in the source and clear only used cells in the destination that the source does not overwrite.
- Formulas are printed with the canonical printer and its reference rewriter. Ruling R39: a formula input that fails to parse (#PARSE!) has no AST and is copied verbatim, unshifted.
- Edits that leave a cell's input unchanged are dropped; a paste that changes nothing creates no step and no event. One recalc and one CellsChanged per paste. Atomic like ApplyBatch.
- Extend the FsCheck undo/redo property from the previous ticket so its generator also produces CopyPaste operations (including overlapping and grid-edge ones).

**Acceptance Criteria:** Tests cover relative, absolute and both mixed forms; ranges with different $ flags per corner; shifting off each grid edge to #REF! (single reference and one range corner); overlapping source and destination in all four directions; blank source cells clearing destination cells; zero-offset canonicalization (`= sum( a1 )` becomes `=SUM(A1)`); destination exceeding the grid throwing with no change; #PARSE! input copied verbatim; undo restoring the original text verbatim; a 100k-used-cell paste running in time proportional to used cells. The FsCheck property including CopyPaste passes. Build has 0 warnings; tests pass with --filter Category!=UI.

## Task 5: Script UNDO, REDO, BEGIN/END, PASTE and DIRTY commands (§10.3)

Vibeless task id: f4efc2546a73062c20e6c7be29855b2a

**Spec:** Extend `gridline script` with the §10.3 Phase 2 commands. Output lines follow the existing rules (UTF-8 without BOM, LF, `ERR\t<message>` on failure, exit 4 iff an ERR line was delivered).

| Command | Output |
|---|---|
| `UNDO` / `REDO` | `OK` or `NOOP` |
| `BEGIN` … `END` | SET and SETX lines between them are applied as one `ApplyBatch` at `END`. Any other command inside the block → `ERR` |
| `PASTE <srcRange> <dstAddr>` | None |
| `DIRTY` | `TRUE` or `FALSE` |

Also: `SAVE <path>` calls `MarkSaved()` after a successful save (never after a failed one), per §10.3 "From Phase 2 on". `LOAD` and `--file` give a clean workbook with empty history.

Ruling R40 for BEGIN/END details the spec leaves open:
- BEGIN and END take no arguments (arguments → ERR). BEGIN inside an open block → ERR and the block stays open. END with no open block → ERR.
- Inside a block, SET/SETX lines are checked as they are read (address, escape sequences, input length). An invalid line prints ERR and is left out of the batch; valid lines are buffered in order. Blank and `#` lines are still ignored.
- Any other command inside a block prints ERR, is not executed, and the block stays open.
- END applies the buffered edits as one ApplyBatch (an empty block is a no-op) and prints nothing; an exception from ApplyBatch prints ERR.
- End of input with an open block prints `ERR\tunterminated BEGIN block` and discards the batch.

UNDO/REDO/DIRTY take no arguments; PASTE takes exactly two (address and range errors → ERR). The Phase 3 commands (INSERTROWS, DELETEROWS, INSERTCOLS, DELETECOLS, NAME, UNNAME, NAMES) still print `ERR\tnot implemented`.

**Acceptance Criteria:** In-process script tests cover each command's output, every R40 case, SAVE then DIRTY FALSE, edit then DIRTY TRUE, undo back to the saved position giving DIRTY FALSE, failed SAVE leaving DIRTY TRUE, and PASTE shifting through the CLI. A process-level acceptance test drives the built gridline executable through build → BEGIN/END batch → UNDO/REDO → PASTE → SAVE → DIRTY and checks stdout byte-exactly and the exit code. Build has 0 warnings; tests pass with --filter Category!=UI.

## Task 6: WPF app shell: projects, MVVM, virtualized grid, layout and AutomationIds (§2, §3, §11.1-§11.3, §11.7)

Vibeless task id: edea08342a3a7fe14694cab5db8aefa6

**Spec:** Create the desktop app and its test project, and the window with a virtualized grid bound to a ViewModel.

Projects (§2, §3.1, §3.2):
- `src/Gridline.App`: `net10.0-windows`, `<UseWPF>true</UseWPF>`, Nullable enabled, C# latest, references Core and IO only, no packages. Executable `Gridline.App.exe`.
- `tests/Gridline.App.Tests`: `net10.0-windows`, packages only from §2 (xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, FlaUI.UIA3, coverlet.collector, FsCheck.Xunit if needed). ViewModel unit tests run by default; FlaUI tests are tagged `[Trait("Category","UI")]`.
- Add both to Gridline.sln. Architecture.Tests gains the §3.2 App row (App may reference Core and IO; MUST NOT depend on Cli) with a violation sanity case, and its BCL-only and no-public-mutable-static-state scans cover App (ruling R41: Architecture.Tests retargets to net10.0-windows so it can reference App; ruling R42: public static readonly DependencyProperty, RoutedEvent and RoutedUICommand identifier fields are immutable registrations and are allowed).

Window (§11.1, §11.2): title `Gridline - <file name or Untitled>`, with ` *` appended when dirty. Default size 1280×800. Layout top to bottom: menu bar; name box and formula bar; grid; status bar.

Grid (§11.3): virtualized, only visible cells realized (a custom-drawn control rendering only the visible rectangle is acceptable and preferred over DataGrid). Scrolling to row 100,000 and column ZZ MUST stay responsive. Column headers show A…ZZ; row headers show 1…100000. Display formatting: numbers use G10 invariant and are right-aligned; text is left-aligned; booleans and errors are centered. The active cell is highlighted. Arrow keys move the active cell; Shift+arrows or mouse drag select a range. The grid repaints on Workbook.CellsChanged.

MVVM: a main ViewModel (INotifyPropertyChanged, built-in WPF binding only) owns the Workbook and exposes title, active address, selection, name box text, formula bar text, SelectionValue (the active cell's §8.3 canonical value, e.g. `N:5`, `BLANK`) and SelectionSum (ruling R43: the sum of Number values in the selection, printed with ToString("R", InvariantCulture), `0` when there are none, -0 as 0; visit only used cells inside the selection).

Menus (§11.5): File: New (Ctrl+N), Open (Ctrl+O), Save (Ctrl+S), Save As (Ctrl+Shift+S), Exit. Edit: Undo (Ctrl+Z), Redo (Ctrl+Y), Copy (Ctrl+C), Paste (Ctrl+V). Import/Export CSV and header context menus are Phase 4. Undo and Redo are wired to the Workbook now and enabled from CanUndo/CanRedo; the file and clipboard commands are completed in later tickets.

AutomationIds (§11.7, exact): NameBox, FormulaBar, Grid, SelectionValue, SelectionSum, MenuFile, MenuEdit, MenuUndo, MenuRedo, MenuSave, MenuOpen (RecoveryDialog comes with autosave). The grid has an AutomationPeer and takes keyboard focus.

Ruling R44: `Gridline.App.exe <path>` opens that file at startup (used by the tests and P-6).

**Acceptance Criteria:** dotnet build Gridline.sln -c Release builds the App with 0 warnings; the §3.2 App rule and its sanity case pass; ViewModel tests cover title (Untitled, file name, asterisk), SelectionValue, SelectionSum (R43) and G10 display text/alignment; a FlaUI test (Category=UI) launches the app, finds every AutomationId listed above, checks the title `Gridline - Untitled` and moves the active cell with arrow keys; a test proves the grid realizes only visible cells and a FlaUI test navigates to ZZ100000 and stays responsive. Non-UI tests pass with --filter Category!=UI and the UI tests pass locally.

## Task 7: GUI cell editing, name box and formula bar (§11.4)

Vibeless task id: 496da84315301947b004524a587b8dbd

**Spec:** Implement §11.4 editing in Gridline.App.

| Input | Action |
|---|---|
| Typing a printable character | Starts editing with that character |
| F2 | Edits the existing input |
| Enter | Commits and moves down |
| Tab | Commits and moves right |
| Esc | Cancels the edit |
| Delete | Clears the selection as one undo step (ApplyBatch) |

- Name box: shows the active address. Typing an address and pressing Enter navigates there. An invalid address reverts the box.
- Formula bar: shows the active cell's input. Enter commits, Esc reverts.

Further rules:
- Commits go through Workbook.SetCell, so each commit is one undo step and recalc/CellsChanged refresh the grid, formula bar and status bar.
- The in-cell editor and the formula bar cap input at Workbook.MaxInputLength.
- Enter on row 100000 and Tab on column ZZ commit and stay in place.
- Delete builds its ApplyBatch only from used cells inside the selection (a whole-grid selection must not create 70 million edits); with no used cells it does nothing.
- Name box navigation scrolls the grid so the target is visible and makes it the active cell; `$` markers and lower case are accepted; the box then shows the canonical address.
- While an edit is open, menu Undo/Redo apply to the edit text box rather than the workbook (ruling R49), matching standard text-box behaviour; once committed they act on the workbook.

**Acceptance Criteria:** ViewModel tests cover each row of the table, both edges, Delete over used and unused cells (one undo step, no step when empty), name box valid/invalid/`$a$1`, formula bar commit and revert. FlaUI tests (Category=UI) type into the Grid, press Enter and Tab and check FormulaBar and SelectionValue, use F2 and Esc, press Delete then MenuUndo, and navigate with NameBox. Build has 0 warnings; non-UI tests pass with --filter Category!=UI and the UI tests pass locally.

## Task 8: GUI file commands, dirty prompt and clipboard (§11.1, §11.5, §11.6, §8.5 fields)

Vibeless task id: 7f40ef08f19c93016dba46f67e6ae2ca

**Spec:** Complete the File and Edit menus in Gridline.App.

File (§11.5): New (Ctrl+N), Open (Ctrl+O), Save (Ctrl+S), Save As (Ctrl+Shift+S), Exit, using GridlineFile.LoadFromPath and the atomic GridlineFile.SaveToPath. A successful save calls MarkSaved; Save on an Untitled workbook acts as Save As. Open and Save As use the standard Win32 file dialogs with a `*.gridline` filter. A load or save error shows a message box and leaves the current workbook and path unchanged.

Title (§11.1): `Gridline - <file name or Untitled>`, with ` *` appended exactly when Workbook.IsDirty is true, including returning to clean after undoing back to the saved position.

Unsaved changes (§11.5): New, Open and Exit on a dirty workbook prompt Save / Don't Save / Cancel. Closing the window with its close button counts as Exit. Save that fails or is cancelled aborts the action. Ruling R45: the prompt is a small modal window with buttons labelled exactly "Save", "Don't Save" and "Cancel" (AutomationIds PromptSave, PromptDontSave, PromptCancel; not normative).

Clipboard (§11.6):
- Copying within the app followed by pasting uses §9.4 semantics (Workbook.CopyPaste from the copied range to the active cell).
- Copy also places the displayed values on the system clipboard as tab-separated text. Ruling R46: displayed values use the grid's display text (G10 numbers); fields are joined by Tab and each row ends with CRLF.
- Pasting external text parses it with §8.5 import rules, using Tab as the delimiter, and writes it at the active cell as one ApplyBatch.
- Ruling R47: a paste is internal when the clipboard text still equals the text this app last placed there; otherwise it is external.

§8.5 import rules that apply to clipboard text (encoding detection does not apply to an in-memory string): records are separated by CRLF, LF or CR, in any mix, and a final terminator does not create an empty row; fields follow RFC 4180 with the delimiter: quoted fields may contain the delimiter, `""` and line breaks, and a `"` inside an unquoted field is a literal character; an unterminated quoted field at EOF is an error. Field j of record i goes to the cell at row i, column j, offset from the active cell. An empty field becomes blank. A field starting with `=` or `'` is stored as input `"'" + field`. Every other field is stored verbatim so §4.3 interprets it. Implement the record/field parser in Gridline.IO as a public, delimiter-parameterised type that Phase 4 CSV import will reuse. A paste that would exceed the grid or fails to parse shows a message and changes nothing.

**Acceptance Criteria:** IO tests cover every §8.5 record and field rule listed (mixed terminators, final terminator, quoted delimiter, doubled quotes, embedded line breaks, literal quote in unquoted field, unterminated quote error, = and ' prefixing, empty field). ViewModel tests cover New/Open/Save/Save As/Exit flows with a fake dialog service, the prompt's three outcomes, failed save aborting, title asterisk tracking IsDirty through undo to the saved position, internal vs external paste and TSV copy text. FlaUI tests (Category=UI) save through MenuSave and the file dialog, observe the asterisk appear and disappear, and copy/paste a formula with shifted references. Build has 0 warnings; non-UI tests pass with --filter Category!=UI and the UI tests pass locally.

## Task 9: Autosave and recovery, FlaUI acceptance suite, P-6 gate and Phase 3 exit (§11.6, §11.7, §12, §13)

Vibeless task id: 0018ae743e38018344a2034f2876195f

**Spec:** Autosave and recovery (§11.6, verbatim): while dirty, the app saves atomically every 60 s to `%LOCALAPPDATA%\Gridline\Recovery\<session-guid>.gridline`. The recovery file is deleted on a successful Save or a clean exit. At startup, if recovery files exist, the app shows a dialog offering to restore each one.

Rulings for what the spec leaves open:
- R48: autosave uses GridlineFile.SaveToPath, never calls MarkSaved and never changes the title; the Recovery directory is created on demand; an autosave failure is ignored and retried on the next tick. A clean exit includes Exit after Don't Save.
- R50: the RecoveryDialog window (AutomationId `RecoveryDialog`) lists each recovery file with its last-write time and offers Restore and Discard per file, plus Close. Restore loads that file into the window as an Untitled, dirty workbook and closes the dialog. Discard deletes the file. Files not acted on stay and are offered at the next startup. A restored session adopts the restored file as its own recovery file (same guid), so it is replaced by the next autosave and deleted by a successful Save or clean exit. A recovery file that fails to load is listed as unreadable and can only be discarded.
- R51: the environment variable `GRIDLINE_AUTOSAVE_INTERVAL_MS` overrides the 60 s interval, as a test seam only; tests also point `LOCALAPPDATA` at a temp directory for the launched process.

FlaUI acceptance suite (Category=UI, §11.7, Phase 3 acceptance): exercise every §11.7 AutomationId (NameBox, FormulaBar, Grid, SelectionValue, SelectionSum, RecoveryDialog, MenuFile, MenuEdit, MenuUndo, MenuRedo, MenuSave, MenuOpen) and run the phase story end to end: open a 100k-cell file, edit and paste cells, undo and redo, save, see the title asterisk follow dirty state; then make an edit, wait for an autosave, kill the process, restart, see RecoveryDialog, restore and find the edit.

P-6 (§12): GUI open of a 100k-cell file until the grid is rendered ≤ 3 s, measured as launch with the file argument (R44) until the grid reports that it has rendered the loaded workbook, median of 5 runs after one warm-up, Release only, Category=UI. Expose a rendered signal the test can read through UI Automation.

Phase exit (§13): DECISIONS.md has a Phase 3 section indexing every Phase 3 decision with § references; dotnet build Gridline.sln -c Release and dotnet test Gridline.sln -c Release --filter Category!=UI pass; the full UI suite passes locally; earlier-phase behaviour still works.

**Acceptance Criteria:** ViewModel/unit tests cover autosave only while dirty, file location, deletion on Save and clean exit, restore adoption and discard. The FlaUI suite covers every §11.7 AutomationId and the phase story including kill-and-recover. The P-6 gate passes in Release with its measured median recorded in DECISIONS.md. Build has 0 warnings; tests pass with --filter Category!=UI; the UI suite passes locally; DECISIONS.md Phase 3 section is complete.
