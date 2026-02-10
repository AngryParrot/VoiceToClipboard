using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.Json;
using System.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using VoiceToClipboard.Models;
using VoiceToClipboard.Services;

namespace VoiceToClipboard;

public partial class App : Application
{
    private AppSettings _settings = new();
    private AudioRecorderService? _audioRecorder;
    private WhisperTranscriptionService? _whisperService;
    private HotkeyService? _hotkeyService;
    private TaskbarIcon? _trayIcon;
    private CursorOverlayWindow? _cursorOverlay;
    private bool _isRecording;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            LoadSettings();
            CreateTrayIcon();
            UpdateTrayState("Initializing...", TrayState.Loading);

            if (!AudioRecorderService.IsMicrophoneAvailable())
            {
                ShowNotification("Error", "No microphone detected. Please connect a microphone and restart.");
            }

            _audioRecorder = new AudioRecorderService();
            _audioRecorder.MaxDurationReached += () =>
            {
                Dispatcher.BeginInvoke(async () =>
                {
                    _hotkeyService?.UnregisterCancelHotkey();
                    await StopAndTranscribe();
                });
            };

            _whisperService = new WhisperTranscriptionService(
                _settings.GetModelFilePath(),
                _settings.Language);

            _whisperService.StatusChanged += status =>
            {
                Dispatcher.BeginInvoke(() => UpdateTrayState(status, TrayState.Loading));
            };

            _whisperService.DownloadProgressChanged += (bytesRead, totalBytes) =>
            {
                var mb = bytesRead / (1024 * 1024);
                var status = totalBytes.HasValue
                    ? $"Downloading model... ({mb}/{totalBytes.Value / (1024 * 1024)} MB)"
                    : $"Downloading model... ({mb} MB)";
                Dispatcher.BeginInvoke(() => UpdateTrayState(status, TrayState.Loading));
            };

            try
            {
                await _whisperService.InitializeAsync(_settings.ModelSize);
            }
            catch (Exception ex)
            {
                ShowNotification("Model Error", $"Failed to load Whisper model: {ex.Message}");
                UpdateTrayState("Model load failed", TrayState.Idle);
                return;
            }

            _hotkeyService = new HotkeyService();
            _hotkeyService.HotkeyPressed += () =>
            {
                Dispatcher.BeginInvoke(async () => await OnHotkeyPressed());
            };
            _hotkeyService.CancelPressed += () =>
            {
                Dispatcher.BeginInvoke(CancelRecording);
            };

            if (!_hotkeyService.Register(_settings.Hotkey))
            {
                ShowNotification("Hotkey Error", $"Failed to register {_settings.Hotkey}. It may be in use by another app.");
            }

            _cursorOverlay = new CursorOverlayWindow();

            UpdateTrayState("Ready", TrayState.Idle);
            ShowNotification("Voice to Clipboard", $"Ready! Press {_settings.Hotkey} to start recording.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup failed: {ex.Message}\n\n{ex.StackTrace}",
                "Voice to Clipboard — Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task OnHotkeyPressed()
    {
        if (!_isRecording)
        {
            _isRecording = true;
            _hotkeyService?.RegisterCancelHotkey();
            _audioRecorder?.StartRecording(_settings.MaxRecordingSeconds);
            UpdateTrayState("Recording... (Esc to cancel)", TrayState.Recording);
            _cursorOverlay?.ShowRecording();
        }
        else
        {
            _hotkeyService?.UnregisterCancelHotkey();
            await StopAndTranscribe();
        }
    }

    private void CancelRecording()
    {
        if (!_isRecording) return;

        _isRecording = false;
        _hotkeyService?.UnregisterCancelHotkey();
        _audioRecorder?.StopRecording(); // discard the audio
        _cursorOverlay?.HideOverlay();
        UpdateTrayState("Ready", TrayState.Idle);
        ShowNotification("Voice to Clipboard", "Recording cancelled.");
    }

    private async Task StopAndTranscribe()
    {
        _isRecording = false;
        var audioData = _audioRecorder?.StopRecording() ?? [];

        var durationSec = audioData.Length / 16000.0;
        Log($"[VTC] Audio captured: {audioData.Length} samples ({durationSec:F1}s)");

        UpdateTrayState("Transcribing...", TrayState.Transcribing);
        _cursorOverlay?.ShowTranscribing();

        if (audioData.Length == 0)
        {
            _cursorOverlay?.HideOverlay();
            ShowNotification("Voice to Clipboard", "No audio captured.");
            UpdateTrayState("Ready", TrayState.Idle);
            return;
        }

        try
        {
            Log("[VTC] Starting transcription...");
            var text = await _whisperService!.TranscribeAsync(audioData);
            Log($"[VTC] Transcription result: '{text}'");

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowNotification("Voice to Clipboard", "No speech detected.");
            }
            else
            {
                ClipboardService.CopyToClipboard(text);
                var preview = text.Length > 100 ? text[..100] + "..." : text;
                ShowNotification("Copied to Clipboard", preview);
            }
        }
        catch (Exception ex)
        {
            ShowNotification("Transcription Error", ex.Message);
        }

        _cursorOverlay?.ShowDone();
        UpdateTrayState("Ready", TrayState.Idle);
    }

    private void LoadSettings()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(settingsPath))
        {
            var json = File.ReadAllText(settingsPath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
        }
    }

    private enum TrayState { Idle, Recording, Transcribing, Loading }

    private void CreateTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            Icon = GenerateIcon(Color.Gray),
            ToolTipText = "Voice to Clipboard — Idle",
            ContextMenu = CreateContextMenu()
        };
        _trayIcon.ForceCreate();
    }

    private System.Windows.Controls.ContextMenu CreateContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var statusItem = new System.Windows.Controls.MenuItem
        {
            Header = "Status: Idle",
            IsEnabled = false
        };
        menu.Items.Add(statusItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var quitItem = new System.Windows.Controls.MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(quitItem);

        return menu;
    }

    private void UpdateTrayState(string status, TrayState state)
    {
        if (_trayIcon == null) return;

        _trayIcon.ToolTipText = $"Voice to Clipboard — {status}";

        var color = state switch
        {
            TrayState.Recording => Color.Red,
            TrayState.Transcribing => Color.Orange,
            TrayState.Loading => Color.CornflowerBlue,
            _ => Color.Gray
        };
        _trayIcon.Icon = GenerateIcon(color);

        if (_trayIcon.ContextMenu?.Items[0] is System.Windows.Controls.MenuItem statusItem)
        {
            statusItem.Header = $"Status: {status}";
        }
    }

    private static Icon GenerateIcon(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 1, 1, 14, 14);

        using var pen = new Pen(Color.White, 1.5f);
        g.DrawLine(pen, 8, 3, 8, 9);
        g.DrawArc(pen, 5, 3, 6, 8, 0, 180);
        g.DrawLine(pen, 8, 11, 8, 13);

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "vtc.log");

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
    }

    private void ShowNotification(string title, string message)
    {
        Log($"Notification: {title} — {message}");
        _trayIcon?.ShowNotification(title, message);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyService?.Dispose();
        _audioRecorder?.Dispose();
        _whisperService?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
