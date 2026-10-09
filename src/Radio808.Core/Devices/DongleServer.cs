using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Radio808.Core.Native;

namespace Radio808.Core.Devices;

/// <summary>
/// Server mode: this computer's USB dongle served to another 808 Radio (an Apple TV, say) with the rtl_tcp protocol,
/// so no Raspberry Pi is needed. One client at a time, a new one taking over from the last, like rtl_tcp itself;
/// the client tunes and sets the gain through the usual commands. Advertised on the network as _rtl-tcp._tcp, the
/// way the Pi setup is, so the radios list it. Unlike rtl_tcp, a slow or vanished client never wedges the dongle:
/// what it can't take is dropped, and the dongle is stopped between clients.
/// </summary>
public sealed unsafe class DongleServer : IDisposable
{
    public const int DefaultPort = 1234;
    private const uint BufferBytes = 64 * 1024, BufferCount = 15;
    private const int MaxQueuedBytes = 6 * 1024 * 1024;   // ~2 s at the usual rates: beyond that the client is too slow

    public RtlSdrInfo Device { get; }
    public int Port { get; }
    public string TunerType { get; }
    public string ServiceName { get; }
    /// <summary>The client being served (address:port), or null.</summary>
    public string? Client { get; private set; }
    public int ClientsServed { get; private set; }
    public long Frequency { get; private set; } = 100_000_000;
    public uint SampleRate { get; private set; } = 2_048_000;
    /// <summary>Bytes streamed in the last second.</summary>
    public long BytesPerSecond { get; private set; }
    public bool Advertised { get; private set; }
    /// <summary>A client came or went, or the tuning changed (raised on the server's threads).</summary>
    public event Action? Changed;

    private IntPtr _dev;
    private readonly object _ctl = new();
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private Socket? _client;
    private volatile bool _disposed;
    private IntPtr _dnssd;

    // the stream: the USB thread fills a queue the sender drains
    private readonly Queue<byte[]> _queue = new();
    private int _queuedBytes;
    private readonly object _queueLock = new();
    private RtlSdrNative.ReadAsyncCallback? _callback;
    private Thread? _usbThread, _sendThread, _cmdThread;
    private volatile bool _streaming;
    private long _bytesThisSecond, _secondStart;

