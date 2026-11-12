using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace JarvisHud.Audio;

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Measures loudness and spectrum of a live audio stream so the orb can move with real sound.
/// It only ever keeps the last 1024 samples in memory to run the FFT: nothing is recorded,
/// written to disk or sent anywhere unless an explicit one-turn PCM consumer is attached.
/// </summary>
public sealed class AudioMeter : IDisposable
{
    public const int Bands = 24;

    private readonly bool _loopback;
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private AudioSpectrum _spectrum = new();
    private int _sampleRate = 48000;
    private int _channels = 2;
    private int _bytesPerSample = 4;
    private bool _isFloat = true;

    /// <summary>Raised on the audio thread with the level (0..1) and the band energies (0..1).</summary>
    public event Action<float, float[]>? Features;

    /// <summary>PCM is emitted only to an explicitly attached one-turn recorder.</summary>
    public event Action<byte[], int, float>? PcmData;

    /// <summary>Raised when capture stops, with the error if it stopped because of one.</summary>
    public event Action<Exception?>? Stopped;

    public bool IsRunning { get; private set; }

    /// <param name="loopback">True to meter what the speakers play, false to meter a microphone.</param>
    public AudioMeter(bool loopback)
    {
        _loopback = loopback;
    }

    public static IReadOnlyList<AudioDevice> ListMicrophones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new AudioDevice(d.ID, d.FriendlyName))
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<AudioDevice>();
        }
    }

    /// <summary>Opens the device and starts metering. Throws if the device cannot be opened.</summary>
    public void Start(string? deviceId = null)
    {
        lock (_gate)
        {
            StopCore();

            using var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            if (_loopback)
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            else if (!string.IsNullOrEmpty(deviceId))
                device = enumerator.GetDevice(deviceId);
            else
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            var capture = _loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
            var format = capture.WaveFormat;
            _sampleRate = format.SampleRate;
            _channels = Math.Max(1, format.Channels);
            _bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
            // WASAPI shared-mode mix formats are 32-bit float; 16/24-bit PCM is handled as integers.
            _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");

            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            _capture = capture;
            _spectrum = new();
            capture.StartRecording();
            IsRunning = true;
        }
    }

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    private void StopCore()
    {
        var capture = _capture;
        _capture = null;
        IsRunning = false;
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try
        {
            capture.StopRecording();
        }
        catch (Exception)
        {
            // The device may already be gone (unplugged); disposing is all that is left to do.
        }
        capture.Dispose();
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        IsRunning = false;
        Stopped?.Invoke(e.Exception);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var buffer = e.Buffer;
        var frameBytes = _bytesPerSample * _channels;
        if (frameBytes <= 0) return;
        var frames = e.BytesRecorded / frameBytes;
        if (frames == 0) return;

        var pcm = PcmData is null ? null : new byte[frames * 2];
        var monoSamples = new float[frames];
        double sumSquares = 0;
        for (var f = 0; f < frames; f++)
        {
            var offset = f * frameBytes;
            float mono = 0;
            for (var c = 0; c < _channels; c++)
            {
                var at = offset + c * _bytesPerSample;
                mono += _isFloat
                    ? BitConverter.ToSingle(buffer, at)
                    : _bytesPerSample switch
                    {
                        2 => BitConverter.ToInt16(buffer, at) / 32768f,
                        3 => ((buffer[at] | (buffer[at + 1] << 8) | ((sbyte)buffer[at + 2] << 16))) / 8388608f,
                        4 => BitConverter.ToInt32(buffer, at) / 2147483648f,
                        _ => 0f,
                    };
            }
            mono = float.IsFinite(mono) ? Math.Clamp(mono / _channels, -1f, 1f) : 0f;
            monoSamples[f] = mono;
            sumSquares += mono * mono;
            if (pcm is not null) { var sample = (short)Math.Clamp((int)(mono * 32767f), -32768, 32767); pcm[f * 2] = (byte)sample; pcm[f * 2 + 1] = (byte)(sample >> 8); }
        }

        var rms = MathF.Sqrt((float)(sumSquares / frames));
        var features = _spectrum.Process(monoSamples, _sampleRate);
        Features?.Invoke(features.Level, features.Bands);
        if (pcm is not null) PcmData?.Invoke(pcm, _sampleRate, rms);
    }

    public void Dispose() => Stop();
}
