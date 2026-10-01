using System;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Color = System.Drawing.Color;
using Size = System.Drawing.Size;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Radio808.App;

/// <summary>
/// A window drawn by the GPU: Direct2D into a flip-model DXGI swap chain, on its own render thread, presenting in step
/// with the display. The instrument panel's live sections are drawn here, so they run at the display's refresh rate
/// without loading the UI thread (GDI+ draws on the CPU, on the UI thread, at ~6 ms a frame at 4K).
///
/// Mouse input passes through to the parent (WM_NCHITTEST → HTTRANSPARENT), which hit-tests in design coordinates as
/// before. If Direct3D can't be used (no GPU, some remote sessions), <see cref="Failed"/> is raised and the parent
/// keeps drawing with GDI+.
/// </summary>
internal sealed class GpuPanel : Control
{

    private readonly Action<IPanelCanvas> _draw;
    private Thread? _thread;
    private volatile bool _run;
    private volatile int _syncInterval = 1;
    private int _targetFps;
    private readonly object _xfLock = new();
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private Color _background = Color.Black;
    private volatile bool _resized;
    private Size _size;

    /// <summary>Raised (on the render thread) if the GPU can't be used; the panel should fall back to GDI+.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Measured frames per second, and the display's refresh rate.</summary>
    public double Fps { get; private set; }
    public double RefreshHz { get; private set; }
    /// <summary>Render-thread CPU time per frame, ms (drawing commands; the GPU does the pixels).</summary>
    public double FrameMs { get; private set; }

    public GpuPanel(Action<IPanelCanvas> draw)
    {
        _draw = draw;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_ERASEBKGND = 0x14, HTTRANSPARENT = -1;
        if (m.Msg == WM_NCHITTEST) { m.Result = HTTRANSPARENT; return; }   // the parent handles the mouse
        if (m.Msg == WM_ERASEBKGND) { m.Result = 1; return; }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e) { }   // the render thread presents

    /// <summary>Design → pixel transform for this window, and the color behind everything.</summary>
    public void SetView(Matrix3x2 transform, Color background)
    {
        lock (_xfLock) { _transform = transform; _background = background; }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        lock (_xfLock) _size = ClientSize;
        _resized = true;
    }

    /// <summary>Starts (or retunes) rendering at up to <paramref name="fps"/> (0 = every display refresh).</summary>
    public void Start(int fps)
    {
        _targetFps = fps;
        RefreshHz = DisplayRefresh(Screen.FromControl(this).DeviceName);
        UpdateSyncInterval();
        if (_run) return;
        lock (_xfLock) _size = ClientSize;
        var hwnd = Handle;
        _run = true;
        _thread = new Thread(() => Render(hwnd)) { IsBackground = true, Name = "gpu panel", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_run) return;
        _run = false;
        _thread?.Join(1000);
        _thread = null;
    }

    public bool Running => _run;

    private void UpdateSyncInterval()
    {
        // present every Nth refresh: the frame rate divides the display's evenly, so motion stays even
        double hz = RefreshHz > 0 ? RefreshHz : 60;
        _syncInterval = _targetFps <= 0 ? 1 : Math.Clamp((int)Math.Round(hz / _targetFps), 1, 4);
    }

    /// <summary>The refresh rate of a display's current mode (60 if it can't be read).</summary>
    private static double DisplayRefresh(string device)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(device, -1, ref dm) && dm.dmDisplayFrequency > 1 ? dm.dmDisplayFrequency : 60;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    // ------------------------------------------------------------------ render thread

    private ID3D11Device? _device;
    private IDXGISwapChain1? _swap;
    private ID2D1Factory1? _d2d;
    private IDWriteFactory? _dwrite;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _ctx;
    private ID2D1Bitmap1? _target;
    private D2DCanvas? _canvas;

