using ProbablyCoop.Core;

internal static class ReplicaLinkTests
{
    internal static void Run()
    {
        var a = new List<Packet>(); var b = new List<Packet>();
        var host = new ReplicaLink(true, (k,s,r,v,p) => { a.Add(new Packet(k, serial:s, revision:r, value:v, payload:p)); return true; });
        var guest = new ReplicaLink(false, (k,s,r,v,p) => { b.Add(new Packet(k, serial:s, revision:r, value:v, payload:p)); return true; });
        int executions = 0;
        host.Execute = _ => { executions++; return (ushort)ShopResult.Applied; };
        var first = new byte[ReplicaLink.ChunkSize * 11 + 17]; new Random(42).NextBytes(first);
        host.Publish(first);
        for (int i=0;i<3;i++) host.Tick(i * 0.01, true);
        if (a.Count != 12) throw new Exception("chunk budget");
        // Deliver reversed, duplicate each, drop one. No half-snapshot may become visible.
        var lost = a[3];
        foreach(var p in a.AsEnumerable().Reverse().Where(p=>p.Value!=3))
        { Roundtrip(p); guest.Receive(p); guest.Receive(p); }
        if (guest.State != null) throw new Exception("partial snapshot exposed");
        guest.Receive(lost);
        if (!guest.State!.SequenceEqual(first)) throw new Exception("snapshot corrupted");
        foreach(var p in b) host.Receive(p);
        a.Clear(); b.Clear();
        if (!guest.Submit(new byte[]{1,2},1)) throw new Exception("command not sent");
        if(b.Count!=1) throw new Exception("command did not leave the guest input callback");
        if(guest.PendingRequest!=1 || guest.CompletedRequest!=0 || host.CompletedRequest!=0) throw new Exception("premature command watermark");
        guest.Tick(1,true); var command=b.Single(); Roundtrip(command);
        host.Receive(command); host.Receive(command);
        if(executions!=1) throw new Exception("duplicate mutation");
        if(host.CompletedRequest!=1) throw new Exception("executed request watermark missing");
        guest.Receive(a.Last());
        if(guest.Waiting || guest.Result!=(ushort)ShopResult.Applied) throw new Exception("command ack");
        if(guest.CompletedRequest!=1 || guest.PendingRequest!=0) throw new Exception("reply watermark missing");
        b.Clear(); byte[] presence=null; host.AcceptPresence=p=>presence=p;
        guest.PublishPresence(new byte[]{1,0,0,0,1,0,0,0}); Roundtrip(b.Single());
        if(!host.Receive(b.Single()) || presence?.Length!=8 || executions!=1 || guest.Waiting) throw new Exception("presence altered business request");
        if(guest.Receive(b.Single()) || host.Receive(new Packet(Kind.NativePresence,payload:new byte[257]))) throw new Exception("invalid presence accepted");
        var open=b.Single();
        guest.PublishPresence(new byte[]{0,0,0,0,2,0,0,0}); var close=b.Last(); Roundtrip(close);
        int changes=0;
        host.AcceptPresence=p=>{ presence=p; changes++; };
        host.Receive(close); host.Receive(open); host.Receive(close);
        if(changes!=1 || presence?[0]!=0 || close.Serial<=open.Serial) throw new Exception("late presence reopened a closed window");
        var staleRead=new Packet(Kind.NativeTooltip,serial:1,revision:8,payload:new byte[]{2});
        Roundtrip(staleRead);
        bool read=false;
        guest.AcceptTooltip=(interest,observed,bytes)=>read=interest==1 && observed==8 && bytes[0]==2;
        if(!guest.Receive(staleRead) || !read || host.Receive(staleRead) || guest.PublishTooltip(1,8,new byte[]{2}) ||
            host.PublishTooltip(1,0,new byte[]{2}) || host.PublishTooltip(1,8,new byte[ReplicaLink.MaxTooltip+1]) ||
            host.Receive(new Packet(Kind.NativePresence,serial:500,payload:new byte[]{1,0,0,0,0,0,0,0})))
            throw new Exception("invalid tooltip routing/bounds/target generation");
        if(Wire.TryDecode(Wire.Encode(new Packet(Kind.NativePresence,payload:new byte[0])),false,out _))
            throw new Exception("wire accepted unsequenced native presence");
        var second = new byte[]{9,8}; host.Publish(second); a.Clear(); host.Tick(2,true);
        guest.Receive(a.Single()); guest.Receive(lost);
        if(!guest.State.SequenceEqual(second)) throw new Exception("old revision applied");
        guest.Reset();
        if(guest.State!=null || guest.Revision!=0 || guest.Waiting) throw new Exception("epoch reset");
        if(guest.CompletedRequest!=0 || guest.PendingRequest!=0) throw new Exception("request watermark crossed epoch");
        CheckWorkerEpochs();
        CheckImmediateRetry();
        CheckTooltipOrdering();
        CheckLostChunkAndAck();
        CheckSnapshotReceipt();
        CheckPinnedCheckpoint();
        Console.WriteLine("PASS replica transfer: bounded chunks, loss/reorder/duplicates, atomic apply, command dedup, epoch reset");
    }
    private static void CheckPinnedCheckpoint()
    {
        var h=new List<Packet>(); var g=new List<Packet>();
        var host=new ReplicaLink(true,(k,s,r,v,p)=>{ h.Add(new Packet(k,serial:s,revision:r,value:v,payload:p)); return true; });
        var guest=new ReplicaLink(false,(k,s,r,v,p)=>{ g.Add(new Packet(k,serial:s,revision:r,value:v,payload:p)); return true; });
        byte[] full={0x50,0x53,0x42,0x31,42};
        void Deliver(int time)
        {
            h.Clear(); g.Clear(); host.Tick(time,true);
            foreach(var piece in h){Roundtrip(piece); guest.Receive(piece);}
            foreach(var reply in g){Roundtrip(reply); host.Receive(reply);}
        }
        if(host.PublishCheckpoint(full)!=1) throw new Exception("first full revision");
        Deliver(0); if(guest.FullRevision!=1 || host.FullRevision!=1) throw new Exception("full not pinned on both peers");
        host.Publish(NativeRecordFrame.Delta(full,1)); Deliver(1);
        if(guest.Revision!=2 || guest.FullRevision!=1 || host.FullRevision!=1) throw new Exception("delta replaced basis");
        host.Publish(NativeRecordFrame.Delta(full,999)); Deliver(2);
        if(guest.Revision!=2 || !host.NeedsFull || !g.Any(p=>p.Kind==Kind.NativeNeedFull)) throw new Exception("orphan was applied");
        if(host.PublishCheckpoint(full)!=4) throw new Exception("repair reused an unconfirmed revision");
        Deliver(3);
        if(host.FullRevision!=4 || guest.FullRevision!=4) throw new Exception("new full did not recover");
        host.Publish(NativeRecordFrame.Delta(full,4)); h.Clear(); host.Tick(4,true);
        if(host.PublishCheckpoint(full)!=6) throw new Exception("identical pending checkpoint crossed an in-flight delta");
        g.Clear(); foreach(var frame in h) guest.Receive(frame); foreach(var reply in g) host.Receive(reply); Deliver(5);
        if(guest.FullRevision!=6 || host.FullRevision!=6) throw new Exception("checkpoint rotated early");
        host.Publish(NativeRecordFrame.Delta(full,999));
        h.Clear(); host.Tick(6,true);
        if(host.PublishCheckpoint(full)!=8) throw new Exception("queued repair revision");
        g.Clear(); foreach(var frame in h) guest.Receive(frame); foreach(var reply in g) host.Receive(reply);
        Deliver(7);
        if(guest.FullRevision!=8 || host.FullRevision!=8 || host.NeedsFull) throw new Exception("orphan discarded already queued full repair");
        host.Publish(NativeRecordFrame.Delta(full,8)); Deliver(8);
        if(!guest.RequestFull(8) || guest.FullState!=null) throw new Exception("could not evict invalid record basis");
        g.Clear(); guest.Tick(9,true); // Drop the request and let the guest repeat it.
        g.Clear(); guest.Tick(9.5,true);
        foreach(var reply in g) host.Receive(reply);
        if(!host.NeedsFull) throw new Exception("already acknowledged bad delta did not request recovery");
        host.PublishCheckpoint(full); Deliver(10);
        if(guest.FullRevision!=10 || host.FullRevision!=10 || guest.RequestFull(8)) throw new Exception("delayed error evicted newer full");
        guest.Reset(); host.Reset();
        if(guest.FullState!=null || host.FullRevision!=0 || host.NeedsFull) throw new Exception("record basis survived new epoch");
        Console.WriteLine("PASS pinned native world: received/acknowledged full, skip intermediate delta, refuse missing base, repair, same bytes in flight, epoch cleanup");
    }
    private static void CheckSnapshotReceipt()
    {
        var packets=new List<Packet>();
        var guest=new ReplicaLink(false,(k,s,r,v,p)=>{packets.Add(new Packet(k,serial:s,revision:r,value:v,payload:p)); return true;});
        guest.Receive(new Packet(Kind.NativeChunk,revision:1,payload:new byte[]{1,0,0,0,3}));
        if(!guest.Submit(new byte[]{42},0) || guest.ProjectionApplied(0,0) || !guest.Waiting) throw new Exception("old projection completed a new request");
        if(!guest.ProjectionApplied(1,(ushort)ShopResult.Applied) || guest.Waiting || !guest.ResultFromProjection ||
            guest.Result!=(ushort)ShopResult.Applied) throw new Exception("applied snapshot did not recover lost reply");
        if(!guest.Receive(new Packet(Kind.NativeReply,serial:1,value:(ushort)ShopResult.Applied))) throw new Exception("late duplicate reply rejected");
        if(!guest.Submit(new byte[]{9},1) || guest.ProjectionApplied(1,(ushort)ShopResult.Applied) || !guest.Waiting)
            throw new Exception("old snapshot consumed second input");
        guest.ProjectionApplied(2,(ushort)ShopResult.Stale);
        if(guest.Waiting || guest.Result!=(ushort)ShopResult.Stale) throw new Exception("rejected command misreported via snapshot");
        Console.WriteLine("PASS snapshot command receipt: lost direct reply, exact pending serial, explicit rejection and harmless late duplicate");
    }
    private static void CheckLostChunkAndAck()
    {
        var a=new List<Packet>(); var b=new List<Packet>();
        var host=new ReplicaLink(true,(k,s,r,v,p)=>{a.Add(new Packet(k,serial:s,revision:r,value:v,payload:p));return true;});
        var guest=new ReplicaLink(false,(k,s,r,v,p)=>{b.Add(new Packet(k,serial:s,revision:r,value:v,payload:p));return true;});
        var source=new byte[ReplicaLink.ChunkSize*2+17]; new Random(796).NextBytes(source);
        host.Publish(source); host.Tick(0,true);
        foreach(var chunk in a.Where(p=>p.Value!=1)) guest.Receive(chunk);
        guest.Tick(.01,true); guest.Tick(.14,true);
        var gap=b.Single();
        if(guest.State!=null || gap.Kind!=Kind.NativeProgress || gap.Value!=1 || gap.Serial!=0 || gap.Revision!=1 ||
            !Wire.TryDecode(Wire.Encode(gap),false,out _)) throw new Exception("missing chunk not requested atomically");
        a.Clear(); host.Receive(gap); host.Receive(gap); host.Tick(.15,true);
        if(a.Count!=1 || a[0].Value!=1 || host.RepairsSent!=1) throw new Exception("repair replayed full snapshot");
        b.Clear(); guest.Receive(a.Single());
        if(b.Single().Kind!=Kind.NativeAck || !guest.State.SequenceEqual(source)) throw new Exception("repair did not complete intact snapshot");
        // Lose that acknowledgement. A later progress report acknowledges only
        // the complete revision and makes the next queued publication eligible.
        b.Clear(); host.Publish(new byte[]{9}); guest.Tick(.16,true); guest.Tick(.57,true);
        var ack=b.Single(); host.Receive(ack); a.Clear(); host.Tick(.58,true);
        if(ack.Serial!=1 || host.Revision!=1 || a.Single().Revision!=2) throw new Exception("lost complete acknowledgement blocked new publication");
        host.Receive(gap); host.Tick(.59,true);
        if(a.Count!=1) throw new Exception("old repair request crossed a revision");
        Console.WriteLine("PASS selective snapshot repair: lost middle chunk, duplicate gap, lost completion ack, intact state and next revision inside fallback deadline");
    }
    private static void CheckTooltipOrdering()
    {
        var gate=new TooltipReadOrder(); gate.Select(0); uint first=gate.Interest;
        if(!gate.Accepts(first,1)) throw new Exception("first read rejected");
        gate.Received(1); gate.Applied(4);
        if(gate.Accepts(first,1) || gate.Accepts(first,3)) throw new Exception("old read replaced newer snapshot");
        gate.Select(0.1); gate.Select(0.2); uint back=gate.Interest;
        if(gate.Accepts(first,6) || !gate.Accepts(back,5)) throw new Exception("previous visit to this item was accepted");
        gate.Received(6); gate.Applied(5);
        if(gate.Accepts(back,6) || !gate.Accepts(back,7)) throw new Exception("older snapshot reset latest read");
        gate.Applied(9); gate.Applied(7);
        if(gate.Accepts(back,8)) throw new Exception("older watermark revived stale read");
        if(gate.Renew(.5,false) || gate.Renew(.7,true) || !gate.Renew(.71,false) ||
            gate.Accepts(back,10) || !gate.Accepts(gate.Interest,10) || gate.Renew(.75,false))
            throw new Exception("unanswered hover did not renew once at the next eligible interval");
    }
    private static void CheckImmediateRetry()
    {
        var attempts=new List<Packet>();
        var guest=new ReplicaLink(false,(kind,serial,revision,value,body)=>{
            if(kind!=Kind.NativeCommand) return true;
            attempts.Add(new Packet(kind,serial:serial,revision:revision,payload:body));
            return attempts.Count>1;
        });
        guest.Receive(new Packet(Kind.NativeChunk,revision:4,payload:new byte[]{1,0,0,0,20}));
        var input=new byte[]{9};
        if(!guest.Submit(input,2) || attempts.Count!=1 || !guest.Waiting) throw new Exception("initial send failure lost pending input");
        input[0]=3;
        guest.Receive(new Packet(Kind.NativeChunk,revision:5,payload:new byte[]{1,0,0,0,21}));
        guest.Tick(2.01,true); if(attempts.Count!=1) throw new Exception("failed immediate send busy loop");
        guest.Tick(2.06,true); guest.Tick(2.55,true);
        if(attempts.Count!=2 || attempts[1].Revision!=4 || attempts[1].Serial!=attempts[0].Serial || attempts[1].Payload[0]!=9)
            throw new Exception("retry changed original input, revision or request number");
        guest.Tick(2.57,true);
        if(attempts.Count!=3) throw new Exception("lost immediate reply was not retried");
    }
    private static void CheckWorkerEpochs()
    {
        var link = new ReplicaLink(false, (_,_,_,_,_)=>true);
        var original = link.CaptureStamp();
        if(!original.IsCurrent(link) || default(ReplicaLink.Stamp).IsCurrent(link) || original.IsCurrent(null))
            throw new Exception("invalid initial replica lifetime");
        // A complete worker result may arrive after two resets in one UI frame:
        // the externally observed connected flag and wire revision can be equal.
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captured = link.CaptureStamp();
        var job = Task.Run(async ()=> { var bytes=await completion.Task; return (Stamp:captured, Bytes:bytes); });
        link.Reset(); link.Reset();
        completion.SetResult(new byte[]{7,8,9});
        var stale = job.GetAwaiter().GetResult();
        if(stale.Stamp.IsCurrent(link) || stale.Bytes.Length!=3)
            throw new Exception("worker result crossed reset epoch");
        var current = link.CaptureStamp();
        var replacement = new ReplicaLink(false, (_,_,_,_,_)=>true);
        replacement.Reset(); replacement.Reset();
        if(!current.IsCurrent(link) || current.IsCurrent(replacement))
            throw new Exception("worker result crossed replacement connection");
        // Ordinary state revisions do not invalidate an in-flight full snapshot.
        var body = new byte[]{1,0,0,0,42};
        if(!link.Receive(new Packet(Kind.NativeChunk,revision:1,payload:body)) || !current.IsCurrent(link))
            throw new Exception("ordinary snapshot revision invalidated epoch");
        link.Reset();
        if(!link.Receive(new Packet(Kind.NativeChunk,revision:1,payload:body)) || current.IsCurrent(link) ||
            !link.CaptureStamp().IsCurrent(link))
            throw new Exception("reused wire revision resurrected old epoch");
        Console.WriteLine("PASS replica worker epochs: delayed completion, reset/reconnect in one frame, replacement link and reused revision");
    }
    private static void Roundtrip(Packet p)
    {
        if(!Wire.TryDecode(Wire.Encode(p),false,out var decoded) || decoded.Kind!=p.Kind ||
            decoded.Revision!=p.Revision || decoded.Serial!=p.Serial || decoded.Value!=p.Value ||
            (decoded.Payload==null)!=(p.Payload==null) || !decoded.Payload.AsSpan().SequenceEqual(p.Payload))
            throw new Exception("native wire roundtrip");
    }
}
