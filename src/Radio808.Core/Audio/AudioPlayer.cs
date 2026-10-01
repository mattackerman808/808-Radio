using System;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Radio808.Core.Dsp;

namespace Radio808.Core.Audio;

/// <summary>
/// Plays stereo audio through WASAPI (shared mode, the default output device). Audio written from the radio's clock
/// domain is resampled to the output rate; the ratio is trimmed by a few hundred ppm to hold the buffer at its target
/// level, which absorbs the drift between the dongle's crystal and the sound card's.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    public const int OutputRate = 48_000;
    private const double TargetMs = 150, MaxMs = 600;

    private readonly double _inRate;
    private readonly StereoResampler _resampler;
    private readonly Ring _ring;
    private WasapiPlayer? _out;
    private float[] _tmp = new float[0];
    private double _fillAvg = -1, _integ, _settle, _setpoint;

    /// <summary>0..1 linear gain.</summary>
    public float Volume { get; set; } = 0.8f;
    public bool Muted { get; set; }
    /// <summary>Current resampling trim in ppm (positive = radio clock fast relative to the sound card).</summary>
    public double DriftPpm { get; private set; }
    public double BufferedMs => _ring.Count / 2 * 1000.0 / OutputRate;
    public int Underruns => _ring.Underruns;

    private AudioPlayer(double inputRate)
    {
        _inRate = inputRate;
        _resampler = new StereoResampler(inputRate, OutputRate);
        _ring = new Ring((int)(MaxMs / 1000 * OutputRate) * 2, (int)(TargetMs / 1000 * OutputRate) * 2, this);
    }

    /// <summary>Opens the default output device; playback follows Windows when the default device changes.</summary>
    public static async Task<AudioPlayer> CreateAsync(double inputRate)
    {
        var p = new AudioPlayer(inputRate);
        p._out = await new WasapiPlayerBuilder()
            .WithDefaultDeviceStreamRouting()
            .WithSharedMode()
            .WithLatency(40)
            .WithCategory(AudioStreamCategory.Media)
            .BuildAsync().ConfigureAwait(false);
        p._out.Init(new SampleToWaveProvider(p._ring));
        p._out.Play();
        return p;
    }

    public string DeviceName => _out?.DeviceFriendlyName ?? "";

    /// <summary>Queues interleaved stereo audio at the input rate. Call from one thread.</summary>
    public void Write(ReadOnlySpan<float> stereo)
    {
        // Drift control: a PI loop on the buffer level. A trim of t drains the buffer at t * 1000 ms per second.
        // Ki = Kp^2 * 1000 / 4 makes it critically damped, with a time constant of 2 / (1000 Kp) = 13 s.
        // (+-1000 ppm is under 2 cents of pitch: inaudible.)
        double dt = stereo.Length / 2 / _inRate;
        double fill = BufferedMs;
        // The setpoint is the level the buffer settles at in the first 2 s (WASAPI keeps part of the audio in the
        // device buffer, so it's below the priming level), so the loop only corrects drift, not startup.
        if (!_ring.Primed) { _settle = 0; _fillAvg = -1; }
        else if (_settle < 2.0)
        {
            _settle += dt;
            if (_settle > 1.0) _fillAvg = _fillAvg < 0 ? fill : _fillAvg + Math.Min(1, dt / 0.5) * (fill - _fillAvg);
            _setpoint = _fillAvg;
        }
        else
        {
            _fillAvg += Math.Min(1, dt / 1.0) * (fill - _fillAvg);
            double err = _fillAvg - _setpoint;
            const double kp = 1.5e-4, ki = kp * kp * 1000 / 4;
            _integ = Math.Clamp(_integ + ki * err * dt, -1.5e-3, 1.5e-3);
            double trim = Math.Clamp(kp * err + _integ, -3e-3, 3e-3);
            DriftPpm = _integ * 1e6;   // the integrator holds the steady-state clock offset
            _resampler.Ratio = _inRate / OutputRate * (1 + trim);
        }
        int max = 2 * _resampler.MaxOutputFrames(stereo.Length / 2);
        if (_tmp.Length < max) _tmp = new float[max * 2];
        int frames = _resampler.Process(stereo, _tmp);
        _ring.Write(_tmp.AsSpan(0, 2 * frames));
    }

    public void Dispose()
    {
        _out?.Dispose();
        _out = null;
    }

    /// <summary>Lock-protected ring buffer read by WASAPI. Starts silent until the target level is reached.</summary>
    private sealed class Ring : ISampleProvider
    {
        private readonly float[] _buf;
        private readonly int _target;
        private readonly AudioPlayer _owner;
        private int _read, _count;
        private readonly object _lock = new();

        public Ring(int capacity, int target, AudioPlayer owner)
        {
            _buf = new float[capacity];
            _target = target;
            _owner = owner;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(OutputRate, 2);
        public int Count { get { lock (_lock) return _count; } }
        public bool Primed { get; private set; }
        public int Underruns { get; private set; }

        public void Write(ReadOnlySpan<float> data)
        {
            lock (_lock)
            {
                int n = Math.Min(data.Length, _buf.Length - _count) & ~1;   // drop on overflow
                int w = (_read + _count) % _buf.Length;
                int first = Math.Min(n, _buf.Length - w);
                data.Slice(0, first).CopyTo(_buf.AsSpan(w));
                data.Slice(first, n - first).CopyTo(_buf);
                _count += n;
                if (!Primed && _count >= _target) Primed = true;
            }
        }

        public int Read(Span<float> buffer)
        {
            float gain = _owner.Muted ? 0 : _owner.Volume;
            int count = buffer.Length, n = 0;
            lock (_lock)
            {
                if (Primed)
                {
                    n = Math.Min(count, _count);
                    int first = Math.Min(n, _buf.Length - _read);
                    _buf.AsSpan(_read, first).CopyTo(buffer);
                    _buf.AsSpan(0, n - first).CopyTo(buffer.Slice(first));
                    _read = (_read + n) % _buf.Length;
                    _count -= n;
                    if (n < count) { Primed = false; Underruns++; }
                }
            }
            for (int i = 0; i < n; i++) buffer[i] *= gain;
            buffer.Slice(n).Clear();
            return count;   // never end-of-stream: silence while starved
        }
    }
}
