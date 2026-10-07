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
/// stations. And next to a strong station the tuner itself overloads well before the ADC clips: the noise floor
/// rises faster than the gain, which only the spectrum shows (<see cref="SignalQuality"/>). So:
/// <list type="bullet">
/// <item>An overload guard (every 100 ms) drops a step as soon as clipping exceeds <see cref="ClipHigh"/>.</item>
/// <item>After each tune, acquisition backs off any overload, then walks the gain on the spectral score (the HD
///   sidebands over the floor once the station has shown them, else the carrier), with clipping as a ceiling,
///   preferring the lowest gain that's as good as the best.</item>
/// <item>Then tracking: every ~15 s (~7 s while HD is on the air but not yet decoding) it tries one step up or down
///   against a fresh baseline and keeps it only if the score clearly improves. With HD synced the metric is nrsc5's
///   MER, which is what matters for HD.</item>
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
        _e.TakeQuality();
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
                // a new station, or an acquisition the overload guard cut short (a hot station tuned at a high gain)
                if (_e.RetuneGeneration != _gen || !_acquired)
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

    // ------------------------------------------------------------------ the spectral score

    /// <summary>The station being played has shown HD sidebands (or synced): steer by them rather than the carrier.</summary>
    private bool _hdSeen;
    /// <summary>Channels that have shown HD sidebands, so a return visit steers by them from the first step.</summary>
    private readonly HashSet<long> _hdChannels = new();
    private DateTime _acquiredAt;
    private bool _acquired;

    private const int Stride = 2;            // gain steps per move while walking (the R820T's are 0.9-5 dB)
    private const double Better = 0.5;       // dB: a clear improvement (tracking)
    /// <summary>
    /// dB: as good as the best, so the lower gain wins. Small: on a weak station the peak is broad (107.7: sidebands
    /// 8.5 dB over the floor at 2.7 dB gain, 9.6 at 16.6-22.9, MER 6.5 -> 7.3), and every half dB of MER counts there.
    /// </summary>
    private const double AsGood = 0.25;

    /// <summary>
    /// dB, higher is better: the HD sidebands over the noise floor once the station has shown them, else the analog
    /// carrier. The sidebands are ~17 dB below the carrier and closer to the neighbours, so on an HD station they're
    /// what has to be peaked.
    /// </summary>
    private double Score(SignalQuality.Reading r) => _hdSeen ? r.SidebandDb : r.CarrierDb;
    private string SpectralMetric => _hdSeen ? "sideband SNR" : "carrier SNR";

    private void NoteHd(SignalQuality.Reading r)
    {
        if (_hdSeen || !(r.HdVisible || _e.Hd.Synced)) return;
        _hdSeen = true;
        _hdChannels.Add(Channel(_e.Frequency));
    }

    private double Top(Dictionary<int, SignalQuality.Reading> seen)
    {
        double top = double.NegativeInfinity;
        foreach (var q in seen.Values) top = Math.Max(top, Score(q));
        return top;
    }

    /// <summary>The lowest gain that scores as good as the best.</summary>
    private int Pick(Dictionary<int, SignalQuality.Reading> seen)
    {
        double top = Top(seen);
        int pick = int.MaxValue;
        foreach (var (i, q) in seen) if (Score(q) >= top - AsGood && i < pick) pick = i;
        return pick;
    }

    /// <summary>
    /// Peaks the gain on the spectral score, with clipping as a hard ceiling. From the remembered (or current) gain it
    /// walks up two steps at a time until the score falls past its best; if nothing up there beat the start, down while
    /// it stays as good as the best; then it tries the steps either side of the pick. Of the gains as good as the best
    /// it takes the lowest: headroom for the music's peaks and less for the tuner to overload on. Climbing to the first
    /// clip instead put 104.9 at ~20 dB, deep in the tuner's overload from 105.3-105.7, where its HD couldn't sync (it
    /// peaks at 7.7 dB: MER 9-10.7, and no sync at all from 16.6 up).
    /// </summary>
    private async Task Acquire(CancellationToken ct)
    {
        State = Phase.Acquiring;
        Metric = "spectrum";
        _acquired = false;
        _hdSeen = _hdChannels.Contains(Channel(_e.Frequency));
        if (_best.TryGetValue(Channel(_e.Frequency), out int known)) Apply(known);
        if (!await Wait(0.8, ct)) return;   // retune skip

        var r = await Measure(ct);
        if (r is null) return;
        // too hot: step down until it isn't
        while (r.Value.clip > ClipLimit && _idx > 0)
        {
            SetCeiling(_idx);
            r = await MeasureAt(_idx - (r.Value.clip > 0.05 ? 3 : 1), ct);
            if (r is null) return;
        }
        var seen = new Dictionary<int, SignalQuality.Reading> { [_idx] = r.Value.q };
        int start = _idx;

        // up while it isn't getting worse (a weak station's peak is broad: small steps add up)
        for (int j = start + Stride; j < _gains.Count && BelowCeiling(j); j += Stride)
        {
            r = await MeasureAt(j, ct);
            if (r is null) return;
            if (r.Value.clip > ClipLimit) { SetCeiling(j); break; }
            seen[j] = r.Value.q;
            if (Score(r.Value.q) < Top(seen) - AsGood) break;   // past the peak
        }
        // nothing above beat the start: down while it stays as good
        if (Score(seen[start]) >= Top(seen) - AsGood)
        {
            for (int j = start - Stride; j >= 0; j -= Stride)
            {
                r = await MeasureAt(j, ct);
                if (r is null) return;
                if (r.Value.clip > ClipLimit) break;
                seen[j] = r.Value.q;
                if (Score(r.Value.q) < Top(seen) - AsGood) break;
            }
        }
        // the steps the walk skipped, either side of the pick
        int pick = Pick(seen);
        foreach (int j in new[] { pick - 1, pick + 1 })
        {
            if (j < 0 || j >= _gains.Count || seen.ContainsKey(j) || (j > pick && !BelowCeiling(j))) continue;
            r = await MeasureAt(j, ct);
            if (r is null) return;
            if (r.Value.clip > ClipLimit) { SetCeiling(j); continue; }
            seen[j] = r.Value.q;
        }
        pick = Pick(seen);
        if (pick != _idx) Apply(pick);
        LastScore = Score(seen[pick]);
        Metric = SpectralMetric;
        Remember();
        _acquiredAt = DateTime.UtcNow;
        _acquired = true;
        State = Phase.Tracking;
    }

    /// <summary>
    /// ADC clipping and the spectrum at the current gain: 100 ms (plus the link's latency) to settle, then 450 ms
    /// measured. Null if the station changed or no samples came.
    /// </summary>
    private async Task<(double clip, SignalQuality.Reading q)?> Measure(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100) + _e.Device.ControlLatency, ct);
        _e.TakeClipFraction();
        _e.TakeQuality();
        await Task.Delay(450, ct);   // long enough that a quiet moment in the music doesn't hide the peaks
        if (!_enabled || _e.IsSeeking || _e.RetuneGeneration != _gen) return null;
        double clip = _e.TakeClipFraction();
        var q = _e.TakeQuality();
        if (q.Blocks == 0) return null;
        _clipRecent += 0.5 * (clip - _clipRecent);
        NoteHd(q);
        return (clip, q);
    }

    private Task<(double clip, SignalQuality.Reading q)?> MeasureAt(int idx, CancellationToken ct)
    {
        Apply(idx);
        return Measure(ct);
    }

    private int _probeDir = 1;

    private async Task Track(CancellationToken ct)
    {
        // quiet time between probes: shorter for the first minute while the station's HD is on the air but not decoding
        bool searching = _hdSeen && !_e.Hd.Synced && DateTime.UtcNow - _acquiredAt < TimeSpan.FromMinutes(1);
        if (!await Wait(searching ? 5 : 12, ct)) return;

        bool hd = _e.Hd.Synced && _e.Hd.MerLower > 0;
        NoteHd(default);
        string metric = hd ? "MER" : SpectralMetric;
        double margin = hd ? 0.3 : Better;
        double window = hd ? 3.2 : 0.8;
        double settle = hd ? 1.6 : 0.1 + _e.Device.ControlLatency.TotalSeconds;

        double baseline = await Sample(metric, 0, window, ct);
        if (double.IsNaN(baseline)) return;
        int from = _idx, to = _idx + _probeDir;
        _probeDir = -_probeDir;   // alternate up and down
        if (to < 0 || to >= _gains.Count) return;
        if (to > from && (_clipRecent > ClipLimit || !BelowCeiling(to))) return;   // known cliff: don't probe up

        Apply(to);
        double probe = await Sample(metric, settle, window, ct);
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

    /// <summary>The metric averaged over a window after settling; NaN if the station changed, HD was lost or it clipped.</summary>
    private async Task<double> Sample(string metric, double settle, double window, CancellationToken ct)
    {
        if (settle > 0 && !await Wait(settle, ct)) return double.NaN;
        if (metric != "MER")
        {
            _e.TakeQuality();
            if (!await Wait(window, ct)) return double.NaN;
            var q = _e.TakeQuality();
            if (q.Blocks == 0) return double.NaN;
            NoteHd(q);
            return metric == "sideband SNR" ? q.SidebandDb : q.CarrierDb;
        }
        double sum = 0; int n = 0;
        var until = DateTime.UtcNow.AddSeconds(window);
        while (DateTime.UtcNow < until)
        {
            if (!await Wait(0.1, ct)) return double.NaN;
            var h = _e.Hd;
            if (!h.Synced) return double.NaN;   // lost HD at this gain: definitely worse
            sum += (h.MerLower + h.MerUpper) / 2; n++;
        }
        return n > 0 ? sum / n : double.NaN;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
