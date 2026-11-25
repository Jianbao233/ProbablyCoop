using System;
using System.Buffers.Binary;

namespace ProbablyCoop.Core;

internal enum LinkState { Waiting, Handshake, Connected, TimedOut, Incompatible, JoinDeclined, JoinClosed }
internal enum JoinRefusal : ushort { Incompatible, Declined, Closed }

/// <summary>Two-party state machine. The adapter authenticates the sender before delivering bytes.</summary>
internal sealed class CoopSession
{
    private readonly bool host;
    private readonly bool requireApproval;
    private readonly byte[] fingerprint;
    private readonly Func<byte[], bool, bool> send;
    private readonly Func<double> measurementClock;
    private readonly bool measureCarrierTraffic;
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
    private ulong nameSequence, receivedNameSequence, pingSequence, receivedPingSequence;
    private string sentPointerName;
    private string sentPointerNameInput;
    private double nextItemPing, nextIncomingItemPing;
    private double lastReceive;
    private double nextControl, nextProbe;
    private double nextRetry;
    private double phaseStart;
    private bool admitted;
    private bool waitingForWorldReady;
    private double worldLoadDeadline;
    private const double WorldLoadGraceSeconds = 60;

    internal LinkState State { get; private set; }
    internal bool AcceptingJoins { get; private set; } = true;
    internal bool JoinPending { get; private set; }
    internal ushort Tiles { get; private set; }
    internal ulong Revision { get; private set; }
    internal ulong WorldGeneration { get; private set; }
    internal bool WorldReloadPending => waitingForWorldReady;
    internal bool HasPendingChange => pendingRequest != 0;
    internal ShopLink Shop { get; }
    internal ReplicaLink Native { get; }
    internal ConnectionMeasurements Measurements { get; } = new ConnectionMeasurements();
    internal event Action<Packet> RemoteCursor;
    internal event Action<Kind> Received;
    internal string RemotePointerName { get; private set; }="";
    internal (ulong Serial, ulong Context, uint Entity) RemoteItemPing { get; private set; }

    internal CoopSession(bool host, byte[] fingerprint, Func<byte[], bool, bool> send,bool requireApproval=false,
        Func<double> measurementClock=null, bool measureCarrierTraffic=false)
    {
        if (fingerprint?.Length != Wire.FingerprintSize) throw new ArgumentException("Invalid fingerprint.");
        this.host = host;
        this.requireApproval=host && requireApproval;
        this.fingerprint = fingerprint;
        this.send = send;
        this.measurementClock = measurementClock;
        this.measureCarrierTraffic = measureCarrierTraffic;
        Shop = new ShopLink(host, (kind, serial, revision, value, payload) =>
            Send(new Packet(kind, session, epoch, serial, revision, value, payload: payload)));
        Native = new ReplicaLink(host, (kind, serial, revision, value, payload) =>
            Send(new Packet(kind, session, epoch, serial, revision, value, payload: payload)));
        if (host) session = Guid.NewGuid();
    }

    internal void ObserveNetworkPump(double now)
    {
        if (State == LinkState.Connected)
            Measurements.ObserveLocalPump(measurementClock?.Invoke() ?? now);
    }

