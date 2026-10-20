using Gridline.Core.Addressing;
using Gridline.Core.Commands;

namespace Gridline.Core.Tests.Commands;

// Requirement IDs: §9.5 (the reference adjustment table), R61 (insert clamping), §5.5 (#REF! for invalid references).
public class StructureRewriterTests
{
    private static StructureRewriter Rows(bool insert, int start, int count) => new(StructureAxis.Rows, insert, start, count);
    private static StructureRewriter Columns(bool insert, int start, int count) => new(StructureAxis.Columns, insert, start, count);

    [Theory]
    [InlineData(true, 3, 2, 2, 2)]
    [InlineData(true, 3, 2, 3, 5)]
    [InlineData(true, 3, 2, 99998, 100000)]
    [InlineData(true, 3, 2, 99999, -1)]
    [InlineData(true, 1, int.MaxValue, 1, -1)]
    [InlineData(false, 3, 2, 2, 2)]
    [InlineData(false, 3, 2, 3, -1)]
    [InlineData(false, 3, 2, 4, -1)]
    [InlineData(false, 3, 2, 5, 3)]
    [InlineData(false, 1, 100000, 100000, -1)]
    public void MapPosition_FollowsTheSingleCellRule(bool insert, int start, int count, int x, int expected) =>
        Assert.Equal(expected, Rows(insert, start, count).MapPosition(x));

    [Theory]
    [InlineData(true, 3, 2, 1, 5, 1, 7)]
    [InlineData(true, 1, 2, 1, 5, 3, 7)]
    [InlineData(true, 6, 2, 1, 5, 1, 5)]
    [InlineData(true, 1, 3, 99996, 99999, 99999, 100000)] // R61 clamp
    [InlineData(true, 1, 2, 99999, 100000, -1, -1)]       // R61 start passes
    [InlineData(false, 3, 2, 1, 10, 1, 8)]
    [InlineData(false, 1, 2, 1, 10, 1, 8)]
    [InlineData(false, 9, 5, 1, 10, 1, 8)]
    [InlineData(false, 3, 2, 3, 4, -1, -1)]
    [InlineData(false, 2, 4, 3, 4, -1, -1)]
    [InlineData(false, 11, 1, 1, 10, 1, 10)]
    public void MapSpan_FollowsTheRangeRule(bool insert, int start, int count, int lo, int hi, int newLo, int newHi)
    {
        var span = Rows(insert, start, count).MapSpan(lo, hi);
        if (newLo < 0) Assert.Null(span);
        else Assert.Equal((newLo, newHi), span);
    }

    [Fact]
    public void Columns_UseTheColumnBound()
    {
        Assert.Equal(-1, Columns(true, 1, 1).MapPosition(702));
        Assert.Equal(702, Columns(true, 1, 1).MapPosition(701));
        Assert.Equal((700, 702), Columns(true, 1, 3).MapSpan(697, 700));
        Assert.Null(Columns(true, 1, 2).MapSpan(701, 702));
        Assert.Equal(new CellAddress(5, 9), Columns(true, 1, 1).With(new CellAddress(2, 9), 5));
    }

    [Fact]
    public void UnmapPosition_InvertsTheMove()
    {
        var insert = Rows(true, 3, 2);
        var delete = Rows(false, 3, 2);
        for (int x = 1; x <= 20; x++)
        {
            int moved = insert.MapPosition(x);
            Assert.Equal(x, insert.UnmapPosition(moved));
            int kept = delete.MapPosition(x);
            if (kept > 0) Assert.Equal(x, delete.UnmapPosition(kept));
        }
    }

    [Fact]
    public void Changed_TracksWhetherAnyReferenceDiffers()
    {
        var map = Rows(true, 5, 1);
        var a1 = CellReference.Parse("$A1");
        Assert.Equal(a1, map.Rewrite(a1));
        Assert.Equal((a1, CellReference.Parse("B4")), map.RewriteRange(a1, CellReference.Parse("B4")));
        Assert.False(map.Changed);
        Assert.Equal(CellReference.Parse("A$6"), map.Rewrite(CellReference.Parse("A$5")));
        Assert.True(map.Changed);
        map.Reset();
        Assert.False(map.Changed);
        Assert.Null(Rows(false, 5, 1).RewriteRange(CellReference.Parse("A5"), CellReference.Parse("$B$5")));
        Assert.Equal((CellReference.Parse("$C$6"), CellReference.Parse("A1")),
            map.RewriteRange(CellReference.Parse("$C$5"), CellReference.Parse("A1"))); // bottom-up kept
        Assert.True(map.Changed);
    }

    [Fact]
    public void InvalidParameters_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rows(true, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rows(true, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rows(false, 2, 100000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Columns(true, 703, 1));
    }
}
