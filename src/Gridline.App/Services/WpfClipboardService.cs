using System.Runtime.InteropServices;
using System.Windows;

namespace Gridline.App.Services;

/// <summary><see cref="IClipboardService"/> over <see cref="Clipboard"/> (Unicode text). Another process holding the
/// clipboard open makes the call fail with CLIPBRD_E_CANT_OPEN; it is retried a few times before giving up.</summary>
public sealed class WpfClipboardService : IClipboardService
{
    private const int ClipboardCantOpen = unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN
    private const int Attempts = 10;
    private const int RetryDelayMilliseconds = 50;

    public bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // copy: true keeps the text on the clipboard after the app exits.
        return Retry(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true));
    }

    public bool TryGetText(out string? text)
    {
        string? read = null;
        bool ok = Retry(() => read = Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null);
        text = read;
        return ok;
    }

    private static bool Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (ExternalException ex) // COMException from the OLE clipboard calls
            {
                if (ex.HResult != ClipboardCantOpen || attempt >= Attempts) return false;
                Thread.Sleep(RetryDelayMilliseconds);
            }
        }
    }
}
