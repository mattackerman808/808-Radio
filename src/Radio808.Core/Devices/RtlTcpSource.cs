using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Radio808.Core.Devices;

/// <summary>
/// An RTL-SDR on another machine (e.g. a Raspberry Pi next to the antenna), served by the standard <c>rtl_tcp</c>.
///
/// The protocol: on connect the server sends "RTL0", the tuner type and its number of gain steps (12 bytes), then
/// streams raw 8-bit I/Q forever; the client sends 5-byte commands (1 byte command, 4 bytes big-endian parameter),
/// which are never acknowledged.
///
/// rtl_tcp queues everything the client hasn't read yet (by default up to 500 USB buffers, ~45 s at our rate), so a
/// client that falls behind gets ever more delayed. To stay live, a reader thread drains the socket continuously into a
/// short queue, and the DSP consumes from that; if the DSP falls behind, the oldest audio is dropped instead.
/// </summary>
public sealed class RtlTcpSource : IIqSource
{
    public const int DefaultPort = 1234;
    private const int ChunkBytes = 64 * 1024;         // ~22 ms at 1.488 MS/s, like a local dongle's USB buffers
    private const int MaxQueuedChunks = 24;           // ~0.5 s: beyond this the DSP is behind; drop the oldest

    // gain steps by tuner type, in tenths of a dB (from librtlsdr: the server sends only their count)
    private static readonly Dictionary<uint, int[]> TunerGains = new()
    {
        [1] = new[] { -10, 15, 40, 65, 90, 115, 140, 165, 190, 215, 240, 290, 340, 420 },
        [2] = new[] { -99, -40, 71, 179, 192 },
        [3] = new[] { -99, -73, -65, -63, -60, -58, -54, 58, 61, 63, 65, 67, 68, 70, 71, 179, 181, 182, 184, 186, 188, 191, 197 },
        [4] = new[] { 0 },
        [5] = new[] { 0, 9, 14, 27, 37, 77, 87, 125, 144, 157, 166, 197, 207, 229, 254, 280, 297, 328, 338, 364, 372, 386, 402, 421, 434, 439, 445, 480, 496 },
        [6] = new[] { 0, 9, 14, 27, 37, 77, 87, 125, 144, 157, 166, 197, 207, 229, 254, 280, 297, 328, 338, 364, 372, 386, 402, 421, 434, 439, 445, 480, 496 },
    };
    private static readonly string[] TunerNames = { "unknown", "E4000", "FC0012", "FC0013", "FC2580", "R820T", "R828D" };

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _send = new();
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly ConcurrentBag<byte[]> _pool = new();
    private readonly SemaphoreSlim _ready = new(0);
    private readonly Cu8Converter _cu8 = new();
    private readonly Thread _reader;
    private Thread? _dsp;
    private volatile bool _running, _closed;
    private int _stoppedRaised;

    private uint _sampleRate;
    private long _frequency;
    private double? _gain;
    private int _ppm;
    private bool _biasTee;

    // link statistics
    private long _bytesReceived, _droppedChunks;
    private double _rateMbps;
    private long _rateBytes, _rateStart = Stopwatch.GetTimestamp();

    public event IqHandler? Samples;
    public event Action<string>? Stopped;

    public string Host { get; }
    public int Port { get; }
    public string TunerType { get; }
    public IReadOnlyList<double> Gains { get; }
    public string Name => $"{TunerType} via rtl_tcp {Host}:{Port}";

    /// <summary>
    /// rtl_tcp sends whole 256 KB USB buffers (88 ms at our rate), so after a command up to one buffer of older samples
    /// is still on its way, plus the network and our queue (which retunes flush). Measured to a Pi 4 on gigabit
    /// Ethernet (tools netlatency): median 69 ms, max 107 ms.
    /// </summary>
    public TimeSpan ControlLatency => TimeSpan.FromMilliseconds(150);

    /// <summary>Samples dropped because the DSP fell behind the network (in chunks of ~22 ms).</summary>
    public long DroppedChunks => Interlocked.Read(ref _droppedChunks);

    public string? LinkStatus
    {
        get
        {
            long now = Stopwatch.GetTimestamp();
            double sec = (now - _rateStart) / (double)Stopwatch.Frequency;
            if (sec > 1)
            {
                long b = Interlocked.Read(ref _bytesReceived);
                _rateMbps = (b - _rateBytes) * 8 / sec / 1e6;
                _rateBytes = b; _rateStart = now;
            }
            return $"{_rateMbps:0.0} Mbit/s  queue {_queue.Count * 22} ms  {DroppedChunks} drops";
        }
    }

