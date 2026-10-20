# Change Request CR-1: Multiple Sheets (Phase 5)

Copied verbatim from the Vibeless phase description (phase id fe8e38af8b5b4ad5379234ddc75f8bdb).

This change request amends ARCHITECTURE.md v1.0. Section numbers CR1.x are requirement IDs. Everything not changed here stays in force, and every Phase 1–3 behavior MUST still hold for single-sheet workbooks.

## CR1.1 Sheets
A workbook has an ordered list of 1 to 255 sheets. A new workbook has one sheet, Sheet1.

Sheet names:
- 1–31 characters.
- Must not contain : \ / ? * [ ].
- Must not begin or end with '.
- Unique case-insensitively.
- An invalid name → ArgumentException.

Active sheet. Exactly one sheet is active. Every unqualified address passed to any API, CLI or script command refers to the active sheet.

## CR1.2 Qualified references
A qualified reference has the form Sheet!A1 or Sheet!A1:B3. Quote the sheet name, as in 'My Sheet'!A1, when it is any of the following:
- contains a character outside [A-Za-z0-9_.];
- starts with a digit;
- looks like a cell reference (^[A-Za-z]{1,3}[0-9]+$);
- is TRUE or FALSE.

Inside quotes, '' stands for '.

A range has a single sheet prefix. Sheet2!A1:Sheet2!B3 is #PARSE!.

Canonical printing (amends §5.5): the sheet name appears in its defined casing, quoted only when required.

Unqualified references in formulas refer to the formula's own sheet.

Missing sheets. A reference to a sheet that does not exist parses successfully and evaluates to #REF!. If that sheet is created later, the reference resolves on recalc. Formulas therefore depend on sheet existence.

Cycles across sheets are detected per §6.7.

## CR1.3 API additions (Gridline.Core.Workbook)
```csharp
public IReadOnlyList<string> SheetNames { get; }          // in order
public string ActiveSheet { get; set; }                    // ArgumentException if unknown; not undoable
public void AddSheet(string name, int? index = null);     // append if null
public void RenameSheet(string oldName, string newName);
public void DeleteSheet(string name);                      // InvalidOperationException if last sheet
public void MoveSheet(string name, int newIndex);
public void InsertRows(string sheet, int row, int count);  // + DeleteRows / InsertColumns / DeleteColumns overloads
```
All of these except ActiveSheet are undoable, one step each.

The existing unqualified overloads act on the active sheet.

Address strings. Every address parameter in the API accepts qualified or unqualified addresses. This includes SetCell, GetValue, CopyPaste, ApplyBatch and DefineName.

GetUsedAddresses():
- With exactly one sheet, it returns unqualified addresses, unchanged from v1.
- With more than one sheet, it returns qualified addresses for all sheets, in sheet order and then row-major.

RenameSheet rewrites every formula and name that refers to the old name. Rewritten formulas are re-emitted per §5.5.

DeleteSheet:
- References to the deleted sheet become #REF! and are re-emitted.
- Names pointing into it get refersTo = #REF!.
- Undo restores the sheet's cells and the verbatim original text of every rewritten formula.

Insert and delete on a sheet adjust qualified references to that sheet from every sheet. They also adjust unqualified references on that same sheet. Unqualified references on other sheets are unaffected.

CopyPaste across sheets. Unqualified references stay unqualified and therefore point at the destination sheet. Qualified references keep their sheet.

## CR1.4 Names
Names are workbook-scoped.

refersTo MUST be sheet-qualified and absolute, e.g. Data!$A$1:$A$10. An unqualified refersTo is qualified with the active sheet when defined.

GetNames() and the script command NAMES return qualified forms.

## CR1.5 File format, version 2
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

Saving always writes version 2.

Loading version 1 migrates it to a single sheet Sheet1. Each name $X$n becomes Sheet1!$X$n. Formula texts are unchanged.

Loading version > 2 → GridlineFormatException containing unsupported version.

Validation: a .gridline file with a duplicate sheet name, an invalid sheet name, zero sheets, or an unknown activeSheet → GridlineFormatException.

## CR1.6 CSV
Csv.Import is unchanged and creates Sheet1.

New: Csv.ImportInto(Workbook wb, string sheetName, Stream source, CsvOptions? options = null).
- It creates a new sheet, appended. An existing name → ArgumentException.
- It is one undo step.

Csv.Export gains an optional string? sheet = null parameter. null means the active sheet.

## CR1.7 CLI and script
import-csv and export-csv accept --sheet <name>.

eval, GET, INPUT, SET, SETX and PASTE accept qualified addresses. Output echoes each address in canonical form, qualified exactly as given.

DUMP follows the GetUsedAddresses rule from CR1.3.

New script commands. Sheet-name arguments use the CR1.2 quoting rules, so quoted names may contain spaces.

| Command | Output |
|---|---|
| ADDSHEET <name> [index] | None |
| RENAMESHEET <old> <new> | None |
| DELETESHEET <name> | None |
| MOVESHEET <name> <index> | None |
| ACTIVATE <name> | None |
| SHEETS | SHEET\t<index>\t<name> per sheet, 0-based, then ACTIVE\t<name> |
| INSERTROWS [<sheet>!]<row> <n> (and DELETEROWS, INSERTCOLS, DELETECOLS) | None. Example: INSERTCOLS 'My Data'!C 2 |

## CR1.8 GUI
A sheet tab strip below the grid (AutomationId SheetTabs). Each tab's AutomationId is SheetTab_<index>.

Clicking a tab activates it. The context menu offers Add, Rename, Delete and Move Left/Right. Dragging reorders tabs.

The name box accepts qualified addresses and switches sheets.

SelectionValue behavior is unchanged.

## CR1.9 Performance
All §12 budgets still apply.

P-8: 10 sheets × 10,000 formulas, each referencing other sheets. RenameSheet on the most-referenced sheet takes ≤ 500 ms.

## CR1.10 Exit criteria
- Same standing requirements as §13.
- Every Phase 1–3 behavior and API member still works unchanged for single-sheet workbooks.
- DECISIONS.md is updated with the CR-1 decisions.
