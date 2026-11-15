using System;
using ProbablyCoop.Sync;

namespace ProbablyCoop.Net;

internal enum NetState : byte
{
    Idle = 0,
    Listening = 1,
    Connecting = 2,
    Open = 3,
    Closed = 4
}

internal enum NetClose : byte
{
    None = 0,
    Local = 1,
    Remote = 2,
    Timeout = 3,
    Refused = 4,
    Corrupt = 5
}

// One two-party connection: version handshake, keepalive, a reliable ordered message
// stream and a lossy latest-wins datagram lane. It knows nothing about the game or
// about replication; it only moves opaque payloads tagged with a channel byte.
//
// The owner supplies the transport as a delegate and feeds received datagrams in.
// Nothing here uses threads or timers: Tick must be called from the main loop.
// Message and Datagram may be raised from inside Receive.
internal sealed class NetLink
{
    internal const double HelloInterval = 0.5;
    internal const double ConnectTimeout = 20;
    internal const double PingInterval = 1;
    internal const double SilenceTimeout = 12;
    internal const double TakeoverSilence = 3;
    private const int LooseOverhead = 32;

    private readonly Func<byte[], bool, bool> send;
    private readonly int maxDatagram;
    private readonly Func<ulong> random;
    private readonly Func<byte[], bool> sendReliable;
    private readonly Action<byte, byte[]> deliver;
    private readonly ulong[] looseOut = new ulong[256];
    private readonly ulong[] looseIn = new ulong[256];
    private NetStream stream;
    private ulong nonce;
    private double deadline;
    private double nextHello;
    private double nextPing;
    private double heard;
    private double pingSentAt;
    private bool pingOpen;
    private ulong pingSerial;

    // send(frame, reliable): reliable is a hint for transports that offer both modes.
    // It is false only for datagrams that are safe to lose.
    internal NetLink(Func<byte[], bool, bool> send, int maxDatagram, Func<ulong> random = null)
    {
        if (maxDatagram < NetStream.Overhead + 64) throw new ArgumentOutOfRangeException(nameof(maxDatagram));
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.maxDatagram = maxDatagram;
        this.random = random ?? RandomId;
        sendReliable = frame => this.send(frame, true);
        deliver = (channel, message) => Message?.Invoke(channel, message);
    }

    internal NetState State { get; private set; }
    internal NetClose Reason { get; private set; }
    internal NetRefusal Refusal { get; private set; }
    internal bool IsHost { get; private set; }
    internal ulong Link { get; private set; }
    internal uint Protocol { get; private set; }

    // Smoothed round trip in seconds, 0 until the first measurement.
    internal double Rtt { get; private set; }

    internal int LooseLimit => maxDatagram - LooseOverhead;
    internal bool Flushed => stream == null || stream.Idle;
    internal int Queued => stream == null ? 0 : stream.Queued;
    internal int Resent => stream == null ? 0 : stream.Resent;

    internal Action<byte, byte[]> Message;
    internal Action<byte, byte[]> Datagram;

    // Raised once per connection, when it opens and when it ends. Ended also fires for a
    // local Close, including one issued before the connection ever opened.
    internal Action Opened;
    internal Action<NetClose> Ended;

    internal void Listen()
    {
        Reset();
        IsHost = true;
        State = NetState.Listening;
    }

    internal void Connect(double now)
    {
        Reset();
        IsHost = false;
        nonce = random();
        deadline = now + ConnectTimeout;
        nextHello = now;
        State = NetState.Connecting;
    }

    internal void Close()
    {
        if (State == NetState.Idle || State == NetState.Closed) return;
        if (State == NetState.Open)
        {
            byte[] bye = NetWire.Open(NetKind.Bye, Link).ToArray();
            send(bye, true);
            send(bye, true);
        }
        Finish(NetClose.Local);
    }

    // The carrier knows the peer is gone (left the lobby, session failed). Nothing is
    // sent; the link ends with the given reason.
    internal void Abort(NetClose reason)
    {
        if (State == NetState.Idle || State == NetState.Closed) return;
        Finish(reason);
    }

