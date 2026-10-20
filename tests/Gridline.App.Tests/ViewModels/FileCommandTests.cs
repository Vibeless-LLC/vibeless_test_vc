using Gridline.App.Services;
using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.IO;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.ViewModels;

/// <summary>§11.5 file commands with a fake dialog service: New, Open, Save, Save As, Exit, the R45 prompt's three
/// outcomes, failures leaving everything unchanged, and the §11.1 title following IsDirty.</summary>
public sealed class FileCommandTests : IDisposable
{
    private readonly string _dir = NewTempDirectory();
    private readonly FakeDialogService _dialogs = new();
    private readonly MainViewModel _vm;

    public FileCommandTests() => _vm = NewViewModel(_dialogs);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private string WriteFile(string name, params (string Address, string Input)[] cells)
    {
        string path = PathOf(name);
        GridlineFile.SaveToPath(Workbook.FromCells(cells.Select(c => new KeyValuePair<string, string>(c.Address, c.Input))), path);
        return path;
    }

    private int Closes()
    {
        int closes = 0;
        _vm.CloseRequested += (_, _) => closes++;
        _vm.ExitCommand.Execute(null);
        return closes;
    }

    // ---- Save and Save As ----

    [Fact]
    public void Save_Untitled_ActsAsSaveAs_WritesMarksSavedAndShowsTheFileName()
    {
        _vm.Workbook.SetCell("A1", "=1+2");
        Assert.Equal("Gridline - Untitled *", _vm.Title);
        _dialogs.SavePath = PathOf("book.gridline");
        var titles = new List<string>();
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Title)) titles.Add(_vm.Title); };

        _vm.SaveCommand.Execute(null);

        Assert.Equal([null], _dialogs.SaveDialogs);
        Assert.Empty(_dialogs.Errors);
        Assert.False(_vm.Workbook.IsDirty);
        Assert.Equal(PathOf("book.gridline"), _vm.FilePath);
        Assert.Equal("Gridline - book.gridline", _vm.Title);
        Assert.Contains("Gridline - book.gridline", titles);
        Assert.Equal("=1+2", GridlineFile.LoadFromPath(PathOf("book.gridline")).GetInput("A1"));
    }

    [Fact]
    public void Save_WithAPath_SavesThereWithoutADialog()
    {
        string path = WriteFile("a.gridline", ("A1", "1"));
        Assert.True(_vm.OpenAtStartup(path));
        _vm.Workbook.SetCell("A1", "2");

        Assert.True(_vm.Save());
        Assert.Empty(_dialogs.SaveDialogs);
        Assert.Equal("Gridline - a.gridline", _vm.Title);
        Assert.Equal("2", GridlineFile.LoadFromPath(path).GetInput("A1"));
    }

    [Fact]
    public void SaveAs_AlwaysAsks_StartsAtTheCurrentPath_AndUpdatesTheTitle()
    {
        string path = WriteFile("a.gridline", ("A1", "1"));
        _vm.OpenAtStartup(path);
        _dialogs.SavePath = PathOf("b.gridline");

        _vm.SaveAsCommand.Execute(null);

        Assert.Equal([path], _dialogs.SaveDialogs);
        Assert.Equal(PathOf("b.gridline"), _vm.FilePath);
        Assert.Equal("Gridline - b.gridline", _vm.Title);
        Assert.True(File.Exists(PathOf("b.gridline")));
    }

    [Fact]
    public void SaveAs_Cancelled_ChangesNothing()
    {
        _vm.Workbook.SetCell("A1", "1");
        _dialogs.SavePath = null;
        Assert.False(_vm.SaveAs());
        Assert.True(_vm.Workbook.IsDirty);
        Assert.Null(_vm.FilePath);
        Assert.Equal("Gridline - Untitled *", _vm.Title);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void Save_Failure_ShowsAnError_AndKeepsPathAndDirtyState()
    {
        string path = WriteFile("a.gridline", ("A1", "1"));
        _vm.OpenAtStartup(path);
        _vm.Workbook.SetCell("A1", "2");
        _dialogs.SavePath = PathOf("missing-dir") + Path.DirectorySeparatorChar + "x.gridline"; // the directory does not exist

        Assert.False(_vm.SaveAs());

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("x.gridline", error.Message);
        Assert.Equal(path, _vm.FilePath);
        Assert.True(_vm.Workbook.IsDirty);
        Assert.Equal("Gridline - a.gridline *", _vm.Title);
        Assert.Equal("1", GridlineFile.LoadFromPath(path).GetInput("A1"));
    }

    [Fact]
    public void Save_CommitsAnOpenEditFirst()
    {
        _dialogs.SavePath = PathOf("e.gridline");
        _vm.BeginEdit("typed");
        Assert.True(_vm.Save());
        Assert.False(_vm.IsEditing);
        Assert.Equal("typed", GridlineFile.LoadFromPath(PathOf("e.gridline")).GetInput("A1"));
        Assert.False(_vm.IsDirty);
    }

    // ---- Title tracking IsDirty ----

    [Fact]
    public void Title_AsteriskFollowsIsDirty_ThroughSaveUndoAndRedo()
    {
        _dialogs.SavePath = PathOf("t.gridline");
        _vm.Workbook.SetCell("A1", "1");
        _vm.Save();
        Assert.Equal("Gridline - t.gridline", _vm.Title);

        _vm.Workbook.SetCell("A1", "2");
        Assert.Equal("Gridline - t.gridline *", _vm.Title);
        _vm.UndoCommand.Execute(null); // back to the saved position: clean again
        Assert.Equal("Gridline - t.gridline", _vm.Title);
        Assert.False(_vm.IsDirty);
        _vm.UndoCommand.Execute(null); // before the saved position: dirty
        Assert.Equal("Gridline - t.gridline *", _vm.Title);
        _vm.RedoCommand.Execute(null);
        Assert.Equal("Gridline - t.gridline", _vm.Title);
        _vm.RedoCommand.Execute(null);
        Assert.Equal("Gridline - t.gridline *", _vm.Title);
    }

    // ---- Open ----

    [Fact]
    public void Open_Clean_LoadsWithoutPrompting()
    {
        string path = WriteFile("o.gridline", ("C3", "hello"));
        _dialogs.OpenPath = path;

        _vm.OpenCommand.Execute(null);

        Assert.Empty(_dialogs.Prompts);
        Assert.Equal(1, _dialogs.OpenDialogs);
        Assert.Equal("hello", _vm.Workbook.GetInput("C3"));
        Assert.Equal("Gridline - o.gridline", _vm.Title);
    }

    [Fact]
    public void Open_DialogCancelled_ChangesNothing()
    {
        _vm.Workbook.SetCell("A1", "1");
        var before = _vm.Workbook;
        _dialogs.DefaultPromptAnswer = UnsavedChangesChoice.DontSave;
        _dialogs.OpenPath = null;
        Assert.False(_vm.Open());
        Assert.Same(before, _vm.Workbook);
        Assert.Equal("Gridline - Untitled *", _vm.Title);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    public void Open_Failure_ShowsAnError_AndKeepsTheCurrentWorkbookAndPath(string kind)
    {
        string current = WriteFile("current.gridline", ("A1", "1"));
        _vm.OpenAtStartup(current);
        _vm.Workbook.SetCell("A1", "2");
        var before = _vm.Workbook;
        string bad = PathOf("bad.gridline");
        if (kind == "corrupt") File.WriteAllText(bad, "{ nope");
        _dialogs.OpenPath = bad;
        _dialogs.DefaultPromptAnswer = UnsavedChangesChoice.DontSave;

        Assert.False(_vm.Open());

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains(bad, error.Message);
        Assert.Same(before, _vm.Workbook);
        Assert.Equal(current, _vm.FilePath);
        Assert.Equal("Gridline - current.gridline *", _vm.Title);
    }

    // ---- The prompt's three outcomes (R45) on New, Open and Exit ----

    [Fact]
    public void New_Dirty_Cancel_KeepsTheWorkbook()
    {
        _vm.Workbook.SetCell("A1", "1");
        var before = _vm.Workbook;
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Cancel);
        Assert.False(_vm.New());
        Assert.Equal(["Untitled"], _dialogs.Prompts);
        Assert.Same(before, _vm.Workbook);
    }

    [Fact]
    public void New_Dirty_DontSave_DiscardsWithoutSaving()
    {
        _vm.Workbook.SetCell("A1", "1");
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.DontSave);
        _dialogs.SavePath = PathOf("never.gridline");
        Assert.True(_vm.New());
        Assert.Empty(_vm.Workbook.GetUsedAddresses());
        Assert.Equal("Gridline - Untitled", _vm.Title);
        Assert.Empty(_dialogs.SaveDialogs);
        Assert.False(File.Exists(PathOf("never.gridline")));
    }

    [Fact]
    public void New_Dirty_Save_SavesThenReplaces()
    {
        string path = WriteFile("s.gridline", ("A1", "1"));
        _vm.OpenAtStartup(path);
        _vm.Workbook.SetCell("A1", "2");
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Save);

        Assert.True(_vm.New());

        Assert.Equal(["s.gridline"], _dialogs.Prompts);
        Assert.Equal("2", GridlineFile.LoadFromPath(path).GetInput("A1"));
        Assert.Equal("Gridline - Untitled", _vm.Title);
        Assert.Null(_vm.FilePath);
    }

    [Fact]
    public void New_Dirty_SaveOnUntitled_CancelledSaveAs_Aborts()
    {
        _vm.Workbook.SetCell("A1", "1");
        var before = _vm.Workbook;
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Save);
        _dialogs.SavePath = null; // the Save As dialog is cancelled
        Assert.False(_vm.New());
        Assert.Single(_dialogs.SaveDialogs);
        Assert.Same(before, _vm.Workbook);
        Assert.True(_vm.IsDirty);
    }

    [Fact]
    public void Open_Dirty_FailedSave_AbortsBeforeTheOpenDialog()
    {
        _vm.Workbook.SetCell("A1", "1");
        var before = _vm.Workbook;
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Save);
        _dialogs.SavePath = Path.Combine(PathOf("no-such-dir"), "x.gridline");
        _dialogs.OpenPath = WriteFile("other.gridline", ("A1", "9"));

        Assert.False(_vm.Open());

        Assert.Single(_dialogs.Errors); // the save error
        Assert.Equal(0, _dialogs.OpenDialogs);
        Assert.Same(before, _vm.Workbook);
        Assert.True(_vm.IsDirty);
    }

    [Fact]
    public void Open_Dirty_Save_SavesThenOpens()
    {
        _vm.Workbook.SetCell("A1", "1");
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Save);
        _dialogs.SavePath = PathOf("mine.gridline");
        _dialogs.OpenPath = WriteFile("other.gridline", ("A1", "9"));

        Assert.True(_vm.Open());

        Assert.Equal("1", GridlineFile.LoadFromPath(PathOf("mine.gridline")).GetInput("A1"));
        Assert.Equal("9", _vm.Workbook.GetInput("A1"));
        Assert.Equal("Gridline - other.gridline", _vm.Title);
    }

    [Fact]
    public void Exit_Clean_ClosesWithoutPrompting()
    {
        Assert.Equal(1, Closes());
        Assert.Empty(_dialogs.Prompts);
    }

    [Theory]
    [InlineData(UnsavedChangesChoice.Cancel, 0)]
    [InlineData(UnsavedChangesChoice.DontSave, 1)]
    public void Exit_Dirty_FollowsThePrompt(UnsavedChangesChoice answer, int expectedCloses)
    {
        _vm.Workbook.SetCell("A1", "1");
        _dialogs.PromptAnswers.Enqueue(answer);
        Assert.Equal(expectedCloses, Closes());
        Assert.Single(_dialogs.Prompts);
        Assert.True(_vm.IsDirty);
    }

    [Fact]
    public void Exit_Dirty_Save_SavesThenCloses_FailedSaveKeepsItOpen()
    {
        _vm.Workbook.SetCell("A1", "1");
        _dialogs.DefaultPromptAnswer = UnsavedChangesChoice.Save;
        _dialogs.SavePath = Path.Combine(PathOf("no-such-dir"), "x.gridline");
        Assert.Equal(0, Closes()); // the save failed
        Assert.Single(_dialogs.Errors);

        _dialogs.SavePath = PathOf("exit.gridline");
        int closes = 0;
        _vm.CloseRequested += (_, _) => closes++;
        _vm.ExitCommand.Execute(null);
        Assert.Equal(1, closes);
        Assert.True(File.Exists(PathOf("exit.gridline")));
    }

    [Fact]
    public void ConfirmClose_IsTheCloseButtonsExit()
    {
        Assert.True(_vm.ConfirmClose()); // clean
        _vm.Workbook.SetCell("A1", "1");
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.Cancel);
        _dialogs.PromptAnswers.Enqueue(UnsavedChangesChoice.DontSave);
        Assert.False(_vm.ConfirmClose());
        Assert.True(_vm.ConfirmClose());
        Assert.Equal(2, _dialogs.Prompts.Count);
    }
}
