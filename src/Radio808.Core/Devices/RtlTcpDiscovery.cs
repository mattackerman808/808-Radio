using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Radio808.Core.Devices;

/// <summary>An rtl_tcp server found on the local network.</summary>
/// <param name="Name">The advertised name, e.g. "rtl_tcp on console".</param>
/// <param name="Host">Its mDNS host name without the trailing dot, e.g. "console.local".</param>
public sealed record RtlTcpServer(string Name, string Host, IPAddress? Address, int Port)
{
    /// <summary>What to save and connect to: "console.local", or "console.local:5000" for a non-default port.</summary>
    public string ConnectAddress => Port == RtlTcpSource.DefaultPort ? Host : $"{Host}:{Port}";
}

/// <summary>
/// Finds rtl_tcp servers that advertise themselves with mDNS / DNS-SD as <c>_rtl-tcp._tcp</c> (the Pi setup script
/// does this through Avahi).
///
/// It sends one-shot multicast queries from an ordinary port on each network interface (RFC 6762 §5.1, "legacy
/// unicast"), so responders answer straight back to us and port 5353, which Windows' own mDNS service holds, isn't
/// needed. Queries are repeated a few times during the browse, and follow-up queries fetch any SRV or address records
/// the first answers didn't include.
/// </summary>
public static class RtlTcpDiscovery
{
    public const string ServiceType = "_rtl-tcp._tcp.local";
    private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);
    private const ushort TypeA = 1, TypePtr = 12, TypeSrv = 33;

    /// <summary>Browses for <paramref name="duration"/> and returns what answered, by name.</summary>
    public static async Task<IReadOnlyList<RtlTcpServer>> BrowseAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var state = new BrowseState();
        var sockets = OpenSockets();
        if (sockets.Count == 0) return Array.Empty<RtlTcpServer>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(duration);
        try
        {
            var receivers = sockets.Select(s => ReceiveLoop(s, state, stop.Token)).ToList();
            // ask at once, then again a few times (multicast is lossy), asking for whatever is still missing
            for (int round = 0; !stop.IsCancellationRequested; round++)
            {
                byte[] query;
                lock (state) query = BuildQuery(state.MissingQuestions());
                foreach (var s in sockets)
                    try { await s.SendToAsync(query, SocketFlags.None, MdnsGroup); } catch (SocketException) { }
                try { await Task.Delay(round == 0 ? 250 : 500, stop.Token); } catch (OperationCanceledException) { }
            }
            await Task.WhenAll(receivers);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
        lock (state) return state.Results();
    }

    private static List<Socket> OpenSockets()
    {
        var list = new List<Socket>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    s.Bind(new IPEndPoint(ua.Address, 0));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ua.Address.GetAddressBytes());
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    list.Add(s);
                }
                catch (SocketException) { s.Dispose(); }
            }
        }
        return list;
    }

    private static async Task ReceiveLoop(Socket s, BrowseState state, CancellationToken ct)
    {
        var buf = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await s.ReceiveAsync(buf, SocketFlags.None, ct); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }   // e.g. ICMP port unreachable on some interfaces
            try { lock (state) Parse(buf.AsSpan(0, n), state); }
            catch (Exception) { /* malformed packet: ignore */ }
        }
    }

    // ------------------------------------------------------------------ what we know so far

    private sealed class BrowseState
    {
        public readonly HashSet<string> Instances = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (string target, int port)> Srv = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, IPAddress> A = new(StringComparer.OrdinalIgnoreCase);

        public List<(string name, ushort type)> MissingQuestions()
        {
            var q = new List<(string, ushort)> { (ServiceType, TypePtr) };
            foreach (var i in Instances) if (!Srv.ContainsKey(i)) q.Add((i, TypeSrv));
            foreach (var (target, _) in Srv.Values) if (!A.ContainsKey(target)) q.Add((target, TypeA));
            return q;
        }

        public List<RtlTcpServer> Results() => Instances
            .Where(Srv.ContainsKey)
            .Select(i =>
            {
                var (target, port) = Srv[i];
                string name = i.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase) ? i[..^(ServiceType.Length + 1)] : i;
                return new RtlTcpServer(name, target, A.GetValueOrDefault(target), port);
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ DNS wire format

    private static byte[] BuildQuery(List<(string name, ushort type)> questions)
    {
        var b = new List<byte>(64);
        ushort id = (ushort)Random.Shared.Next(1, 65535);   // legacy unicast: responders echo the ID
        b.AddRange(new byte[] { (byte)(id >> 8), (byte)id, 0, 0, 0, (byte)questions.Count, 0, 0, 0, 0, 0, 0 });
        foreach (var (name, type) in questions)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                b.Add((byte)bytes.Length);
                b.AddRange(bytes);
            }
            b.Add(0);
            b.AddRange(new byte[] { (byte)(type >> 8), (byte)type, 0, 1 });   // class IN
        }
        return b.ToArray();
    }

    private static void Parse(ReadOnlySpan<byte> p, BrowseState state)
    {
        if (p.Length < 12 || (p[2] & 0x80) == 0) return;   // not a response
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        int rr = BinaryPrimitives.ReadUInt16BigEndian(p[6..]) + BinaryPrimitives.ReadUInt16BigEndian(p[8..]) +
                 BinaryPrimitives.ReadUInt16BigEndian(p[10..]);
        int pos = 12;
        for (int i = 0; i < qd; i++) { ReadName(p, ref pos); pos += 4; }
        for (int i = 0; i < rr; i++)
        {
            string name = ReadName(p, ref pos);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(p[pos..]);
            int len = BinaryPrimitives.ReadUInt16BigEndian(p[(pos + 8)..]);
            int data = pos + 10;
            pos = data + len;
            if (pos > p.Length) return;
            switch (type)
            {
                case TypePtr when name.Equals(ServiceType, StringComparison.OrdinalIgnoreCase):
                    int at = data;
                    state.Instances.Add(ReadName(p, ref at));
                    break;
                case TypeSrv when len >= 7:
                    int t = data + 6;
                    state.Srv[name] = (ReadName(p, ref t), BinaryPrimitives.ReadUInt16BigEndian(p[(data + 4)..]));
                    break;
                case TypeA when len == 4:
                    state.A[name] = new IPAddress(p.Slice(data, 4));
                    break;
            }
        }
    }

    /// <summary>Reads a possibly compressed name; leaves <paramref name="pos"/> after it.</summary>
    private static string ReadName(ReadOnlySpan<byte> p, ref int pos)
    {
        var sb = new StringBuilder();
        int at = pos, jumps = 0;
        bool jumped = false;
        while (true)
        {
            byte len = p[at];
            if (len == 0) { at++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (++jumps > 20) throw new FormatException("name loop");
                int target = ((len & 0x3F) << 8) | p[at + 1];
                if (!jumped) { pos = at + 2; jumped = true; }
                at = target;
                continue;
            }
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(p.Slice(at + 1, len)));
            at += 1 + len;
        }
        if (!jumped) pos = at;
        return sb.ToString();
    }
}