    /// <summary>Parses "host", "host:port" or "[v6addr]:port".</summary>
    public static (string host, int port) ParseAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith('['))
        {
            int end = address.IndexOf(']');
            if (end > 0)
            {
                string h = address[1..end];
                return (h, end + 2 < address.Length && address[end + 1] == ':' && int.TryParse(address[(end + 2)..], out int p6) ? p6 : DefaultPort);
            }
        }
        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out int p))
            return (address[..colon], p);
        return (address, DefaultPort);
    }

    /// <summary>Connects to an rtl_tcp server ("host" or "host:port") and reads its header.</summary>
    public RtlTcpSource(string address, TimeSpan? timeout = null)
    {
        (Host, Port) = ParseAddress(address);
        if (Host.Length == 0) throw new InvalidOperationException("No rtl_tcp server address is set.");
        _tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
        try
        {
            if (!_tcp.ConnectAsync(Host, Port).Wait(timeout ?? TimeSpan.FromSeconds(5)))
                throw new TimeoutException();
        }
        catch (Exception ex)
        {
            _tcp.Dispose();
            var inner = ex is AggregateException a ? a.InnerException ?? ex : ex;
            throw new InvalidOperationException($"Could not connect to the rtl_tcp server at {Host}:{Port}" +
                (inner is TimeoutException ? " (no answer)." : $" ({inner.Message.TrimEnd('.')})."), inner);
        }
        _stream = _tcp.GetStream();
        _stream.ReadTimeout = 5000;
        try
        {
            Span<byte> hdr = stackalloc byte[12];
            _stream.ReadExactly(hdr);
            if (hdr[0] != 'R' || hdr[1] != 'T' || hdr[2] != 'L' || hdr[3] != '0')
                throw new InvalidDataException("not an rtl_tcp server");
            uint tuner = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..]);
            uint count = BinaryPrimitives.ReadUInt32BigEndian(hdr[8..]);
            TunerType = tuner < TunerNames.Length ? TunerNames[tuner] : $"tuner {tuner}";
            Gains = TunerGains.TryGetValue(tuner, out var g) && g.Length == count
                ? Array.ConvertAll(g, x => x / 10.0)
                : Array.Empty<double>();
        }
        catch (Exception ex)
        {
            _tcp.Dispose();
            throw new InvalidOperationException($"{Host}:{Port} didn't answer like an rtl_tcp server ({ex.Message.TrimEnd('.')}).", ex);
        }
        // the server streams continuously, so silence means it's gone (e.g. rtl_tcp shutting down keeps the socket
        // open but stops sending): time out and let the app reconnect
        _stream.ReadTimeout = 3000;
        // the server streams from the moment we connect: read (and discard, until started) from now on
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "rtl_tcp reader", Priority = ThreadPriority.AboveNormal };
        _reader.Start();
        Send(0x08, 0);   // RTL2832 digital AGC off, as for a local dongle
    }

    private void Send(byte cmd, uint param)
    {
        Span<byte> b = stackalloc byte[5];
        b[0] = cmd;
        BinaryPrimitives.WriteUInt32BigEndian(b[1..], param);
        lock (_send)
        {
            try { _stream.Write(b); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                throw new InvalidOperationException($"rtl_tcp: lost the connection to {Host}:{Port}", ex);
            }
        }
    }

    public uint SampleRate
    {
        get => _sampleRate;
        set { Send(0x02, value); _sampleRate = value; }
    }

    public long Frequency
    {
        get => Interlocked.Read(ref _frequency);
        set
        {
            Send(0x01, (uint)value);
            Interlocked.Exchange(ref _frequency, value);
            Flush();   // everything queued is from the old frequency
        }
    }

    public double? Gain
    {
        get => _gain;
        set
        {
            if (value is null) Send(0x03, 0);
            else
            {
                if (_gain is null) Send(0x03, 1);
                Send(0x04, (uint)(int)Math.Round(value.Value * 10));
            }
            _gain = value;
        }
    }

    public int Ppm
    {
        get => _ppm;
        set { Send(0x05, (uint)value); _ppm = value; }
    }

    /// <summary>Needs an rtl_tcp from 2018 or later (osmocom 0.6); older servers ignore it.</summary>
    public bool BiasTee
    {
        get => _biasTee;
        set { Send(0x0e, value ? 1u : 0u); _biasTee = value; }
    }

    public void Start()
    {
        if (_running || _closed) return;
        Flush();
        _running = true;
        _dsp = new Thread(DspLoop) { IsBackground = true, Name = "rtl_tcp dsp", Priority = ThreadPriority.AboveNormal };
        _dsp.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _ready.Release();
        if (Thread.CurrentThread != _dsp) _dsp?.Join();
        _dsp = null;
    }

    private void Flush()
    {
        while (_queue.TryDequeue(out var c)) _pool.Add(c);
    }

    private void ReadLoop()
    {
        string? error = null;
        try
        {
            while (!_closed)
            {
                if (!_pool.TryTake(out var chunk)) chunk = new byte[ChunkBytes];
                // whole chunks keep I and Q aligned (the header was 12 bytes, so the stream is pair-aligned)
                _stream.ReadExactly(chunk);
                Interlocked.Add(ref _bytesReceived, ChunkBytes);
                if (!_running) { _pool.Add(chunk); continue; }
                _queue.Enqueue(chunk);
                _ready.Release();
                while (_queue.Count > MaxQueuedChunks && _queue.TryDequeue(out var old))
                {
                    _pool.Add(old);
                    Interlocked.Increment(ref _droppedChunks);
                }
            }
        }
        catch (EndOfStreamException) { error = $"The rtl_tcp server at {Host}:{Port} closed the connection."; }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            error = $"The rtl_tcp server at {Host}:{Port} stopped sending.";
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            error = $"Lost the connection to the rtl_tcp server at {Host}:{Port}.";
        }
        if (!_closed && error != null) RaiseStopped(error);
    }

    private void DspLoop()
    {
        var iq = new float[ChunkBytes];
        while (_running)
        {
            _ready.Wait();
            if (!_running) break;
            if (!_queue.TryDequeue(out var chunk)) continue;   // flushed meanwhile
            _cu8.Convert(chunk, iq);
            _pool.Add(chunk);
            try { Samples?.Invoke(iq); }
            catch (Exception) { /* a consumer bug must not kill the stream */ }
        }
    }

    private void RaiseStopped(string message)
    {
        if (Interlocked.Exchange(ref _stoppedRaised, 1) != 0) return;
        _running = false;
        _ready.Release();
        Stopped?.Invoke(message);
    }

    public void Dispose()
    {
        _closed = true;
        Stop();
        try { _tcp.Client.Shutdown(SocketShutdown.Both); } catch { }
        _tcp.Dispose();
        _reader.Join(2000);
    }
}
