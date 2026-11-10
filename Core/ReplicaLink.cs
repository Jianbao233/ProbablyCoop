using System;
using System.Buffers.Binary;

namespace ProbablyCoop.Core;

/// <summary>Bounded, atomic snapshots and exactly-once requests inside an authenticated
/// CoopSession epoch. Large snapshots never increase the transport datagram limit.</summary>
internal sealed partial class ReplicaLink
{
    internal const int ChunkSize = 16000, MaxBytes = 4 * 1024 * 1024, MaxCommand = 4096;
    private readonly bool host;
    private readonly Func<Kind, ulong, ulong, ushort, byte[], bool> send;
    private byte[] latest, outgoing, assembly, request;
    private bool[] chunks;
    private int received, nextChunk;
    private ulong sequence, outgoingRevision, assemblingRevision, lastRequest, pending;
    private ulong pendingRevision;
    private ushort lastResult;
    private double retry, commandRetry;
    private double nextProgress;
    private bool progressed;
    private int gap=-1,sentThrough;
    private ulong wantProjection,appliedCommand;
    internal int RepairsSent {get;private set;}
    internal int GapsRequested {get;private set;}
    // Local lifetime marker, not a wire revision. A reset may restart revision
    // numbering while a worker is still finishing a snapshot of the old epoch.
    private ulong generation;
    private ulong presenceSerial;
    internal readonly struct Stamp
    {
        private readonly ReplicaLink owner;
        private readonly ulong generation;
        internal Stamp(ReplicaLink owner, ulong generation)
        { this.owner = owner; this.generation = generation; }
        internal bool IsCurrent(ReplicaLink link) => owner != null &&
            ReferenceEquals(owner, link) && generation == link.generation;
    }
    internal Stamp CaptureStamp() => new Stamp(this, generation);
    internal byte[] State { get; private set; }
    internal byte[] FullState { get; private set; }
    internal ulong FullRevision { get; private set; }
    internal bool NeedsFull { get; private set; }
    private bool forcePublish;
    private ulong requestedFull;
    private double retryFull;
    internal ulong Revision { get; private set; }
    internal bool Waiting => pending != 0;
    internal ulong PendingRequest => pending;
    internal ulong CompletedRequest => lastRequest;
    internal ushort CompletedResult => lastResult;
    internal bool ResultFromProjection { get; private set; }
    internal ushort Result { get; private set; }
    internal Func<byte[], ushort> Execute;
    internal Action<byte[]> AcceptPresence;
    internal const int MaxTooltip=16000;
    internal Action<uint,ulong,byte[]> AcceptTooltip;
    internal bool PublishTooltip(uint interest,ulong observation,byte[] value) =>
        host && interest!=0 && observation!=0 && value!=null && value.Length is >0 and <=MaxTooltip &&
        send(Kind.NativeTooltip,interest,observation,0,value);

