using Newtonsoft.Json.Linq;
using ProbablyCoop;
using ProbablyCoop.Core;

internal static class CaptureChangeTests
{
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    internal static void Run()
    {
        var journal=new CaptureChangeJournal<object>();
        var bag=new object(); var machine=new object(); var tag=new object();
        journal.Observe(bag,tag); journal.Observe(machine,tag);
        ulong old=journal.Version(bag),other=journal.Version(machine);
        Check(journal.Changed(tag) && !journal.IsCurrent(bag,old) && !journal.IsCurrent(machine,other),"shared dependency missed");
        ulong captured=journal.Version(bag);
        journal.Changed(tag);
        Check(!journal.IsCurrent(bag,captured),"encoder completion cleared a later mutation");
        journal.Forget(bag); Check(journal.Tracks(tag),"one owner removed another owner's dependency");
        journal.Forget(machine); Check(journal.Count==0 && journal.SourceCount==0,"dead graph retained");
        journal.Observe(bag,tag); old=journal.Version(bag); journal.Clear(); journal.Observe(bag,tag);
        Check(!journal.IsCurrent(bag,old),"reconnect accepted old capture stamp");
        old=journal.Version(bag); journal.InvalidateAll(); Check(!journal.IsCurrent(bag,old),"query context did not invalidate");
        journal.Observe(machine,bag); journal.Observe(bag,machine);
        old=journal.Version(machine); journal.Changed(tag);
        Check(!journal.IsCurrent(machine,old),"nested parent or cyclic dependency missed");

        var fields=new NativeDeferredFields(workerItems:true);
        var source=World(fields,false,1);
        fields.Seal();
        var full=fields.PackFrame(source,default,true,null,0,out var basis,out _);
        var expected=NativeSnapshotPages.Unpack(full,default);
        foreach(bool audit in new[]{false,true})
        {
            var deltaFields=new NativeDeferredFields(workerItems:true);
            var current=new JObject { ["roots"]=new JArray(new JObject { ["items"]=new JArray(new JObject {
                ["legacyId"]=1,["data"]=deltaFields.BorrowRecord(basis,1) }) }) };
            deltaFields.Seal();
            var delta=deltaFields.PackFrame(current,default,true,basis,7,out var checkpoint,out int count,audit ? full.Single : null);
            Check(delta.Delta && checkpoint==null && count==1 && deltaFields.ConfirmedRecords.Contains(1),"clean root was not referenced");
            var expanded=NativeRecordBasis.ExpandForAudit(NativeRecordFrame.Body(delta.Single,7),full.Single);
            Check(JToken.DeepEquals(expected,NativeSnapshotBinary.Unpack(expanded)),"borrowed root changed its fields/children");
        }
        var rotation=new NativeDeferredFields(workerItems:true);
        var rotationRoot=new JObject { ["roots"]=new JArray(new JObject { ["items"]=new JArray(new JObject {
            ["legacyId"]=1,["data"]=rotation.BorrowRecord(basis,1) }) }) };
        rotation.Seal();
        var rotated=rotation.PackFrame(rotationRoot,default,true,null,0,out var rotatedBasis,out int references,
            preferPages:true,completeCaptureBasis:basis);
        Check(rotated.Full && references==0 && rotatedBasis!=null && rotatedBasis.Contains(1)
            && JToken.DeepEquals(expected,NativeSnapshotPages.Unpack(rotated,default)),
            "rotated full checkpoint must be self-contained with identical nested records");
        // Exceed the delta topology heuristic. Restoring the borrowed record on
        // the worker must retain frozen nested fields and support paged output.
        foreach(bool paged in new[]{false,true})
        {
            var rebuilding=new NativeDeferredFields(workerItems:true);
            var root=World(rebuilding,true,2);
            var items=(JArray)root["roots"][0]["items"];
            items.Insert(0,new JObject { ["legacyId"]=1,["data"]=rebuilding.BorrowRecord(basis,1) });
            for(uint i=3;i<=14;i++) items.Add(new JObject { ["legacyId"]=i,["data"]=Item(rebuilding,i) });
            rebuilding.Seal();
            var frame=rebuilding.PackFrame(root,default,true,basis,7,out var replacement,out _,preferPages:paged);
            Check(frame.Full && replacement!=null && replacement.Contains(1),"full fallback did not replace basis");
            var decoded=NativeSnapshotPages.Unpack(frame,default);
            Check(JToken.DeepEquals(expected["roots"][0]["items"][0],decoded["roots"][0]["items"][0]),"fallback damaged borrowed nested graph");
            var twice=new NativeDeferredFields(workerItems:true);
            var again=new JObject { ["roots"]=new JArray(new JObject { ["items"]=new JArray(new JObject {
                ["legacyId"]=1,["data"]=twice.BorrowRecord(basis,1) }) }) };
            twice.Seal();
            var repeated=twice.PackFrame(again,default,true,basis,7,out _,out _);
            Check(repeated.Delta,"copy mutated previous acknowledged basis");
        }
        NestedSiblingCapture();
        Console.WriteLine("PASS change-driven capture: mutation stamps, shared/cyclic dependencies, cleanup, reconnect, nested sibling borrowing, differential audit, paged fallback and full checkpoint rotation");
    }
    private static JObject World(NativeDeferredFields fields,bool plain,uint id)=>new JObject {
        ["roots"]=new JArray(new JObject { ["items"]=new JArray(new JObject { ["legacyId"]=id,["data"]=Item(fields,id,plain) }) }) };
    private static JObject Item(NativeDeferredFields fields,uint id,bool plain=true,Func<JObject> child=null,int amount=17)
    {
        var row=new NativeDeferredFields.ItemRow {
            Fields=fields.Capture(new Facts {Amount=amount},typeof(Facts).GetFields()),State=new JObject(),Modified=new JObject(),
            Shape=new JObject(),ModifiedShape=new JObject(),Types=new JArray(),Features=new JArray(),
            Content=plain ? null : new JObject { ["entity"]=100u,["element"]=new JObject { ["entity"]=101u,["items"]=new JArray(child==null ? Item(fields,102) : child()) } }
        };
        var target=fields.StageItem(row); target["entity"]=id; return target;
    }
    private sealed class Facts { public int Amount=17; public string Name="嵌套容器"; }
    private static void NestedSiblingCapture()
    {
        var baseline=new NativeDeferredFields(workerItems:true);
        var root=World(baseline,false,1);
        ((JArray)root["roots"][0]["items"]).Add(new JObject { ["legacyId"]=2,["data"]=Item(baseline,2) });
        baseline.Seal();
        var full=baseline.PackFrame(root,default,true,null,0,out var basis,out _);
        var original=NativeSnapshotPages.Unpack(full,default);
        var expected=(JObject)original.DeepClone();
        expected["roots"][0]["items"][0]["data"]["fields"]["Amount"]=38;
        byte[] restoredDelta=null;
        foreach(bool audit in new[]{true,false})
        {
            var changed=new NativeDeferredFields(workerItems:true);
            var parent=Item(changed,1,false,()=>changed.BorrowRecord(basis,102,nested:true),38);
            var world=new JObject { ["roots"]=new JArray(new JObject { ["items"]=new JArray(
                new JObject { ["legacyId"]=1,["data"]=parent },
                new JObject { ["legacyId"]=2,["data"]=changed.BorrowRecord(basis,2) }) }) };
            changed.AuditedRecords.Add(1); changed.Seal();
            var delta=changed.PackFrame(world,default,true,basis,19,out _,out _,audit ? full.Single : null);
            Check(delta.Delta && changed.ConfirmedRecords.Contains(102) && changed.UntrackedRecords.SetEquals(new uint[]{1}),
                "nested comparison or audit fallback failed");
            Check(JToken.DeepEquals(expected,NativeSnapshotBinary.Unpack(NativeRecordBasis.ExpandForAudit(
                NativeRecordFrame.Body(delta.Single,19),full.Single))),"editing a parent changed its borrowed child");
            if(audit) restoredDelta=delta.Single;
            else Check(restoredDelta.AsSpan().SequenceEqual(delta.Single),"inline frozen record changed the existing wire bytes");
        }
        var refreshed=new NativeDeferredFields(workerItems:true);
        var fallback=World(refreshed,true,3);
        ((JArray)fallback["roots"][0]["items"]).Add(new JObject { ["legacyId"]=1,["data"]=Item(refreshed,1,false,
            ()=>refreshed.BorrowRecord(basis,102,nested:true),38) });
        refreshed.Seal();
        var paged=refreshed.PackFrame(fallback,default,true,basis,19,out var replacement,out int borrowedRoots,preferPages:true);
        Check(paged.Full && replacement.Contains(102) && borrowedRoots==0,"full fallback lost the inline nested record");
        Check(JToken.DeepEquals(expected["roots"][0]["items"][0]["data"],NativeSnapshotPages.Unpack(paged,default)["roots"][0]["items"][1]["data"]),
            "materializing full fallback changed the inline nested record");
        var illegal=new NativeDeferredFields(workerItems:true);
        var invalid=World(illegal,true,5);
        ((JArray)invalid["roots"][0]["items"]).Add(new JObject { ["legacyId"]=2,["data"]=illegal.BorrowRecord(basis,2) });
        illegal.Seal();
        try { illegal.PackFrame(invalid,default,true,null,0,out _,out _); throw new Exception("missing basis accepted"); }
        catch(InvalidOperationException) { }
    }
}
