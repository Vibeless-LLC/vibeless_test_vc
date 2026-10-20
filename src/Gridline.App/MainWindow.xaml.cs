using System.Windows;
using System.Windows.Input;
using Gridline.App.ViewModels;

namespace Gridline.App;

/// <summary>Thin code-behind (§11): sets the DataContext, closes on request and gives the grid keyboard focus at launch.</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => Keyboard.Focus(SheetGrid);
    }
}
