using System;
using System.Collections.Generic;
using System.Threading;
using Radio808.Core.Native;

namespace Radio808.Core.Hd;

/// <summary>
/// Blends HD audio into the analog FM audio like an HD receiver. Runs in the analog audio stream (stereo at the
/// receiver's audio rate), whose sample count is the output clock: it's derived from the dongle's sample clock, so
/// the analog and HD timelines can't drift apart.
///
/// Time alignment: broadcasters delay the analog so it matches HD, but after nrsc5 the HD audio still arrives some
/// seconds ahead of the analog. The offset is measured by cross-correlating loudness envelopes of the analog and HD
/// audio; then the HD sample matching what the analog plays right now is played, so switching is seamless.
///
/// HD frames nrsc5 marks unavailable stay in the timeline as gaps, so HD stays in step through dropouts; since HD
/// arrives early, the blender sees a gap coming and crossfades to analog before it. After dropouts it requires
/// progressively longer runs of clean HD (2, 4, 8 ... 30 s) before switching back, so weak stations don't flap.
///
/// HD2+ carry different content from the analog, so they can't be aligned: they play from a short buffer and fall
/// back to analog on dropouts.
/// </summary>
public sealed unsafe class HdBlender
{
    private const double InRate = Nrsc5Native.AudioSampleRate;   // 44100
    private const int Capacity = 44100 * 16;                      // 16 s of HD history + lookahead
    private const double FadeSeconds = 0.10;
    private const double LookaheadSeconds = 0.15;                 // must exceed the fade
    private const double LegacyBufferSeconds = 0.50;
    private const double EnvRate = 200;                           // envelope buckets per second
    private const int EnvRing = 200 * 40;                         // 40 s of envelopes
    private const double AlignWaitSeconds = 15;                   // then give up and play unaligned

    private readonly double _outRate;
    private readonly object _lock = new();

    // HD timeline: absolute frame index since the last reset; ring buffer of the last Capacity frames.
    private readonly float[] _fifo = new float[Capacity * 2];
    private long _written;
    private readonly List<(long Start, long End)> _gaps = new();   // unavailable frames, oldest first
    private long _goodFramesTotal;
    private double _readPos;   // absolute (fractional) HD frame playing now

    // Output clock (never reset) and blend state.
    private long _outSamples;
    private double _now;
    private bool _hdOn;
    private float _mix;
    private float _gain = 0.5f;
    private double _analogPower = 1e-3, _hdPower = 1e-3;

    // Hysteresis.
    private double _requiredGood = 0.5;
    private double _lastDropout = double.NegativeInfinity;
    private int _dropouts;

    // Alignment: output time at which the analog played HD frame 0's content.
    private double _t0 = double.NaN;   // output time when HD frame 0 arrived
    private double _align = double.NaN;
    private double _alignScore;
    private bool _correlating;
    private double _nextCorrelate;
    private int _session;

    // Loudness envelopes (mean |mono| per 5 ms bucket).
    private readonly float[] _aEnv = new float[EnvRing];   // indexed by output-clock bucket
    private long _aBucket = -1; private double _aAcc; private int _aCnt;
    private readonly float[] _hEnv = new float[EnvRing];   // indexed by HD-frame bucket, NaN = gap
    private long _hBucket = -1; private double _hAcc; private int _hCnt; private bool _hBad;

    public HdBlender(double outputRate)
    {
        _outRate = outputRate;
        Array.Fill(_hEnv, float.NaN);
    }

    /// <summary>Stay on analog even when HD is available.</summary>
    public volatile bool ForceAnalog;

    /// <summary>
    /// Swap nrsc5's left and right. On all five stations tested (2026-09-30), nrsc5's HD audio was mirrored relative
    /// to the analog stereo decode, which follows FCC 73.322 and is verified with a synthetic signal. nrsc5 rebuilds
    /// HDC stereo with faad2's DRM parametric-stereo code (drm_add_pan), so the likely cause is a pan sign
    /// convention that differs between DRM and HDC. Without the swap the image flips at every analog/HD switch.
    /// </summary>
    public volatile bool SwapHdChannels = true;
    /// <summary>Selected program; only HD1 (0) simulcasts the analog and can be aligned.</summary>
    public volatile int Program;

    public bool PlayingHd => _mix > 0;
    public bool Aligned => !double.IsNaN(_align);
    /// <summary>How far HD content runs ahead of the analog (seconds), once measured.</summary>
    public double HdLeadSeconds => Aligned ? _align - _t0 : double.NaN;
    public double AlignScore => _alignScore;
    /// <summary>Loudness-match gain applied to HD.</summary>
    public float HdGain => _gain;

