using System;

namespace Radio808.Core.Dsp;

/// <summary>
/// Cubic (Catmull-Rom) resampler for interleaved stereo with a continuously adjustable ratio. Meant for audio that is
/// already band-limited well below both Nyquist rates (FM/HD audio is 15-20 kHz into 44.1-48 kHz).
/// </summary>
public sealed class StereoResampler
{
    private float[] _work = new float[0];
    private readonly float[] _hist = new float[6];   // last 3 input frames
    private double _pos = 1;                        // read position in frames, relative to _work

    /// <summary>Input frames consumed per output frame.</summary>
    public double Ratio { get; set; }

    public StereoResampler(double inRate, double outRate) => Ratio = inRate / outRate;

    public int MaxOutputFrames(int inputFrames) => (int)((inputFrames + 3) / Ratio) + 2;

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int inFrames = input.Length / 2, frames = inFrames + 3;
        if (_work.Length < 2 * frames) _work = new float[2 * frames * 2];
        _hist.CopyTo(_work, 0);
        input.CopyTo(_work.AsSpan(6));
        int n = 0;
        double pos = _pos, ratio = Ratio;
        var w = _work;
        while (pos + 2 < frames)
        {
            int i = (int)pos;
            float t = (float)(pos - i);
            int b = 2 * i;
            for (int c = 0; c < 2; c++)
            {
                float p0 = w[b - 2 + c], p1 = w[b + c], p2 = w[b + 2 + c], p3 = w[b + 4 + c];
                output[2 * n + c] = p1 + 0.5f * t * (p2 - p0 + t * (2 * p0 - 5 * p1 + 4 * p2 - p3 + t * (3 * (p1 - p2) + p3 - p0)));
            }
            n++;
            pos += ratio;
        }
        Array.Copy(_work, 2 * (frames - 3), _hist, 0, 6);
        _pos = pos - (frames - 3);
        return n;
    }
}
