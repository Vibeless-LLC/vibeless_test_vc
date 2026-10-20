# Phase 2 — Core Engine, Persistence and CLI

Source: Vibeless project test_vc, phase "Core Engine, Persistence and CLI" (id 0634ec8c955c9dcc6cbb3cabf6e7140c).

All Phase 2 deliverables were built and merged during Phase 1 (ruling R1): the Workbook API, iterative recalc, byte-exact save, strict load, atomic save, gridline eval/set/script, the NetArchTest suite and the P-gates. These Phase 2 tickets close the gaps found by the Phase 1 reviews in each deliverable. They also add the end-to-end acceptance coverage the phase criteria call for. Phase 3/4 scope is out: undo, paste, canonical printing, names, lookup/conditional/date functions, CSV and the GUI.

Task text is copied verbatim from the Vibeless tickets.

## Global Constraints

The Global Constraints section of docs/plans/phase-1-foundation.md applies unchanged. The controller rulings R1–R32 recorded in DECISIONS.md remain binding.

## Task 1: Phase 2 acceptance: end-to-end CLI build, save, reload byte identity (§8.1, §10, §13)

Vibeless task id: 16bbf3ce4f572bfb1f2962c400595018

**Spec:** Add process-level acceptance tests in tests/Gridline.Cli.Tests that drive the built gridline executable (dotnet gridline.dll) to prove the Phase 2 acceptance criteria end to end. Do not reach into Core directly.
- A `gridline script` session builds a workbook covering numbers, text containing every §8.1 escape class, booleans, formulas, an error result and a cycle. It runs SAVE to a temp file, then LOAD, then SAVE to a second file. Both files must be byte-identical.
- `gridline eval <file> --all` on the reloaded file prints the same canonical values as DUMP in the original session. Cycle members print `E:#CIRC!`.
- `gridline set <file> A1 <x>` followed by `gridline eval` reflects the change, and the saved file stays canonical: re-saving through script LOAD/SAVE yields identical bytes.
- Run every case under at least one non-UTF-8 console code page simulation where feasible: pass raw UTF-8 bytes on stdin containing non-ASCII text.
Tests must not depend on the current directory and must use unique temp directories. Implements Phase 2 acceptance: build from CLI, save, reload, identical bytes; cycles show #CIRC!.

**Acceptance Criteria:** The new process-level tests pass in Release and cover: build → save → load → save byte identity; eval --all matching DUMP; #CIRC! for cycle members; a set-then-eval round trip; non-ASCII stdin. dotnet build has 0 warnings and dotnet test -c Release --filter Category!=UI passes.

## Task 2: Workbook API hardening and coverage gaps (§9.1, §9.2)

Vibeless task id: a4e593f7d600e3cc12491e49f75736da

**Spec:** Close the Workbook gaps found in the Phase 1 reviews without changing the public surface beyond what is listed here.
1. CellsChangedEventArgs.Addresses must not be mutable by handlers. Return a read-only wrapper (Array.AsReadOnly or equivalent). Record the public CellsChangedEventArgs(IReadOnlyList<string>) constructor in the DECISIONS.md Workbook entry so the documented surface is exact.
2. Make ToSortedAddresses / GetUsedAddresses sort row-major on a projected integer key (row << 10 | col) with the default comparer, and avoid the double copy.
3. Trim the rollback undo-log capacity after large recalcs, so a 100k-cell edit does not retain about 100k tuples indefinitely.
4. Add the missing tests:
   - a formula replaced by a different formula with the same value still raises CellsChanged including the edited cell;
   - clearing a cell holding Text "" (input "'") raises the event;
   - a subscriber exception propagates and the edit stays committed (R25);
   - SetCell/GetValue accept `$A$1` as A1;
   - rollback after a throwing function when the replaced formula had a range precedent (=SUM(A1:A3) replaced, then A2 edited, recomputes correctly).
Workbook stays single-threaded, atomic and P-3 compliant.

**Acceptance Criteria:** Tests for each item pass. A handler cannot mutate Addresses: a cast to IList throws or no mutable type is exposed. P-3 and P-2 gates still pass. DECISIONS.md is updated with §9.1/§9.2 references. Build has 0 warnings and the full non-UI Release test run passes.

## Task 3: Recalc engine hardening: range index removal, name dedupe, cycle tests (§6.1, §6.7)

Vibeless task id: 4cb91ce362c402940dacc575e8e74a44