    /// <summary>After a dropout: seconds of clean signal still needed before HD resumes.</summary>
    public double RetryIn
    {
        get
        {
            if (_dropouts == 0) return 0;
            lock (_lock) return Math.Max(0, _requiredGood - CleanRunSeconds());
        }
    }

    // ------------------------------------------------------------------ decoder thread

    /// <summary>nrsc5 audio: interleaved int16 stereo at 44.1 kHz. Unavailable frames are kept as gaps.</summary>
    public void Add(short* samples, int count, bool unavailable)
    {
        int frames = count / 2;
        bool bad = unavailable;
        lock (_lock)
        {
            if (double.IsNaN(_t0)) _t0 = _now;
            double pow = 0;
            int li = SwapHdChannels ? 1 : 0, ri = 1 - li;
            for (int i = 0; i < frames; i++)
            {
                long abs = _written + i;
                int w = (int)(abs % Capacity);
                float l = bad ? 0 : samples[2 * i + li] / 32768f, r = bad ? 0 : samples[2 * i + ri] / 32768f;
                _fifo[2 * w] = l;
                _fifo[2 * w + 1] = r;
                pow += l * l + r * r;

                long b = (long)(abs * EnvRate / InRate);   // HD envelope, bucketed on the HD content timeline
                if (b != _hBucket)
                {
                    if (_hBucket >= 0) _hEnv[_hBucket % EnvRing] = _hBad || _hCnt == 0 ? float.NaN : (float)(_hAcc / _hCnt);
                    _hBucket = b; _hAcc = 0; _hCnt = 0; _hBad = false;
                }
                _hAcc += Math.Abs(l + r) * 0.5; _hCnt++; _hBad |= bad;
            }
            if (bad)
            {
                long end = _written + frames - 1;
                if (_gaps.Count > 0 && _gaps[^1].End >= _written - 1)
                    _gaps[^1] = (_gaps[^1].Start, end);
                else
                    _gaps.Add((_written, end));
                while (_gaps.Count > 0 && _gaps[0].End < _written - Capacity) _gaps.RemoveAt(0);
            }
            else
            {
                _goodFramesTotal += frames;
                TrackPower(0, pow, frames, InRate);
            }
            _written += frames;
        }
    }

