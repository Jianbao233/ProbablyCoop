using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ProbablyCoop.Core;

namespace ProbablyCoop.Net;

// UDP carrier for one NetLink: loopback (two game processes on one machine) or a single
// IPv4 interface of a LAN / virtual LAN adapter.
//
// Datagram = tag(16) + link frame. For PSL the tag is the room token itself; for PSI
// it is HMAC-SHA256(token) over the frame, truncated to 16 bytes. That authenticates
// the sender as someone who holds the room code. Nothing is encrypted.
//
// Room code: PSL-<port>-<token>  (loopback)   PSI-<ipv4>-<port>-<token>  (LAN)
internal sealed class LanTransport : IDisposable
{
    // Largest link frame. With the tag and IP/UDP headers a datagram stays under the
    // 1280-byte MTU that virtual LAN adapters commonly impose.
    internal const int MaxFrame = 1100;
    private const int TagSize = 16;
    private const int MinFrame = 6;
    private const int KindOffset = TagSize + 4;
    private const int BaseDrain = 24;
    private const int MaxDrain = 128;

    private readonly UdpClient socket;
    private readonly byte[] token;
    private readonly HMACSHA256 mac;
    private readonly byte[] frame = new byte[MaxFrame];
    private IPEndPoint peer;
    private IPEndPoint replyTo;
    private bool disposed;

    // code == null hosts a room; otherwise joins the room the code describes.
    // Throws FormatException for a bad code or bind address and SocketException when the
    // socket cannot be opened.
    internal LanTransport(double now, string code = null, IPAddress bindAddress = null,
        int listenPort = 0, IPEndPoint mappedEndpoint = null)
    {
        IsHost = code == null;
        int port = 0;
        Guid id = Guid.NewGuid();
        IPAddress advertised = bindAddress;
        if (!IsHost && !DirectRoomEndpoint.TryParseCode(code, out advertised, out port, out id))
            throw new FormatException("Invalid direct room code.");
        token = id.ToByteArray();
        Lan = advertised != null;
        if (IsHost && Lan && !DirectRoomEndpoint.ValidHostAddress(advertised))
            throw new FormatException("Enter the IPv4 address of a LAN or VPN adapter.");
        IPAddress listenAddress = IsHost ? advertised ?? IPAddress.Loopback : (Lan ? IPAddress.Any : IPAddress.Loopback);
        socket = new UdpClient(new IPEndPoint(listenAddress, IsHost ? listenPort : 0));
        if (Lan)
        {
            // A full send burst of the stream can exceed a 64 KiB socket queue between
            // two frames of the receiving game.
            try { socket.Client.ReceiveBufferSize = Math.Max(socket.Client.ReceiveBufferSize, 256 * 1024); }
            catch (SocketException) { /* The OS queue stays usable at its current size. */ }
            mac = new HMACSHA256(token);
        }
        if (IsHost)
        {
            LocalEndpoint = (IPEndPoint)socket.Client.LocalEndPoint;
            port = LocalEndpoint.Port;
        }
        else peer = Lan && mappedEndpoint != null ? mappedEndpoint : new IPEndPoint(advertised ?? IPAddress.Loopback, port);
        string prefix = Lan ? $"PSI-{advertised}" : "PSL";
        Code = $"{prefix}-{port.ToString(CultureInfo.InvariantCulture)}-{id:N}";
        Link = new NetLink(Send, MaxFrame);
        if (IsHost) Link.Listen();
        else Link.Connect(now);
    }

    internal NetLink Link { get; }
    internal string Code { get; }
    internal IPEndPoint LocalEndpoint { get; }
    internal bool IsHost { get; }
    internal bool Lan { get; }
    internal int Sent { get; private set; }
    internal int Received { get; private set; }

    internal static bool IsCode(string text) =>
        IsLoopbackCode(text) || IsIpv4Code(text);

    internal static bool IsLoopbackCode(string text) => text?.StartsWith("PSL-", StringComparison.Ordinal) == true;
    internal static bool IsIpv4Code(string text) => text?.StartsWith("PSI-", StringComparison.Ordinal) == true;

    // Reads pending datagrams and hands them to the link. Message handlers run in here.
    internal void Poll(double now)
    {
        if (disposed) return;
        // A host room outlives one guest connection: after the guest leaves, times out
        // or restarts, wait for the next Hello.
        if (IsHost && Link.State == NetState.Closed && Link.Reason != NetClose.Local)
        {
            peer = null;
            Link.Listen();
        }

        long start = Stopwatch.GetTimestamp();
        for (int count = 0; count < MaxDrain && !disposed && socket.Available > 0; count++)
        {
            // Past the base budget, keep draining for at most 3 ms in total.
            if (count >= BaseDrain && Stopwatch.GetTimestamp() - start >= Stopwatch.Frequency / 333) break;
            var sender = new IPEndPoint(IPAddress.Any, 0);
            byte[] bytes;
            try { bytes = socket.Receive(ref sender); }
            catch (SocketException) { break; }

            int length = bytes.Length - TagSize;
            if (length < MinFrame || length > MaxFrame) continue;
            if (mac == null && !IPAddress.IsLoopback(sender.Address)) continue;
            bool known = peer != null && peer.Equals(sender);
            // A guest that restarted comes back from a new port. Its Hello must reach
            // the link so the link can refuse it or let it take over; every other frame
            // from an unknown endpoint is dropped.
            bool hello = IsHost && bytes[KindOffset] == (byte)NetKind.Hello;
            if (peer != null && !known && !hello) continue;
            if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, TagSize), Tag(bytes))) continue;

            Buffer.BlockCopy(bytes, TagSize, frame, 0, length);
            bool valid;
            replyTo = sender;
            try { valid = Link.Receive(frame, length, now); }
            finally { replyTo = null; }
            if (!valid) continue;
            Received++;
            if (hello && Link.State == NetState.Open) peer = sender;
        }
    }

    // Runs link timers and sends what is queued.
    internal void Flush(double now)
    {
        if (!disposed) Link.Tick(now);
    }

    public void Dispose()
    {
        if (disposed) return;
        Link.Close();
        disposed = true;
        socket.Dispose();
        mac?.Dispose();
    }

    private ReadOnlySpan<byte> Tag(byte[] datagram) => mac == null ? token :
        mac.ComputeHash(datagram, TagSize, datagram.Length - TagSize).AsSpan(0, TagSize);

    // While a received datagram is being handled, replies go back to its sender. That
    // is what lets a host that has no guest yet answer a Hello or refuse one.
    private bool Send(byte[] bytes, bool reliable)
    {
        IPEndPoint target = replyTo ?? peer;
        if (disposed || target == null) return false;
        var datagram = new byte[TagSize + bytes.Length];
        Buffer.BlockCopy(bytes, 0, datagram, TagSize, bytes.Length);
        Tag(datagram).CopyTo(datagram);
        try
        {
            if (socket.Send(datagram, datagram.Length, target) != datagram.Length) return false;
        }
        catch (SocketException) { return false; }
        Sent++;
        return true;
    }
}
