using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.ViewModels;

/// <summary>§11.5 commands: Undo/Redo follow CanUndo/CanRedo; file commands are always available; Copy/Paste while no edit is open.</summary>
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
    public void FileAndClipboardCommands_AreEnabled_CopyPasteOnlyWithoutAnOpenEdit()
    {
        var vm = NewViewModel();
        Assert.True(vm.NewCommand.CanExecute(null));
        Assert.True(vm.OpenCommand.CanExecute(null));
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.SaveAsCommand.CanExecute(null));
        Assert.True(vm.ExitCommand.CanExecute(null));
        Assert.True(vm.CopyCommand.CanExecute(null));
        Assert.True(vm.PasteCommand.CanExecute(null));

        int copyEvents = 0, pasteEvents = 0;
        vm.CopyCommand.CanExecuteChanged += (_, _) => copyEvents++;
        vm.PasteCommand.CanExecuteChanged += (_, _) => pasteEvents++;
        vm.BeginEdit("x");
        Assert.False(vm.CopyCommand.CanExecute(null)); // the edit box handles Ctrl+C/Ctrl+V itself
        Assert.False(vm.PasteCommand.CanExecute(null));
        vm.CancelEdit();
        Assert.True(vm.CopyCommand.CanExecute(null));
        Assert.True(vm.PasteCommand.CanExecute(null));
        Assert.Equal(2, copyEvents);
        Assert.Equal(2, pasteEvents);
    }
}
