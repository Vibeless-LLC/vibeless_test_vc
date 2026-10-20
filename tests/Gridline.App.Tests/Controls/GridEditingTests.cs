using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Gridline.App.Controls;
using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.Core.Addressing;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.Controls;

/// <summary>The grid's §11.4 plumbing (STA, hosted in an HwndSource): typed text and F2 open the in-cell editor over the
/// active cell, Enter/Tab/Esc in the editor, Delete on the grid, the editor following scrolling (D-38).</summary>
public class GridEditingTests
{
    private sealed class Host : IDisposable
    {
        public MainViewModel Vm { get; } = NewViewModel();
        public GridControl Grid { get; }
        public ScrollViewer Scroller { get; }
        private readonly HwndSource _source;

        public Host()
        {
            Grid = new GridControl { Source = Vm };
            Scroller = new ScrollViewer { CanContentScroll = true, Content = Grid, Width = 800, Height = 600 };
            _source = new HwndSource(new HwndSourceParameters("grid-edit-test") { Width = 800, Height = 600 }) { RootVisual = Scroller };
            Scroller.UpdateLayout();
        }

        public bool Key(Key key, UIElement? target = null, bool preview = false)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, _source, 0, key)
            {
                RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent,
            };
            (target ?? Grid).RaiseEvent(args);
            Scroller.UpdateLayout();
            return args.Handled;
        }

        /// <summary>A key pressed in the editor: the preview pass first (as WPF does), then the bubbling pass.</summary>
        public void EditorKey(Key key)
        {
            if (!Key(key, Grid.Editor, preview: true)) Key(key, Grid.Editor);
        }

        public bool Type(string text)
        {
            var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, Grid, text))
            {
                RoutedEvent = TextCompositionManager.TextInputEvent,
            };
            Grid.RaiseEvent(args);
            Scroller.UpdateLayout();
            return args.Handled;
        }

        public void Dispose() => _source.Dispose();
    }

    private static CellAddress A(string s) => CellAddress.Parse(s);

    [Fact]
    public void TypedCharacter_OpensTheEditorOverTheActiveCell_WithThatCharacter() => RunSta(() =>
    {
        using var h = new Host();
        h.Vm.Workbook.SetCell("B2", "old");
        h.Vm.SelectCell(A("B2"), extend: false);
        Assert.Equal(Visibility.Collapsed, h.Grid.Editor.Visibility);

        Assert.True(h.Type("é")); // non-ASCII arrives through TextInput like any character
        Assert.True(h.Vm.IsEditing);
        Assert.Equal(Visibility.Visible, h.Grid.Editor.Visibility);
        Assert.Equal("é", h.Grid.Editor.Text);
        Assert.Equal("é", h.Vm.FormulaBarText);
        Assert.Equal(1, h.Grid.Editor.CaretIndex);
        Rect r = h.Grid.EditorBounds;
        Assert.Equal(60 + 80 + 1, r.X); // B: after the row header and column A
        Assert.Equal(22 + 22 + 1, r.Y); // row 2
        Assert.Equal(22 - 2, r.Height);
        Assert.Equal("old", h.Vm.Workbook.GetInput("B2"));
    });

    [Theory]
    [InlineData("\r")]
    [InlineData("\b")]
    [InlineData("\u001b")]
    [InlineData("\t")]
    [InlineData("")]
    public void ControlCharacters_DoNotStartEditing(string text) => RunSta(() =>
    {
        using var h = new Host();
        Assert.False(h.Type(text));
        Assert.False(h.Vm.IsEditing);
        Assert.False(GridControl.IsPrintable(text));
    });

    [Fact]
    public void F2_EditsTheExistingInput_EnterCommitsAndMovesDown() => RunSta(() =>
    {
        using var h = new Host();
        h.Vm.Workbook.SetCell("A1", "=1+1");
        Assert.True(h.Key(Key.F2));
        Assert.Equal("=1+1", h.Grid.Editor.Text);
        Assert.Equal(4, h.Grid.Editor.CaretIndex);

        h.Grid.Editor.Text = "=1+2"; // typing in the editor
        Assert.Equal("=1+2", h.Vm.FormulaBarText);
        h.EditorKey(Key.Enter);
        Assert.False(h.Vm.IsEditing);
        Assert.Equal(Visibility.Collapsed, h.Grid.Editor.Visibility);
        Assert.Equal("=1+2", h.Vm.Workbook.GetInput("A1"));
        Assert.Equal("A2", h.Vm.ActiveAddress);
    });

    [Fact]
    public void Tab_InTheEditor_CommitsAndMovesRight() => RunSta(() =>
    {
        using var h = new Host();
        h.Type("7");
        h.EditorKey(Key.Tab);
        Assert.Equal("7", h.Vm.Workbook.GetInput("A1"));
        Assert.Equal("B1", h.Vm.ActiveAddress);
    });

    [Fact]
    public void Esc_InTheEditor_Cancels() => RunSta(() =>
    {
        using var h = new Host();
        h.Vm.Workbook.SetCell("A1", "keep");
        h.Type("x");
        h.EditorKey(Key.Escape);
        Assert.False(h.Vm.IsEditing);
        Assert.Equal(Visibility.Collapsed, h.Grid.Editor.Visibility);
        Assert.Equal("keep", h.Vm.Workbook.GetInput("A1"));
        Assert.Equal("A1", h.Vm.ActiveAddress);
    });

    [Fact]
    public void EnterAndTab_WithoutAnEdit_Move() => RunSta(() =>
    {
        using var h = new Host();
        Assert.True(h.Key(Key.Enter));
        Assert.Equal("A2", h.Vm.ActiveAddress);
        Assert.True(h.Key(Key.Tab));
        Assert.Equal("B2", h.Vm.ActiveAddress);
        Assert.False(h.Vm.Workbook.CanUndo);
    });

    [Fact]
    public void Delete_OnTheGrid_ClearsTheSelection() => RunSta(() =>
    {
        using var h = new Host();
        h.Vm.Workbook.ApplyBatch([new("A1", "1"), new("A2", "2")]);
        h.Vm.SelectCell(A("A2"), extend: true);
        Assert.True(h.Key(Key.Delete));
        Assert.Empty(h.Vm.Workbook.GetUsedAddresses());
        h.Vm.UndoCommand.Execute(null);
        Assert.Equal(2, h.Vm.Workbook.GetUsedAddresses().Count);
    });

    [Fact]
    public void Editor_FollowsItsCellWhenTheGridScrolls_AndIsClippedOutOfView() => RunSta(() =>
    {
        using var h = new Host();
        h.Vm.SelectCell(A("C5"), extend: false);
        h.Type("a");
        double top = h.Grid.EditorBounds.Y;
        h.Grid.SetVerticalOffset(2);
        h.Scroller.UpdateLayout();
        Assert.Equal(top - 2 * 22, h.Grid.EditorBounds.Y);
        h.Grid.SetVerticalOffset(1000); // the cell is far above the view: the editor is clipped away, the edit stays open
        h.Scroller.UpdateLayout();
        Assert.True(h.Vm.IsEditing);
        Assert.True(h.Grid.Editor.Clip is { Bounds.IsEmpty: true } || h.Grid.Editor.Clip.Bounds.Height == 0);
        h.Grid.SetVerticalOffset(0);
        h.Scroller.UpdateLayout();
        Assert.Equal(top, h.Grid.EditorBounds.Y);
        Assert.Equal(22 - 2, h.Grid.Editor.Clip.Bounds.Height);
    });

    [Fact]
    public void FormulaBarTyping_ShowsTheSameTextInTheEditor() => RunSta(() =>
    {
        using var h = new Host();
        h.Vm.FormulaBarText = "=SUM(";
        Assert.Equal(Visibility.Visible, h.Grid.Editor.Visibility);
        Assert.Equal("=SUM(", h.Grid.Editor.Text);
        h.Vm.CancelEdit();
        Assert.Equal(Visibility.Collapsed, h.Grid.Editor.Visibility);
    });

    [Fact]
    public void Editor_CapsInputAtMaxInputLength() => RunSta(() =>
    {
        using var h = new Host();
        Assert.Equal(Workbook.MaxInputLength, h.Grid.Editor.MaxLength);
    });

    [Fact]
    public void Editor_IsAnAutomationChildOfTheGrid() => RunSta(() =>
    {
        using var h = new Host();
        h.Type("q");
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(h.Grid);
        Assert.Contains(peer.GetChildren(), c => c.GetAutomationId() == "CellEditor");
    });
}
