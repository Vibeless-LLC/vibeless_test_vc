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

        /// <summary>The modifier state the grid sees (the test seam), so a key held on the desktop cannot leak in.</summary>
        public ModifierKeys Modifiers { get; set; } = ModifierKeys.None;

        public Host()
        {
            Grid = new GridControl { Source = Vm };
            Grid.ModifierProvider = () => Modifiers;
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

    private static bool Shows(GridControl grid, CellAddress cell)
    {
        var v = grid.VisibleRange;
        return cell.Column >= v.FirstColumn && cell.Column <= v.LastColumn && cell.Row >= v.FirstRow && cell.Row <= v.LastRow;
    }

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
    public void ShiftFromTheModifierSeam_ReversesEnterAndTab_WithAndWithoutAnEdit() => RunSta(() =>
    {
        // The grid reads modifiers only through GridControl.ModifierProvider, so the result does not depend on the
        // desktop's real keyboard state (final review: a stuck Shift used to flip these tests).
        using var h = new Host();
        h.Vm.SelectCell(CellAddress.Parse("C3"), extend: false);
        h.Modifiers = ModifierKeys.Shift;
        Assert.True(h.Key(Key.Enter));
        Assert.Equal("C2", h.Vm.ActiveAddress);
        Assert.True(h.Key(Key.Tab));
        Assert.Equal("B2", h.Vm.ActiveAddress);
        h.Modifiers = ModifierKeys.None;
        h.Type("x");
        h.Modifiers = ModifierKeys.Shift;
        h.EditorKey(Key.Enter); // commits and moves up
        Assert.Equal("x", h.Vm.Workbook.GetInput("B2"));
        Assert.Equal("B1", h.Vm.ActiveAddress);
    });

    [Fact]
    public void ShiftArrowFromTheModifierSeam_ExtendsTheSelection_AndCtrlTypingDoesNotStartAnEdit() => RunSta(() =>
    {
        using var h = new Host();
        h.Modifiers = ModifierKeys.Shift;
        Assert.True(h.Key(Key.Right));
        Assert.Equal("B1", h.Vm.ActiveAddress);
        Assert.Equal(CellAddress.Parse("A1"), h.Vm.SelectionAnchor); // extended, not moved
        h.Modifiers = ModifierKeys.Control;
        Assert.False(h.Type("x"));
        Assert.False(h.Vm.IsEditing);
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

    [Fact]
    public void TypingWhileTheActiveCellIsScrolledOutOfView_ScrollsItIntoView_SoTheEditorIsVisible() => RunSta(() =>
    {
        using var h = new Host();
        h.Vm.SelectCell(A("C5"), extend: false);
        h.Grid.SetVerticalOffset(5000);
        h.Grid.SetHorizontalOffset(300);
        h.Scroller.UpdateLayout();
        Assert.False(Shows(h.Grid, A("C5")));

        Assert.True(h.Type("z"));

        Assert.True(h.Vm.IsEditing);
        Assert.True(Shows(h.Grid, A("C5")));
        Assert.Equal(22 - 2, h.Grid.Editor.Clip.Bounds.Height); // drawn, not clipped away
        Assert.Equal(80 - 2, h.Grid.Editor.Clip.Bounds.Width);
    });

    [Fact]
    public void FormulaBarTypingWhileTheActiveCellIsOutOfView_AlsoScrollsItIntoView() => RunSta(() =>
    {
        using var h = new Host();
        h.Grid.SetVerticalOffset(5000);
        h.Scroller.UpdateLayout();
        h.Vm.FormulaBarText = "x";
        h.Scroller.UpdateLayout();
        Assert.True(Shows(h.Grid, A("A1")));
    });

    [Fact]
    public void R49_EditorHistory_StartsAtTheEditsStartText_WhicheverBoxOpenedIt() => RunSta(() =>
    {
        using var h = new Host();
        // An earlier edit leaves its text and history in the editor.
        h.Type("a");
        h.Grid.Editor.Text = "abc";
        h.EditorKey(Key.Enter);
        Assert.Equal("abc", h.Vm.Workbook.GetInput("A1"));

        // An edit opened from the formula bar: the editor shows the start text and cannot undo past it.
        h.Vm.FormulaBarText = "q";
        Assert.Equal("q", h.Grid.Editor.Text);
        Assert.False(h.Grid.Editor.CanUndo);
        h.Grid.Editor.Text = "qr";
        Assert.True(h.Grid.Editor.CanUndo);
        h.Grid.Editor.Undo();
        Assert.Equal("q", h.Grid.Editor.Text);
        Assert.False(h.Grid.Editor.CanUndo);
        h.Vm.CancelEdit();

        // An edit opened from the grid (F2) likewise.
        Assert.True(h.Key(Key.F2));
        Assert.Equal("", h.Grid.Editor.Text); // A2 is blank
        Assert.False(h.Grid.Editor.CanUndo);
    });
}
