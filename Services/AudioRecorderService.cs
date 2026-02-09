using System.IO;
using NAudio.Wave;

namespace VoiceToClipboard.Services;

public class AudioRecorderService : IDisposable
{
    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private readonly object _lock = new();
    private bool _recording;
    private System.Timers.Timer? _maxDurationTimer;

    public bool IsRecording => _recording;
    public event Action? MaxDurationReached;

    public void StartRecording(int maxRecordingSeconds)
    {
        lock (_lock)
        {
            if (_recording) return;

            _buffer = new MemoryStream();

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1), // 16kHz, 16-bit, mono
                BufferMilliseconds = 100
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn.StartRecording();
            _recording = true;

            _maxDurationTimer = new System.Timers.Timer(maxRecordingSeconds * 1000);
            _maxDurationTimer.AutoReset = false;
            _maxDurationTimer.Elapsed += (_, _) => MaxDurationReached?.Invoke();
            _maxDurationTimer.Start();
        }
    }

    public float[] StopRecording()
    {
        lock (_lock)
        {
            if (!_recording) return [];

            _maxDurationTimer?.Stop();
            _maxDurationTimer?.Dispose();
            _maxDurationTimer = null;

            _waveIn?.StopRecording();
            _recording = false;

            var pcmBytes = _buffer?.ToArray() ?? [];
            _buffer?.Dispose();
            _buffer = null;

            _waveIn?.Dispose();
            _waveIn = null;

            return ConvertPcm16ToFloat(pcmBytes);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            _buffer?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // handled in StopRecording
    }

    private static float[] ConvertPcm16ToFloat(byte[] pcmBytes)
    {
        int sampleCount = pcmBytes.Length / 2;
        var floats = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(pcmBytes, i * 2);
            floats[i] = sample / 32768f;
        }
        return floats;
    }

    public static bool IsMicrophoneAvailable()
    {
        return WaveInEvent.DeviceCount > 0;
    }

    public void Dispose()
    {
        _maxDurationTimer?.Dispose();
        _waveIn?.Dispose();
        _buffer?.Dispose();
    }
}
