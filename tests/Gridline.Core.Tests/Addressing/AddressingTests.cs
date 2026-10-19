using Gridline.Core.Addressing;

namespace Gridline.Core.Tests.Addressing;

// Requirement IDs: §4.2 (address grammar), §4.3 (grid bounds).
public class AddressingTests
{
    [Fact]
    public void Bounds_AreExpected()
    {
        Assert.Equal(702, GridBounds.MaxColumns);
        Assert.Equal(100000, GridBounds.MaxRows);
    }

    [Theory]
    [InlineData("a1", 1, 1, "A1")]
    [InlineData("A1", 1, 1, "A1")]
    [InlineData("$B$7", 2, 7, "B7")]
    [InlineData("$b7", 2, 7, "B7")]
    [InlineData("b$7", 2, 7, "B7")]
    [InlineData("zz100000", 702, 100000, "ZZ100000")]
    [InlineData("AA10", 27, 10, "AA10")]
    [InlineData("Z1", 26, 1, "Z1")]
    public void Address_Parses(string text, int col, int row, string canonical)
    {
        Assert.True(CellAddress.TryParse(text, out var a));
        Assert.Equal(new CellAddress(col, row), a);
        Assert.Equal(canonical, a.ToString());
        Assert.Equal(a, CellAddress.Parse(text));
    }

    [Theory]
    [InlineData("A0")]
    [InlineData("AAA1")]
    [InlineData("A100001")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("$")]
    [InlineData("$$A1")]
    [InlineData("A$")]
    [InlineData("A")]
    [InlineData("1")]
    [InlineData("1A")]
    [InlineData("A1 ")]
    [InlineData(" A1")]
    [InlineData("A-1")]
    [InlineData("A1:B2")]
    [InlineData("A01")]
    [InlineData("A1.5")]
    [InlineData("Ä1")]
    [InlineData("A99999999999")]
    public void Address_Rejects(string text)
    {
        Assert.False(CellAddress.TryParse(text, out _));
        Assert.Throws<ArgumentException>(() => CellAddress.Parse(text));
    }

    [Fact]
    public void Address_NullRejected()
    {
        Assert.False(CellAddress.TryParse(null, out _));
        Assert.Throws<ArgumentException>(() => CellAddress.Parse(null!));
    }

    [Theory]
    [InlineData("A1", false, false)]
    [InlineData("$A1", true, false)]
    [InlineData("A$1", false, true)]
    [InlineData("$A$1", true, true)]
    public void Reference_KeepsAbsoluteFlags(string text, bool colAbs, bool rowAbs)
    {
        Assert.True(CellReference.TryParse(text, out var r));
        Assert.Equal(new CellAddress(1, 1), r.Address);
        Assert.Equal(colAbs, r.ColumnAbsolute);
        Assert.Equal(rowAbs, r.RowAbsolute);
        Assert.Equal(text, r.ToString());
    }

    [Fact]
    public void Reference_ToStringUppercases_AndRejectsBad()
    {
        Assert.Equal("$B$7", CellReference.Parse("$b$7").ToString());
        Assert.False(CellReference.TryParse("$", out _));
        Assert.Throws<ArgumentException>(() => CellReference.Parse("A0"));
    }

    [Theory]
    [InlineData("A", 1)]
    [InlineData("Z", 26)]
    [InlineData("AA", 27)]
    [InlineData("AZ", 52)]
    [InlineData("BA", 53)]
    [InlineData("ZZ", 702)]
    [InlineData("zz", 702)]
    public void Column_LettersToNumber(string letters, int n)
    {
        Assert.True(ColumnName.TryParse(letters, out var c));
        Assert.Equal(n, c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAA")]
    [InlineData("A1")]
    [InlineData("$A")]
    public void Column_LettersRejected(string letters)
        => Assert.False(ColumnName.TryParse(letters, out _));

    [Fact]
    public void Column_RoundTripsAll()
    {
        for (int i = 1; i <= GridBounds.MaxColumns; i++)
        {
            Assert.True(ColumnName.TryParse(ColumnName.FromNumber(i), out var back));
            Assert.Equal(i, back);
        }
        Assert.Equal("ZZ", ColumnName.FromNumber(702));
        Assert.Equal("AA", ColumnName.FromNumber(27));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(703)]
    [InlineData(-1)]
    public void Column_FromNumberOutOfRangeThrows(int n)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ColumnName.FromNumber(n));

    [Fact]
    public void Address_ConstructorValidates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellAddress(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellAddress(703, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellAddress(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellAddress(1, 100001));
        Assert.Throws<ArgumentOutOfRangeException>(() => default(CellAddress).ToString());
    }

    [Theory]
    [InlineData("A1:B3", "A1:B3")]
    [InlineData("B3:A1", "A1:B3")]
    [InlineData("b1:a3", "A1:B3")]
    [InlineData("$A$1:$B$3", "A1:B3")]
    [InlineData("C1:A5", "A1:C5")]
    [InlineData("A5:C1", "A1:C5")]
    [InlineData("A1:A1", "A1:A1")]
    [InlineData("ZZ100000:A1", "A1:ZZ100000")]
    public void Range_ParsesAndNormalises(string text, string canonical)
    {
        Assert.True(CellRange.TryParse(text, out var r));
        Assert.Equal(canonical, r.ToString());
        Assert.Equal(r, CellRange.Parse(text));
        Assert.True(r.TopLeft.Column <= r.BottomRight.Column);
        Assert.True(r.TopLeft.Row <= r.BottomRight.Row);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A1")]
    [InlineData("A1:")]
    [InlineData(":B2")]
    [InlineData("A1:B2:C3")]
    [InlineData("A1: B2")]
    [InlineData(" A1:B2")]
    [InlineData("A0:B2")]
    [InlineData("A1:AAA2")]
    [InlineData("A1:B100001")]
    [InlineData("$:$")]
    public void Range_Rejects(string text)
    {
        Assert.False(CellRange.TryParse(text, out _));
        Assert.Throws<ArgumentException>(() => CellRange.Parse(text));
    }

    [Fact]
    public void Range_NullRejected()
    {
        Assert.False(CellRange.TryParse(null, out _));
        Assert.Throws<ArgumentException>(() => CellRange.Parse(null!));
    }

    [Fact]
    public void Range_ConstructorNormalises_AndContains()
    {
        var r = new CellRange(new CellAddress(2, 3), new CellAddress(1, 1));
        Assert.Equal(new CellAddress(1, 1), r.TopLeft);
        Assert.Equal(new CellAddress(2, 3), r.BottomRight);
        Assert.True(r.Contains(new CellAddress(2, 2)));
        Assert.False(r.Contains(new CellAddress(3, 2)));
        Assert.Equal(6L, r.CellCount);
    }
}
