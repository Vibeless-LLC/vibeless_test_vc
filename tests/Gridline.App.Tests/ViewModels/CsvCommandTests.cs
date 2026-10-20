using System.Text;
using Gridline.App.Services;
using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.IO;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.ViewModels;

/// <summary>File > Import CSV and Export CSV (§11.5, §8.5, R74, D-49) with a fake dialog service: success, cancel, a
/// failure message leaving everything unchanged, the unsaved-changes prompt, an open edit committed first, and the title,
/// dirty state, history and recovery session afterwards.</summary>
public sealed class CsvCommandTests : IDisposable
{
    private readonly string _dir = NewTempDirectory();
    private readonly FakeDialogService _dialogs = new();
    private readonly MainViewModel _vm;

    public CsvCommandTests() => _vm = NewViewModel(_dialogs);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private string WriteCsv(string name, string text)
    {
        string path = PathOf(name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        return path;
    }

    // ---- Import ----

    [Fact]
    public void Import_AdoptsTheCsv_AsUntitled_NotDirty_WithAnEmptyHistory()
    {
        _dialogs.OpenCsvPath = WriteCsv("data.csv", "1,2,=A1+B1\r\nhello,\"a,b\",TRUE\r\n");

        Assert.True(_vm.ImportCsvCommand.CanExecute(null));
        _vm.ImportCsvCommand.Execute(null);

        Assert.Equal(1, _dialogs.OpenCsvDialogs);
        Assert.Empty(_dialogs.Errors);
        Assert.Equal("N:2", _vm.Workbook.GetValue("B1").ToCanonicalString());
        Assert.Equal("T:=A1+B1", _vm.Workbook.GetValue("C1").ToCanonicalString()); // §8.5: a leading = stays text
        Assert.Equal("a,b", _vm.Workbook.GetInput("B2"));
        Assert.Null(_vm.FilePath);
        Assert.False(_vm.IsDirty);
        Assert.False(_vm.Workbook.CanUndo);
        Assert.False(_vm.UndoCommand.CanExecute(null));
        Assert.Equal("Gridline - Untitled", _vm.Title);
        Assert.Equal("A1", _vm.ActiveAddress);
        Assert.Equal("N:1", _vm.SelectionValue);
    }

    [Fact]
    public void Import_FromANamedFile_BecomesUntitled()
    {
        string file = PathOf("a.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "1")]), file);
        Assert.True(_vm.OpenAtStartup(file));
        _dialogs.OpenCsvPath = WriteCsv("b.csv", "9");

        Assert.True(_vm.ImportCsv());

        Assert.Null(_vm.FilePath);
        Assert.Equal("Gridline - Untitled", _vm.Title);
        Assert.Equal("9", _vm.Workbook.GetInput("A1"));
    }

    [Fact]
    public void Import_Cancelled_ChangesNothing()
    {
        _vm.Workbook.SetCell("A1", "keep");
        _vm.Workbook.MarkSaved();
        _dialogs.OpenCsvPath = null;
        Workbook before = _vm.Workbook;

        Assert.False(_vm.ImportCsv());

        Assert.Equal(1, _dialogs.OpenCsvDialogs);
        Assert.Same(before, _vm.Workbook);
        Assert.Empty(_dialogs.Errors);
    }

    [Theory]
    [InlineData("missing.csv", null)]                       // I/O: the file does not exist
    [InlineData("bad.csv", "a,\"unterminated\r\n")]          // format: unterminated quoted field
    public void Import_Failure_ShowsAMessage_AndLeavesTheWorkbookUnchanged(string name, string? content)
    {
        _vm.Workbook.SetCell("A1", "keep");
        Workbook before = _vm.Workbook;
        _dialogs.OpenCsvPath = content is null ? PathOf(name) : WriteCsv(name, content);
        _dialogs.DefaultPromptAnswer = UnsavedChangesChoice.DontSave;

        Assert.False(_vm.ImportCsv());

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains(name, error.Message);
        Assert.Same(before, _vm.Workbook);
        Assert.Equal("keep", _vm.Workbook.GetInput("A1"));
        Assert.True(_vm.IsDirty);
        Assert.Equal("Gridline - Untitled *", _vm.Title);
    }

    [Fact]
    public void Import_OnADirtyWorkbook_Prompts_CancelAborts_BeforeTheDialog()
    {
        _vm.Workbook.SetCell("A1", "x");
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Cancel);
        _dialogs.OpenCsvPath = WriteCsv("d.csv", "1");

        Assert.False(_vm.ImportCsv());

        Assert.Equal(["Untitled"], _dialogs.Prompts);
        Assert.Equal(0, _dialogs.OpenCsvDialogs);
        Assert.Equal("x", _vm.Workbook.GetInput("A1"));
    }

    [Fact]
    public void Import_OnADirtyWorkbook_DontSave_Imports()
    {
        _vm.Workbook.SetCell("A1", "x");
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.DontSave);
        _dialogs.OpenCsvPath = WriteCsv("d.csv", "1");

        Assert.True(_vm.ImportCsv());

        Assert.Equal("1", _vm.Workbook.GetInput("A1"));
        Assert.False(_vm.IsDirty);
    }

