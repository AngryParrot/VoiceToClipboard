# Voice to Clipboard

A native Windows system tray application that records voice via a global hotkey, transcribes it locally using [Whisper](https://github.com/openai/whisper), and copies the text to your clipboard.

All transcription happens **offline** — no data leaves your machine.

## How It Works

1. Press **Ctrl+Alt+R** to start recording (tray icon turns red)
2. Speak into your microphone
3. Press **Ctrl+Alt+R** again to stop (tray icon turns orange while transcribing)
4. The transcribed text is copied to your clipboard — just **Ctrl+V** to paste

Press **Escape** at any time during recording to cancel — the audio is discarded and no transcription occurs.

## Requirements

- Windows 10 (build 19041+) or later
- .NET 8 SDK
- A microphone

## Getting Started

```bash
git clone https://github.com/AngryParrot/VoiceToClipboard.git
cd VoiceToClipboard
dotnet build
dotnet run
```

On first run, the app downloads the Whisper `small` model (~465 MB) from Hugging Face. This only happens once — the model is cached in `%LOCALAPPDATA%\VoiceToClipboard\Models\`.

## Configuration

Edit `appsettings.json` to customize:

```json
{
  "Hotkey": "Ctrl+Alt+R",
  "ModelSize": "small",
  "ModelPath": "",
  "Language": "auto",
  "Device": "cpu",
  "MaxRecordingSeconds": 300,
  "VadEnabled": true
}
```

| Setting | Description |
|---|---|
| `Hotkey` | Global hotkey combo (e.g. `Ctrl+Alt+R`, `Ctrl+Shift+F9`) |
| `ModelSize` | Whisper model: `tiny`, `base`, `small`, `medium`, `large` |
| `ModelPath` | Custom model directory (default: `%LOCALAPPDATA%\VoiceToClipboard\Models\`) |
| `Language` | Language code (e.g. `en`, `de`, `fr`) or `auto` for detection |
| `MaxRecordingSeconds` | Auto-stop recording after this many seconds |

### Model Sizes

| Model | Size | Speed | Accuracy |
|---|---|---|---|
| tiny | ~75 MB | Fastest | Lower |
| base | ~142 MB | Fast | Moderate |
| **small** | **~465 MB** | **Moderate** | **High** |
| medium | ~1.5 GB | Slow | Higher |
| large | ~3 GB | Slowest | Highest |

## Tech Stack

- **WPF** — system tray UI via [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)
- **Whisper.net** — C# wrapper for [whisper.cpp](https://github.com/ggerganov/whisper.cpp)
- **NAudio** — microphone capture (WASAPI)
- **Win32 P/Invoke** — global hotkey registration

## License

MIT
