using System.Globalization;
using System.IO;
using Gridline.App.Services;
using Gridline.Core;

namespace Gridline.App.ViewModels;

/// <summary>One row of the RecoveryDialog (R50): a recovery file, its last-write time and, when it loaded, the workbook
/// that Restore adopts. An unreadable file has no workbook and can only be discarded.</summary>
public sealed class RecoveryItemViewModel : ObservableObject
{
    private string _status;

    internal RecoveryItemViewModel(RecoveryFile file, Workbook? workbook, string? loadError)
    {
        File = file;
        Workbook = workbook;
        _status = workbook is null ? "Unreadable: " + loadError : "";
    }

    public RecoveryFile File { get; }

    /// <summary>The loaded recovery file; null when it could not be read.</summary>
    internal Workbook? Workbook { get; }

    public bool IsReadable => Workbook is not null;

    public string FileName => Path.GetFileName(File.Path);

    /// <summary>The last-write time, local, <c>yyyy-MM-dd HH:mm:ss</c>.</summary>
    public string LastWriteText => File.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Empty, or why the row cannot be restored or discarded.</summary>
    public string Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    /// <summary>Stable per-row AutomationIds for UI tests (not normative): <c>RecoveryRestore_&lt;guid&gt;</c> and
    /// <c>RecoveryDiscard_&lt;guid&gt;</c>, guid in the <c>N</c> format.</summary>
    public string RestoreAutomationId => "RecoveryRestore_" + File.SessionId.ToString("N");

    public string DiscardAutomationId => "RecoveryDiscard_" + File.SessionId.ToString("N");

    public RelayCommand RestoreCommand { get; internal set; } = null!;

    public RelayCommand DiscardCommand { get; internal set; } = null!;
}