    [Fact]
    public void Import_OnADirtyWorkbook_Save_SavesFirst_ThenImports()
    {
        _vm.Workbook.SetCell("A1", "x");
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Save);
        _dialogs.SavePath = PathOf("saved.gridline");
        _dialogs.OpenCsvPath = WriteCsv("d.csv", "1");

        Assert.True(_vm.ImportCsv());

        Assert.Equal("x", GridlineFile.LoadFromPath(PathOf("saved.gridline")).GetInput("A1"));
        Assert.Equal("1", _vm.Workbook.GetInput("A1"));
        Assert.Null(_vm.FilePath);
    }

    [Fact]
    public void Import_CommitsAnOpenEdit_SoItCountsAsAChange()
    {
        _vm.BeginEdit("typed");
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Cancel);

        Assert.False(_vm.ImportCsv());

        Assert.False(_vm.IsEditing);
        Assert.Equal("typed", _vm.Workbook.GetInput("A1"));
        Assert.Single(_dialogs.Prompts);
    }

    [Fact]
    public void Import_DeletesTheRecoveryFile_AsNewDoes()
    {
        string recoveryDir = PathOf("Recovery");
        var store = new DirectoryRecoveryStore(recoveryDir);
        var dialogs = new FakeDialogService { DefaultPromptAnswer = UnsavedChangesChoice.DontSave };
        var vm = new MainViewModel(dialogs, new FakeClipboardService(), store, new FakeAutosaveTimer());
        vm.Workbook.SetCell("A1", "dirty");
        Assert.True(vm.Autosave());
        Assert.True(File.Exists(vm.RecoveryPath));
        dialogs.OpenCsvPath = WriteCsv("r.csv", "1");

        Assert.True(vm.ImportCsv());

        Assert.False(File.Exists(vm.RecoveryPath));
        Assert.False(vm.Autosave()); // clean: nothing to write
        vm.EndSession();
    }

    // ---- Export ----

    [Fact]
    public void Export_WritesTheCsvWithACommaAtomically_AndChangesNoPathTitleOrDirtyState()
    {
        string file = PathOf("book.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "1"), new("B1", "a,b"), new("A2", "=A1*2")]), file);
        Assert.True(_vm.OpenAtStartup(file));
        _vm.Workbook.SetCell("C1", "x"); // dirty
        _dialogs.SaveCsvPath = PathOf("out.csv");

        _vm.ExportCsvCommand.Execute(null);

        Assert.Equal([file], _dialogs.SaveCsvDialogs);
        Assert.Empty(_dialogs.Errors);
        Assert.Equal(CsvFormat.Export(_vm.Workbook, ','), File.ReadAllBytes(PathOf("out.csv")));
        Assert.Equal(file, _vm.FilePath);
        Assert.True(_vm.IsDirty);
        Assert.Equal("Gridline - book.gridline *", _vm.Title);
        Assert.True(_vm.Workbook.CanUndo);
        Assert.Equal("", GridlineFile.LoadFromPath(file).GetInput("C1")); // the .gridline file is untouched
    }

    [Fact]
    public void Export_Untitled_PassesNoPath_AndStaysUntitledAndClean()
    {
        _dialogs.SaveCsvPath = PathOf("e.csv");
        Assert.True(_vm.ExportCsv());
        Assert.Equal([null], _dialogs.SaveCsvDialogs);
        Assert.True(File.Exists(PathOf("e.csv")));
        Assert.Equal("Gridline - Untitled", _vm.Title);
        Assert.False(_vm.IsDirty);
    }

    [Fact]
    public void Export_Cancelled_WritesNothing()
    {
        _dialogs.SaveCsvPath = null;
        Assert.False(_vm.ExportCsv());
        Assert.Single(_dialogs.SaveCsvDialogs);
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void Export_Failure_ShowsAMessage_AndLeavesEverythingUnchanged()
    {
        _vm.Workbook.SetCell("A1", "1");
        _dialogs.SaveCsvPath = PathOf(Path.Combine("no-such-folder", "out.csv"));

        Assert.False(_vm.ExportCsv());

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("out.csv", error.Message);
        Assert.True(_vm.IsDirty);
        Assert.Equal("Gridline - Untitled *", _vm.Title);
        Assert.Null(_vm.FilePath);
        Assert.Equal("1", _vm.Workbook.GetInput("A1"));
    }

    [Fact]
    public void Export_CommitsAnOpenEdit_First_WithoutAPrompt()
    {
        _vm.BeginEdit("typed");
        _dialogs.SaveCsvPath = PathOf("t.csv");

        Assert.True(_vm.ExportCsv());

        Assert.Empty(_dialogs.Prompts);
        Assert.False(_vm.IsEditing);
        byte[] expected = [.. Encoding.UTF8.Preamble, .. "typed\r\n"u8]; // §8.5: BOM, CRLF
        Assert.Equal(expected, File.ReadAllBytes(PathOf("t.csv")));
    }
}
