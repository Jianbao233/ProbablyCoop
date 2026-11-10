using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;

// QA-only relay on the same adapter as the two disposable Unity players.
// Forwards signed datagrams byte-for-byte and drops a selected fragment and
// acknowledgements of that one revision. It never knows the room secret.
public sealed class ProbablyCoopQaLanRelay : IDisposable
{
    private readonly UdpClient socket;
    private readonly IPEndPoint host;
    private readonly Thread thread;
    private readonly Stopwatch clock=Stopwatch.StartNew();
    private volatile bool stopping;
    private IPEndPoint guest;
    private IPEndPoint targetGuest;
    private Guid observedSession, observedEpoch, targetSession, targetEpoch;
    private ulong highestObservedRevision;
    private Guid progressSession, progressEpoch;
    private ulong latestProgressWanted;
    private ulong targetRevision,fragmentSequence;
    private long drop=-1,gap=-1,repair=-1,ack=-1,repeatAck=-1,next=-1;
    private int ackDrops,chunks;
    private readonly bool dropReply;
    private ulong lostCommand;
    private int replyDrops;
    private IPEndPoint replyGuest;
    private bool nextCommand;
    private readonly bool activeLoss;
    private volatile bool armed;
    public void ArmActiveLoss() { armed=true; }
    private uint hoverEntity, hoverInterest, lostHoverEntity;
    private ulong lostHoverObservation, lostHoverInterest;
    private string error;
    private readonly int delayMilliseconds;
    private readonly bool injectLoss;
    private readonly Queue<string> lossTrace = new Queue<string>();
    private int delayedForwarded;
    private long minimumDelay=long.MaxValue,maximumDelay;
    private readonly Queue<(byte[] Bytes, IPEndPoint Destination, long Due)> delayed = new Queue<(byte[], IPEndPoint, long)>();
    public int Port { get; private set; }

    public ProbablyCoopQaLanRelay(string ip,int port,bool dropReply,bool activeLoss,int delayMilliseconds=0,bool injectLoss=true)
    {
        if(delayMilliseconds<0 || delayMilliseconds>1000) throw new ArgumentOutOfRangeException(nameof(delayMilliseconds));
        this.delayMilliseconds=delayMilliseconds;
        this.injectLoss=injectLoss;
        host=new IPEndPoint(IPAddress.Parse(ip),port);
        this.dropReply=dropReply;
        this.activeLoss=activeLoss;
        socket=new UdpClient(new IPEndPoint(host.Address,0));
        socket.Client.ReceiveBufferSize=1024*1024;
        socket.Client.SendBufferSize=1024*1024;
        if(delayMilliseconds>0) socket.Client.ReceiveTimeout=5;
        Port=((IPEndPoint)socket.Client.LocalEndPoint).Port;
        thread=new Thread(Forward) { IsBackground=true,Name="ProbablyCoop QA UDP relay" };
        thread.Start();
    }
    private void Forward()
    {
        try
        {
            while(!stopping)
            {
                while(delayed.Count>0 && delayed.Peek().Due<=clock.ElapsedMilliseconds)
                {
                    var scheduled=delayed.Dequeue();
                    long elapsed=clock.ElapsedMilliseconds-scheduled.Due+delayMilliseconds;
                    socket.Send(scheduled.Bytes,scheduled.Bytes.Length,scheduled.Destination);
                    delayedForwarded++;
                    minimumDelay=Math.Min(minimumDelay,elapsed);
                    maximumDelay=Math.Max(maximumDelay,elapsed);
                }
                var from=new IPEndPoint(IPAddress.Any,0);
                byte[] data;
                try { data=socket.Receive(ref from); }
                catch(SocketException ex) when(ex.SocketErrorCode==SocketError.ConnectionReset) { continue; }
                catch(SocketException ex) when(ex.SocketErrorCode==SocketError.TimedOut) { continue; }
                if(!from.Address.Equals(host.Address) || data.Length<=16) continue;
                bool fromHost=from.Equals(host);
                if(!fromHost && !from.Equals(guest))
                {
                    if(!WireKind(data,17,1)) continue; // A new Unity guest's Hello.
                    guest=from;
                }
                if(guest==null || injectLoss && DropOrObserve(data,fromHost)) continue;
                var destination=fromHost ? guest : host;
                if(delayMilliseconds==0) socket.Send(data,data.Length,destination);
                else
                {
                    if(delayed.Count>=8192) throw new InvalidOperationException("QA delay queue exhausted");
                    delayed.Enqueue((data,destination,clock.ElapsedMilliseconds+delayMilliseconds));
                }
            }
        }
        catch(Exception ex) { if(!stopping) error=ex.ToString(); }
    }
    private static bool WireKind(byte[] data,int offset,int kind)
    {
        return data.Length>=offset+60 && (offset!=17 || data[16]==0) &&
            BitConverter.ToUInt32(data,offset)==0x504f4350 && data[offset+5]==kind;
    }
    private static int FirstWireOffset(byte[] data) =>
        data[16]==0 ? 17 : data[16]==3 && data.Length>28 && data[27]==0 ? 28 : 0;

