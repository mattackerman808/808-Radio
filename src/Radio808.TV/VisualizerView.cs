using System.Reflection;
using System.Runtime.InteropServices;
using Metal;
using MetalKit;
using Radio808.Core.Dsp;
using Radio808.Core.Radio;
using Radio808.Shared;

namespace Radio808.TV;

/// <summary>
/// The screen saver: a music visualizer in the MilkDrop tradition, on the GPU. Each frame reads the last one back
/// through a warp (Visualizer.metal), fades it, and draws the waveform over it in a drifting hue; the bass, mids,
/// treble and beats from the radio's audio drive the motion. Presets rotate every half minute. Any press ends it.
/// </summary>
public sealed class VisualizerView : MTKView
{
    private const int Bands = 16, Wave = 512;
    private readonly RadioController _c;
    private IMTLCommandQueue? _queue;
    private IMTLRenderPipelineState? _warp, _show;
    private IMTLTexture[] _feedback = Array.Empty<IMTLTexture>();
    private int _current;
    private IMTLBuffer? _audio;
    private readonly float[] _block = new float[AudioAnalyzer.BlockSize], _bandDb = new float[Bands], _wave = new float[Wave];
    private AudioAnalyzer? _analyzer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _last, _bassAvg = 0.05, _lastBeat, _presetAt;
    private float _bass, _mid, _treb, _beat, _hue;
    private int _preset;
    private readonly Random _rng = new();
    public Action? Dismissed { get; set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public float ResX, ResY, Time, Dt, Bass, Mid, Treb, Beat, Hue;
        public int Preset;
        public float Blend, Pad;
    }

    public VisualizerView(RadioController c, CGRect frame) : base(frame, MTLDevice.SystemDefault!)
    {
        _c = c;
        ColorPixelFormat = MTLPixelFormat.BGRA8Unorm;
        FramebufferOnly = true;
        PreferredFramesPerSecond = 60;
        BackgroundColor = UIColor.Black;
        Delegate = new Renderer(this);
        AddGestureRecognizer(new UIPanGestureRecognizer(_ => Dismissed?.Invoke()));   // a swipe on the touch surface
        AddGestureRecognizer(new UITapGestureRecognizer(_ => Dismissed?.Invoke()));
    }

    /// <summary>Compiles the shader and builds the pipelines. False (and a log line) if the GPU won't have it.</summary>
    public bool Setup()
    {
        try
        {
            var device = Device!;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Radio808.TV.Visualizer.metal")
                ?? throw new InvalidOperationException("Visualizer.metal is not in the app");
            string source = new StreamReader(stream).ReadToEnd();
            var library = device.CreateLibrary(source, new MTLCompileOptions(), out NSError? err);
            if (library == null) throw new InvalidOperationException("shader: " + err?.LocalizedDescription);
            var vertex = library.CreateFunction("vmain");
            _warp = Pipeline(device, vertex, library.CreateFunction("fwarp"));
            _show = Pipeline(device, vertex, library.CreateFunction("fshow"));
            _queue = device.CreateCommandQueue();
            _audio = device.CreateBuffer((nuint)(Wave * sizeof(float)), MTLResourceOptions.StorageModeShared);
            _preset = _rng.Next(6);
            _presetAt = _clock.Elapsed.TotalSeconds;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("visualizer: " + ex.Message);
            return false;
        }
    }

    private static IMTLRenderPipelineState Pipeline(IMTLDevice device, IMTLFunction vertex, IMTLFunction fragment)
    {
        var d = new MTLRenderPipelineDescriptor { VertexFunction = vertex, FragmentFunction = fragment };
        d.ColorAttachments[0].PixelFormat = MTLPixelFormat.BGRA8Unorm;
        return device.CreateRenderPipelineState(d, out NSError? err) ?? throw new InvalidOperationException("pipeline: " + err?.LocalizedDescription);
    }

