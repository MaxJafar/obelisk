namespace JarvisHud.Audio;

public sealed record AudioFeatures(float Level, float[] Bands);

/// <summary>Bounded, causal FFT shared by microphone and actual Jarvis playback.</summary>
public sealed class AudioSpectrum
{
    public const int BandCount = 24;
    private const int Size = 1024;
    private readonly float[] _ring = new float[Size], _re = new float[Size], _im = new float[Size];
    private int _position;

    public AudioFeatures Process(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        double power = 0;
        foreach (var input in mono)
        {
            var sample = float.IsFinite(input) ? Math.Clamp(input, -1f, 1f) : 0f;
            power += sample * sample;
            _ring[_position] = sample;
            _position = (_position + 1) % Size;
        }
        var rms = mono.IsEmpty ? 0f : MathF.Sqrt((float)(power / mono.Length));
        var level = Math.Clamp((20f * MathF.Log10(rms + 1e-7f) + 50f) / 42f, 0f, 1f);
        var bands = new float[BandCount];
        // Gate the frame, including residual FFT history, when the current stream is silent.
        if (level <= 0f) return new(0f, bands);
        for (var i = 0; i < Size; i++)
        {
            _re[i] = _ring[(_position + i) % Size] * (0.5f - 0.5f * MathF.Cos(MathF.Tau * i / (Size - 1)));
            _im[i] = 0f;
        }
        for (int i = 1, j = 0; i < Size; i++)
        {
            var bit = Size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (_re[i], _re[j]) = (_re[j], _re[i]); (_im[i], _im[j]) = (_im[j], _im[i]); }
        }
        for (var length = 2; length <= Size; length <<= 1)
        {
            var angle = -MathF.Tau / length;
            var wr = MathF.Cos(angle); var wi = MathF.Sin(angle);
            for (var start = 0; start < Size; start += length)
            {
                float r = 1, im = 0;
                for (var j = 0; j < length / 2; j++)
                {
                    var a = start + j; var b = a + length / 2;
                    var tr = _re[b] * r - _im[b] * im; var ti = _re[b] * im + _im[b] * r;
                    _re[b] = _re[a] - tr; _im[b] = _im[a] - ti; _re[a] += tr; _im[a] += ti;
                    (r, im) = (r * wr - im * wi, r * wi + im * wr);
                }
            }
        }
        var high = MathF.Min(9000, sampleRate * 0.48f);
        for (var band = 0; band < BandCount; band++)
        {
            var first = Math.Clamp((int)(70 * MathF.Pow(high / 70, band / (float)BandCount) * Size / sampleRate), 1, Size / 2 - 1);
            var last = Math.Clamp((int)MathF.Ceiling(70 * MathF.Pow(high / 70, (band + 1) / (float)BandCount) * Size / sampleRate), first + 1, Size / 2);
            float sum = 0;
            for (var i = first; i < last; i++) sum += MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);
            var db = 20f * MathF.Log10(sum / (last - first) / (Size / 4f) + 1e-7f);
            bands[band] = Math.Clamp((db + 62f + band * 0.65f) / 50f, 0f, 1f) * Math.Clamp(level * 4f, 0f, 1f);
        }
        return new(level, bands);
    }
}
