using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Radio808.Core.Native;

namespace Radio808.Core.Hd;

/// <summary>A map image's extent: the north-west corner (<see cref="North"/>, <see cref="West"/>) and the south-east corner.</summary>
public readonly record struct MapBounds(double North, double West, double South, double East);

/// <summary>Snapshot of the HD station state. Replaced wholesale (copy-on-write), so readers never lock.</summary>
public sealed class HdStatus
{
    public bool Synced;
    public float MerLower, MerUpper, Ber;
    /// <summary>The median bit error rate of the last few frames (nrsc5 reports one per ~1.5 s frame), or -1 until there are 3.</summary>
    public float BerAvg = -1;
    /// <summary>When HD last synced (UTC).</summary>
    public DateTime SyncedAt = DateTime.MinValue;
    public string? StationName, Slogan, Message, Alert;
    public string? Title, Artist, Album;
    public byte[]? AlbumArt, StationLogo, WeatherMap;
    public DateTime WeatherTime, TrafficTime;
    public byte[]?[] TrafficTiles = new byte[]?[9];   // row-major 3x3
    /// <summary>Where the weather image goes on a map: the latitude/longitude of its north-west and south-east corners.</summary>
    public MapBounds? WeatherBounds;
    /// <summary>The traffic mosaic's corners (the whole 3x3), from the last tile received.</summary>
    public MapBounds? TrafficBounds;
    /// <summary>The last HERE image's header, for the record: name, part numbers, corners.</summary>
    public string? LastHereInfo;
    /// <summary>The station's Service Information Guide advertises the HERE image service (weather radar and traffic maps), so they're coming.</summary>
    public bool HereImages;
    /// <summary>The data services the guide lists, by content, for the panel (e.g. "HERE images, TTN traffic").</summary>
    public string? DataServices;
    /// <summary>The file name each traffic tile came with (same order as <see cref="TrafficTiles"/>).</summary>
    public string?[] TrafficNames = new string?[9];
    /// <summary>Audio programs seen (0 = HD1), with their program type name.</summary>
    public SortedDictionary<uint, string?> Programs = new();
    public DateTime LastAudio = DateTime.MinValue;
    public int FilesReceived;
    public string? LastFile, Error;

    public HdStatus Clone()
    {
        var c = (HdStatus)MemberwiseClone();
        c.Programs = new SortedDictionary<uint, string?>(Programs);
        return c;
    }
}

/// <summary>
/// Owns the libnrsc5 session and a worker thread. The receiver hands 744,187.5 S/s baseband blocks to
/// <see cref="Enqueue"/> (device thread, never blocks); the worker pipes them into nrsc5, whose callbacks (on the
/// worker thread) update <see cref="Status"/> and feed audio to the <see cref="HdBlender"/>.
/// </summary>
public sealed unsafe class HdDecoder : IDisposable
{
    private sealed class Block
    {
        public float[] Data = Array.Empty<float>();
        public int Floats;
        public int Generation;
    }

    private readonly BlockingCollection<Block?> _queue = new(64);   // ~3 s of 64 KiB device buffers
    private readonly ConcurrentBag<Block> _pool = new();
    private readonly HdBlender _blender;
    private readonly Nrsc5Native.Callback _callback;   // kept alive while nrsc5 holds it
    private readonly object _lock = new();
    private readonly Dictionary<uint, byte[]> _lotImages = new();
    private readonly ConcurrentDictionary<uint, byte[]> _programArt = new(), _programLogo = new();

    private Thread? _thread;
    private volatile bool _running;
    private IntPtr _nrsc5;
    private HdStatus _status = new();
    private readonly Queue<float> _berHist = new();   // the last few frames' bit error rates, for a steady reading
    private int _berSkip;
    private volatile uint _program;
    private volatile int _generation;      // bumped on retune; blocks from older generations are discarded
    private int _sessionGeneration = -1;
    private int _pendingArtLot = -1;
    private int _dropped;
    private int _inFlight;

    private readonly GCHandle _self;   // handed to nrsc5 as the opaque pointer, so the static callback finds us

    public HdDecoder(HdBlender blender)
    {
        _blender = blender;
        _callback = OnEventStatic;
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
    }

    public static string LibraryVersion => Nrsc5Native.Version();
    public HdStatus Status { get { lock (_lock) return _status; } }
    public int DroppedBlocks => _dropped;

