using System.Windows;

namespace Gridline.App;

/// <summary>The sheet tab's Rename dialog (CR1.8, R88): a box (<c>RenameSheetBox</c>) holding the current name, selected.
/// OK returns the typed name verbatim (the workbook validates it); Cancel, Esc and the close button return null.</summary>
public partial class RenameSheetWindow : Window
{
    public RenameSheetWindow(string currentName)
    {
        InitializeComponent();
        RenameSheetBox.Text = currentName;
        Loaded += (_, _) =>
        {
            RenameSheetBox.Focus();
            RenameSheetBox.SelectAll();
        };
    }

    /// <summary>The name typed, or null when the dialog was cancelled.</summary>
    public string? NewName { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        NewName = RenameSheetBox.Text;
        DialogResult = true;
    }
}
