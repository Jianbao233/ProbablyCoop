using ProbablyCoop.Core;

internal static class IndependentRequestTests
{
    internal static void Run()
    {
        var replies = new List<Packet>();
        var outgoing = new List<Packet>();
        var executions = new List<byte>();
        var host = new ReplicaLink(true, (k,s,r,v,p) => { replies.Add(new Packet(k,serial:s,revision:r,value:v,payload:p)); return true; });
        var guest = new ReplicaLink(false, (k,s,r,v,p) => { outgoing.Add(new Packet(k,serial:s,revision:r,value:v,payload:p)); return true; });
        host.Execute = data => { executions.Add(data[0]); return data[0] == 2 ? (ushort)ShopResult.NoSpace : (ushort)ShopResult.Applied; };
        host.Publish(new byte[] { 1 }); host.Tick(0,true);
        guest.Receive(replies.Single()); host.Receive(outgoing.Single());
        replies.Clear(); outgoing.Clear();
        if (!guest.SubmitIndependent(new byte[] {1},0,out ulong a) ||
            !guest.SubmitIndependent(new byte[] {2},0,out ulong b) ||
            !guest.SubmitIndependent(new byte[] {3},0,out ulong c)) throw new Exception("independent inputs blocked");
        host.Receive(outgoing[2]); host.Receive(outgoing[1]);
        if (executions.Count != 0) throw new Exception("causal order violated");
        host.Receive(outgoing[0]); host.Receive(outgoing[1]);
        if (!executions.SequenceEqual(new byte[] {1,2,3})) throw new Exception("duplicate or reordered mutation");
        guest.Receive(replies.First(p=>p.Serial==c));
        guest.Receive(replies.First(p=>p.Serial==b));
        if (!guest.TryTakeIndependentResult(b,out ushort rejected) || rejected != (ushort)ShopResult.NoSpace)
            throw new Exception("rejection attached to wrong input");
        outgoing.Clear(); guest.Tick(0.6,true);
        var retry = outgoing.Single(p=>p.Kind==Kind.NativeCommand);
        if (retry.Serial != a) throw new Exception("lost reply not retried individually");
        replies.Clear(); host.Receive(retry); guest.Receive(replies.Single());
        if (!guest.TryTakeIndependentResult(a,out _) || !guest.TryTakeIndependentResult(c,out _) || executions.Count!=3)
            throw new Exception("lost reply caused a duplicate or stranded completion");
        if (guest.Result != 0 || guest.CompletedRequest != 0)
            throw new Exception("independent receipts overwrote an unrelated legacy UI result");
        if (!guest.Submit(new byte[]{9},1)) throw new Exception("legacy input not resumed");
        var legacyPacket=outgoing.Last();
        if (!guest.SubmitIndependent(new byte[]{10},1,out ulong simultaneous)) throw new Exception("legacy request blocked an independent hand");
        host.Receive(outgoing.Last());
        if (executions.Count!=3) throw new Exception("later independent hand crossed the legacy transaction");
        host.Receive(legacyPacket);
        foreach(var packet in replies.TakeLast(2).Reverse()) guest.Receive(packet);
        if (!executions.TakeLast(2).SequenceEqual(new byte[]{9,10}) || guest.Waiting ||
            !guest.TryTakeIndependentResult(simultaneous,out _)) throw new Exception("coexisting legacy/independent receipts mixed");
        guest.Reset(); host.Reset();
        if (guest.IndependentInFlight!=0 || guest.TryTakeIndependentResult(a,out _)) throw new Exception("epoch leaked requests");
        replies.Clear(); outgoing.Clear(); executions.Clear();
        host.Publish(new byte[]{1}); host.Tick(2,true);
        guest.Receive(replies.Single()); host.Receive(outgoing.Single());
        replies.Clear(); outgoing.Clear();
        var window = new List<ulong>();
        for(int index=0;index<ReplicaLink.RequestWindow;index++)
        {
            if(!guest.SubmitIndependent(new byte[]{index==7 ? (byte)2 : (byte)1},2,out ulong serial)) throw new Exception("request window too small");
            window.Add(serial);
        }
        if(guest.SubmitIndependent(new byte[]{1},2,out _)) throw new Exception("unbounded pending requests");
        foreach(var packet in outgoing.AsEnumerable().Reverse().ToArray()) host.Receive(packet);
        guest.Receive(replies.Last()); // Every earlier direct reply is permanently lost.
        guest.ProjectionApplied(host.CompletedRequest,host.CompletedResult);
        var checkpointReceipts=host.CaptureCommandReceipts();
        if(checkpointReceipts.Length!=ReplicaLink.RequestWindow) throw new Exception("snapshot receipt window incomplete");
        foreach(var receipt in checkpointReceipts) guest.ConfirmSnapshotReceipt(receipt.Serial,receipt.Result);
        if(executions.Count!=ReplicaLink.RequestWindow) throw new Exception("buffered request window did not drain");
        foreach(ulong serial in window)
            if(!guest.TryTakeIndependentResult(serial,out ushort outcome) ||
                outcome!=(ushort)(serial==8 ? ShopResult.NoSpace : ShopResult.Applied))
                throw new Exception("snapshot recovery lost or mixed an individual receipt");
        if(!guest.SubmitIndependent(new byte[]{1},3,out ulong next) || next!=(ulong)ReplicaLink.RequestWindow+1)
            throw new Exception("rejected enqueue introduced a sequence gap");
        guest.Reset(); host.Reset(); replies.Clear(); outgoing.Clear();
        host.Publish(new byte[]{1}); host.Tick(4,true);
        guest.Receive(replies.Single()); host.Receive(outgoing.Single());
        replies.Clear(); outgoing.Clear();
        if(!guest.SubmitIndependent(new byte[]{1},4,out ulong forgotten)) throw new Exception("lost receipt fixture failed");
        host.Receive(outgoing.Last());
        for(int index=1;index<ReplicaLink.RequestWindow;index++)
        {
            if(!guest.Submit(new byte[]{9},4)) throw new Exception("legacy input blocked before sequence window filled");
            host.Receive(outgoing.Last()); guest.Receive(replies.Last());
        }
        if(guest.Submit(new byte[]{9},4)) throw new Exception("legacy traffic escaped the lost independent receipt window");
        outgoing.Clear(); guest.Tick(5,true);
        var forgottenRetry=outgoing.Single(packet=>packet.Kind==Kind.NativeCommand);
        if(forgottenRetry.Serial!=forgotten) throw new Exception("lost independent receipt not retained");
        host.Receive(forgottenRetry); guest.Receive(replies.Last());
        if(!guest.TryTakeIndependentResult(forgotten,out _) || !guest.Submit(new byte[]{9},5))
            throw new Exception("mixed command sequence did not recover after lost receipt");
        Console.WriteLine("PASS independent input requests: ordered execution, parallel issue, individual rejection, lost replies, dedup, coexisting legacy traffic, bounded shared window and reset");
    }
}