    internal void PublishPresence(byte[] value)
    {
        if(host || !ValidPresence(value)) return;
        send(Kind.NativePresence,++presenceSerial,Revision,0,value);
    }
    internal static bool ValidPresence(ReadOnlySpan<byte> value) => value.Length is >=8 and <=256 &&
        value.Length%4==0 && (BinaryPrimitives.ReadUInt32LittleEndian(value)==0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(4,4))!=0);

    internal ReplicaLink(bool host, Func<Kind, ulong, ulong, ushort, byte[], bool> send)
    { this.host = host; this.send = send; }

    internal void Reset()
    {
        ClearIndependentRequests();
        generation++;
        latest = outgoing = assembly = request = State = FullState = null; chunks = null;
        FullRevision=0; NeedsFull=forcePublish=false;
        requestedFull=0; retryFull=0;
        received = nextChunk = 0;
        sequence = outgoingRevision = assemblingRevision = lastRequest = pending = pendingRevision = Revision = presenceSerial = 0;
        retry = commandRetry = 0; lastResult = Result = 0;
        nextProgress=0; progressed=false; gap=-1; sentThrough=0; wantProjection=appliedCommand=0;
        ResultFromProjection=false;
        RepairsSent=GapsRequested=0;
    }

    internal void Publish(byte[] value)
    {
        if (!host || value == null || value.Length == 0 || value.Length > MaxBytes)
            throw new ArgumentException("Invalid native snapshot");
        if(NeedsFull && NativeRecordFrame.IsDelta(value)) return;
        NeedsFull=false;
        if (latest != null && latest.AsSpan().SequenceEqual(value)) return;
        latest = (byte[])value.Clone();
    }

    internal ulong PublishCheckpoint(byte[] value)
    {
        if(!NativeRecordFrame.IsFull(value)) throw new ArgumentException("Expected a complete native checkpoint");
        Publish(value);
        if(outgoing!=null && outgoing.AsSpan().SequenceEqual(value)) return outgoingRevision;
        if(outgoing==null && !forcePublish && State!=null && ReferenceEquals(State,FullState) && State.AsSpan().SequenceEqual(value))
            return FullRevision;
        forcePublish=true;
        return sequence+1;
    }

    internal void Tick(double now, bool connected)
    {
        if (!connected) return;
        if (host)
        {
            if (outgoing == null && latest != null && (forcePublish || State == null || !State.AsSpan().SequenceEqual(latest)))
            { outgoing = latest; outgoingRevision = ++sequence; nextChunk = sentThrough = 0; gap=-1; retry = 0; forcePublish=false; }
            if(outgoing==null) return;
            int count = (outgoing.Length + ChunkSize - 1) / ChunkSize;
            int budget=4;
            if(gap>=0)
            {
                int index=gap; gap=-1;
                if(now<retry || index<nextChunk)
                {
                    budget--;
                    if(SendChunk(index)) RepairsSent++;
                }
            }
            if(now<retry) return;
            for (int n=0;n<budget && nextChunk<count;n++,nextChunk++)
            {
                if(!SendChunk(nextChunk)) break;
                sentThrough=Math.Max(sentThrough,nextChunk+1);
            }
            if (nextChunk >= count) { nextChunk = 0; retry = now + 1; }
        }
        else
        {
            if(requestedFull!=0 && now>=retryFull)
            {
                send(Kind.NativeNeedFull,0,requestedFull,0,null);
                retryFull=now+0.25;
            }
            if(Waiting && now>=commandRetry) SendRequest(now);
            RetryIndependentRequests(now);
            ReportProgress(now);
        }
    }
    private bool SendChunk(int index)
    {
        int offset=index*ChunkSize;
        var payload=new byte[4+Math.Min(ChunkSize,outgoing.Length-offset)];
        BinaryPrimitives.WriteInt32LittleEndian(payload,outgoing.Length);
        outgoing.AsSpan(offset,payload.Length-4).CopyTo(payload.AsSpan(4));
        return send(Kind.NativeChunk,0,outgoingRevision,(ushort)index,payload);
    }
    internal bool ProjectionApplied(ulong through,ushort result)
    {
        if(host || result>(ushort)ShopResult.Failed || (through==0)!=(result==(ushort)ShopResult.None))
            throw new ArgumentException("Invalid native snapshot receipt");
        appliedCommand=Math.Max(appliedCommand,through);
        if(!Waiting || through!=pending) return false;
        FinishRequest(result,true); return true;
    }
    private void FinishRequest(ushort result,bool fromProjection)
    {
        lastRequest=pending; pending=0; request=null; Result=result; ResultFromProjection=fromProjection;
        if(result==(ushort)ShopResult.Applied) { wantProjection=lastRequest; progressed=true; }
    }
    private void ReportProgress(double now)
    {
        if(assembly==null && (Waiting || Revision==0)) return;
        double interval=assembly!=null || wantProjection>appliedCommand ? 0.12 : 0.40;
        if(progressed || nextProgress==0) { progressed=false; nextProgress=now+interval; return; }
        if(now<nextProgress || Revision==ulong.MaxValue) return;
        nextProgress=now+interval;
        int missing=chunks==null ? 0 : Array.IndexOf(chunks,false);
        if(missing>=0 && send(Kind.NativeProgress,Revision,Revision+1,(ushort)missing,null)) GapsRequested++;
    }
    private void CommitOutgoing()
    {
        State=outgoing; outgoing=null; Revision=outgoingRevision; gap=-1;
        RememberFull();
    }
    private void RememberFull()
    {
        if(!NativeRecordFrame.IsFull(State)) return;
        FullState=State; FullRevision=Revision;
        requestedFull=0;
    }
    internal bool RequestFull(ulong failedBase)
    {
        if(host || failedBase==0 || FullRevision!=failedBase || !NativeRecordFrame.IsDelta(State)) return false;
        FullRevision=0; FullState=null;
        requestedFull=Revision; retryFull=0;
        return true;
    }

    internal bool Submit(byte[] command,double now)
    {
        if (host || Waiting || Revision == 0 || command == null || command.Length == 0 || command.Length > MaxCommand || !CanIssueRequest()) return false;
        pending = ++nextIssuedRequest; pendingRevision=Revision; request = (byte[])command.Clone(); Result = 0; ResultFromProjection=false;
        SendRequest(now);
        return true;
    }
    private void SendRequest(double now)
    {
        bool sent=send(Kind.NativeCommand,pending,pendingRevision,0,request);
        commandRetry=now+(sent ? 0.5 : 0.05);
    }

    internal bool Receive(Packet packet)
    {
        if(packet.Kind==Kind.NativeNeedFull)
        {
            if(!host) return false;
            bool sending=packet.Revision==outgoingRevision && NativeRecordFrame.IsDelta(outgoing);
            bool alreadyReceived=packet.Revision>FullRevision && packet.Revision<=Revision && NativeRecordFrame.IsDelta(State);
            if((sending || alreadyReceived) && !NativeRecordFrame.IsFull(outgoing))
            {
                outgoing=null; gap=-1; forcePublish=true;
                // A replacement checkpoint may already be queued behind
                // the rejected delta. Preserve its promised wire revision.
                NeedsFull=!NativeRecordFrame.IsFull(latest);
                if(NeedsFull) latest=null;
            }
            return true;
        }
        if(packet.Kind==Kind.NativeTooltip)
        {
            if(host || packet.Serial==0 || packet.Serial>uint.MaxValue || packet.Revision==0 || packet.Payload==null ||
                packet.Payload.Length is 0 or >MaxTooltip) return false;
            AcceptTooltip?.Invoke((uint)packet.Serial,packet.Revision,packet.Payload);
            return true;
        }
        if(packet.Kind==Kind.NativePresence)
        {
            if(!host || packet.Serial==0 || !ValidPresence(packet.Payload)) return false;
            if(packet.Serial<=presenceSerial) return true;
            presenceSerial=packet.Serial;
            AcceptPresence?.Invoke(packet.Payload); return true;
        }
        if (host && packet.Kind == Kind.NativeAck)
        {
            if (outgoing == null) return packet.Revision == Revision;
            if (packet.Revision != outgoingRevision) return false;
            CommitOutgoing(); return true;
        }
        if(packet.Kind==Kind.NativeProgress)
        {
            if(!host || packet.Serial==ulong.MaxValue || packet.Revision!=packet.Serial+1 ||
                packet.Value>=(MaxBytes+ChunkSize-1)/ChunkSize) return false;
            // Repeat the last complete acknowledgement and ask for exactly one
            // missing logical chunk. Never advance a revision from a partial receipt.
            if(outgoing!=null && packet.Serial==outgoingRevision) CommitOutgoing();
            if(outgoing!=null && packet.Revision==outgoingRevision && packet.Value<sentThrough) gap=packet.Value;
            return true;
        }
        if (host && packet.Kind == Kind.NativeCommand)
        {
            return ReceiveOrderedCommand(packet);
        }
        if (!host && packet.Kind == Kind.NativeReply)
        {
            if (packet.Value is 0 or > (ushort)ShopResult.Failed) return false;
            if (ReceiveIndependentReply(packet)) return true;
            if (!Waiting) return packet.Serial == lastRequest;
            if (packet.Serial != pending) return false;
            FinishRequest(packet.Value,false);
            return true;
        }
        if (host || packet.Kind != Kind.NativeChunk || packet.Revision == 0) return false;
        if (packet.Revision <= Revision)
        { send(Kind.NativeAck, 0, Revision, 0, null); return true; }
        var body = packet.Payload;
        if (body == null || body.Length <= 4) return false;
        int length = BinaryPrimitives.ReadInt32LittleEndian(body);
        int offsetBytes = packet.Value * ChunkSize;
        if (length < 1 || length > MaxBytes || offsetBytes >= length ||
            body.Length != 4 + Math.Min(ChunkSize, length - offsetBytes)) return false;
        if (assembly == null || packet.Revision > assemblingRevision)
        {
            assembly = new byte[length]; chunks = new bool[(length + ChunkSize - 1) / ChunkSize];
            received = 0; assemblingRevision = packet.Revision;
        }
        if (packet.Revision != assemblingRevision || assembly.Length != length) return false;
        if (!chunks[packet.Value])
        { body.AsSpan(4).CopyTo(assembly.AsSpan(offsetBytes)); chunks[packet.Value] = true; received++; progressed=true; }
        if (received == chunks.Length)
        {
            if(NativeRecordFrame.IsDelta(assembly) && !NativeRecordFrame.HasBase(assembly,FullRevision))
            {
                assembly=null; chunks=null;
                send(Kind.NativeNeedFull,0,assemblingRevision,0,null);
                return true;
            }
            State = assembly; Revision = assemblingRevision; assembly = null; chunks = null;
            RememberFull();
            send(Kind.NativeAck, 0, Revision, 0, null);
        }
        return true;
    }
}
