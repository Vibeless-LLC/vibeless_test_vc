namespace Gridline.Core;

/// <summary>
/// The classification of raw cell input (§4.3). <see cref="Value"/> is the constant value for Blank, Number,
/// Text and Boolean (Blank for Formula); <see cref="FormulaText"/> is the text after '=' for Formula, else empty.
/// </summary>
public readonly record struct CellInputResult(CellInputKind Kind, CellValue Value, string FormulaText = "");