    public override bool CanBecomeFocused => true;

    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        Dismissed?.Invoke();   // any button, the menu button included (it ends the saver, not the app)
    }

    public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent evt) { }

    // ---- the music

    private void Listen(double now, double dt)
    {
        var eng = _c.Engine;
        bool have = eng != null && !_c.Settings.Muted && eng.TryGetLatestAudio(_block);
        if (have)
        {
            _analyzer ??= new AudioAnalyzer(Bands, FmReceiver.AudioRate);
            _analyzer.Analyze(_block, _bandDb);
            float Level(int from, int to)
            {
                float s = 0; for (int b = from; b <= to; b++) s += Math.Clamp((_bandDb[b] + 42) / 36f, 0, 1);
                return s / (to - from + 1);
            }
            float bass = Level(0, 2), mid = Level(3, 8), treb = Level(9, 15);
            float k = (float)Math.Min(1, dt * 12);
            _bass += (bass - _bass) * k; _mid += (mid - _mid) * k; _treb += (treb - _treb) * k;
            // a beat: the bass well above its recent average, at most a few a second
            _bassAvg += (bass - _bassAvg) * Math.Min(1, dt * 1.2);
            if (bass > 0.25 && bass > _bassAvg * 1.35 + 0.05 && now - _lastBeat > 0.22) { _beat = 1; _lastBeat = now; _hue += 0.04f; }
            // the waveform, 512 points from the latest audio
            int step = AudioAnalyzer.BlockSize / Wave;
            for (int i = 0; i < Wave; i++) _wave[i] = Math.Clamp(_block[i * step] * 1.6f, -1, 1);
        }
        else
        {
            _bass *= 0.98f; _mid *= 0.98f; _treb *= 0.98f;
            for (int i = 0; i < Wave; i++) _wave[i] *= 0.97f;
        }
        _beat *= (float)Math.Exp(-dt * 5);
        _hue += (float)(dt * 0.012);
        if (now - _presetAt > 40) { _preset = (_preset + 1 + _rng.Next(5)) % 6; _presetAt = now; }
    }

    // ---- the frames

    private sealed class Renderer : MTKViewDelegate
    {
        private readonly VisualizerView _v;
        public Renderer(VisualizerView v) => _v = v;
        public override void DrawableSizeWillChange(MTKView view, CGSize size) => _v._feedback = Array.Empty<IMTLTexture>();
        public override void Draw(MTKView view) => _v.Frame();
    }

    private unsafe void Frame()
    {
        if (_queue == null || _warp == null || _show == null || _audio == null) return;
        var drawable = CurrentDrawable;
        var pass = CurrentRenderPassDescriptor;
        if (drawable == null || pass == null) return;
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _last, 0, 0.1);
        _last = now;
        Listen(now, dt);

        // the feedback textures: half the drawable's size is plenty, and cheap
        var size = DrawableSize;
        if (_feedback.Length == 0)
        {
            var td = MTLTextureDescriptor.CreateTexture2DDescriptor(MTLPixelFormat.BGRA8Unorm, (nuint)(size.Width / 2), (nuint)(size.Height / 2), false);
            td.Usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.RenderTarget;
            td.StorageMode = MTLStorageMode.Private;
            _feedback = new[] { Device!.CreateTexture(td)!, Device.CreateTexture(td)! };
            _current = 0;
        }
        Marshal.Copy(_wave, 0, _audio.Contents, Wave);
        var u = new Uniforms
        {
            ResX = (float)size.Width, ResY = (float)size.Height, Time = (float)now, Dt = (float)dt,
            Bass = _bass, Mid = _mid, Treb = _treb, Beat = _beat, Hue = _hue % 1, Preset = _preset,
        };
        int next = 1 - _current;
        var cmd = _queue.CommandBuffer()!;

        // pass 1: the last frame, warped, plus the waveform, into the other feedback texture
        var fb = new MTLRenderPassDescriptor();
        fb.ColorAttachments[0].Texture = _feedback[next];
        fb.ColorAttachments[0].LoadAction = MTLLoadAction.DontCare;
        fb.ColorAttachments[0].StoreAction = MTLStoreAction.Store;
        var enc = cmd.CreateRenderCommandEncoder(fb)!;
        enc.SetRenderPipelineState(_warp);
        enc.SetFragmentBytes((IntPtr)(&u), (nuint)sizeof(Uniforms), 0);
        enc.SetFragmentBuffer(_audio, 0, 1);
        enc.SetFragmentTexture(_feedback[_current], 0);
        enc.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
        enc.EndEncoding();

        // pass 2: that, with a bloom, to the screen
        var show = cmd.CreateRenderCommandEncoder(pass)!;
        show.SetRenderPipelineState(_show);
        show.SetFragmentTexture(_feedback[next], 0);
        show.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
        show.EndEncoding();

        cmd.PresentDrawable(drawable);
        cmd.Commit();
        _current = next;
    }
}
