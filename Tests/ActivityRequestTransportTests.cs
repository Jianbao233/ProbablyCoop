using ProbablyCoop.Core;

internal static class ActivityRequestTransportTests
{
    internal static void Run()
    {
        var upstream=new List<Packet>(); var downstream=new List<Packet>(); var executed=new List<byte>();
        var host=new ReplicaLink(true,(kind,id,revision,result,data)=> { downstream.Add(new Packet(kind,serial:id,revision:revision,value:result,payload:data)); return true; });
        var guest=new ReplicaLink(false,(kind,id,revision,result,data)=> { upstream.Add(new Packet(kind,serial:id,revision:revision,value:result,payload:data)); return true; });
        var ledger=new ActivityRequests();
        host.Execute=data=> { executed.Add(data[0]); return (ushort)(data[0]==2 ? ShopResult.Busy : ShopResult.Applied); };
        host.Publish(new byte[]{1}); host.Tick(0,true);
        guest.Receive(downstream.Single()); host.Receive(upstream.Single()); upstream.Clear(); downstream.Clear();
        var entries=new Dictionary<ulong,ActivityRequests.Entry>();
        ulong Issue(byte operation,string kind)
        {
            Require(guest.SubmitIndependent(new[]{operation},0,out var id) && ledger.Track(id,kind,kind,id),"independent dispatch rejected");
            entries.Add(id,ledger.Entries.Single(entry=>entry.Serial==id)); return id;
        }
        void Take()
        {
            foreach(var entry in ledger.Entries.ToArray())
                if(!entry.Result.HasValue && guest.TryTakeIndependentResult(entry.Serial,out var result))
                    Require(ledger.Reply(entry.Serial,result,out _),"receipt association failed");
            ledger.Collect();
        }
        var a=Issue(1,"control"); var b=Issue(2,"phone"); var c=Issue(3,"editor");
        var original=upstream.ToArray();
        foreach(var packet in original.Reverse()) host.Receive(packet);
        Require(executed.SequenceEqual(new byte[]{1,2,3}),"host mutation order changed");
        guest.Receive(downstream.Single(packet=>packet.Serial==c)); Take();
        ledger.Project(a); ledger.Collect();
        Require(ledger.Count==3,"reply released another activity or crossed an older projection");
        // Both earlier ACKs remain lost; recover their individual outcomes from
        // the actual transport's checkpoint receipt window, after view retirement.
        ledger.Retire(); var replacement=Issue(3,"phone");
        foreach(var receipt in host.CaptureCommandReceipts()) guest.ConfirmSnapshotReceipt(receipt.Serial,receipt.Result);
        ledger.Project(c); Take();
        Require(entries[a].Result==(ushort)ShopResult.Applied && entries[b].Result==(ushort)ShopResult.Busy &&
            !entries[b].Active && ledger.Count==1 && ledger.Pending("phone") && !entries[replacement].Result.HasValue,
            "lost/reordered receipts changed the replacement phone view");
        downstream.Clear(); host.Receive(upstream.Last());
        guest.Receive(downstream.Single()); Take();
        Require(ledger.Count==1,"ACK alone retired replacement before its graph");
        ledger.Project(replacement); ledger.Collect();
        host.Receive(original[0]); guest.Receive(downstream.Last());
        Require(executed.SequenceEqual(new byte[]{1,2,3,3}) && ledger.Count==0 &&
            entries[replacement].Result==(ushort)ShopResult.Applied && guest.Result==0 && guest.CompletedRequest==0,
            "late replay repeated a mutation or leaked into legacy/global results");
        Console.WriteLine("PASS native activity transport: real request ordering, lost/reordered ACKs, checkpoint recovery, old-view retirement and exact independent outcomes (fixture)");
    }
    private static void Require(bool value,string message) { if(!value) throw new Exception(message); }
}
