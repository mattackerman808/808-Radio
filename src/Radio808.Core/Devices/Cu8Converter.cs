using System;

namespace Radio808.Core.Devices;

/// <summary>
/// Converts the RTL's 8-bit unsigned I/Q to float (about -1..1), with a slow DC blocker that removes the RTL's center
/// spike. Not thread-safe: one per stream.
/// </summary>
internal sealed class Cu8Converter
{
    private readonly float[] _lut = new float[256];
    private float _dcI, _dcQ;

    public Cu8Converter()
    {
        for (int i = 0; i < 256; i++) _lut[i] = (i - 127.4f) / 128f;
    }

    /// <summary>Converts <paramref name="src"/> (an even number of bytes) into the start of <paramref name="dest"/>.</summary>
    public void Convert(ReadOnlySpan<byte> src, Span<float> dest)
    {
        float dcI = _dcI, dcQ = _dcQ;
        const float a = 1e-5f;
        for (int i = 0; i + 1 < src.Length; i += 2)
        {
            float x = _lut[src[i]], y = _lut[src[i + 1]];
            dcI += a * (x - dcI); dcQ += a * (y - dcQ);
            dest[i] = x - dcI; dest[i + 1] = y - dcQ;
        }
        _dcI = dcI; _dcQ = dcQ;
    }
}