**Spec:** Harden Gridline.Core.Dependencies against the scale issues found in the Phase 1 review. All changes must keep the §6.7 no-recursion rule and the R20-R22 semantics.
1. RangeIndex bucket removal is currently linear (bucket.IndexOf). Make it O(1) amortised, for example by storing each entry's slot per tile and fixing it on swap-remove. Test it: clearing all 100,000 running-total formulas (B_r = SUM($A$1:A_r)) through Workbook.SetCell(addr, "") must complete in under 2 s in Release, and the result must be correct afterwards.
2. PrecedentExtractor deduplicates names with List.Contains, which is quadratic. Use an ordinal HashSet.
3. Reduce per-node level probing in BFS dependent lookup when many distinct range shapes exist. Add a Release performance test: a 100k chain edit in a workbook that also holds at least 50 distinct range shapes must stay within P-2 (250 ms). If it already does without code changes, keep the test and document that.
4. Add tests:
   - two independent cycles sharing one downstream cell: the downstream cell is #CIRC! via propagation and EvaluatedFormulaCount is correct;
   - the R22 behaviour that downstream non-cycle cells are evaluated, not marked as cycle members.
Dependencies MUST NOT depend on Functions or Commands.

**Acceptance Criteria:** The bulk-clear test passes within 2 s in Release. The mixed-range chain test passes within 250 ms. The new cycle tests pass. All P-gates and architecture tests still pass. DECISIONS.md is updated (§6.1, §6.7). Build has 0 warnings.

## Task 4: Persistence hardening: atomic save edge cases and loader test gaps (§8.1-§8.4)

Vibeless task id: 4414eb12d3dd8fc33d418283924f251e

**Spec:** Close the Gridline.IO gaps from the Phase 1 reviews.
1. GridlineFile.SaveToPath:
   - Decouple the test seam from production error handling. Temp-file cleanup must happen in a finally block (or behind a success flag) for every exception type. Only I/O-class failures are wrapped in GridlineIOException. The seam must not change the production catch semantics.
   - Reject a target path ending in a directory separator before creating any temp file.
   - Use a temp name scheme that cannot push a valid target over the file-name length limit. For example, shorten the suffix, or fall back to a short random name in the same directory.
2. Tests:
   - saving over a read-only target file gives GridlineIOException and leaves the original intact;
   - assert InnerException is set for the directory / missing-directory cases;
   - remove the test that mutates Environment.CurrentDirectory, or isolate it in a non-parallel collection;
   - test-cleanup Dispose must tolerate UnauthorizedAccessException.
3. Loader and writer tests:
   - one combined-string byte-exact test with every §8.1 escape class in a single input;
   - fix the duplicated AAA1 test (TooLarge should use a column such as XFD);
   - rename misnamed tests (Load_RunsFullRecalc_AndHasNoUndoOrDirtyState, Fail_FormulaStillLoads_*) to describe what they assert;
   - add a `"version": 1e0` rejection test per R28.
Byte-exact output and the atomic-replace guarantee must not change.

**Acceptance Criteria:** The new and renamed tests pass. P-1, P-5 and P-7 still pass. No test changes the process current directory in a parallel collection. DECISIONS.md is updated (§8.4). Build has 0 warnings and the full non-UI Release run passes.

## Task 5: CLI and script-mode hardening (§10.1-§10.3)

Vibeless task id: 8911c17c3c2a9ef670e129c902dde2d3

**Spec:** Close the CLI gaps from the Phase 1 reviews. The exit codes, output formats and rulings R29-R31 do not change.
1. Mid-script broken pipe:
   - If stdout fails mid-script after an ERR line was already printed, exit 4 (script ERR) rather than 1.
   - Otherwise exit 1, with the stderr message describing an output failure rather than "internal error".
   - Test both cases with a throwing stdout stream.
2. ReadLfLine:
   - Cap a single script line at MaxInputLength plus a reasonable prefix allowance.
   - On overflow, discard the rest of the line up to LF, print `ERR\t<message>`, and continue.
   - Test it.
3. Add pin tests:
   - a mid-line lone CR is kept in a SET input;
   - an unterminated final line is executed;
   - empty stdin gives exit 0 with no output;
   - SETX with `\\t` stores a backslash followed by t;
   - a trailing CR at EOF without LF is stripped.
4. Add a direct Core unit test for CellValue.EscapeText.
5. Replace the partial `Core.Workbook` qualification in ScriptCommand with the using directive.
Cli MUST NOT depend on App or System.Windows*.

