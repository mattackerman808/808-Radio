using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Radio808.Core.Radio;

/// <summary>
/// Keeps the tuner gain at the peak for the station being played.
///
/// Two failure modes bound the right gain. Too much and the RTL's 8-bit ADC clips: the "front end falls off", and
/// on a strong station HD MER collapses within one 3 dB step (measured on 98.5: 0.08% clipping / MER 12 dB at
/// 16.6 dB gain, 7.6% / 10.5 dB at 19.7, 32% / 6.3 at 22.9). Too little and the tuner's noise dominates on weak
/// stations. So:
/// <list type="bullet">
/// <item>An overload guard (every 100 ms) drops a step as soon as clipping exceeds <see cref="ClipHigh"/>.</item>
/// <item>After each tune, acquisition backs off any overload, then climbs/descends on a fast quality metric (pilot SNR
///   for stereo, envelope ripple otherwise), preferring the lowest gain that's as good as the best.</item>
/// <item>Then tracking: every ~15 s it tries one step up or down against a fresh baseline and keeps it only if the
///   quality clearly improves. With HD synced the metric is nrsc5's MER, which is what matters for HD.</item>
/// </list>
/// The best gain per station is remembered, so returning to a station starts at its peak.
/// </summary>
public sealed class GainOptimizer : IDisposable
{
    /// <summary>Clipping that triggers an immediate step down (fraction of samples, over 100 ms).</summary>
    public const double ClipHigh = 0.006;
    /// <summary>
    /// Clipping above which acquisition won't go up. HD degrades before clipping gets heavy: on 97.3, 0.04-0.06%
    /// clipping already cost 2.3 dB of MER (98.5 tolerated 0.08%), so the threshold is low.
    /// </summary>
    public const double ClipLimit = 0.0002;

    public enum Phase { Off, Acquiring, Tracking, Paused }

    private readonly RadioEngine _e;
    private readonly IReadOnlyList<double> _gains;
    private readonly Dictionary<long, int> _best = new();
    private readonly CancellationTokenSource _cts = new();
    private int _idx;
    private int _gen = -1;
    private volatile bool _enabled;
    private Task? _loop;
    private double _clipRecent;
    private DateTime? _hotSince;

    public Phase State { get; private set; } = Phase.Off;
    /// <summary>The quality metric in use: "MER", "pilot SNR", "ripple", or "" when idle.</summary>
    public string Metric { get; private set; } = "";
    public double LastScore { get; private set; } = double.NaN;
    /// <summary>Recent ADC clipping (fraction of samples, smoothed).</summary>
    public double Clipping => _clipRecent;
    /// <summary>True while the ADC is overloading.</summary>
    public bool Overload => _clipRecent > ClipLimit;
    public int Moves { get; private set; }
    public double GainDb => _gains.Count > 0 ? _gains[_idx] : 0;