    /// <summary>Selected audio program (0 = HD1 ... 7 = HD8).</summary>
    public uint Program
    {
        get => _program;
        set
        {
            if (_program == value) return;
            _program = value;
            _blender.SwitchProgram((int)value);
            _pendingArtLot = -1;
            _programArt.TryGetValue(value, out var art);
            _programLogo.TryGetValue(value, out var logo);
            Update(s =>
            {
                s.Title = s.Artist = s.Album = null;
                s.AlbumArt = art;
                s.StationLogo = logo ?? s.StationLogo;
            });
        }
    }

    /// <summary>The station changed: the next block starts a fresh nrsc5 session.</summary>
    public void Retune()
    {
        Interlocked.Increment(ref _generation);
        _blender.Clear();
        lock (_lock) _status = new HdStatus();
    }

    public void Start()
    {
        if (_running) return;
        if (_thread != null && !_thread.Join(10000)) return;
        while (_queue.TryTake(out var stale)) if (stale != null) _pool.Add(stale);
        _inFlight = 0;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "HD decoder", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread == null) return;
        _running = false;
        // Discard the backlog and wake the worker. The worker closes the nrsc5 session itself: closing it from
        // here while a pipe call is in flight would be a use-after-free.
        while (_queue.TryTake(out var b)) if (b != null) { _pool.Add(b); Interlocked.Decrement(ref _inFlight); }
        _queue.Add(null);
        if (_thread.Join(10000)) _thread = null;
        _blender.Clear();
        lock (_lock) _status = new HdStatus();
    }

    /// <summary>Blocks queued or being decoded.</summary>
    public int Pending => Volatile.Read(ref _inFlight);

    /// <summary>Offline use: waits until every queued block has been decoded.</summary>
    public void WaitIdle()
    {
        while (_running && Volatile.Read(ref _inFlight) > 0) Thread.Sleep(1);
    }

    /// <summary>Baseband at 744,187.5 S/s as separate I and Q. Copies and hands off; never blocks.</summary>
    public void Enqueue(ReadOnlySpan<float> i, ReadOnlySpan<float> q)
    {
        if (!_running || i.Length == 0) return;
        if (!_pool.TryTake(out var b)) b = new Block();
        int n = 2 * i.Length;
        if (b.Data.Length < n) b.Data = new float[n];
        for (int k = 0; k < i.Length; k++) { b.Data[2 * k] = i[k]; b.Data[2 * k + 1] = q[k]; }
        b.Floats = n;
        b.Generation = _generation;
        Interlocked.Increment(ref _inFlight);
        if (!_queue.TryAdd(b))
        {
            Interlocked.Decrement(ref _inFlight);
            _dropped++;
            _pool.Add(b);
        }
    }

    private void Run()
    {
        try
        {
            while (_running)
            {
                var b = _queue.Take();
                if (b == null || !_running) break;
                try { ProcessBlock(b); }
                finally { _pool.Add(b); Interlocked.Decrement(ref _inFlight); }
            }
        }
        catch (Exception ex)
        {
            Update(s => s.Error = ex.Message);
            _running = false;
        }
        finally
        {
            CloseSession();   // only this thread ever touches the nrsc5 session
            _sessionGeneration = -1;
        }
    }

    private void ProcessBlock(Block b)
    {
        int gen = _generation;
        if (b.Generation != gen) return;   // from before the last retune
        if (gen != _sessionGeneration)
        {
            OpenSession();
            _sessionGeneration = gen;
        }
        fixed (float* p = b.Data)
            Nrsc5Native.nrsc5_pipe_samples_cf32(_nrsc5, p, (uint)b.Floats);
    }

    private void OpenSession()
    {
        CloseSession();
        if (Nrsc5Native.nrsc5_open_pipe(out _nrsc5) != 0 || _nrsc5 == IntPtr.Zero)
            throw new InvalidOperationException("nrsc5_open_pipe failed");
        Nrsc5Native.nrsc5_set_mode(_nrsc5, Nrsc5Native.ModeFm);
        Nrsc5Native.nrsc5_set_callback(_nrsc5, _callback, GCHandle.ToIntPtr(_self));
        Nrsc5Native.nrsc5_start(_nrsc5);

        _blender.Clear();
        _lotImages.Clear();
        _programArt.Clear();
        _programLogo.Clear();
        _pendingArtLot = -1;
        // the program is not reset here: a retune sets HD1 on the UI thread (RadioEngine.Frequency), before the
        // session restarts on this thread, so a program chosen right after a tune (a preset's HD2) isn't undone
        _blender.Program = (int)_program;
        lock (_lock) _status = new HdStatus();
    }

    private void CloseSession()
    {
        if (_nrsc5 == IntPtr.Zero) return;
        Nrsc5Native.nrsc5_close(_nrsc5);
        _nrsc5 = IntPtr.Zero;
    }

    // ---- nrsc5 callbacks (worker thread) ----

    /// <summary>Native code can only call a static method on tvOS (ahead-of-time compiled, no JIT): route to the instance.</summary>