    internal void Connect(double now)
    {
        State = LinkState.Handshake;
        Shop.Reset();
        Native.Reset();
        WorldGeneration = 0;
        waitingForWorldReady = false;
        worldLoadDeadline = 0;
        epoch = Guid.Empty;
        admitted=!requireApproval;
        JoinPending=false;
        challenge = host ? 0 : BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) | 1UL;
        lastRequest = pendingRequest = cursorSequence = receivedCursorSequence = 0;
        ResetPointerProfile();
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
        WorldGeneration = 0;
        waitingForWorldReady = false;
        worldLoadDeadline = 0;
        JoinPending=false;
        challenge=0;
        pendingRequest = 0;
        ResetPointerProfile();
        Shop.Reset();
        Native.Reset();
    }

    internal bool BeginWorldReload(double now)
    {
        if (!host || State != LinkState.Connected || waitingForWorldReady || WorldGeneration == ulong.MaxValue) return false;
        waitingForWorldReady = true;
        ResetWorld(Guid.NewGuid(), WorldGeneration + 1);
        worldLoadDeadline = now + WorldLoadGraceSeconds;
        lastReceive = now;
        nextControl = now;
        return true;
    }

    private void ResetWorld(Guid replacement, ulong generation)
    {
        Measurements.CancelPendingProbe();
        epoch = replacement;
        WorldGeneration = generation;
        Shop.SetAuthority(null);
        Shop.Reset();
        Native.Reset();
        lastRequest = pendingRequest = cursorSequence = receivedCursorSequence = 0;
        Tiles = 0;
        Revision = 0;
        ResetPointerProfile();
    }

    private void SendWelcome() => Send(new Packet(Kind.Welcome, session, epoch, challenge, WorldGeneration, build: fingerprint));
    private void SendReady() => Send(new Packet(Kind.Ready, session, epoch, challenge, WorldGeneration));

    private void ResetPointerProfile()
    {
        nameSequence=receivedNameSequence=pingSequence=receivedPingSequence=0;
        nextItemPing=nextIncomingItemPing=0;
        sentPointerName=null;
        sentPointerNameInput=null;
        RemotePointerName="";
        RemoteItemPing=default;
    }

    internal void SetPointerName(string value)
    {
        if(State!=LinkState.Connected || sentPointerNameInput==value) return;
        string name=PointerProfile.NormalizeName(value);
        if(sentPointerName!=name && !Send(new Packet(Kind.PointerName,session,epoch,++nameSequence,payload:PointerProfile.Encode(name)))) return;
        sentPointerName=name;
        sentPointerNameInput=value;
    }

    internal bool SendItemPing(uint entity,ulong context,double now)
    {
        if(State!=LinkState.Connected || entity==0 || context==0 || now<nextItemPing) return false;
        var payload=new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,entity);
        nextItemPing=now+0.5;
        return Send(new Packet(Kind.ItemPing,session,epoch,++pingSequence,context,payload:payload));
    }

    internal void Tick(double now)
    {
        if (host && State != LinkState.Connected) Shop.Tick(now, false);
        if(host && (State is LinkState.JoinDeclined or LinkState.JoinClosed) && now-phaseStart>=4)
        {
            State=LinkState.TimedOut;
            return;
        }
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
                    SendReady();
            }
            return;
        }
        if (State != LinkState.Connected) return;
        double measuredNow = measurementClock?.Invoke() ?? now;
        Measurements.Tick(measuredNow);
        // A slow native load can block the entire Unity thread, including
        // sends and heartbeats. Extend the silence window only for an
        // announced generation with no received full checkpoint yet.
        if (now - lastReceive >= 12 && (now >= worldLoadDeadline || Native.FullRevision != 0))
        {
            State = LinkState.TimedOut;
            return;
        }
        if (waitingForWorldReady)
        {
            if (now >= nextControl)
            {
                nextControl = now + 1;
                SendWelcome();
            }
            return;
        }
        if (now >= nextControl)
        {
            nextControl = now + 2;
            if (host) SendState();
        }
        // A capture callback can take much of a second before this tick starts.
        // Base the probe cadence and timestamp on the actual send-side clock.
        if (measuredNow >= nextProbe)
        {
            nextProbe = measuredNow + 2;
            if (!Send(new Packet(Kind.Ping, session, epoch, Measurements.BeginProbe(measuredNow))))
                Measurements.CancelPendingProbe();
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
        if (!ReceiveCore(bytes, cursorChannel, now)) return false;
        if (!measureCarrierTraffic && State == LinkState.Connected) Measurements.AddReceived(bytes.Length);
        return true;
    }

    private bool ReceiveCore(ReadOnlySpan<byte> bytes, bool cursorChannel, double now)
    {
        if (State is not (LinkState.Handshake or LinkState.Connected or LinkState.JoinDeclined or LinkState.JoinClosed) ||
            !Wire.TryDecode(bytes, cursorChannel, out var packet)) return false;
        if((State is LinkState.JoinDeclined or LinkState.JoinClosed) && (!host || packet.Kind!=Kind.Hello)) return false;
        if (host && packet.Kind == Kind.Hello)
        {
            if (packet.Serial == 0 || packet.Session != Guid.Empty || packet.Epoch != Guid.Empty ||
                (challenge != 0 && challenge != packet.Serial)) return false;
            if(State is LinkState.JoinDeclined or LinkState.JoinClosed)
            {
                SendRefusal(State==LinkState.JoinClosed ? JoinRefusal.Closed : JoinRefusal.Declined);
                return true;
            }
            if (!Compatible(packet.Build))
            {
                Send(new Packet(Kind.Reject, serial: packet.Serial));
                State = LinkState.Incompatible;
                return true;
            }
            challenge=packet.Serial;
            if(!AcceptingJoins && State!=LinkState.Connected)
            {
                RefuseJoin(now,JoinRefusal.Closed);
                return true;
            }
            if(!admitted)
            {
                JoinPending=true;
                Send(new Packet(Kind.JoinPending,serial:challenge));
                return true;
            }
            if (epoch == Guid.Empty)
            {
                challenge = packet.Serial;
                epoch = Guid.NewGuid();
            }
            SendWelcome();
            Received?.Invoke(packet.Kind);
            return true;
        }
        if (!host && State == LinkState.Handshake && packet.Serial == challenge)
        {
            if(packet.Kind==Kind.JoinPending && epoch==Guid.Empty && packet.Session==Guid.Empty && packet.Epoch==Guid.Empty)
            {
                JoinPending=true;
                return true;
            }
            if (packet.Kind == Kind.Reject && packet.Session == Guid.Empty && packet.Epoch == Guid.Empty)
            {
                JoinPending=false;
                State = (JoinRefusal)packet.Value switch {
                    JoinRefusal.Closed=>LinkState.JoinClosed,
                    JoinRefusal.Declined=>LinkState.JoinDeclined,
                    _=>LinkState.Incompatible
                };
                return true;
            }
            if (packet.Kind == Kind.Welcome && packet.Revision == 0 && packet.Session != Guid.Empty && packet.Epoch != Guid.Empty)
            {
                if (epoch != Guid.Empty && (epoch != packet.Epoch || session != packet.Session)) return false;
                if (!Compatible(packet.Build))
                {
                    State = LinkState.Incompatible;
                    return true;
                }
                session = packet.Session;
                epoch = packet.Epoch;
                JoinPending=false;
                nextControl = now + 1;
                SendReady();
                Received?.Invoke(packet.Kind);
                return true;
            }
        }
        if (!host && State == LinkState.Connected && packet.Kind == Kind.Welcome && packet.Revision != 0)
        {
            if (packet.Session != session || packet.Serial != challenge || packet.Epoch == Guid.Empty || !Compatible(packet.Build))
                return false;
            if (packet.Epoch != epoch)
            {
                if (WorldGeneration == ulong.MaxValue || packet.Revision != WorldGeneration + 1) return false;
                ResetWorld(packet.Epoch, packet.Revision);
                worldLoadDeadline = now + WorldLoadGraceSeconds;
            }
            else if (packet.Revision != WorldGeneration) return false;
            lastReceive = now;
            SendReady();
            Received?.Invoke(packet.Kind);
            return true;
        }
        if (epoch == Guid.Empty || packet.Epoch != epoch || packet.Session != session) return false;

        if (host && packet.Kind == Kind.Ready && packet.Serial == challenge && packet.Revision == WorldGeneration)
        {
            waitingForWorldReady = false;
            MarkConnected(now);
            SendState();
        }
        else if (waitingForWorldReady) return false;
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
        else if (packet.Kind is Kind.NativeChunk or Kind.NativeAck or Kind.NativeCommand or Kind.NativeReply or Kind.NativePresence or Kind.NativeTooltip or Kind.NativeProgress or Kind.NativeNeedFull or Kind.NativeSyncFailed)
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
            Send(new Packet(Kind.Pong, session, epoch, packet.Serial));
        else if (packet.Kind == Kind.Pong)
            Measurements.AcceptPong(packet.Serial, measurementClock?.Invoke() ?? now);
        else if (packet.Kind == Kind.Cursor)
        {
            if (packet.Serial <= receivedCursorSequence) return false;
            receivedCursorSequence = packet.Serial;
            RemoteCursor?.Invoke(packet);
            return true;
        }
        else if(packet.Kind==Kind.PointerName)
        {
            if(packet.Serial<=receivedNameSequence || !PointerProfile.TryRead(packet.Payload,out string name)) return false;
            receivedNameSequence=packet.Serial;
            RemotePointerName=name;
        }
        else if(packet.Kind==Kind.ItemPing)
        {
            if(packet.Serial<=receivedPingSequence) return false;
            receivedPingSequence=packet.Serial;
            if(now<nextIncomingItemPing) return false;
            nextIncomingItemPing=now+0.20;
            RemoteItemPing=(packet.Serial,packet.Revision,BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload));
        }
        else return false;

        lastReceive = now;
        Received?.Invoke(packet.Kind);
        return true;
    }

    internal void SetAcceptingJoins(bool enabled,double now)
    {
        if(!host || AcceptingJoins==enabled) return;
        AcceptingJoins=enabled;
        if(!enabled && State==LinkState.Handshake && challenge!=0) RefuseJoin(now,JoinRefusal.Closed);
    }

    internal void ApproveJoin()
    {
        if(!host || !JoinPending || !AcceptingJoins || State!=LinkState.Handshake) return;
        JoinPending=false;
        admitted=true;
        epoch=Guid.NewGuid();
        SendWelcome();
    }

    internal void DeclineJoin(double now)
    {
        if(host && JoinPending && State==LinkState.Handshake) RefuseJoin(now,JoinRefusal.Declined);
    }

    private void RefuseJoin(double now,JoinRefusal reason)
    {
        JoinPending=false;
        State=reason==JoinRefusal.Closed ? LinkState.JoinClosed : LinkState.JoinDeclined;
        phaseStart=now;
        SendRefusal(reason);
    }

    private void SendRefusal(JoinRefusal reason)=>Send(new Packet(Kind.Reject,serial:challenge,value:(ushort)reason));

    private bool Compatible(byte[] other) => fingerprint.AsSpan().SequenceEqual(other);

    private void MarkConnected(double now)
    {
        if (State != LinkState.Connected)
        {
            nextProbe = measurementClock?.Invoke() ?? now;
            Measurements.Reset(nextProbe);
        }
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

    private bool Send(Packet packet, bool cursor = false)
    {
        var bytes = Wire.Encode(packet);
        if (!send(bytes, cursor)) return false;
        if (!measureCarrierTraffic && State == LinkState.Connected) Measurements.AddSent(bytes.Length);
        return true;
    }
}
