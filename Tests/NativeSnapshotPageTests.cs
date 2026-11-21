using Newtonsoft.Json.Linq;
using ProbablyCoop;
using ProbablyCoop.Core;

internal static class NativeSnapshotPageTests
{
    internal static void Run()
    {
        var large=Fixture(660,800);
        Reject(()=>large.Fields.Pack(large.Data,default,true,null,0,out _,out _));
        large=Fixture(660,800);
        var checkpoint=large.Fields.PackFrame(large.Data,default,true,null,0,out var basis,out _);
        Check(checkpoint.Count>2,"large nested inventory did not split");
        var join=NativeJoinSnapshot.Read(checkpoint,checkpoint,1);
        Check(join.Run==large.Run && !join.Required &&
            (string)join.Extensions?[0]?["id"]=="example_extension","first page lost the join or extension contract");
        var all=NativeSnapshotPages.Unpack(checkpoint,default);
        var nested=(JArray)all["roots"][0]["items"][0]["data"]["content"]["children"];
        Check(nested.Count==660 && (uint)nested[659]["entity"]==660 &&
            (int)nested[659]["state"]["tag_values"][799]==659+799,"nested ownership or scalar data changed");
        Check((int)nested[0]["state"]["$probablyCoopPageItem"]==3,"mod-owned tag name was mistaken for a page reference");
        var smaller=Fixture(4,4,large.Run);
        var plain=smaller.Fields.Pack(smaller.Data);
        smaller=Fixture(4,4,large.Run);
        var paged=smaller.Fields.PackFrame(smaller.Data,default,true,null,0,out _,out _,preferPages:true);
        Check(JToken.DeepEquals(NativeSnapshotBinary.Unpack(plain),NativeSnapshotPages.Unpack(paged,default)),
            "paged and standard code reconstructed different item data");
        var next=Fixture(660,800,large.Run);
        var delta=next.Fields.PackFrame(next.Data,default,true,basis,1,out _,out int referenced);
        Check(delta.Delta && referenced==1,"large base no longer supports a small steady-state delta");

        Reject(()=>NativeSnapshotPages.Unpack(InvalidReferences(0,0),default)); // Two owners for one record.
        Reject(()=>NativeSnapshotPages.Unpack(InvalidReferences(1),default));
        CheckFrozenCapture();
        CheckTransfer();
        CheckSendBackpressure();
        Console.WriteLine("PASS paged checkpoints: large nested parity, typed references, delta basis, atomic transfer, duplicate/lost chunks, bounded failure and retry");
    }

    private static void CheckFrozenCapture()
    {
        var fields=new NativeDeferredFields(workerItems:true,pagedSnapshots:true);
        var items=new JArray();
        for(uint id=1;id<=2;id++)
        {
            var values=new byte[300000];
            values[values.Length-1]=(byte)id;
            var item=fields.StageItem(new NativeDeferredFields.ItemRow {
                State=new JObject { ["cells"]=fields.CaptureBytes(values) }
            });
            item["entity"]=id;
            items.Add(new JObject { ["data"]=item });
        }
        fields.Seal();
        var root=new JObject { ["roots"]=new JArray(new JObject { ["items"]=items }) };
        var frame=fields.PackFrame(root,default,true,null,0,out _,out _);
        var restored=NativeSnapshotPages.Unpack(frame,default);
        Check(frame.Count==3 && (int)restored["roots"][0]["items"][1]["data"]["state"]["cells"][299999]==2,
            "the large frozen capture exhausted a single page or lost its values during adaptive splitting");
    }

    private static NativeSnapshotFrame InvalidReferences(params int[] ordinals)
    {
        var references=new Dictionary<JToken,int>(NativeDeferredFields.ReferenceComparer.Instance);
        var entries=new JArray();
        foreach(int ordinal in ordinals)
        {
            var marker=new JObject();
            references.Add(marker,ordinal);
            entries.Add(marker);
        }
        return new NativeSnapshotFrame(new[] {
            NativeSnapshotBinary.Pack(new JObject { ["slots"]=entries },default,null,out _,references),
            NativeSnapshotBinary.Pack(new JObject { ["pageItems"]=new JArray(new JObject()) })
        });
    }

