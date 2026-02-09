using System.Windows;

namespace VoiceToClipboard.Services;

public static class ClipboardService
{
    public static void CopyToClipboard(string text)
    {
        Clipboard.SetDataObject(text, true);
    }
}
