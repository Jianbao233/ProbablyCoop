using System;

namespace ProbablyCoop.Core;

internal enum LinkState { Waiting, Handshake, Connected, TimedOut, Incompatible }

/// <summary>Two-party state machine. The adapter authenticates the sender before delivering bytes.</summary>
internal sealed class CoopSession
{
    private readonly bool host;
    private readonly byte[] fingerprint;
    private readonly Func<byte[], bool, bool> send;
    private Guid session;
    private Guid epoch;
    // Correlates a Welcome/Reject with the guest's current connection attempt.
    private ulong challenge;
    private ulong lastRequest;
    private ulong pendingRequest;
    private ushort pendingTile;
    private ulong pendingRevision;
    private ulong cursorSequence;
    private ulong receivedCursorSequence;
    private double lastReceive;
    private double nextControl;
    private double nextRetry;
    private double phaseStart;

    internal LinkState State { get; private set; }
    internal ushort Tiles { get; private set; }
    internal ulong Revision { get; private set; }
    internal bool HasPendingChange => pendingRequest != 0;
    internal ShopLink Shop { get; }
    internal ReplicaLink Native { get; }
    internal event Action<Packet> RemoteCursor;
    internal event Action<Kind> Received;

    internal CoopSession(bool host, byte[] fingerprint, Func<byte[], bool, bool> send)
    {
        if (fingerprint?.Length != Wire.FingerprintSize) throw new ArgumentException("Invalid fingerprint.");
        this.host = host;
        this.fingerprint = fingerprint;
        this.send = send;
        Shop = new ShopLink(host, (kind, serial, revision, value, payload) =>
            Send(new Packet(kind, session, epoch, serial, revision, value, payload: payload)));
        Native = new ReplicaLink(host, (kind, serial, revision, value, payload) =>
            Send(new Packet(kind, session, epoch, serial, revision, value, payload: payload)));
        if (host) session = Guid.NewGuid();
    }

