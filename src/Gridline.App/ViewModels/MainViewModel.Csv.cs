using System.IO;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.App.ViewModels;

/// <summary>
/// File > Import CSV and Export CSV (§11.5, §8.5, R74), always with a comma. Import acts like Open: an open edit is
/// committed and a dirty workbook prompts first; the imported workbook is adopted as Untitled, not dirty, with an empty
/// history, so the recovery session behaves as after New. Export writes atomically through <see cref="CsvFormat"/> and
/// changes no path, title or dirty state. A failure shows a message and leaves the workbook unchanged. See DECISIONS D-49.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The CSV delimiter the GUI uses (R74).</summary>
    public const char CsvDelimiter = ',';

    /// <summary>Import CSV: after the unsaved-changes prompt, the CSV Open dialog, then the import. Returns true when a
    /// file was imported.</summary>
    public bool ImportCsv()
    {
        if (!ConfirmUnsavedChanges()) return false;
        string? path = _dialogs.ShowOpenCsvDialog();
        if (path is null) return false;
        Workbook imported;
        try
        {
            imported = CsvFormat.ImportFromPath(path, CsvDelimiter);
        }
        catch (Exception ex) when (ex is GridlineFormatException or GridlineIOException or ArgumentException or NotSupportedException or IOException)
        {
            _dialogs.ShowError("Gridline", $"Cannot import '{path}'.{Environment.NewLine}{ex.Message}");
            return false;
        }
        Adopt(imported, null); // Untitled; CsvFormat returns a clean workbook with an empty history (R70)
        return true;
    }

    /// <summary>Export CSV: commits an open edit, then the CSV Save dialog, then an atomic export. Returns true when the
    /// file was written.</summary>
    public bool ExportCsv()
    {
        if (!CommitPendingEdit()) return false;
        string? path = _dialogs.ShowSaveCsvDialog(_filePath);
        if (path is null) return false;
        try
        {
            CsvFormat.ExportToPath(_workbook, path, CsvDelimiter);
        }
        catch (Exception ex) when (ex is GridlineIOException or ArgumentException or NotSupportedException or IOException)
        {
            _dialogs.ShowError("Gridline", $"Cannot export '{path}'.{Environment.NewLine}{ex.Message}");
            return false;
        }
        return true;
    }
}
