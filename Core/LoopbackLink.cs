using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ProbablyCoop.Core;

/// <summary>Direct IPv4 transport bound to a single host interface.</summary>
internal sealed class LoopbackLink : IDisposable
{
    private const int HeaderSize = 17;
    private const byte LeaveMarker = 2;
    private readonly UdpClient socket;
    private readonly byte[] token;
    private readonly bool host;
    private readonly HMACSHA256 mac;
    private IPEndPoint peer;
    private readonly DirectFragments fragments = new DirectFragments();
    private ulong nextFragmentSequence;
    internal CoopSession Session { get; }
    internal string Code { get; }
    internal IPEndPoint LocalEndpoint { get; }
    internal int Sent { get; private set; }
    internal int Received { get; private set; }
    internal int PeakDrain { get; private set; }
    internal int ExtendedDrains { get; private set; }

    internal LoopbackLink(byte[] fingerprint, double now, string code = null, IPAddress bindAddress = null,
        int listenPort = 0, IPEndPoint mappedEndpoint = null, bool requireApproval=false, Func<double> measurementClock=null)
    {
        if (fingerprint?.Length != Wire.FingerprintSize)
            throw new ArgumentException("Invalid fingerprint.", nameof(fingerprint));
        host = code == null;
        int port = 0;
        Guid id = Guid.NewGuid();
        IPAddress advertised = bindAddress;
        if (!host && !DirectRoomEndpoint.TryParseCode(code, out advertised, out port, out id))
            throw new FormatException("Invalid direct room code.");
        token = id.ToByteArray();
        bool lan = advertised != null;
        if (host && lan && !DirectRoomEndpoint.ValidHostAddress(advertised))
            throw new FormatException("Enter the IPv4 address of a LAN or VPN adapter.");
        IPAddress listenAddress = host ? advertised ?? IPAddress.Loopback : (lan ? IPAddress.Any : IPAddress.Loopback);
        socket = new UdpClient(new IPEndPoint(listenAddress, host ? listenPort : 0));
        if (lan)
        {
            // Four native state chunks plus authentication/fragment headers
            // can exceed a 64 KiB socket queue between player frames.
            try { socket.Client.ReceiveBufferSize=Math.Max(socket.Client.ReceiveBufferSize,256*1024); }
            catch(SocketException) { /* The OS queue remains usable at its current size. */ }
            mac = new HMACSHA256(token);
        }
        if (host)
        {
            LocalEndpoint = (IPEndPoint)socket.Client.LocalEndPoint;
            port = LocalEndpoint.Port;
        }
        else peer = lan && mappedEndpoint != null ? mappedEndpoint : new IPEndPoint(advertised ?? IPAddress.Loopback, port);
        string prefix = lan ? $"PSI-{advertised}" : "PSL";
        Code = $"{prefix}-{port.ToString(CultureInfo.InvariantCulture)}-{id:N}";
        Session = new CoopSession(host, fingerprint, Send, requireApproval, measurementClock, measureCarrierTraffic:true);
        if (!host) Session.Connect(now);
    }

    private ReadOnlySpan<byte> Tag(byte[] datagram) => mac == null ? token :
        mac.ComputeHash(datagram, 16, datagram.Length - 16).AsSpan(0, 16);

    internal void Tick(double now,Action beforeSessionTick=null)
    {
        Session.ObserveNetworkPump(now);
        int count=0;
        long start=Stopwatch.GetTimestamp();
        for (; count < 80 && socket.Available > 0; count++)
        {
            // Keep the original drain for handshakes/local packets. After it,
            // use up to 2 ms total to finish bursts from an established LAN peer.
            if(count>=24 && (mac==null || peer==null || Stopwatch.GetTimestamp()-start>=Stopwatch.Frequency/500)) break;
            var sender = new IPEndPoint(IPAddress.Any, 0);
            byte[] bytes;
            try { bytes = socket.Receive(ref sender); }
            catch (SocketException) { break; }
            int maximum = mac == null ? HeaderSize + Wire.MaxSize : DirectFragments.BodyOffset + DirectFragments.ChunkSize;
            if (bytes.Length < HeaderSize || bytes.Length > maximum ||
                (mac == null && !IPAddress.IsLoopback(sender.Address)) ||
                (peer != null && !peer.Equals(sender)) ||
                !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, 16), Tag(bytes))) continue;
            // Count authenticated traffic from the active peer even if the
            // snapshot is missing another fragment and cannot be reassembled.
            if (Session.State == LinkState.Connected)
                Session.Measurements.AddReceived(bytes.Length);
            byte marker = bytes[16];
            if (marker == LeaveMarker && bytes.Length == HeaderSize)
            {
                if (peer != null) DropPeer();
                continue;
            }
            ReadOnlySpan<byte> payload;
            if (marker == DirectFragments.Marker && mac != null && peer != null)
            {
                var joined = fragments.Receive(bytes, now);
                if (joined == null) continue;
                payload = joined;
            }
            else
            {
                if (marker > 1) continue;
                payload = bytes.AsSpan(HeaderSize);
                if (mac != null && payload.Length > DirectFragments.ChunkSize) continue;
            }
            if (peer == null)
            {
                if (!host || !Wire.TryDecode(payload, marker == 1, out var packet) || packet.Kind != Kind.Hello)
                    continue;
                peer = sender;
                Session.Connect(now);
            }
            if (Session.Receive(payload, marker == 1, now)) Received++;
        }
        PeakDrain=Math.Max(PeakDrain,count);
        if(count>24) ExtendedDrains++;
        // Commands received this tick may change the authority. Capture after
        // draining them, before the single bounded replication send tick.
        Session.ObserveNetworkPump(now);
        beforeSessionTick?.Invoke();
        Session.Tick(now);
        if (host && Session.State is LinkState.TimedOut or LinkState.Incompatible)
            DropPeer();
    }

    private void DropPeer()
    {
        peer = null;
        fragments.Reset();
        Session.DropPeer();
    }

    private bool Send(byte[] bytes, bool cursor)
    {
        if (peer == null) return false;
        if (mac != null && bytes.Length > DirectFragments.ChunkSize)
        {
            if (cursor) return false;
            ulong sequence = ++nextFragmentSequence;
            for (int index = 0; index < DirectFragments.Count(bytes.Length); index++)
                if (!SendDatagram(DirectFragments.Encode(bytes, sequence, index))) return false;
        }
        else
        {
            var datagram = new byte[HeaderSize + bytes.Length];
            datagram[16] = cursor ? (byte)1 : (byte)0;
            bytes.CopyTo(datagram, HeaderSize);
            if (!SendDatagram(datagram)) return false;
        }
        Sent++;
        return true;
    }

    private bool SendDatagram(byte[] datagram)
    {
        Tag(datagram).CopyTo(datagram);
        try
        {
            if (socket.Send(datagram, datagram.Length, peer) != datagram.Length) return false;
            if (Session.State == LinkState.Connected) Session.Measurements.AddSent(datagram.Length);
            return true;
        }
        catch (SocketException) { return false; }
    }

    public void Dispose()
    {
        if (peer != null)
        {
            var goodbye = new byte[HeaderSize];
            goodbye[16] = LeaveMarker;
            SendDatagram(goodbye);
        }
        socket.Dispose();
        mac?.Dispose();
    }
}