    public DongleServer(RtlSdrInfo device, int port = DefaultPort)
    {
        Device = device;
        Port = port;
        int r = RtlSdrNative.rtlsdr_open(out _dev, device.Index);
        if (r < 0 || _dev == IntPtr.Zero)
            throw new InvalidOperationException($"Could not open {device} (error {r}). Is another program using it?");
        TunerType = RtlSdrNative.rtlsdr_get_tuner_type(_dev) switch { 1 => "E4000", 2 => "FC0012", 3 => "FC0013", 4 => "FC2580", 5 => "R820T", 6 => "R828D", _ => "unknown" };
        RtlSdrNative.rtlsdr_set_sample_rate(_dev, SampleRate);
        RtlSdrNative.rtlsdr_set_center_freq(_dev, (uint)Frequency);
        RtlSdrNative.rtlsdr_set_tuner_gain_mode(_dev, 0);
        string host = Environment.MachineName;
        int dot = host.IndexOf('.');
        if (dot > 0) host = host[..dot];
        ServiceName = $"rtl_tcp on {host}";
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.IPv6Any, Port);
        _listener.Server.DualMode = true;
        _listener.Start(1);
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rtl_tcp server" };
        _acceptThread.Start();
        Advertised = DnsSd.Register(ServiceName, "_rtl-tcp._tcp", Port, out _dnssd);
    }

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            Socket s;
            try { s = _listener!.AcceptSocket(); }
            catch (Exception) { break; }
            if (_disposed) { s.Close(); break; }
            Serve(s);
        }
    }

    private void Serve(Socket s)
    {
        DropClient();   // the newest connection wins, as with rtl_tcp
        s.NoDelay = true;
        s.SendBufferSize = 1 << 20;
        s.SendTimeout = 5000;
        // the hello: "RTL0", the tuner type and its number of gain steps
        var hdr = new byte[12];
        "RTL0"u8.CopyTo(hdr);
        int tuner = RtlSdrNative.rtlsdr_get_tuner_type(_dev);
        int gains = RtlSdrNative.rtlsdr_get_tuner_gains(_dev, null);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), (uint)Math.Max(tuner, 0));
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(8), (uint)Math.Max(gains, 0));
        try { s.Send(hdr); }
        catch (Exception) { s.Close(); return; }

        _client = s;
        Client = s.RemoteEndPoint is IPEndPoint ep ? $"{(ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address)}:{ep.Port}" : s.RemoteEndPoint?.ToString();
        ClientsServed++;
        lock (_queueLock) { _queue.Clear(); _queuedBytes = 0; }
        _streaming = true;
        _callback = OnBuffer;
        lock (_ctl) RtlSdrNative.rtlsdr_reset_buffer(_dev);
        _usbThread = new Thread(UsbLoop) { IsBackground = true, Name = "rtl_tcp usb", Priority = ThreadPriority.AboveNormal };
        _sendThread = new Thread(() => SendLoop(s)) { IsBackground = true, Name = "rtl_tcp send" };
        _cmdThread = new Thread(() => CommandLoop(s)) { IsBackground = true, Name = "rtl_tcp commands" };
        _usbThread.Start(); _sendThread.Start(); _cmdThread.Start();
        Changed?.Invoke();
    }

    private void DropClient()
    {
        var s = _client;
        if (s == null) return;
        _client = null;
        _streaming = false;
        lock (_ctl) RtlSdrNative.rtlsdr_cancel_async(_dev);
        try { s.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        s.Close();
        lock (_queueLock) { _queue.Clear(); _queuedBytes = 0; Monitor.PulseAll(_queueLock); }
        _usbThread?.Join(2000); _sendThread?.Join(2000); _cmdThread?.Join(2000);
        _usbThread = _sendThread = _cmdThread = null;
        Client = null;
        BytesPerSecond = 0;
        Changed?.Invoke();
    }

    private void UsbLoop()
    {
        RtlSdrNative.rtlsdr_read_async(_dev, _callback!, IntPtr.Zero, BufferCount, BufferBytes);
        if (_streaming) { _streaming = false; lock (_queueLock) Monitor.PulseAll(_queueLock); }
    }

    private void OnBuffer(byte* buf, uint len, IntPtr ctx)
    {
        if (!_streaming) return;
        var copy = new ReadOnlySpan<byte>(buf, (int)len).ToArray();
        lock (_queueLock)
        {
            // a client that can't keep up loses the oldest audio, not the connection, and never backs up the dongle
            while (_queuedBytes + copy.Length > MaxQueuedBytes && _queue.Count > 0) _queuedBytes -= _queue.Dequeue().Length;
            _queue.Enqueue(copy);
            _queuedBytes += copy.Length;
            Monitor.Pulse(_queueLock);
        }
    }

    private void SendLoop(Socket s)
    {
        _secondStart = Environment.TickCount64;
        while (_streaming)
        {
            byte[]? block;
            lock (_queueLock)
            {
                while (_queue.Count == 0 && _streaming) Monitor.Wait(_queueLock, 500);
                if (!_streaming) break;
                block = _queue.Dequeue();
                _queuedBytes -= block.Length;
            }
            try { s.Send(block); }
            catch (Exception) { break; }
            _bytesThisSecond += block.Length;
            long now = Environment.TickCount64;
            if (now - _secondStart >= 1000) { BytesPerSecond = _bytesThisSecond * 1000 / Math.Max(1, now - _secondStart); _bytesThisSecond = 0; _secondStart = now; Changed?.Invoke(); }
        }
        if (ReferenceEquals(_client, s)) new Thread(DropClient) { IsBackground = true }.Start();
    }

    /// <summary>The client's 5-byte commands: a code and a big-endian parameter, the same set rtl_tcp takes.</summary>
    private void CommandLoop(Socket s)
    {
        var cmd = new byte[5];
        while (_streaming)
        {
            int got = 0;
            try
            {
                while (got < 5)
                {
                    int n = s.Receive(cmd, got, 5 - got, SocketFlags.None);
                    if (n <= 0) { got = -1; break; }
                    got += n;
                }
            }
            catch (Exception) { got = -1; }
            if (got < 0) break;
            uint p = BinaryPrimitives.ReadUInt32BigEndian(cmd.AsSpan(1));
            lock (_ctl)
            {
                switch (cmd[0])
                {
                    case 0x01: RtlSdrNative.rtlsdr_set_center_freq(_dev, p); Frequency = p; break;
                    case 0x02: RtlSdrNative.rtlsdr_set_sample_rate(_dev, p); SampleRate = p; break;
                    case 0x03: RtlSdrNative.rtlsdr_set_tuner_gain_mode(_dev, (int)p); break;
                    case 0x04: RtlSdrNative.rtlsdr_set_tuner_gain(_dev, (int)p); break;
                    case 0x05: RtlSdrNative.rtlsdr_set_freq_correction(_dev, (int)p); break;
                    case 0x08: RtlSdrNative.rtlsdr_set_agc_mode(_dev, (int)p); break;
                    case 0x09: RtlSdrNative.rtlsdr_set_direct_sampling(_dev, (int)p); break;
                    case 0x0a: RtlSdrNative.rtlsdr_set_offset_tuning(_dev, (int)p); break;
                    case 0x0d: SetGainByIndex((int)p); break;
                    case 0x0e: RtlSdrNative.rtlsdr_set_bias_tee(_dev, (int)p); break;
                    default: break;   // IF gain, test mode, crystal frequencies: not offered
                }
            }
            if (cmd[0] is 0x01 or 0x02) Changed?.Invoke();
        }
        if (ReferenceEquals(_client, s)) new Thread(DropClient) { IsBackground = true }.Start();
    }

    private void SetGainByIndex(int index)
    {
        int count = RtlSdrNative.rtlsdr_get_tuner_gains(_dev, null);
        if (count <= 0) return;
        var gains = new int[count];
        fixed (int* g = gains) RtlSdrNative.rtlsdr_get_tuner_gains(_dev, g);
        if (index >= 0 && index < count) RtlSdrNative.rtlsdr_set_tuner_gain(_dev, gains[index]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DnsSd.Unregister(_dnssd);
        try { _listener?.Stop(); } catch (Exception) { }
        DropClient();
        _acceptThread?.Join(2000);
        if (_dev != IntPtr.Zero) { RtlSdrNative.rtlsdr_close(_dev); _dev = IntPtr.Zero; }
    }
}

/// <summary>
/// DNS-SD (Bonjour) registration through the system's own responder: macOS has it built in; Windows has it when
/// Apple's Bonjour is installed (iTunes, or the Bonjour Print Services), and without it the server is simply not
/// advertised (a radio can still be given its address).
/// </summary>
internal static class DnsSd
{
    [DllImport("/usr/lib/system/libsystem_dnssd.dylib", EntryPoint = "DNSServiceRegister")]
    private static extern int RegisterMac(out IntPtr sdRef, uint flags, uint interfaceIndex, string? name, string regtype, string? domain, string? host, ushort port, ushort txtLen, IntPtr txtRecord, IntPtr callback, IntPtr context);
    [DllImport("/usr/lib/system/libsystem_dnssd.dylib", EntryPoint = "DNSServiceRefDeallocate")]
    private static extern void DeallocateMac(IntPtr sdRef);
    [DllImport("dnssd.dll", EntryPoint = "DNSServiceRegister")]
    private static extern int RegisterWin(out IntPtr sdRef, uint flags, uint interfaceIndex, string? name, string regtype, string? domain, string? host, ushort port, ushort txtLen, IntPtr txtRecord, IntPtr callback, IntPtr context);
    [DllImport("dnssd.dll", EntryPoint = "DNSServiceRefDeallocate")]
    private static extern void DeallocateWin(IntPtr sdRef);

    public static bool Register(string name, string type, int port, out IntPtr sdRef)
    {
        sdRef = IntPtr.Zero;
        ushort netPort = (ushort)IPAddress.HostToNetworkOrder((short)port);
        try
        {
            int r = OperatingSystem.IsMacOS() ? RegisterMac(out sdRef, 0, 0, name, type, null, null, netPort, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
                  : OperatingSystem.IsWindows() ? RegisterWin(out sdRef, 0, 0, name, type, null, null, netPort, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
                  : -1;
            return r == 0 && sdRef != IntPtr.Zero;
        }
        catch (Exception) { return false; }   // no responder on this system
    }

    public static void Unregister(IntPtr sdRef)
    {
        if (sdRef == IntPtr.Zero) return;
        try
        {
            if (OperatingSystem.IsMacOS()) DeallocateMac(sdRef);
            else if (OperatingSystem.IsWindows()) DeallocateWin(sdRef);
        }
        catch (Exception) { }
    }
}