    private static void CheckTransfer()
    {
        var outgoing=new List<Packet>();
        var receipts=new List<Packet>();
        var host=new ReplicaLink(true,(kind,serial,revision,value,payload)=>
        { outgoing.Add(new Packet(kind,serial:serial,revision:revision,value:value,payload:payload));return true; });
        var guest=new ReplicaLink(false,(kind,serial,revision,value,payload)=>
        { receipts.Add(new Packet(kind,serial:serial,revision:revision,value:value,payload:payload));return true; });
        var first=NativeSnapshotBinary.Pack(new JObject());
        var last=new byte[ReplicaLink.ChunkSize*2+1];
        first.CopyTo(last,0);
        var frame=new NativeSnapshotFrame(new[] { first,last });
        host.PublishCheckpoint(frame);
        host.Tick(0,true);
        Deliver(outgoing.Single());
        Check(guest.Revision==0 && guest.Transfer.CompletePages==1,"partial checkpoint was published");
        host.Receive(receipts.Single());
        outgoing.Clear(); receipts.Clear();
        host.Tick(.01,true);
        var finalChunk=outgoing[2];
        Deliver(outgoing[2]); Deliver(outgoing[0]); Deliver(outgoing[0]);
        Check(guest.Transfer.ReceivedPageBytes==ReplicaLink.ChunkSize+1 && guest.Snapshot==null,
            "duplicate bytes inflated progress or exposed a partial checkpoint");
        guest.Tick(.02,true); guest.Tick(.15,true);
        var repair=receipts.Single();
        Check(repair.Kind==Kind.NativeProgress && repair.Value==((1<<ReplicaLink.ChunkIndexBits)|1) &&
            Wire.TryDecode(Wire.Encode(repair),false,out _) && host.Receive(repair),
            "gap report lost the requested page number");
        outgoing.Clear();
        host.Tick(.16,true);
        Deliver(outgoing.Single());
        Check(guest.Snapshot.Count==2 && guest.FullRevision==1 && host.Revision==0,
            "completion did not publish one logical revision");
        // Lose completion ACK. A duplicate successfully recovers it.
        receipts.Clear();
        Deliver(finalChunk);
        host.Receive(receipts.Single());
        Check(host.Revision==1 && host.FullSnapshot.Count==2,"lost final page acknowledgement stranded the host");
        host.Reset(); guest.Reset(); outgoing.Clear(); receipts.Clear();
        host.PublishCheckpoint(frame);
        host.Tick(1,true);
        Deliver(outgoing.Single());
        Check(guest.Transfer.CompletePages==1,"test did not stage a partially received checkpoint");
        outgoing.Clear();
        host.FailSynchronization(NativeSyncFailure.HostEncode);
        host.Tick(1.1,true);
        Check(!Wire.TryDecode(Wire.Encode(new Packet(Kind.NativeSyncFailed,value:(ushort)NativeSyncFailure.GuestPageFormat)),
            false,out _),"guest-only reason was accepted as a host failure");
        Deliver(outgoing.Single());
        Check(guest.SynchronizationFailed && guest.FailureReason==NativeSyncFailure.HostEncode &&
            guest.Snapshot==null && guest.Transfer.Pages==0,
            "failure did not abort only the guest transfer");
        host.Reset(); guest.Reset(); outgoing.Clear(); receipts.Clear();
        host.PublishCheckpoint(first);
        host.Tick(2,true);
        Deliver(outgoing.Single());
        Check(!host.SynchronizationFailed && !guest.SynchronizationFailed &&
            guest.FailureReason==NativeSyncFailure.None && guest.FullRevision==1,
            "failed transfer survived a new epoch");

        void Deliver(Packet packet)
        {
            Check(Wire.TryDecode(Wire.Encode(packet),false,out var decoded) && guest.Receive(decoded),"wire rejected a valid page packet");
        }
    }

    private static void CheckSendBackpressure()
    {
        var sent = new List<Packet>();
        int attempts = 0;
        var host = new ReplicaLink(true, (kind, serial, revision, value, body) =>
        {
            if (++attempts <= 2) return false;
            sent.Add(new Packet(kind, serial: serial, revision: revision, value: value, payload: body));
            return true;
        });
        var guest = new ReplicaLink(false, (kind, serial, revision, value, body) =>
            host.Receive(new Packet(kind, serial: serial, revision: revision, value: value, payload: body)));
        var data = new byte[ReplicaLink.ChunkSize + 4096];
        new Random(74).NextBytes(data);
        var bytes = NativeSnapshotBinary.Pack(new JObject { ["data"] = Convert.ToBase64String(data) });
        Check(bytes.Length > ReplicaLink.ChunkSize && bytes.Length <= 2 * ReplicaLink.ChunkSize,
            "backpressure fixture must span two chunks");
        host.PublishCheckpoint(bytes);
        host.Tick(0, true);
        host.Tick(0.01, true);
        Check(attempts == 1 && sent.Count == 0, "transport was retried during the first cooldown");
        host.Tick(0.06, true);
        host.Tick(0.12, true);
        Check(attempts == 2 && sent.Count == 0, "second send rejection did not extend the cooldown");
        host.Tick(0.17, true);
        Check(sent.Count == 2 && sent.All(guest.Receive) && guest.State.SequenceEqual(bytes) && host.Revision == 1,
            "backpressure lost a pending chunk or its completion acknowledgement");
        Console.WriteLine("PASS checkpoint send backpressure: paced retry preserves first chunk and complete revision");
    }

    private static (NativeDeferredFields Fields,JObject Data,Guid Run) Fixture(int children,int entries,Guid run=default)
    {
        if(run==Guid.Empty) run=Guid.NewGuid();
        var fields=new NativeDeferredFields(workerItems:true);
        var contents=new JArray();
        for(int index=0;index<children;index++)
        {
            var target=fields.StageItem(new NativeDeferredFields.ItemRow {
                State=new JObject {
                    ["$probablyCoopPageItem"]=3,
                    ["tag_values"]=new JArray(Enumerable.Range(index,entries))
                }
            });
            target["entity"]=index+1;
            contents.Add(target);
        }
        var storage=fields.StageItem(new NativeDeferredFields.ItemRow {
            Content=new JObject { ["children"]=contents }
        });
        storage["entity"]=9900;
        fields.Seal();
        return (fields,new JObject {
            ["actors"]=new JObject { ["run"]=run.ToString() },
            ["partnerSetupRequired"]=false,
            ["modExtensions"]=new JArray(new JObject { ["id"]="example_extension" }),
            ["roots"]=new JArray(new JObject { ["items"]=new JArray(new JObject { ["data"]=storage }) })
        },run);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch(InvalidDataException) { return; }
        throw new Exception("Invalid or over-budget checkpoint was accepted");
    }
    private static void Check(bool condition,string failure)
    {
        if(!condition) throw new Exception(failure);
    }
}
