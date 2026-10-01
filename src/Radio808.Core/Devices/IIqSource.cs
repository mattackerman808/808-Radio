using System;

namespace Radio808.Core.Devices;

/// <summary>A source of complex samples at a fixed rate, tunable: an RTL-SDR, or a recording.</summary>
public interface IIqSource : IDisposable
{
    /// <summary>Interleaved float I/Q, on the source's own thread.</summary>
    event IqHandler? Samples;
    /// <summary>Streaming stopped unexpectedly (e.g. the dongle was unplugged).</summary>
    event Action<string>? Stopped;

    string Name { get; }
    uint SampleRate { get; set; }
    long Frequency { get; set; }
    /// <summary>Tuner gain in dB, or null for automatic.</summary>
    double? Gain { get; set; }
    /// <summary>Gain steps the tuner supports (dB, ascending); empty if not adjustable.</summary>
    System.Collections.Generic.IReadOnlyList<double> Gains { get; }
    void Start();
    void Stop();
}
