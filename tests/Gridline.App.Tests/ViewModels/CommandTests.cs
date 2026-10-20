using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.ViewModels;

/// <summary>§11.5 commands: Undo/Redo follow CanUndo/CanRedo now; file and clipboard commands are Task 8 stubs.</summary>
public class CommandTests
{
    [Fact]
    public void UndoRedo_EnabledFromWorkbook_AndRaiseCanExecuteChanged()
    {
        var vm = NewViewModel();
        int undoEvents = 0, redoEvents = 0;
        vm.UndoCommand.CanExecuteChanged += (_, _) => undoEvents++;
        vm.RedoCommand.CanExecuteChanged += (_, _) => redoEvents++;
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));

        vm.Workbook.SetCell("A1", "1");
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
        Assert.True(undoEvents > 0);

        vm.UndoCommand.Execute(null);
        Assert.Equal("", vm.Workbook.GetInput("A1"));
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.True(vm.RedoCommand.CanExecute(null));
        Assert.True(redoEvents > 0);

        vm.RedoCommand.Execute(null);
        Assert.Equal("1", vm.Workbook.GetInput("A1"));
    }

    [Fact]
    public void Task8Commands_AreDisabledStubs()
    {
        var vm = NewViewModel();
        Assert.False(vm.OpenCommand.CanExecute(null));
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));
        Assert.False(vm.CopyCommand.CanExecute(null));
        Assert.False(vm.PasteCommand.CanExecute(null));
        Assert.True(vm.NewCommand.CanExecute(null));
        Assert.True(vm.ExitCommand.CanExecute(null));
    }
}