    /// <summary>New station or program: forget the HD timeline and alignment, back to analog.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            ResetTimeline();
            _hdOn = false;
            _mix = 0;
            _requiredGood = 0.5;
            _dropouts = 0;
            _lastDropout = double.NegativeInfinity;
        }
    }

    /// <summary>
    /// nrsc5 lost sync: after re-sync the frame count no longer matches the broadcast timeline. Restart the
    /// timeline and re-measure alignment, but keep the weak-signal hysteresis (this is a dropout).
    /// </summary>
    public void TimelineLost()
    {
        lock (_lock)
        {
            if (_hdOn) { _hdOn = false; Dropout(); }
            ResetTimeline();
        }
    }

    private void ResetTimeline()
    {
        _written = 0;
        _gaps.Clear();
        _goodFramesTotal = 0;
        _readPos = 0;
        _t0 = double.NaN;
        _align = double.NaN;
        _alignScore = 0;
        _nextCorrelate = 0;
        _session++;
        Array.Fill(_hEnv, float.NaN);
        _hBucket = -1; _hAcc = 0; _hCnt = 0; _hBad = false;
    }

    // ------------------------------------------------------------------ audio path

    /// <summary>Blends HD into interleaved analog stereo, in place.</summary>
    public void Process(Span<float> buffer)
    {
        double outRate = _outRate;
        int frames = buffer.Length / 2;
        lock (_lock)
        {
            // analog envelope on the output clock
            double aPow = 0;
            for (int i = 0; i < frames; i++)
            {
                float aL = buffer[2 * i], aR = buffer[2 * i + 1];
                aPow += aL * aL + aR * aR;
                long b = (long)((_outSamples + i) * EnvRate / outRate);
                if (b != _aBucket)
                {
                    if (_aBucket >= 0) _aEnv[_aBucket % EnvRing] = _aCnt == 0 ? 0 : (float)(_aAcc / _aCnt);
                    _aBucket = b; _aAcc = 0; _aCnt = 0;
                }
                _aAcc += Math.Abs(aL + aR) * 0.5; _aCnt++;
            }
            TrackPower(aPow, 0, frames, outRate);

            MaybeStartCorrelation();

            // where the HD read position should be right now
            bool alignable = Program == 0;
            bool aligned = alignable && Aligned;
            bool waitingForAlign = alignable && !aligned && _goodFramesTotal / InRate < AlignWaitSeconds;
            double desired = aligned ? (_now - _align) * InRate : _written - LegacyBufferSeconds * InRate;
            double baseStep = InRate / outRate;
            double span = frames * baseStep;

            double err = desired - _readPos;
            if (_mix == 0 || Math.Abs(err) > 0.25 * InRate)
            {
                if (_mix > 0) _hdOn = false;   // big jump while audible: fade out first
                else _readPos = desired;
                err = desired - _readPos;
            }
            double step = baseStep * (1 + Math.Clamp(err / InRate * 0.02, -0.002, 0.002));

            // usable for this buffer plus the lookahead (so fades finish before a gap)?
            double lookEnd = _readPos + span + LookaheadSeconds * InRate;
            bool available = RangeGood(_readPos - 2, lookEnd + 2);
            double cleanRun = CleanRunSeconds();

            bool want = !ForceAnalog && !waitingForAlign && available;
            if (_hdOn && !want)
            {
                _hdOn = false;
                if (!ForceAnalog && !available) Dropout();
            }
            else if (!_hdOn && want && cleanRun >= _requiredGood)
            {
                _hdOn = true;
            }

            if (!_hdOn && _mix == 0)
            {
                _readPos += span;   // keep tracking the timeline
                _outSamples += frames;
                _now = _outSamples / outRate;
                return;             // pure analog
            }

            float fadeStep = (float)(1 / (FadeSeconds * outRate));
            for (int i = 0; i < frames; i++)
            {
                float aL = buffer[2 * i], aR = buffer[2 * i + 1];
                float hL = 0, hR = 0;
                long i0 = (long)Math.Floor(_readPos);
                if (i0 - 1 >= _written - Capacity && i0 + 2 < _written && i0 >= 1)
                {
                    float t = (float)(_readPos - i0);
                    int im = (int)((i0 - 1) % Capacity), a0 = (int)(i0 % Capacity), a1 = (int)((i0 + 1) % Capacity), a2 = (int)((i0 + 2) % Capacity);
                    hL = CatmullRom(_fifo[2 * im], _fifo[2 * a0], _fifo[2 * a1], _fifo[2 * a2], t);
                    hR = CatmullRom(_fifo[2 * im + 1], _fifo[2 * a0 + 1], _fifo[2 * a1 + 1], _fifo[2 * a2 + 1], t);
                }
                _readPos += step;

                _mix = _hdOn ? Math.Min(1, _mix + fadeStep) : Math.Max(0, _mix - fadeStep);
                float g = _gain * _mix, a = 1 - _mix;
                buffer[2 * i] = aL * a + hL * g;
                buffer[2 * i + 1] = aR * a + hR * g;
            }
            _outSamples += frames;
            _now = _outSamples / outRate;
        }
    }

    /// <summary>True if frames [from, to] have all arrived, are still buffered, and contain no gap.</summary>
    private bool RangeGood(double from, double to)
    {
        long a = (long)Math.Floor(from), b = (long)Math.Ceiling(to);
        if (a < 0 || b >= _written || a < _written - Capacity) return false;
        foreach (var (s, e) in _gaps)
            if (s <= b && e >= a) return false;
        return true;
    }

    /// <summary>Seconds of clean HD up to the read position (gaps ahead don't count; the lookahead handles those).</summary>
    private double CleanRunSeconds()
    {
        long pos = (long)_readPos;
        long lastBadBefore = -1;
        foreach (var (s, e) in _gaps)
            if (s <= pos) lastBadBefore = Math.Min(e, pos);
        return (pos - lastBadBefore) / InRate;
    }

    private void Dropout()
    {
        // Dropouts less than a minute apart escalate the clean-signal requirement.
        _dropouts = _now - _lastDropout < 60 ? _dropouts + 1 : 1;
        _lastDropout = _now;
        _requiredGood = Math.Min(30, Math.Pow(2, _dropouts));
    }

    // ------------------------------------------------------------------ alignment

    private void MaybeStartCorrelation()
    {
        if (_correlating || Program != 0 || double.IsNaN(_t0) || _now < _nextCorrelate) return;
        if (_goodFramesTotal < InRate * 6) return;   // need some clean HD first
        _nextCorrelate = _now + (Aligned ? 20 : 2);
        _correlating = true;

        var a = (float[])_aEnv.Clone();
        var h = (float[])_hEnv.Clone();
        long aLast = _aBucket - 1, hLast = _hBucket - 1;
        double t0 = _t0;
        int session = _session;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            double align = Correlate(a, aLast, h, hLast, t0, out double score);
            lock (_lock)
            {
                _correlating = false;
                if (session != _session || double.IsNaN(align)) return;
                // accept a new estimate; while HD is audible the servo slews to it
                if (!Aligned || Math.Abs(align - _align) < 0.03 || score > _alignScore + 0.05 || score > 0.7)
                {
                    _align = align;
                    _alignScore = score;
                }
            }
        });
    }

    /// <summary>
    /// Finds the output time at which the analog played HD frame 0's content: maximizes the normalized correlation
    /// between HD envelope bucket m and analog envelope bucket k0 + m.
    /// </summary>
    internal static double Correlate(float[] aEnv, long aLast, float[] hEnv, long hLast, double t0, out double score)
    {
        score = 0;
        long kMin = (long)((t0 - 3) * EnvRate), kMax = (long)((t0 + 14) * EnvRate);
        long hFirst = Math.Max(0, hLast - EnvRing + 1), aFirst = Math.Max(0, aLast - EnvRing + 1);
        var scores = new double[kMax - kMin + 1];
        double best = -2; long bestK = -1;
        for (long k0 = kMin; k0 <= kMax; k0++)
        {
            double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0; int n = 0;
            for (long m = hFirst; m <= hLast; m++)
            {
                long k = k0 + m;
                if (k < aFirst || k > aLast) continue;
                float hv = hEnv[m % EnvRing];
                if (float.IsNaN(hv)) continue;
                float av = aEnv[k % EnvRing];
                sa += av; sb += hv; saa += av * av; sbb += hv * hv; sab += av * hv; n++;
            }
            double c = double.NaN;
            if (n >= EnvRate * 4)
            {
                double cov = sab - sa * sb / n, va = saa - sa * sa / n, vb = sbb - sb * sb / n;
                c = va > 0 && vb > 0 ? cov / Math.Sqrt(va * vb) : double.NaN;
            }
            scores[k0 - kMin] = c;
            if (!double.IsNaN(c) && c > best) { best = c; bestK = k0; }
        }
        if (bestK < 0 || best < 0.5) return double.NaN;

        // the peak must stand out from everything more than 50 ms away
        double runnerUp = -2;
        for (long k0 = kMin; k0 <= kMax; k0++)
            if (Math.Abs(k0 - bestK) > EnvRate * 0.05 && !double.IsNaN(scores[k0 - kMin]))
                runnerUp = Math.Max(runnerUp, scores[k0 - kMin]);
        if (best - runnerUp < 0.08) return double.NaN;

        // sub-bucket refinement (parabola through the peak and its neighbours)
        double refine = 0;
        long i = bestK - kMin;
        if (i > 0 && i < scores.Length - 1 && !double.IsNaN(scores[i - 1]) && !double.IsNaN(scores[i + 1]))
        {
            double y0 = scores[i - 1], y1 = scores[i], y2 = scores[i + 1];
            double den = y0 - 2 * y1 + y2;
            if (den < 0) refine = Math.Clamp(0.5 * (y0 - y2) / den, -0.5, 0.5);
        }
        score = best;
        return (bestK + refine) / EnvRate;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Loudness match: slow (~3 s) power averages of analog and HD; gain = sqrt of the ratio.</summary>
    private void TrackPower(double analogPow, double hdPow, int frames, double rate)
    {
        double alpha = Math.Min(1, frames / (3.0 * rate));
        if (analogPow > 1e-6 * frames) _analogPower += (analogPow / frames - _analogPower) * alpha;
        if (hdPow > 1e-6 * frames) _hdPower += (hdPow / frames - _hdPower) * alpha;
        _gain = (float)Math.Clamp(Math.Sqrt(_analogPower / _hdPower), 0.25, 2.0);
    }

    private static float CatmullRom(float p0, float p1, float p2, float p3, float t) =>
        p1 + 0.5f * t * (p2 - p0 + t * (2 * p0 - 5 * p1 + 4 * p2 - p3 + t * (3 * (p1 - p2) + p3 - p0)));
}
