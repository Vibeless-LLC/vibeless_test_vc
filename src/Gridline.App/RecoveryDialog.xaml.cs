using System.Windows;
using Gridline.App.ViewModels;

namespace Gridline.App;

/// <summary>The startup recovery prompt (§11.6, §11.7 <c>RecoveryDialog</c>, R50). The rules live in
/// <see cref="RecoveryViewModel"/>; this window only binds to it and closes when it asks.</summary>
public partial class RecoveryDialog : Window
{
    public RecoveryDialog(RecoveryViewModel recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        InitializeComponent();
        DataContext = recovery;
        recovery.CloseRequested += (_, _) => Close();
    }
}