**Acceptance Criteria:** All new tests pass. A broken pipe never escapes Run. An oversized line yields ERR and the script continues. Output stays UTF-8 without BOM with LF. DECISIONS.md is updated (§10.3). Build has 0 warnings and the full non-UI Release run passes.

## Task 6: Evaluator and function library polish and regression coverage (§5, §6, §7.1-§7.3)

Vibeless task id: cae954858a4bcf59efeb8b8ebf3322e6

**Spec:** Address the deferred correctness and regression-coverage findings in the Phase 1 parser, evaluator and function code. Do not add functions: the library stays at the Phase 1 set, and lookup, conditional and date functions belong to Phase 4.

1. Decide each of these Excel-parity questions, document the decision in DECISIONS.md citing §7.0/§7.1, and make the code match it:
   - Omitted direct arguments in COUNT/COUNTA/AVERAGE. Today AVERAGE counts an omitted argument as 0 while COUNT and COUNTA skip it. Make them consistent.
   - SUMPRODUCT error order: return the first error in argument order.
   - The MOD doc comment contradicts its fmod implementation. Fix the comment, and decide whether MOD with huge magnitudes, e.g. MOD(1E+300,7), returns #NUM!.

2. Remove duplication and correct code style:
   - The tokenizer must use ErrorCodes as the single source of the eight codes, not a duplicated list.
   - CellValue escaping should use character literals instead of (char)92, and scan the text once.
   - Remove the unused return value of the Take helper in TextFunctions.

3. Add regression tests:
   - ROUND family: ROUND(1E+300,-299), ROUND(1.23E-10,12), ROUND(999.5,0)=1000, ROUNDUP(-1.21,1)=-1.3, ROUNDDOWN(1.29,1)=1.2.
   - SEARCH: SEARCH("a?c*d","abcxacd")=1, the dangling `~`, SEARCH("*b","abc",3)=#VALUE!, and a near-miss pathological pattern against REPT("a",30000) that must finish in under 1 s.
   - Parser stack-machine cases: SUM(1,(2+3)*4), A1:B2+1, -(-1), 1+2*3^2%, 2*-3^2.
   - IF and NOT with a multi-cell range condition.
   - OR(TRUE, error cell) ordering.

4. Fix test hygiene:
   - The FormulaParserTests helper must honour an explicit null arity provider.
   - Small-stack thread tests must catch exceptions inside the thread and assert after Join.
   - The tail-recursive Burn helper must become non-tail so the JIT cannot turn it into a loop.

The rules R6-R19 stay in force.

**Acceptance Criteria:** Every decision is recorded in DECISIONS.md with § references, and the code and tests match it. All listed regression tests pass, and the pathological SEARCH finishes in under 1 s. Architecture tests and P-gates still pass. Build has 0 warnings and the full non-UI Release run passes.

## Task 7: Architecture and performance gate hardening, Phase 2 exit (§3.2, §3.3, §12, §13)

Vibeless task id: 95a963c4c1a7efeca5357c2c7a4a0dab

**Spec:** Make the quality gates harder to fool, then close Phase 2.

1. §3.3 sanity coverage. Add one known-violation type per namespace rule (Addressing, Parsing, Evaluation, Functions, Dependencies). Each must be flagged by the same shared helper the production tests use, so every rule has proven teeth, not just Parsing.

2. Mutable-static checker (§3.3). Extend it to flag:
   - get-only public static properties whose type is mutable (e.g. List<T>, arrays);
   - public static events.
   Add sanity cases for these, plus init-only properties and protected static fields.

3. Forbidden-type rules. Add discriminating sanity types for Directory-only and Console-only use. Record in DECISIONS that the prefix matching also bans FileInfo, DirectoryInfo and ConsoleColor.

4. Performance gates (§12). Make P-7 memory less noisy: measure it in a dedicated child process, or document why the in-host measurement is reliable. Make sure the gate collection cannot run in parallel with other test assemblies' heavy work, or document the risk. Budgets do not change.

5. Phase 2 exit (§13):
   - dotnet build Gridline.sln -c Release has 0 warnings.
   - dotnet test Gridline.sln -c Release --filter Category!=UI passes.
   - DECISIONS.md is coherent, with no stale entries and § citations throughout.
   - The work is committed.

**Acceptance Criteria:** Every §3.3 rule has a known-violation sanity test that fails it through the production helper. The extended static checker flags the new cases. All P-gates pass in Release. Build has 0 warnings, the full non-UI Release run passes, and DECISIONS.md is updated and committed.
