using System.IO;

namespace VoiceToClipboard.Models;

public class AppSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt+R";
    public string ModelSize { get; set; } = "small";
    public string ModelPath { get; set; } = "";
    public string Language { get; set; } = "auto";
    public string Device { get; set; } = "cpu";
    public int MaxRecordingSeconds { get; set; } = 300;
    public bool VadEnabled { get; set; } = true;

    public string GetModelDirectory()
    {
        if (!string.IsNullOrEmpty(ModelPath))
            return ModelPath;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "VoiceToClipboard", "Models");
    }

    public string GetModelFileName()
    {
        return $"ggml-{ModelSize}.bin";
    }

    public string GetModelFilePath()
    {
        return Path.Combine(GetModelDirectory(), GetModelFileName());
    }
}
