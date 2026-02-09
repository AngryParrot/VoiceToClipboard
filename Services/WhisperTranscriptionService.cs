using System.Diagnostics;
using System.IO;
using Whisper.net;

namespace VoiceToClipboard.Services;

public class WhisperTranscriptionService : IDisposable
{
    private WhisperFactory? _factory;
    private readonly string _modelPath;
    private readonly string _language;

    private static readonly Dictionary<string, (string Url, long MinSizeBytes)> ModelInfo = new()
    {
        ["tiny"]   = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin", 70_000_000),
        ["base"]   = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin", 140_000_000),
        ["small"]  = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin", 460_000_000),
        ["medium"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin", 1_500_000_000),
        ["large"]  = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin", 3_000_000_000),
    };

    public event Action<string>? StatusChanged;
    public event Action<long, long?>? DownloadProgressChanged;

    public WhisperTranscriptionService(string modelPath, string language)
    {
        _modelPath = modelPath;
        _language = language;
    }

    public async Task InitializeAsync(string modelSize)
    {
        var sizeKey = modelSize.ToLowerInvariant();

        if (File.Exists(_modelPath))
        {
            if (ModelInfo.TryGetValue(sizeKey, out var info))
            {
                var fileSize = new FileInfo(_modelPath).Length;
                if (fileSize < info.MinSizeBytes)
                {
                    StatusChanged?.Invoke("Model file is corrupt, re-downloading...");
                    File.Delete(_modelPath);
                }
            }
        }

        if (!File.Exists(_modelPath))
        {
            if (!ModelInfo.TryGetValue(sizeKey, out var info))
                throw new InvalidOperationException($"Unknown model size: {modelSize}");

            StatusChanged?.Invoke($"Downloading {sizeKey} model...");
            var modelDir = Path.GetDirectoryName(_modelPath)!;
            Directory.CreateDirectory(modelDir);

            await DownloadWithCurl(info.Url, _modelPath);

            var downloadedSize = new FileInfo(_modelPath).Length;
            if (downloadedSize < info.MinSizeBytes)
            {
                File.Delete(_modelPath);
                throw new InvalidOperationException(
                    $"Download incomplete: got {downloadedSize / (1024 * 1024)}MB, expected at least {info.MinSizeBytes / (1024 * 1024)}MB");
            }
        }

        StatusChanged?.Invoke("Loading model...");
        _factory = WhisperFactory.FromPath(_modelPath);
        StatusChanged?.Invoke("Ready");
    }

    private async Task DownloadWithCurl(string url, string outputPath)
    {
        // Use curl which handles redirects, retries, and large downloads reliably
        var psi = new ProcessStartInfo
        {
            FileName = "curl",
            ArgumentList = {
                "-L",                // follow redirects
                "--retry", "3",      // retry on failure
                "--retry-delay", "5",
                "-o", outputPath,
                "--progress-bar",
                url
            },
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start curl");

        // Read stderr for progress (curl writes progress to stderr)
        var progressTask = Task.Run(async () =>
        {
            using var reader = process.StandardError;
            var buffer = new char[256];
            while (!process.HasExited)
            {
                var count = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (count > 0)
                {
                    // Check the output file size periodically for progress
                    if (File.Exists(outputPath))
                    {
                        try
                        {
                            var size = new FileInfo(outputPath).Length;
                            DownloadProgressChanged?.Invoke(size, null);
                        }
                        catch { }
                    }
                }
            }
        });

        await process.WaitForExitAsync();
        await progressTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Download failed (curl exit code {process.ExitCode})");
        }
    }

    public async Task<string> TranscribeAsync(float[] audioData)
    {
        if (_factory == null)
            throw new InvalidOperationException("Model not loaded. Call InitializeAsync first.");

        if (audioData.Length == 0)
            return string.Empty;

        var builder = _factory.CreateBuilder();

        if (_language != "auto")
        {
            builder.WithLanguage(_language);
        }
        else
        {
            builder.WithLanguageDetection();
        }

        using var processor = builder.Build();

        var segments = new List<string>();

        await foreach (var segment in processor.ProcessAsync(audioData))
        {
            segments.Add(segment.Text);
        }

        return string.Join("", segments).Trim();
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