#if __TVOS__
    [ObjCRuntime.MonoPInvokeCallback(typeof(Nrsc5Native.Callback))]
#endif
    private static void OnEventStatic(IntPtr e, IntPtr opaque)
    {
        if (GCHandle.FromIntPtr(opaque).Target is HdDecoder d) d.OnEvent(e, opaque);
    }

    private void OnEvent(IntPtr e, IntPtr opaque)
    {
        try
        {
            switch (Nrsc5Native.EventType(e))
            {
                case Nrsc5Native.EventSync:
                    lock (_berHist) { _berHist.Clear(); _berSkip = 1; }   // the first frame after sync is often rough
                    Update(s => { s.Synced = true; s.SyncedAt = DateTime.UtcNow; s.BerAvg = -1; });
                    break;
                case Nrsc5Native.EventLostSync:
                    Update(s => s.Synced = false);
                    _blender.TimelineLost();
                    break;
                case Nrsc5Native.EventMer:
                    float lo = Nrsc5Native.F32(e, 8), up = Nrsc5Native.F32(e, 12);
                    Update(s => { s.MerLower = lo; s.MerUpper = up; });
                    break;
                case Nrsc5Native.EventBer:
                    float ber = Nrsc5Native.F32(e, 8);
                    float med = -1;
                    lock (_berHist)
                    {
                        if (_berSkip > 0) _berSkip--;
                        else
                        {
                            _berHist.Enqueue(ber);
                            while (_berHist.Count > 6) _berHist.Dequeue();
                            if (_berHist.Count >= 3) { var sorted = _berHist.OrderBy(b => b).ToArray(); med = sorted[sorted.Length / 2]; }
                        }
                    }
                    Update(s => { s.Ber = ber; s.BerAvg = med; });
                    break;
                case Nrsc5Native.EventAudio:
                    OnAudio(e);
                    break;
                case Nrsc5Native.EventId3:
                    OnId3(e);
                    break;
                case Nrsc5Native.EventSig:
                    OnSig(e);
                    break;
                case Nrsc5Native.EventLot:
                    OnLot(e);
                    break;
                case Nrsc5Native.EventAudioService:
                    uint prog = Nrsc5Native.U32(e, 8), type = Nrsc5Native.U32(e, 16);
                    string? typeName = Nrsc5Native.ProgramTypeName(type);
                    Update(s => s.Programs[prog] = typeName);
                    break;
                case Nrsc5Native.EventStationName:
                    string? name = Nrsc5Native.Str(e, 8);
                    Update(s => s.StationName = name);
                    break;
                case Nrsc5Native.EventStationSlogan:
                    string? slogan = Nrsc5Native.Str(e, 8);
                    Update(s => s.Slogan = slogan);
                    break;
                case Nrsc5Native.EventStationMessage:
                    string? msg = Nrsc5Native.Str(e, 8);
                    Update(s => s.Message = msg);
                    break;
                case Nrsc5Native.EventEmergencyAlert:
                    string? alert = Nrsc5Native.Str(e, 8);
                    Update(s => s.Alert = alert);
                    break;
                case Nrsc5Native.EventHereImage:
                    OnHereImage(e);
                    break;
            }
        }
        catch
        {
            // Never let an exception unwind into native code.
        }
    }

    private void OnAudio(IntPtr e)
    {
        uint program = Nrsc5Native.U32(e, 8);
        var data = Nrsc5Native.Ptr(e, 16);
        long count = Nrsc5Native.Size(e, 24);   // int16 values, interleaved stereo
        uint flags = Nrsc5Native.U32(e, 32);

        if (!_status.Programs.ContainsKey(program))
            Update(s => s.Programs[program] = null);
        if (program != _program) return;
        _blender.Add((short*)data, (int)count, (flags & Nrsc5Native.AudioFlagUnavailable) != 0);
        if ((flags & Nrsc5Native.AudioFlagUnavailable) == 0)
            Update(s => s.LastAudio = DateTime.UtcNow);
    }

    private void OnId3(IntPtr e)
    {
        if (Nrsc5Native.U32(e, 8) != _program) return;
        string? title = Nrsc5Native.Str(e, 16), artist = Nrsc5Native.Str(e, 24), album = Nrsc5Native.Str(e, 32);
        uint xhdrMime = Nrsc5Native.U32(e, 64);
        int xhdrLot = Nrsc5Native.I32(e, 72);

        byte[]? art = null;
        if (xhdrMime == Nrsc5Native.MimePrimaryImage && xhdrLot >= 0)
        {
            _pendingArtLot = xhdrLot;
            _lotImages.TryGetValue((uint)xhdrLot, out art);
        }
        Update(s =>
        {
            if (title != null) s.Title = title;
            if (artist != null) s.Artist = artist;
            if (album != null) s.Album = album;
            if (art != null) s.AlbumArt = art;
        });
    }

    /// <summary>
    /// The Service Information Guide: every service on the station with its components. The data components'
    /// content types say what the station sends besides audio, in particular whether the HERE image service
    /// (weather radar and traffic maps) is there, well before its first image arrives.
    /// </summary>
    private void OnSig(IntPtr e)
    {
        var names = new SortedSet<string>();
        bool here = false;
        for (var service = Nrsc5Native.Ptr(e, 8); service != IntPtr.Zero; service = Nrsc5Native.Ptr(service, 0))
        {
            // nrsc5_sig_service_t: next, type (byte 8), number (16-bit at 10), name (pointer at 16), components (24)
            for (var comp = Nrsc5Native.Ptr(service, 24); comp != IntPtr.Zero; comp = Nrsc5Native.Ptr(comp, 0))
            {
                // nrsc5_sig_component_t: next, type (byte 8), id (9), then for data: port (16-bit at 12), service data type (14), type (16), mime (20)
                if (Marshal.ReadByte(comp, 8) != Nrsc5Native.SigComponentData) continue;
                uint mime = Nrsc5Native.U32(comp, 20);
                string? label = mime switch
                {
                    Nrsc5Native.MimeHereImage => "HERE images",
                    Nrsc5Native.MimeHereTpeg => "HERE TPEG",
                    Nrsc5Native.MimeNavteq => "Navteq",
                    Nrsc5Native.MimeHdTmc => "TMC",
                    Nrsc5Native.MimeTtnTpeg1 or Nrsc5Native.MimeTtnTpeg2 or Nrsc5Native.MimeTtnTpeg3 => "TTN TPEG",
                    Nrsc5Native.MimeTtnStmTraffic => "TTN traffic",
                    Nrsc5Native.MimeTtnStmWeather => "TTN weather",
                    Nrsc5Native.MimePrimaryImage or Nrsc5Native.MimeStationLogo => null,   // album art and logos: audio-related, not a data service
                    _ => Marshal.ReadByte(service, 8) == Nrsc5Native.SigServiceData ? $"data {mime:X8}" : null,
                };
                if (mime == Nrsc5Native.MimeHereImage) here = true;
                if (label != null) names.Add(label);
            }
        }
        string? list = names.Count == 0 ? null : string.Join(", ", names);
        Update(s => { s.HereImages = here; s.DataServices = list; });
    }

    private void OnLot(IntPtr e)
    {
        uint lot = Nrsc5Native.U32(e, 12);
        uint size = Nrsc5Native.U32(e, 16);
        string? name = Nrsc5Native.Str(e, 24);
        var data = Nrsc5Native.Ptr(e, 32);
        var service = Nrsc5Native.Ptr(e, 48);
        var component = Nrsc5Native.Ptr(e, 56);

        Update(s => { s.FilesReceived++; s.LastFile = name; });
        if (data == IntPtr.Zero || size < 8 || size > 4 * 1024 * 1024) return;

        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, (int)size);
        if (!IsImage(bytes)) return;

        // The SIG component says what the file is (album art vs. logo); the SIG audio service it belongs to says
        // which program (its audio component's port field).
        uint componentMime = component == IntPtr.Zero ? 0 : Nrsc5Native.U32(component, 20);
        int program = -1;
        if (service != IntPtr.Zero && Marshal.ReadByte(service, 8) == Nrsc5Native.SigServiceAudio)
        {
            var audioComponent = Nrsc5Native.Ptr(service, 32);
            program = audioComponent != IntPtr.Zero
                ? Marshal.ReadByte(audioComponent, 12)
                : Nrsc5Native.U16(service, 10) - 1;
        }
        bool current = program < 0 || program == _program;

        if (componentMime == Nrsc5Native.MimeStationLogo)
        {
            if (program >= 0) _programLogo[(uint)program] = bytes;
            if (current) Update(s => s.StationLogo = bytes);
            return;
        }

        if (_lotImages.Count > 64) _lotImages.Clear();
        _lotImages[lot] = bytes;
        if (componentMime == Nrsc5Native.MimePrimaryImage && program >= 0)
            _programArt[(uint)program] = bytes;
        if ((int)lot == _pendingArtLot || (componentMime == Nrsc5Native.MimePrimaryImage && current))
            Update(s => s.AlbumArt = bytes);
    }

    private void OnHereImage(IntPtr e)
    {
        int type = Nrsc5Native.I32(e, 8);
        int seq = Nrsc5Native.I32(e, 12);
        int n1 = Nrsc5Native.I32(e, 16), n2 = Nrsc5Native.I32(e, 20);
        // the image's map corners: latitude1/longitude1 = north/west edges, latitude2/longitude2 = south/east
        var bounds = new MapBounds(Nrsc5Native.F32(e, 32), Nrsc5Native.F32(e, 36), Nrsc5Native.F32(e, 40), Nrsc5Native.F32(e, 44));
        string name = Nrsc5Native.Str(e, 48) ?? "";
        uint size = Nrsc5Native.U32(e, 56);
        var data = Nrsc5Native.Ptr(e, 64);
        if (data == IntPtr.Zero || size == 0 || size > 4 * 1024 * 1024) return;

        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, (int)size);
        string info = $"{name} type {type} seq {seq} part {n1}/{n2} {size} bytes N{bounds.North:0.0000} W{bounds.West:0.0000} S{bounds.South:0.0000} E{bounds.East:0.0000}";
        Update(s => s.LastHereInfo = info);

        if (type == Nrsc5Native.HereImageWeather)
        {
            Update(s => { s.WeatherMap = bytes; s.WeatherBounds = bounds; s.WeatherTime = DateTime.Now; s.FilesReceived++; s.LastFile = name; });
        }
        else if (type == Nrsc5Native.HereImageTraffic)
        {
            // The 3x3 mosaic's tiles come as parts 1-9 in row-major order from the north-west corner, named
            // "trafficMap_<row>_<col>_<hash>.png" with 0-based row and column (KOIT 96.5, 2026-10-02). The part
            // number is the reliable index; the name is a fallback (0- or 1-based).
            int idx = n1 is >= 1 and <= 9 ? n1 - 1 : -1;
            var parts = name.Split('_');
            if (idx < 0 && parts.Length >= 3 && int.TryParse(parts[1], out int row) && int.TryParse(parts[2], out int col))
            {
                if (row is >= 0 and <= 2 && col is >= 0 and <= 2) idx = row * 3 + col;
                else if (row is >= 1 and <= 3 && col is >= 1 and <= 3) idx = (row - 1) * 3 + (col - 1);
            }
            if (idx < 0) return;
            // The corners the header gives for tile (a, b) are the center tile's box grown by a tiles north and
            // south and b tiles east and west (not the tile's own extent), so the whole mosaic is the center tile
            // plus one tile each way, whichever part this is.
            int a = idx / 3, b = idx % 3;
            double latC = (bounds.North + bounds.South) / 2, lonC = (bounds.West + bounds.East) / 2;
            double latHalf = (bounds.North - bounds.South) / (2 * (2 * a + 1)) * 3, lonHalf = (bounds.East - bounds.West) / (2 * (2 * b + 1)) * 3;
            var mosaic = new MapBounds(latC + latHalf, lonC - lonHalf, latC - latHalf, lonC + lonHalf);
            Update(s =>
            {
                var tiles = (byte[]?[])s.TrafficTiles.Clone();
                tiles[idx] = bytes;
                s.TrafficTiles = tiles;
                var names = (string?[])s.TrafficNames.Clone();
                names[idx] = name;
                s.TrafficNames = names;
                s.TrafficBounds = mosaic;
                s.TrafficTime = DateTime.Now;
                s.FilesReceived++;
                s.LastFile = name;
            });
        }
    }

    private static bool IsImage(byte[] b) =>
        (b[0] == 0xFF && b[1] == 0xD8) ||                                   // JPEG
        (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||   // PNG
        (b[0] == 'G' && b[1] == 'I' && b[2] == 'F');                         // GIF

    private void Update(Action<HdStatus> change)
    {
        lock (_lock)
        {
            var s = _status.Clone();
            change(s);
            _status = s;
        }
    }

    public void Dispose()
    {
        Stop();
        _queue.Dispose();
        if (_self.IsAllocated) _self.Free();
    }
}
