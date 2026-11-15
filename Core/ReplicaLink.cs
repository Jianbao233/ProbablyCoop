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
    internal const int ChunkIndexBits = 9, ChunkIndexMask = (1 << ChunkIndexBits) - 1;
    private NativeSnapshotFrame latest, outgoing;
    private byte[] assembly, request;
    private byte[][] pages;
    private bool[] chunks;
    private int received, nextChunk;
    private int sendingPage, receivingPage, completeBytes, receivedBytes;
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
    internal NativeSnapshotFrame Snapshot { get; private set; }
    internal NativeSnapshotFrame FullSnapshot { get; private set; }
    internal byte[] State => Snapshot?.Single;
    internal byte[] FullState => FullSnapshot?.Single;
    internal bool SynchronizationFailed { get; private set; }
    private double nextFailureNotice;
    internal (int Pages, int CompletePages, int PageBytes, int ReceivedPageBytes, int ReceivedBytes) Transfer =>
        pages == null ? (0, 0, 0, 0, 0) :
        (pages.Length, receivingPage, assembly?.Length ?? 0, receivedBytes, completeBytes + receivedBytes);
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
        latest = outgoing = Snapshot = FullSnapshot = null;
        request = null;
        ClearAssembly();
        FullRevision=0; NeedsFull=forcePublish=false;
        requestedFull=0; retryFull=0;
        received = nextChunk = 0;
        sequence = outgoingRevision = assemblingRevision = lastRequest = pending = pendingRevision = Revision = presenceSerial = 0;
        retry = commandRetry = 0; lastResult = Result = 0;
        nextProgress=0; progressed=false; gap=-1; sentThrough=0; wantProjection=appliedCommand=0;
        ResultFromProjection=false;
        RepairsSent=GapsRequested=0;
        SynchronizationFailed=false; nextFailureNotice=0; sendingPage=0;
    }

    internal void Publish(byte[] value)
    {
        if (value == null || value.Length is <1 or >MaxBytes) throw new ArgumentException("Invalid native snapshot");
        Publish(new NativeSnapshotFrame(new[] { (byte[])value.Clone() }));
    }

    internal void Publish(NativeSnapshotFrame value)
    {
        if (!host || value == null) throw new ArgumentException("Invalid native snapshot");
        if(NeedsFull && value.Delta) return;
        NeedsFull=false;
        if (value.SameContent(latest)) return;
        latest = value;
    }

    internal ulong PublishCheckpoint(byte[] value) =>
        PublishCheckpoint(new NativeSnapshotFrame(new[] { (byte[])value.Clone() }));

    internal ulong PublishCheckpoint(NativeSnapshotFrame value)
    {
        if(!value.Full) throw new ArgumentException("Expected a complete native checkpoint");
        Publish(value);
        if(value.SameContent(outgoing)) return outgoingRevision;
        if(outgoing==null && !forcePublish && ReferenceEquals(Snapshot,FullSnapshot) && value.SameContent(Snapshot))
            return FullRevision;
        forcePublish=true;
        return sequence+1;
    }

    internal void Tick(double now, bool connected)
    {
        if (!connected) return;
        if (SynchronizationFailed)
        {
            if (host && now >= nextFailureNotice)
            {
                send(Kind.NativeSyncFailed,0,Math.Max(Revision,outgoingRevision),0,null);
                nextFailureNotice=now+0.5;
            }
            return;
        }
        if (host)
        {
            if (outgoing == null && latest != null && (forcePublish || !latest.SameContent(Snapshot)))
            { outgoing = latest; outgoingRevision = ++sequence; sendingPage=0; StartPage(); forcePublish=false; }
            if(outgoing==null) return;
            int count = ChunkCount(outgoing[sendingPage]);
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
        var page=outgoing[sendingPage];
        int offset=index*ChunkSize;
        var payload=new byte[4+Math.Min(ChunkSize,page.Length-offset)];
        BinaryPrimitives.WriteInt32LittleEndian(payload,page.Length);
        page.AsSpan(offset,payload.Length-4).CopyTo(payload.AsSpan(4));
        ulong serial=outgoing.Count==1 ? 0 : ((ulong)outgoing.Count << 16) | (uint)sendingPage;
        return send(Kind.NativeChunk,serial,outgoingRevision,(ushort)index,payload);
    }
    private static int ChunkCount(byte[] page) => (page.Length+ChunkSize-1)/ChunkSize;
    private void StartPage() { nextChunk=sentThrough=0; gap=-1; retry=0; }
    internal void FailSynchronization()
    {
        if(!host || SynchronizationFailed) return;
        SynchronizationFailed=true;
        outgoing=latest=null;
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
        if(pages==null && (Waiting || Revision==0)) return;
        double interval=pages!=null || wantProjection>appliedCommand ? 0.12 : 0.40;
        if(progressed || nextProgress==0) { progressed=false; nextProgress=now+interval; return; }
        if(now<nextProgress || Revision==ulong.MaxValue) return;
        nextProgress=now+interval;
        int missing=chunks==null ? 0 : Array.IndexOf(chunks,false);
        if(missing>=0 && send(Kind.NativeProgress,Revision,Revision+1,(ushort)((receivingPage<<ChunkIndexBits)|missing),null)) GapsRequested++;
    }
    private void CommitOutgoing()
    {
        Snapshot=outgoing; outgoing=null; Revision=outgoingRevision; gap=-1;
        RememberFull();
    }
    private void RememberFull()
    {
        if(Snapshot?.Full!=true) return;
        FullSnapshot=Snapshot; FullRevision=Revision;
        requestedFull=0;
    }
    internal bool RequestFull(ulong failedBase)
    {
        if(host || failedBase==0 || FullRevision!=failedBase || !NativeRecordFrame.IsDelta(State)) return false;
        FullRevision=0; FullSnapshot=null;
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
        if (packet.Kind == Kind.NativeSyncFailed && !host && packet.Revision>=Revision)
        {
            SynchronizationFailed=true;
            ClearAssembly();
            return true;
        }
        if (SynchronizationFailed) return false;
        if(packet.Kind==Kind.NativeNeedFull)
        {
            if(!host) return false;
            bool sending=packet.Revision==outgoingRevision && outgoing?.Delta==true;
            bool alreadyReceived=packet.Revision>FullRevision && packet.Revision<=Revision && NativeRecordFrame.IsDelta(State);
            if((sending || alreadyReceived) && outgoing?.Full!=true)
            {
                outgoing=null; gap=-1; forcePublish=true;
                // A replacement checkpoint may already be queued behind
                // the rejected delta. Preserve its promised wire revision.
                NeedsFull=latest?.Full!=true;
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
            if (packet.Revision!=outgoingRevision || packet.Value>sendingPage) return false;
            if (packet.Value<sendingPage) return true;
            if (sentThrough!=ChunkCount(outgoing[sendingPage])) return false;
            if (sendingPage+1==outgoing.Count) CommitOutgoing();
            else { sendingPage++; StartPage(); }
            return true;
        }
        if(packet.Kind==Kind.NativeProgress)
        {
            if(!host || packet.Serial==ulong.MaxValue || packet.Revision!=packet.Serial+1 ||
                (packet.Value & ChunkIndexMask)>=(MaxBytes+ChunkSize-1)/ChunkSize) return false;
            // Repeat the last complete acknowledgement and ask for exactly one
            // missing logical chunk. Never advance a revision from a partial receipt.
            if(outgoing!=null && packet.Serial==outgoingRevision && sendingPage==outgoing.Count-1 &&
                sentThrough==ChunkCount(outgoing[sendingPage])) CommitOutgoing();
            int page=packet.Value>>ChunkIndexBits, chunk=packet.Value & ChunkIndexMask;
            if(outgoing!=null && packet.Revision==outgoingRevision && page==sendingPage && chunk<sentThrough) gap=chunk;
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
        return ReceiveChunk(packet);
    }

    private bool ReceiveChunk(Packet packet)
    {
        ulong advertised=packet.Serial>>16;
        int pageCount=packet.Serial==0 ? 1 : (int)advertised;
        int page=(int)(packet.Serial & 0xffff);
        if(advertised>NativeSnapshotFrame.MaxPages || packet.Serial!=0 && pageCount<2 || page>=pageCount) return false;
        if (packet.Revision <= Revision)
        {
            if(packet.Revision==Revision && pageCount==Snapshot.Count)
                send(Kind.NativeAck,0,Revision,(ushort)(Snapshot.Count-1),null);
            return true;
        }
        var body = packet.Payload;
        if (body == null || body.Length <= 4) return false;
        int length = BinaryPrimitives.ReadInt32LittleEndian(body);
        int offsetBytes = packet.Value * ChunkSize;
        if (length < 1 || length > MaxBytes || offsetBytes >= length ||
            body.Length != 4 + Math.Min(ChunkSize, length - offsetBytes)) return false;
        if (pages==null || packet.Revision>assemblingRevision)
        {
            if(page!=0) return false;
            ClearAssembly();
            pages=new byte[pageCount][];
            assemblingRevision=packet.Revision;
        }
        if (packet.Revision!=assemblingRevision || pageCount!=pages.Length || page>receivingPage) return false;
        if (page<receivingPage)
        { send(Kind.NativeAck,0,assemblingRevision,(ushort)page,null); return true; }
        if (length>NativeSnapshotFrame.MaxPackedBytes-completeBytes) return RejectAssembly();
        if (assembly==null) { assembly=new byte[length]; chunks=new bool[ChunkCount(assembly)]; }
        if (assembly.Length!=length) return false;
        if (!chunks[packet.Value])
        {
            body.AsSpan(4).CopyTo(assembly.AsSpan(offsetBytes)); chunks[packet.Value]=true;
            received++; receivedBytes+=body.Length-4; progressed=true;
        }
        if (received == chunks.Length)
        {
            if(pageCount>1 && !NativeRecordFrame.IsFull(assembly)) return RejectAssembly();
            if(pageCount==1 && NativeRecordFrame.IsDelta(assembly) && !NativeRecordFrame.HasBase(assembly,FullRevision))
            {
                ClearAssembly();
                send(Kind.NativeNeedFull,0,packet.Revision,0,null);
                return true;
            }
            pages[receivingPage++]=assembly;
            completeBytes+=assembly.Length;
            assembly=null; chunks=null; received=receivedBytes=0;
            if(receivingPage==pageCount)
            {
                Snapshot=new NativeSnapshotFrame(pages);
                Revision=assemblingRevision;
                ClearAssembly();
                RememberFull();
            }
            send(Kind.NativeAck,0,packet.Revision,(ushort)page,null);
        }
        return true;
    }
    private bool RejectAssembly()
    {
        ClearAssembly();
        SynchronizationFailed=true;
        return false;
    }
    private void ClearAssembly()
    {
        assembly=null; chunks=null; pages=null;
        receivingPage=completeBytes=receivedBytes=received=0;
    }
}