    private bool MatchesLossTarget(byte[] data,int offset) => guest.Equals(targetGuest) &&
        new Guid(data.AsSpan(offset+8,16))==targetSession && new Guid(data.AsSpan(offset+24,16))==targetEpoch;

    private void TraceLoss(long now,string detail)
    {
        if(drop>=0 && now-drop>2500) return;
        lock(lossTrace)
        {
            if(lossTrace.Count>=96) lossTrace.Dequeue();
            lossTrace.Enqueue($"{now}ms {detail}");
        }
    }

    public string LossTrace
    {
        get { lock(lossTrace) return string.Join(Environment.NewLine,lossTrace); }
    }

    private bool DropOrObserve(byte[] data,bool fromHost)
    {
        if(activeLoss && !fromHost && WireKind(data,17,17) && data.Length>=85)
        {
            hoverEntity=BitConverter.ToUInt32(data,77);
            hoverInterest=BitConverter.ToUInt32(data,81);
        }
        if(activeLoss && fromHost && lostHoverEntity==0 && next>=0 && hoverEntity!=0)
        {
            int offset=FirstWireOffset(data);
            if(offset!=0 && WireKind(data,offset,18) && MatchesLossTarget(data,offset) &&
                BitConverter.ToUInt64(data,offset+40)==hoverInterest)
            {
                lostHoverEntity=hoverEntity; lostHoverInterest=hoverInterest;
                lostHoverObservation=BitConverter.ToUInt64(data,offset+48);
                return true;
            }
        }
        long now=clock.ElapsedMilliseconds;
        if(dropReply && fromHost && WireKind(data,17,16))
        {
            ulong serial=BitConverter.ToUInt64(data,57);
            if(lostCommand==0 && serial>0 && BitConverter.ToUInt16(data,73)==1)
            { lostCommand=serial; replyGuest=guest; }
            if(lostCommand!=0 && serial==lostCommand && guest.Equals(replyGuest)) { replyDrops++; return true; }
        }
        if(dropReply && !fromHost && lostCommand!=0 && guest.Equals(replyGuest) && WireKind(data,17,15) &&
            BitConverter.ToUInt64(data,57)>lostCommand) nextCommand=true;
        if(fromHost)
        {
            int offset=FirstWireOffset(data);
            if(offset!=0 && WireKind(data,offset,13) && data.Length>=offset+64)
            {
                var session=new Guid(data.AsSpan(offset+8,16));
                var epoch=new Guid(data.AsSpan(offset+24,16));
                ulong revision=BitConverter.ToUInt64(data,offset+48);
                ushort slot=BitConverter.ToUInt16(data,offset+56);
                if(session!=observedSession || epoch!=observedEpoch)
                {
                    observedSession=session; observedEpoch=epoch; highestObservedRevision=0;
                }
                bool firstTransmission=revision>highestObservedRevision;
                highestObservedRevision=Math.Max(highestObservedRevision,revision);
                if(drop<0 || now-drop<=2500)
                    TraceLoss(now,$"source revision={revision} slot={slot} first={firstTransmission} armed={armed} fragment={(offset==28 ? BitConverter.ToUInt64(data,17) : 0)}");
                // A prior progress request can cross the first send in flight.
                // Reserve this probe for loss that causes a new repair request.
                bool alreadyRequested=latestProgressWanted==revision && progressSession==session && progressEpoch==epoch;
                if(targetRevision==0 && firstTransmission && !alreadyRequested && revision>=3 && offset==28 && slot==0 &&
                    (!activeLoss || armed && hoverEntity!=0))
                {
                    targetRevision=revision; targetSession=session; targetEpoch=epoch; targetGuest=guest;
                    fragmentSequence=BitConverter.ToUInt64(data,17);
                    chunks=(BitConverter.ToInt32(data,offset+60)+15999)/16000;
                    TraceLoss(now,$"target revision={revision} fragment={fragmentSequence} logicalChunks={chunks}");
                }
                if(targetRevision!=0 && MatchesLossTarget(data,offset))
                {
                    if(gap>=0 && revision==targetRevision && slot==0 && repair<0) repair=now;
                    if(repeatAck>=0 && revision>targetRevision && next<0) next=now;
                }
            }
            if(targetRevision!=0 && guest.Equals(targetGuest) && data[16]==3 && data.Length>=28 &&
                BitConverter.ToUInt64(data,17)==fragmentSequence && data[27]==1 && drop<0)
            {
                drop=now; TraceLoss(now,$"drop revision={targetRevision} fragment={fragmentSequence} index=1"); return true;
            }
        }
        if(!fromHost && WireKind(data,17,19))
        {
            ulong complete=BitConverter.ToUInt64(data,57),wanted=BitConverter.ToUInt64(data,65);
            ushort slot=BitConverter.ToUInt16(data,73);
            if(targetRevision==0 && slot==0)
            {
                progressSession=new Guid(data.AsSpan(25,16)); progressEpoch=new Guid(data.AsSpan(41,16));
                latestProgressWanted=wanted;
            }
            if(drop<0 || now-drop<=2500) TraceLoss(now,$"progress complete={complete} wanted={wanted} slot={slot}");
            if(targetRevision!=0 && MatchesLossTarget(data,17))
            {
                if(wanted==targetRevision && slot==0 && drop>=0 && gap<0) gap=now;
                if(complete==targetRevision && ack>=0 && repeatAck<0) repeatAck=now;
            }
        }
        if(!fromHost && targetRevision!=0)
        {
            if(WireKind(data,17,14) && MatchesLossTarget(data,17) && BitConverter.ToUInt64(data,65)==targetRevision)
            {
                if(ack<0) ack=now;
                ackDrops++; TraceLoss(now,$"drop ack revision={targetRevision}"); return true;
            }
        }
        if(!fromHost && (drop<0 || now-drop<=2500))
        {
            if(WireKind(data,17,14)) TraceLoss(now,$"ack revision={BitConverter.ToUInt64(data,65)}");
            else if(WireKind(data,17,20)) TraceLoss(now,$"needFull revision={BitConverter.ToUInt64(data,65)}");
        }
        return false;
    }
    public ulong LostCommand { get { return lostCommand; } }
    public bool DelayPassed => error==null && delayedForwarded>0 && minimumDelay>=delayMilliseconds;
    public string DelaySummary => $"{(DelayPassed ? "PASS" : "FAIL")} configured_one_way_ms={delayMilliseconds} forwarded={delayedForwarded} min_observed_ms={minimumDelay} max_observed_ms={maximumDelay} error={error ?? "none"}";
    public uint LostHoverEntity { get { return lostHoverEntity; } }
    public ulong LostHoverInterest { get { return lostHoverInterest; } }
    public ulong LostHoverObservation { get { return lostHoverObservation; } }
    public bool Passed { get { return error==null && drop>=0 && gap>=drop && repair>=gap && repair-drop<1000 &&
        ack>=repair && repeatAck>=ack && next>=repeatAck && next-ack<1000 && (!dropReply || replyDrops>0 && nextCommand); } }
    public string Summary { get { return string.Format(CultureInfo.InvariantCulture,
        "{0} real IPv4 fragment loss: revision={1} chunks={2} gap_ms={3} repair_ms={4} ack_drops={5} next_revision_ms_after_ack_loss={6} error={7} lost_command={8} reply_drops={9} next_command={10}",
        Passed ? "PASS" : "FAIL",targetRevision,chunks,gap<0 ? -1 : gap-drop,repair<0 ? -1 : repair-drop,ackDrops,next<0 ? -1 : next-ack,error??"none",lostCommand,replyDrops,nextCommand); } }
    public void Dispose()
    {
        stopping=true; socket.Close();
        if(!thread.Join(3000)) throw new InvalidOperationException("QA relay thread did not exit");
    }
}