    internal void Connect(double now)
    {
        State = LinkState.Handshake;
        Shop.Reset();
        Native.Reset();
        epoch = Guid.Empty;
        challenge = host ? 0 : BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) | 1UL;
        lastRequest = pendingRequest = cursorSequence = receivedCursorSequence = 0;
        phaseStart = lastReceive = now;
        nextControl = now;
        if (!host)
        {
            session = Guid.Empty;
            Revision = 0;
            Tiles = 0;
        }
    }

    internal void DropPeer()
    {
        State = LinkState.Waiting;
        pendingRequest = 0;
        Shop.Reset();
        Native.Reset();
    }

    internal void Tick(double now)
    {
        if (host && State != LinkState.Connected) Shop.Tick(now, false);
        if (State == LinkState.Handshake)
        {
            if (now - phaseStart >= 60)
            {
                State = LinkState.TimedOut;
                return;
            }
            if (!host && now >= nextControl)
            {
                nextControl = now + 1;
                if (epoch == Guid.Empty)
                    Send(new Packet(Kind.Hello, serial: challenge, build: fingerprint));
                else
                    Send(new Packet(Kind.Ready, session, epoch, challenge));
            }
            return;
        }
        if (State != LinkState.Connected) return;
        if (now - lastReceive >= 12)
        {
            State = LinkState.TimedOut;
            return;
        }
        if (now >= nextControl)
        {
            nextControl = now + 2;
            if (host) SendState();
            Send(new Packet(Kind.Ping, session, epoch));
        }
        if (pendingRequest != 0 && now >= nextRetry) SendPending(now);
        Shop.Tick(now, true);
        Native.Tick(now, true);
    }

    internal void ChangeTile(int tile, double now)
    {
        if (State != LinkState.Connected || (uint)tile >= 16 || HasPendingChange || Shop.View.Active) return;
        if (host)
        {
            Apply(tile);
            SendState();
            return;
        }
        pendingRequest = lastRequest + 1;
        pendingTile = (ushort)tile;
        pendingRevision = Revision;
        SendPending(now);
    }

    internal void MoveCursor(ushort x, ushort y, bool visible, bool click, ulong surface = 0,
        uint? viewport = null)
    {
        if (State != LinkState.Connected) return;
        byte[] fallback = null;
        if (viewport.HasValue)
        {
            fallback = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fallback, viewport.Value);
        }
        Send(new Packet(Kind.Cursor, session, epoch, ++cursorSequence, revision: surface, x: x, y: y,
                flags: (byte)((visible ? 1 : 0) | (click ? 2 : 0)), payload: fallback), true);
    }

    internal bool Receive(ReadOnlySpan<byte> bytes, bool cursorChannel, double now)
    {
        if (State is not (LinkState.Handshake or LinkState.Connected) ||
            !Wire.TryDecode(bytes, cursorChannel, out var packet)) return false;
        if (host && packet.Kind == Kind.Hello)
        {
            if (packet.Serial == 0 || packet.Session != Guid.Empty || packet.Epoch != Guid.Empty ||
                (challenge != 0 && challenge != packet.Serial)) return false;
            if (!Compatible(packet.Build))
            {
                Send(new Packet(Kind.Reject, serial: packet.Serial));
                State = LinkState.Incompatible;
                return true;
            }
            if (epoch == Guid.Empty)
            {
                challenge = packet.Serial;
                epoch = Guid.NewGuid();
            }
            Send(new Packet(Kind.Welcome, session, epoch, challenge, build: fingerprint));
            Received?.Invoke(packet.Kind);
            return true;
        }
        if (!host && State == LinkState.Handshake && packet.Serial == challenge)
        {
            if (packet.Kind == Kind.Reject && packet.Session == Guid.Empty && packet.Epoch == Guid.Empty)
            {
                State = LinkState.Incompatible;
                return true;
            }
            if (packet.Kind == Kind.Welcome && packet.Session != Guid.Empty && packet.Epoch != Guid.Empty)
            {
                if (epoch != Guid.Empty && (epoch != packet.Epoch || session != packet.Session)) return false;
                if (!Compatible(packet.Build))
                {
                    State = LinkState.Incompatible;
                    return true;
                }
                session = packet.Session;
                epoch = packet.Epoch;
                nextControl = now + 1;
                Send(new Packet(Kind.Ready, session, epoch, challenge));
                Received?.Invoke(packet.Kind);
                return true;
            }
        }
        if (epoch == Guid.Empty || packet.Epoch != epoch || packet.Session != session) return false;

        if (host && packet.Kind == Kind.Ready && packet.Serial == challenge)
        {
            MarkConnected(now);
            SendState();
        }
        else if (!host && packet.Kind == Kind.State)
        {
            if (packet.Revision < Revision || packet.Serial < lastRequest ||
                packet.Serial > (pendingRequest == 0 ? lastRequest : pendingRequest)) return false;
            if (State == LinkState.Handshake) MarkConnected(now);
            Tiles = packet.Value;
            Revision = packet.Revision;
            if (pendingRequest != 0 && packet.Serial == pendingRequest)
            {
                lastRequest = pendingRequest;
                pendingRequest = 0;
            }
        }
        else if (State != LinkState.Connected) return false;
        else if (packet.Kind is Kind.ShopCommand or Kind.ShopState or Kind.ShopAck)
        {
            if (!Shop.Receive(packet)) return false;
        }
        else if (packet.Kind is Kind.NativeChunk or Kind.NativeAck or Kind.NativeCommand or Kind.NativeReply or Kind.NativePresence or Kind.NativeTooltip or Kind.NativeProgress or Kind.NativeNeedFull)
        {
            if (!Native.Receive(packet)) return false;
        }
        else if (host && packet.Kind == Kind.ChangeTile)
        {
            if (packet.Serial == lastRequest + 1)
            {
                lastRequest = packet.Serial;
                if (packet.Revision == Revision && !Shop.View.Active) Apply(packet.Value);
            }
            SendState(); // Duplicate, stale and out-of-order requests only receive current state.
        }
        else if (packet.Kind == Kind.Ping)
            Send(new Packet(Kind.Pong, session, epoch));
        else if (packet.Kind == Kind.Cursor)
        {
            if (packet.Serial <= receivedCursorSequence) return false;
            receivedCursorSequence = packet.Serial;
            RemoteCursor?.Invoke(packet);
            return true;
        }
        else if (packet.Kind != Kind.Pong) return false;

        lastReceive = now;
        Received?.Invoke(packet.Kind);
        return true;
    }

    private bool Compatible(byte[] other) => fingerprint.AsSpan().SequenceEqual(other);

    private void MarkConnected(double now)
    {
        State = LinkState.Connected;
        lastReceive = now;
        nextControl = now;
    }

    private void Apply(int tile)
    {
        Tiles ^= (ushort)(1 << tile);
        Revision++;
    }

    private void SendState() =>
        Send(new Packet(Kind.State, session, epoch, lastRequest, Revision, Tiles));

    private void SendPending(double now)
    {
        nextRetry = now + 1;
        Send(new Packet(Kind.ChangeTile, session, epoch, pendingRequest, pendingRevision, pendingTile));
    }

    private bool Send(Packet packet, bool cursor = false) => send(Wire.Encode(packet), cursor);
}