    private void Render(IntPtr hwnd)
    {
        try
        {
            CreateDevice(hwnd);
            var clock = Stopwatch.StartNew();
            double fpsStart = 0, cpu = 0;
            int frames = 0;
            while (_run)
            {
                Size size;
                lock (_xfLock) size = _size;
                if (size.Width < 1 || size.Height < 1) { Thread.Sleep(20); continue; }
                if (_resized)
                {
                    _resized = false;
                    _ctx!.Target = null;
                    _target?.Dispose();
                    _swap!.ResizeBuffers(2, (uint)size.Width, (uint)size.Height, Format.Unknown, SwapChainFlags.None).CheckError();
                    CreateTarget();
                }
                long t0 = Stopwatch.GetTimestamp();
                Matrix3x2 xf; Color bg;
                lock (_xfLock) { xf = _transform; bg = _background; }
                var ctx = _ctx!;
                ctx.BeginDraw();
                ctx.Transform = Matrix3x2.Identity;
                ctx.Clear(new Color4(bg.R / 255f, bg.G / 255f, bg.B / 255f, 1));
                ctx.Transform = xf;
                ctx.AntialiasMode = AntialiasMode.PerPrimitive;
                ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
                try { _draw(_canvas!.View(bg)); }
                catch (Exception ex) { AppLog.Write(ex); }
                var hr = ctx.EndDraw();
                cpu += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (hr.Failure) { RecreateDevice(hwnd); continue; }
                var pr = _swap!.Present((uint)_syncInterval, PresentFlags.None);   // waits for the display
                if (pr.Failure) { RecreateDevice(hwnd); continue; }
                // minimized or fully covered: Present doesn't wait for the display then, so don't spin
                if (pr.Code == unchecked((int)0x087A0001) /* DXGI_STATUS_OCCLUDED */) Thread.Sleep(50);
                frames++;
                double now = clock.Elapsed.TotalSeconds;
                if (now - fpsStart >= 1)
                {
                    Fps = frames / (now - fpsStart);
                    FrameMs = cpu / frames;
                    frames = 0; cpu = 0; fpsStart = now;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            _run = false;
            Failed?.Invoke(ex);
        }
        finally
        {
            ReleaseDevice();
        }
    }

    private void CreateDevice(IntPtr hwnd)
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        if (D3D11.D3D11CreateDevice((IDXGIAdapter)null!, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out _device).Failure)
            D3D11.D3D11CreateDevice((IDXGIAdapter)null!, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out _device).CheckError();
        using (var dxgiDevice = _device!.QueryInterface<IDXGIDevice1>())
        {
            dxgiDevice.MaximumFrameLatency = 1;   // present the newest frame, don't queue several ahead
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            Size size;
            lock (_xfLock) size = _size;
            var desc = new SwapChainDescription1
            {
                Width = (uint)Math.Max(1, size.Width), Height = (uint)Math.Max(1, size.Height),
                Format = Format.B8G8R8A8_UNorm, BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0), Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard, AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            };
            _swap = factory.CreateSwapChainForHwnd(_device, hwnd, desc, null, null);
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
            _d2d ??= D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            _dwrite ??= DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
            _d2dDevice = _d2d.CreateDevice(dxgiDevice);
        }
        _ctx = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _ctx.UnitMode = UnitMode.Pixels;
        _canvas ??= new D2DCanvas(_d2d!, _dwrite!);
        _canvas.Attach(_ctx);
        CreateTarget();
        _resized = false;
    }

    private void CreateTarget()
    {
        using var surface = _swap!.GetBuffer<IDXGISurface>(0);
        _target = _ctx!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
        _ctx.Target = _target;
    }

    private void RecreateDevice(IntPtr hwnd)
    {
        ReleaseDevice(keepFactories: true);
        CreateDevice(hwnd);
    }

    private void ReleaseDevice(bool keepFactories = false)
    {
        if (_ctx != null) _ctx.Target = null;
        _target?.Dispose(); _target = null;
        _ctx?.Dispose(); _ctx = null;
        _d2dDevice?.Dispose(); _d2dDevice = null;
        _swap?.Dispose(); _swap = null;
        _device?.Dispose(); _device = null;
        if (keepFactories) return;
        _canvas?.Dispose(); _canvas = null;
        _dwrite?.Dispose(); _dwrite = null;
        _d2d?.Dispose(); _d2d = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Stop();
        base.Dispose(disposing);
    }
}