    // Queues a message for reliable in-order delivery. The array must not be modified
    // afterwards.
    internal bool Send(byte channel, byte[] message) =>
        State == NetState.Open && stream.Send(channel, message);

    // Sends one datagram that may be lost. A newer datagram on the same channel
    // supersedes older ones on arrival.
    internal bool SendLoose(byte channel, byte[] payload)
    {
        if (State != NetState.Open || payload == null || payload.Length == 0 || payload.Length > LooseLimit)
            return false;
        var writer = NetWire.Open(NetKind.Datagram, Link);
        writer.Byte(channel);
        writer.ULong(++looseOut[channel]);
        writer.Bytes(payload);
        return send(writer.ToArray(), false);
    }

    internal void Tick(double now)
    {
        if (State == NetState.Connecting)
        {
            if (now >= deadline)
            {
                Finish(NetClose.Timeout);
                return;
            }
            if (now < nextHello) return;
            nextHello = now + HelloInterval;
            var hello = NetWire.Open(NetKind.Hello, 0);
            hello.ULong(nonce);
            hello.UInt(NetWire.Oldest);
            hello.UInt(NetWire.Protocol);
            send(hello.ToArray(), true);
            return;
        }
        if (State != NetState.Open) return;
        if (now - heard > SilenceTimeout)
        {
            Finish(NetClose.Timeout);
            return;
        }
        if (now >= nextPing)
        {
            nextPing = now + PingInterval;
            pingSentAt = now;
            pingOpen = true;
            var ping = NetWire.Open(NetKind.Ping, Link);
            ping.ULong(++pingSerial);
            send(ping.ToArray(), true);
        }
        stream.Pump(now);
    }

    // Returns true when the datagram is a valid frame of this link. A transport that has
    // not fixed its remote endpoint yet may lock onto the sender once this returns true
    // and State is Open.
    internal bool Receive(byte[] data, int count, double now)
    {
        if (State == NetState.Idle || State == NetState.Closed) return false;
        if (!NetWire.TryOpen(data, count, out var kind, out ulong link, out var body)) return false;
        switch (kind)
        {
            case NetKind.Hello: return OnHello(link, body, now);
            case NetKind.Welcome: return OnWelcome(link, body, now);
            case NetKind.Refuse: return OnRefuse(link, body);
        }
        if (State != NetState.Open || link != Link) return false;

        switch (kind)
        {
            case NetKind.Ping:
            {
                ulong serial = body.ULong();
                if (!body.Ok || !body.AtEnd) return false;
                heard = now;
                var pong = NetWire.Open(NetKind.Pong, Link);
                pong.ULong(serial);
                send(pong.ToArray(), true);
                return true;
            }
            case NetKind.Pong:
            {
                ulong serial = body.ULong();
                if (!body.Ok || !body.AtEnd) return false;
                heard = now;
                if (serial != pingSerial || !pingOpen) return true;
                double sample = Math.Max(0, now - pingSentAt);
                pingOpen = false;
                Rtt = Rtt <= 0 ? sample : Rtt * 0.75 + sample * 0.25;
                stream.Rto = Math.Min(2.0, Math.Max(0.2, Rtt * 2 + 0.1));
                return true;
            }
            case NetKind.Segment:
            {
                // Delivery can run a handler that closes this link, so keep a local
                // reference instead of reading the field again.
                var current = stream;
                bool accepted = current.OnSegment(body, now);
                if (accepted) heard = now;
                if (current.Broken && State == NetState.Open) Finish(NetClose.Corrupt);
                return accepted;
            }
            case NetKind.Ack:
            {
                var current = stream;
                bool accepted = current.OnAckFrame(body, now);
                if (accepted) heard = now;
                if (current.Broken && State == NetState.Open) Finish(NetClose.Corrupt);
                return accepted;
            }
            case NetKind.Datagram:
            {
                byte channel = body.Byte();
                ulong serial = body.ULong();
                byte[] payload = body.Bytes(maxDatagram);
                if (!body.Ok || !body.AtEnd || serial == 0 || payload.Length == 0) return false;
                heard = now;
                if (serial <= looseIn[channel]) return true;
                looseIn[channel] = serial;
                Datagram?.Invoke(channel, payload);
                return true;
            }
            case NetKind.Bye:
                if (!body.AtEnd) return false;
                Finish(NetClose.Remote);
                return true;
        }
        return false;
    }

