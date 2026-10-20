using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.IO;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.ViewModels;

/// <summary>§11.1 title: <c>Gridline - &lt;file name or Untitled&gt;</c>, with <c> *</c> when dirty; R44 startup open.</summary>
public class TitleTests
{
    [Fact]
    public void NewViewModel_IsUntitledAndClean()
    {
        var vm = NewViewModel();
        Assert.Equal("Gridline - Untitled", vm.Title);
        Assert.False(vm.IsDirty);
        Assert.Null(vm.FilePath);
    }

    [Fact]
    public void Edit_AppendsAsterisk_UndoRemovesIt()
    {
        var vm = NewViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Workbook.SetCell("A1", "5");
        Assert.Equal("Gridline - Untitled *", vm.Title);
        Assert.Contains(nameof(MainViewModel.Title), changed);

        vm.UndoCommand.Execute(null);
        Assert.Equal("Gridline - Untitled", vm.Title);
        vm.RedoCommand.Execute(null);
        Assert.Equal("Gridline - Untitled *", vm.Title);
    }

    [Fact]
    public void OpenAtStartup_ShowsFileName_ThenAsteriskWhenDirty()
    {
        string dir = NewTempDirectory();
        try
        {
            string path = Path.Combine(dir, "budget.gridline");
            GridlineFile.SaveToPath(Workbook.FromCells([new("B2", "7")]), path);
            var dialogs = new FakeDialogService();
            var vm = new MainViewModel(dialogs);

            Assert.True(vm.OpenAtStartup(path));
            Assert.Empty(dialogs.Errors);
            Assert.Equal("Gridline - budget.gridline", vm.Title);
            Assert.Equal(Path.GetFullPath(path), vm.FilePath);
            Assert.Equal("7", vm.Workbook.GetInput("B2"));

            vm.Workbook.SetCell("A1", "x");
            Assert.Equal("Gridline - budget.gridline *", vm.Title);
            vm.Workbook.MarkSaved();
            vm.Workbook.SetCell("A1", "y");
            vm.UndoCommand.Execute(null); // back to the marked position
            Assert.Equal("Gridline - budget.gridline", vm.Title);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("directory")]
    public void OpenAtStartup_Failure_ShowsErrorAndStaysUntitled(string kind)
    {
        string dir = NewTempDirectory();
        try
        {
            string path = Path.Combine(dir, "bad.gridline");
            if (kind == "corrupt") File.WriteAllText(path, "{ not json");
            if (kind == "directory") Directory.CreateDirectory(path);
            var dialogs = new FakeDialogService();
            var vm = new MainViewModel(dialogs);

            Assert.False(vm.OpenAtStartup(path));
            var error = Assert.Single(dialogs.Errors);
            Assert.Contains(path, error.Message);
            Assert.Equal("Gridline - Untitled", vm.Title);
            Assert.Null(vm.FilePath);
            Assert.Empty(vm.Workbook.GetUsedAddresses());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void New_ReplacesWorkbookAndResetsSelection()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("C3", "1");
        vm.SelectCell(Core.Addressing.CellAddress.Parse("C3"), extend: false);
        int contentChanges = 0;
        vm.ContentChanged += (_, _) => contentChanges++;

        vm.NewCommand.Execute(null);
        Assert.Equal("Gridline - Untitled", vm.Title);
        Assert.Empty(vm.Workbook.GetUsedAddresses());
        Assert.Equal("A1", vm.ActiveAddress);
        Assert.Equal(1, contentChanges);

        vm.Workbook.SetCell("A1", "2"); // the new workbook is observed
        Assert.Equal("Gridline - Untitled *", vm.Title);
    }

    [Fact]
    public void Exit_RaisesCloseRequested()
    {
        var vm = NewViewModel();
        int closes = 0;
        vm.CloseRequested += (_, _) => closes++;
        vm.ExitCommand.Execute(null);
        Assert.Equal(1, closes);
    }
}
