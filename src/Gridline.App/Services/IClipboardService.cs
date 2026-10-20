namespace Gridline.App.Services;

/// <summary>
/// The system clipboard as plain text (§11.6), so ViewModel tests use a fake. Both members report failure instead of
/// throwing: the clipboard is shared with other processes and may be locked.
/// </summary>
public interface IClipboardService
{
    /// <summary>Puts <paramref name="text"/> on the clipboard. Returns false when the clipboard could not be opened.</summary>
    bool TrySetText(string text);

    /// <summary>Reads the clipboard text (null when it holds no text). Returns false when the clipboard could not be opened.</summary>
    bool TryGetText(out string? text);
}