    internal GainOptimizer(RadioEngine engine, IReadOnlyList<double> gains, double startDb)
    {
        _e = engine;
        _gains = gains;
        _idx = Nearest(startDb);
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_gains.Count == 0) value = false;
            _enabled = value;
            if (value) _gen = -1;
            _loop ??= Task.Run(() => Run(_cts.Token));   // runs either way: it also monitors overload
        }
    }

    private int Nearest(double db)
    {
        int best = 0;
        for (int i = 1; i < _gains.Count; i++) if (Math.Abs(_gains[i] - db) < Math.Abs(_gains[best] - db)) best = i;
        return best;
    }

    private void Apply(int idx)
    {
        _idx = Math.Clamp(idx, 0, _gains.Count - 1);
        _e.ApplyGain(_gains[_idx]);
        _e.TakeClipFraction();   // measurements start fresh at the new gain
        // a network dongle's samples from before the change are still arriving for a while: don't judge by them
        _settleUntil = DateTime.UtcNow + _e.Device.ControlLatency;
    }

    private DateTime _settleUntil;

    // ------------------------------------------------------------------ the loop

    private async Task Run(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_enabled)
                {
                    // fixed gain: still watch for overload, so it can be shown
                    State = Phase.Off; Metric = "";
                    await Task.Delay(200, ct);
                    _clipRecent += 0.3 * (_e.TakeClipFraction() - _clipRecent);
                    continue;
                }
                if (_e.IsSeeking) { State = Phase.Paused; _gen = -1; await Task.Delay(200, ct); continue; }
                if (_e.RetuneGeneration != _gen)
                {
                    _gen = _e.RetuneGeneration;
                    await Acquire(ct);
                    continue;
                }
                State = Phase.Tracking;
                await Track(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Waits, running the overload guard. Returns false if the station changed (abort what you're doing).</summary>
    private async Task<bool> Wait(double seconds, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(100, ct);
            double clip = _e.TakeClipFraction();
            if (!_enabled || _e.IsSeeking || _e.RetuneGeneration != _gen) return false;
            if (DateTime.UtcNow < _settleUntil) continue;
            _clipRecent += 0.3 * (clip - _clipRecent);
            if (clip > ClipHigh && _idx > 0)
            {
                SetCeiling(_idx);
                Apply(_idx - (clip > 0.05 ? 3 : 1));   // the front end fell off: back down now
                Moves++;
                Remember();
                return false;
            }
            // soft guard: light but persistent clipping already costs HD MER
            if (_clipRecent > 2 * ClipLimit) _hotSince ??= DateTime.UtcNow; else _hotSince = null;
            if (_hotSince is DateTime hot && DateTime.UtcNow - hot > TimeSpan.FromSeconds(2) && _idx > 0)
            {
                _hotSince = null;
                SetCeiling(_idx);
                Apply(_idx - 1);
                _clipRecent = 0;
                Moves++;
                Remember();
                return false;
            }
        }
        return true;
    }

    private void Remember() => _best[Channel(_e.Frequency)] = _idx;

    // The lowest gain seen clipping on each station: probes stay below it (probing into the cliff costs a few
    // seconds of bad HD). Forgotten after a few minutes, since conditions change.
    private readonly Dictionary<long, (int idx, DateTime until)> _ceiling = new();

    private void SetCeiling(int idx) => _ceiling[Channel(_e.Frequency)] = (idx, DateTime.UtcNow.AddMinutes(10));

    private bool BelowCeiling(int idx) =>
        !_ceiling.TryGetValue(Channel(_e.Frequency), out var c) || DateTime.UtcNow > c.until || idx < c.idx;
    private static long Channel(long hz) => (hz + 50_000) / 100_000;

    /// <summary>Fast quality metric (higher is better): pilot SNR for stereo stations, else -20 log10(ripple).</summary>
    private (double score, string metric) FastScore()
    {
        var st = _e.Receiver.Stereo;
        if (st.PilotLocked) return (st.PilotSnrDb, "pilot SNR");
        double r = Math.Max(1e-3, _e.Receiver.Equalizer.Ripple);
        return (-20 * Math.Log10(r), "ripple");
    }

    /// <summary>Averages the fast metric over a window; NaN if the station changed or clipped meanwhile.</summary>
    private async Task<double> MeasureFast(double settle, double window, CancellationToken ct)
    {
        if (!await Wait(settle, ct)) return double.NaN;
        double sum = 0; int n = 0;
        var until = DateTime.UtcNow.AddSeconds(window);
        while (DateTime.UtcNow < until)
        {
            if (!await Wait(0.1, ct)) return double.NaN;
            var (s, m) = FastScore();
            sum += s; n++; Metric = m;
        }
        return n > 0 ? sum / n : double.NaN;
    }

    /// <summary>
    /// Finds the highest gain that doesn't clip, then backs off one step for headroom. The RTL's noise figure keeps
    /// improving with gain right up to where the ADC saturates (98.5: MER flat at 12 dB from 7.7 to 16.6 dB, then a
    /// cliff), and the pilot/ripple metrics are too flat on strong stations to steer by, so clipping is the guide here
    /// and the quality metrics fine-tune afterwards.
    /// </summary>
    private async Task Acquire(CancellationToken ct)
    {
        State = Phase.Acquiring;
        Metric = "clipping";
        if (_best.TryGetValue(Channel(_e.Frequency), out int known)) Apply(known);
        if (!await Wait(0.8, ct)) return;   // retune skip

        double clip = await MeasureClip(ct);
        if (double.IsNaN(clip)) return;
        if (clip > ClipLimit)
        {
            // too hot: step down until it isn't
            while (clip > ClipLimit && _idx > 0)
            {
                SetCeiling(_idx);
                Apply(_idx - (clip > 0.05 ? 3 : 1));
                clip = await MeasureClip(ct);
                if (double.IsNaN(clip)) return;
            }
        }
        else
        {
            // climb until the first step that clips, then come back two (one for headroom)
            while (_idx < _gains.Count - 1)
            {
                Apply(_idx + 1);
                clip = await MeasureClip(ct);
                if (double.IsNaN(clip)) return;
                if (clip > ClipLimit) { SetCeiling(_idx); Apply(Math.Max(0, _idx - 2)); break; }
            }
        }
        if (!await Wait(0.3, ct)) return;
        var (s, m) = FastScore();
        LastScore = s; Metric = m;
        Remember();
        State = Phase.Tracking;
    }

    /// <summary>ADC clipping at the current gain: 100 ms to settle, then 450 ms measured. NaN if the station changed.</summary>
    private async Task<double> MeasureClip(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100) + _e.Device.ControlLatency, ct);
        _e.TakeClipFraction();
        await Task.Delay(450, ct);   // long enough that a quiet moment in the music doesn't hide the peaks
        if (!_enabled || _e.IsSeeking || _e.RetuneGeneration != _gen) return double.NaN;
        double clip = _e.TakeClipFraction();
        _clipRecent += 0.5 * (clip - _clipRecent);
        return clip;
    }

    private int _probeDir = 1;

    private async Task Track(CancellationToken ct)
    {
        if (!await Wait(12, ct)) return;   // quiet time between probes

        bool hd = _e.Hd.Synced && _e.Hd.MerLower > 0;
        string metric = hd ? "MER" : _e.Receiver.Stereo.PilotLocked ? "pilot SNR" : "ripple";
        double margin = hd ? 0.3 : metric == "pilot SNR" ? 1.0 : 0.5;
        double window = hd ? 3.2 : 1.2, settle = hd ? 1.6 : 0.6;

        double baseline = await Score(metric, 0, window, ct);
        if (double.IsNaN(baseline)) return;
        int from = _idx, to = _idx + _probeDir;
        _probeDir = -_probeDir;   // alternate up and down
        if (to < 0 || to >= _gains.Count) return;
        if (to > from && (_clipRecent > ClipLimit || !BelowCeiling(to))) return;   // known cliff: don't probe up

        Apply(to);
        double probe = await Score(metric, settle, window, ct);
        if (to > from && _clipRecent > ClipLimit) SetCeiling(to);   // found the edge while probing
        if (double.IsNaN(probe))
        {
            if (_e.RetuneGeneration == _gen && _idx == to) { Apply(from); if (to > from) SetCeiling(to); }   // HD lost or clipped
            return;
        }
        // keep the probe only if it's clearly better (ties stay put: no drifting)
        bool keep = probe > baseline + margin;
        if (keep) { Moves++; LastScore = probe; Remember(); }
        else
        {
            Apply(from);
            LastScore = baseline;
            if (to > from && probe < baseline - margin) SetCeiling(to);   // up made it worse: that's the edge
        }
        Metric = metric;
    }

    private async Task<double> Score(string metric, double settle, double window, CancellationToken ct)
    {
        if (settle > 0 && !await Wait(settle, ct)) return double.NaN;
        double sum = 0; int n = 0;
        var until = DateTime.UtcNow.AddSeconds(window);
        while (DateTime.UtcNow < until)
        {
            if (!await Wait(0.1, ct)) return double.NaN;
            double v;
            if (metric == "MER")
            {
                var h = _e.Hd;
                if (!h.Synced) return double.NaN;   // lost HD at this gain: definitely worse
                v = (h.MerLower + h.MerUpper) / 2;
            }
            else if (metric == "pilot SNR")
            {
                var st = _e.Receiver.Stereo;
                if (!st.PilotLocked) return double.NaN;
                v = st.PilotSnrDb;
            }
            else v = -20 * Math.Log10(Math.Max(1e-3, _e.Receiver.Equalizer.Ripple));
            sum += v; n++;
        }
        Metric = metric;
        return n > 0 ? sum / n : double.NaN;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
