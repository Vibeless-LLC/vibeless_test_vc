using Gridline.Core.Addressing;

namespace Gridline.Core.Dependencies;

/// <summary>
/// A single-cell precedent with its sheet (CR1.2): <see cref="Sheet"/> is null for an unqualified reference (the
/// formula's own sheet), else the sheet name upper-cased (R20 style), so equal precedents compare equal whatever the casing.
/// </summary>
public readonly record struct CellPrecedent(string? Sheet, CellAddress Address);

/// <summary>
/// A normalised range precedent with its sheet (CR1.2): <see cref="Sheet"/> is null for an unqualified range, else the
/// sheet name upper-cased (R20 style).
/// </summary>
public readonly record struct RangePrecedent(string? Sheet, CellRange Range);
