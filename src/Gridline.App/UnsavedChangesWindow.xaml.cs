using System.Windows;
using Gridline.App.Services;

namespace Gridline.App;

/// <summary>The unsaved-changes prompt (§11.5, R45): a small modal window with Save, Don't Save and Cancel. Anything
/// other than Save or Don't Save (Cancel, Esc, the close button) is <see cref="UnsavedChangesChoice.Cancel"/>.</summary>
public partial class UnsavedChangesWindow : Window
{
    public UnsavedChangesWindow(string documentName)
    {
        InitializeComponent();
        MessageText.Text = $"Do you want to save changes to {documentName}?";
        Loaded += (_, _) => SaveButton.Focus();
    }

    public UnsavedChangesChoice Choice { get; private set; } = UnsavedChangesChoice.Cancel;

    private void Save_Click(object sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Save);

    private void DontSave_Click(object sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.DontSave);

    private void Close(UnsavedChangesChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
