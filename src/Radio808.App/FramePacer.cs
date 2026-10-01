using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Radio808.App;

/// <summary>
/// Runs an animation frame on the UI thread once per display refresh (or at a lower fixed rate), for smooth motion.
///
/// WinForms timers tick on the 15.6 ms system clock and drift against the display, so motion judders. Instead, a
/// background thread waits for each composition of the desktop (DwmFlush returns once per refresh), then has the UI
/// thread run the frame and paint it synchronously. A frame is only requested once the previous one has been painted,
/// so a slow frame drops the next refresh instead of queueing up work.
/// </summary>
internal sealed class FramePacer : IDisposable
{
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    private readonly Control _target;
    private readonly Action _frame;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _active = new(false);
    private volatile bool _disposed;
    private int _pending;
    private double _minSeconds;

    /// <summary>Frames actually run per second (measured).</summary>
    public double Fps { get; private set; }

    public FramePacer(Control target, Action frame)
    {
        _target = target;
        _frame = frame;
        _thread = new Thread(Run) { IsBackground = true, Name = "frame pacer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Starts running frames, at most <paramref name="maxFps"/> a second (and never faster than the display).</summary>
    public void Start(double maxFps)
    {
        _minSeconds = 1 / Math.Max(1, maxFps);
        _active.Set();
    }

    public void Stop() => _active.Reset();
    public bool Running => _active.IsSet;

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double last = 0, fpsStart = 0;
        int frames = 0;
        while (!_disposed)
        {
            _active.Wait();
            if (_disposed) break;
            // wait for the next refresh; if the compositor can't tell us (e.g. remote desktop), fall back to ~1 ms steps
            if (DwmFlush() != 0) Thread.Sleep(1);
            double now = clock.Elapsed.TotalSeconds;
            // at a capped rate, skip refreshes until it's time (with a little slack, so 60 on a 60 Hz display isn't 30)
            if (now - last < _minSeconds * 0.9) continue;
            if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) continue;   // still painting the last one
            last = now;
            try
            {
                _target.BeginInvoke(() =>
                {
                    try { if (_active.IsSet && !_disposed) _frame(); }
                    finally { Volatile.Write(ref _pending, 0); }
                });
            }
            catch (InvalidOperationException) { Volatile.Write(ref _pending, 0); Thread.Sleep(50); }   // no window handle yet/anymore
            frames++;
            if (now - fpsStart >= 1)
            {
                Fps = frames / (now - fpsStart);
                frames = 0; fpsStart = now;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _active.Set();
    }
}