    private bool OnHello(ulong link, SyncReader body, double now)
    {
        ulong theirs = body.ULong();
        uint oldest = body.UInt();
        uint newest = body.UInt();
        if (!IsHost || link != 0 || !body.Ok || !body.AtEnd || theirs == 0) return false;
        if (State == NetState.Open)
        {
            if (theirs == nonce)
            {
                // Our Welcome was lost; the guest is still retrying.
                heard = now;
                SendWelcome();
                return true;
            }
            // A different nonce from the same endpoint means the guest restarted. Once
            // the old connection has gone quiet, end it; the owner listens again and
            // the guest's next retry starts a clean connection.
            if (now - heard >= TakeoverSilence) Finish(NetClose.Remote);
            else SendRefuse(theirs, NetRefusal.Busy);
            return false;
        }
        if (State != NetState.Listening) return false;

        uint agreed = NetWire.Agree(oldest, newest);
        if (agreed == 0)
        {
            SendRefuse(theirs, NetRefusal.Protocol);
            return false;
        }
        nonce = theirs;
        Link = random();
        Protocol = agreed;
        Open(now);
        SendWelcome();
        Opened?.Invoke();
        return true;
    }

    private bool OnWelcome(ulong link, SyncReader body, double now)
    {
        ulong echo = body.ULong();
        uint chosen = body.UInt();
        if (IsHost || !body.Ok || !body.AtEnd || link == 0 || echo != nonce) return false;
        if (State == NetState.Open)
        {
            if (link != Link) return false;
            heard = now;
            return true;
        }
        if (State != NetState.Connecting) return false;
        if (chosen < NetWire.Oldest || chosen > NetWire.Protocol)
        {
            Refusal = NetRefusal.Protocol;
            Finish(NetClose.Refused);
            return false;
        }
        Link = link;
        Protocol = chosen;
        Open(now);
        Opened?.Invoke();
        return true;
    }

    private bool OnRefuse(ulong link, SyncReader body)
    {
        ulong echo = body.ULong();
        byte reason = body.Byte();
        if (IsHost || State != NetState.Connecting || link != 0 || !body.Ok || !body.AtEnd || echo != nonce)
            return false;
        // Busy is transient: the host is still attached to a previous connection.
        // Keep retrying until the connect deadline.
        if (reason != (byte)NetRefusal.Protocol) return false;
        Refusal = NetRefusal.Protocol;
        Finish(NetClose.Refused);
        return false;
    }

    private void SendWelcome()
    {
        var welcome = NetWire.Open(NetKind.Welcome, Link);
        welcome.ULong(nonce);
        welcome.UInt(Protocol);
        send(welcome.ToArray(), true);
    }

    private void SendRefuse(ulong echo, NetRefusal reason)
    {
        var refuse = NetWire.Open(NetKind.Refuse, 0);
        refuse.ULong(echo);
        refuse.Byte((byte)reason);
        send(refuse.ToArray(), true);
    }

    private void Open(double now)
    {
        stream = new NetStream(Link, maxDatagram, sendReliable, deliver);
        heard = now;
        nextPing = now + PingInterval;
        State = NetState.Open;
    }

    private void Finish(NetClose reason)
    {
        stream?.Stop();
        stream = null;
        Reason = reason;
        State = NetState.Closed;
        Ended?.Invoke(reason);
    }

    private void Reset()
    {
        stream?.Stop();
        stream = null;
        Array.Clear(looseOut, 0, looseOut.Length);
        Array.Clear(looseIn, 0, looseIn.Length);
        nonce = 0;
        Link = 0;
        Protocol = 0;
        Rtt = 0;
        pingSerial = 0;
        pingSentAt = 0;
        pingOpen = false;
        Reason = NetClose.None;
        Refusal = 0;
        State = NetState.Idle;
    }

    private static ulong RandomId()
    {
        ulong value;
        do value = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) ^
                   BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 8);
        while (value == 0);
        return value;
    }
}
