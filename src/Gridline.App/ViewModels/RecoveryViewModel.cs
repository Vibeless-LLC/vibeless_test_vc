using System.Collections.ObjectModel;
using Gridline.App.Services;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.App.ViewModels;

/// <summary>
/// The RecoveryDialog's ViewModel (§11.6, R50): one row per recovery file, each loaded up front so an unreadable file is
/// listed as such. Restore hands the row's workbook to the main ViewModel and closes the dialog; Discard deletes the file
/// and removes the row (the dialog closes when no row is left); Close leaves the remaining files for the next startup.
/// </summary>
public sealed class RecoveryViewModel : ObservableObject
{
    private readonly Action<RecoveryFile, Workbook> _restore;
    private readonly Func<RecoveryFile, bool> _discard;

    /// <param name="files">The recovery files to offer.</param>
    /// <param name="restore">Adopts a restored workbook (the main ViewModel).</param>
    /// <param name="discard">Deletes a recovery file; false when it could not be deleted.</param>
    internal RecoveryViewModel(IEnumerable<RecoveryFile> files, Action<RecoveryFile, Workbook> restore, Func<RecoveryFile, bool> discard)
    {
        ArgumentNullException.ThrowIfNull(files);
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _discard = discard ?? throw new ArgumentNullException(nameof(discard));
        Items = new ObservableCollection<RecoveryItemViewModel>(files.Select(Load));
    }

    public ObservableCollection<RecoveryItemViewModel> Items { get; }

    /// <summary>The file that was restored, or null.</summary>
    public RecoveryFile? Restored { get; private set; }

    /// <summary>Raised when the dialog should close: a Restore, or the last row discarded. (The Close button and Esc close
    /// the window directly.)</summary>
    public event EventHandler? CloseRequested;

    private RecoveryItemViewModel Load(RecoveryFile file)
    {
        Workbook? workbook = null;
        string? error = null;
        try { workbook = GridlineFile.LoadFromPath(file.Path); }
        catch (Exception ex) when (ex is GridlineFormatException or GridlineIOException or ArgumentException or NotSupportedException or System.IO.IOException)
        {
            error = ex.Message;
        }
        var item = new RecoveryItemViewModel(file, workbook, error);
        item.RestoreCommand = new RelayCommand(() => Restore(item), () => item.IsReadable && Restored is null);
        item.DiscardCommand = new RelayCommand(() => Discard(item), () => Restored is null);
        return item;
    }

    private void Restore(RecoveryItemViewModel item)
    {
        if (item.Workbook is null || Restored is not null) return;
        Restored = item.File;
        _restore(item.File, item.Workbook);
        RequestClose();
    }

    private void Discard(RecoveryItemViewModel item)
    {
        if (!_discard(item.File))
        {
            item.Status = "Could not delete this file.";
            return;
        }
        Items.Remove(item);
        if (Items.Count == 0) RequestClose();
    }

    private void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
