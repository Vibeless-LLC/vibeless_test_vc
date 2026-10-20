using System.Text;
using Gridline.Core;

namespace Gridline.IO.Tests;

/// <summary>Atomic path-based save and load (§8.4).</summary>
public sealed class GridlineFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridline-io-" + Guid.NewGuid().ToString("N"));

    public GridlineFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (Exception) { }
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Workbook Sample()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "10");
        wb.SetCell("B1", "=A1*2");
        return wb;
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Save_NewPath_WritesCanonicalBytes()
    {
        var wb = Sample();
        GridlineFile.SaveToPath(wb, P("new.gridline"));
        Assert.Equal(GridlineSerializer.Serialize(wb), File.ReadAllBytes(P("new.gridline")));
        Assert.Equal("new.gridline", Path.GetFileName(Assert.Single(Directory.GetFiles(_dir))));
    }

    [Fact]
    public void Save_ReplacesExistingFile()
    {
        File.WriteAllText(P("a.gridline"), "old content");
        var wb = Sample();
        GridlineFile.SaveToPath(wb, P("a.gridline"));
        Assert.Equal(GridlineSerializer.Serialize(wb), File.ReadAllBytes(P("a.gridline")));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Save_FailureBeforeReplace_LeavesOriginalIntact()
    {
        var original = Encoding.UTF8.GetBytes("original bytes");
        File.WriteAllBytes(P("a.gridline"), original);
        var ex = Assert.Throws<GridlineIOException>(() =>
            GridlineFile.SaveToPath(Sample(), P("a.gridline"), beforeReplace: _ => throw new IOException("boom")));
        Assert.IsType<IOException>(ex.InnerException);
        Assert.Equal(original, File.ReadAllBytes(P("a.gridline")));
    }

    [Fact]
    public void Save_FailureBeforeReplace_NonIoExceptionPropagatesUnchanged_AfterCleanup()
    {
        File.WriteAllText(P("a.gridline"), "x");
        Assert.Throws<InvalidOperationException>(() =>
            GridlineFile.SaveToPath(Sample(), P("a.gridline"), beforeReplace: _ => throw new InvalidOperationException("boom")));
        Assert.Equal("x", File.ReadAllText(P("a.gridline")));
        Assert.Equal("a.gridline", Path.GetFileName(Assert.Single(Directory.GetFiles(_dir))));
    }

    [Fact]
    public void Save_PathEndingInSeparator_ThrowsGridlineIOException_BeforeCreatingTemp()
    {
        var ex = Assert.Throws<GridlineIOException>(() => GridlineFile.SaveToPath(Sample(), _dir + Path.DirectorySeparatorChar));
        Assert.NotNull(ex.InnerException);
        ex = Assert.Throws<GridlineIOException>(() => GridlineFile.SaveToPath(Sample(), P("x") + Path.AltDirectorySeparatorChar));
        Assert.NotNull(ex.InnerException);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Save_LongTargetName_StillSaves()
    {
        string name = new string('n', 252) + ".gl"; // 255 chars, the usual per-component limit
        var wb = Sample();
        GridlineFile.SaveToPath(wb, P(name));
        Assert.Equal(GridlineSerializer.Serialize(wb), File.ReadAllBytes(P(name)));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Save_ReadOnlyTarget_ThrowsGridlineIOException_AndLeavesOriginalIntact()
    {
        File.WriteAllText(P("ro.gridline"), "original");
        File.SetAttributes(P("ro.gridline"), FileAttributes.ReadOnly);
        var ex = Assert.Throws<GridlineIOException>(() => GridlineFile.SaveToPath(Sample(), P("ro.gridline")));
        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
        Assert.Equal("original", File.ReadAllText(P("ro.gridline")));
        Assert.Equal("ro.gridline", Path.GetFileName(Assert.Single(Directory.GetFiles(_dir))));
    }

    [Fact]
    public void Save_TempFileIsInTargetDirectory_AndFlushedBeforeReplace()
    {
        var wb = Sample();
        string? tmp = null;
        byte[]? tmpBytes = null;
        GridlineFile.SaveToPath(wb, P("a.gridline"), beforeReplace: t =>
        {
            tmp = t;
            tmpBytes = File.ReadAllBytes(t);
        });
        Assert.NotNull(tmp);
        Assert.Equal(Path.GetFullPath(_dir), Path.GetDirectoryName(Path.GetFullPath(tmp!)));
        Assert.EndsWith(".tmp", tmp);
        Assert.Equal(GridlineSerializer.Serialize(wb), tmpBytes);
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void Save_FailureBeforeReplace_BestEffortRemovesTempFile()
    {
        Assert.Throws<GridlineIOException>(() =>
            GridlineFile.SaveToPath(Sample(), P("a.gridline"), beforeReplace: _ => throw new IOException()));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Save_MissingDirectory_ThrowsGridlineIOException()
    {
        var ex = Assert.Throws<GridlineIOException>(() => GridlineFile.SaveToPath(Sample(), Path.Combine(_dir, "nope", "a.gridline")));
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Save_TargetIsDirectory_ThrowsGridlineIOException()
    {
        Directory.CreateDirectory(P("d"));
        var ex = Assert.Throws<GridlineIOException>(() => GridlineFile.SaveToPath(Sample(), P("d")));
        Assert.NotNull(ex.InnerException);
        Assert.True(Directory.Exists(P("d")));
    }

    [Fact]
    public void Save_ArgumentProblems_AreArgumentExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => GridlineFile.SaveToPath(null!, P("a")));
        Assert.Throws<ArgumentNullException>(() => GridlineFile.SaveToPath(Sample(), null!));
        Assert.Throws<ArgumentException>(() => GridlineFile.SaveToPath(Sample(), ""));
    }

    [Fact]
    public void Load_RoundTrip()
    {
        GridlineFile.SaveToPath(Sample(), P("a.gridline"));
        var wb = GridlineFile.LoadFromPath(P("a.gridline"));
        Assert.Equal("=A1*2", wb.GetInput("B1"));
        Assert.Equal(20.0, wb.GetValue("B1").Number);
    }

    // §9.3, §8.2: a freshly loaded workbook is clean with an empty history, from a path or from bytes.
    [Fact]
    public void Load_IsCleanWithEmptyHistory()
    {
        var saved = Sample();
        Assert.True(saved.IsDirty);
        GridlineFile.SaveToPath(saved, P("a.gridline"));
        foreach (var wb in new[] { GridlineFile.LoadFromPath(P("a.gridline")), GridlineSerializer.Deserialize(GridlineSerializer.Serialize(saved)) })
        {
            Assert.False(wb.IsDirty);
            Assert.False(wb.CanUndo);
            Assert.False(wb.CanRedo);
            Assert.False(wb.Undo());
            wb.SetCell("A1", "1");
            Assert.True(wb.IsDirty);
            Assert.True(wb.Undo());
            Assert.False(wb.IsDirty);
            Assert.Equal("10", wb.GetInput("A1"));
        }
    }

    [Fact]
    public void Load_MissingFile_ThrowsGridlineIOException()
    {
        var ex = Assert.Throws<GridlineIOException>(() => GridlineFile.LoadFromPath(P("missing.gridline")));
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
    }

    [Fact]
    public void Load_MissingDirectory_ThrowsGridlineIOException() =>
        Assert.Throws<GridlineIOException>(() => GridlineFile.LoadFromPath(Path.Combine(_dir, "nope", "a.gridline")));

    [Fact]
    public void Load_DirectoryPath_ThrowsGridlineIOException() =>
        Assert.Throws<GridlineIOException>(() => GridlineFile.LoadFromPath(_dir));

    [Fact]
    public void Load_MalformedFile_ThrowsGridlineFormatException()
    {
        File.WriteAllText(P("bad.gridline"), "{ not json");
        Assert.Throws<GridlineFormatException>(() => GridlineFile.LoadFromPath(P("bad.gridline")));
    }

    [Fact]
    public void Load_ArgumentProblems_AreArgumentExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => GridlineFile.LoadFromPath(null!));
        Assert.Throws<ArgumentException>(() => GridlineFile.LoadFromPath(""));
    }

    [Fact]
    public void IOException_IsDistinctFromFormatException()
    {
        Assert.False(typeof(GridlineIOException).IsAssignableTo(typeof(GridlineFormatException)));
        Assert.False(typeof(GridlineFormatException).IsAssignableTo(typeof(GridlineIOException)));
    }

    [Fact]
    public void Loader_CellsRejectedByFromCells_StillFormatError()
    {
        // A cell whose formula text is fine but address is out of grid is caught by the loader's own checks;
        // a duplicate canonical address is rejected by FromCells (ArgumentException) and must map to a format error.
        var json = "{\"format\":\"gridline\",\"version\":1,\"names\":{},\"cells\":[[\"A1\",\"1\"],[\"A1\",\"2\"]]}";
        Assert.Throws<GridlineFormatException>(() => GridlineSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }
}
