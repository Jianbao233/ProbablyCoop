using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

internal static class NativeSnapshotIndexTests
{
    internal static void Run()
    {
        var before=Snapshot();
        var first=NativeSnapshotIndex.Build(before,null);
        Check(first.Nodes.Count==3 && first.Unchanged.Count==0,"first snapshot must build all entities");
        var current=(JObject)before.DeepClone();
        var same=NativeSnapshotIndex.Build(current,first);
        Check(same.Unchanged.SetEquals(new uint[]{1,2,3}),"unchanged tree");
        Check(same.ChangedTopLevel.Count==0,"identical snapshot changed metadata");
        Check(!ReferenceEquals(same.Nodes[3],first.Nodes[3]),"index retained old parent graph");
        same.Nodes[3]["text"]="new production output";
        var changed=NativeSnapshotIndex.Build(current,first);
        Check(changed.Unchanged.Count==0,"descendant mutation failed to invalidate ancestors");
        Check(changed.PropertyChanged(1,"child") && !changed.PropertyChanged(1,"name") &&
            changed.PropertyChanged(2,"child") && !changed.PropertyChanged(2,"kind") &&
            changed.PropertyChanged(3,"text"),"nested readout invalidated unrelated item properties");
        Check(first.PropertyChanged(1,"name") && !same.PropertyChanged(1,"name") &&
            same.PropertyChanged(999,"fields"),"new/missing item did not force initial hydration");
        CheckItemSections();
        CheckRootGrids();
        CheckItemAbilities();
        CheckInventoryRootState();
        CheckFrontendMetadata();
        // Moving a root item leaves the node itself unchanged: native parent/order
        // reconciliation is performed separately on the main thread.
        var moved=(JObject)before.DeepClone();
        moved["roots"][0]["place"]=7;
        Check(NativeSnapshotIndex.Build(moved,first).Unchanged.SetEquals(new uint[]{1,2,3}),"location-only change invalidated renderer");
        var replacement=(JObject)before.DeepClone();
        replacement["roots"][0]["items"][0]["data"]["child"]=new JObject { ["entity"]=4,["text"]="new" };
        var newer=NativeSnapshotIndex.Build(replacement,first);
        Check(newer.Nodes.Keys.ToHashSet().SetEquals(new uint[]{1,4}) && newer.Unchanged.Count==0,"removed/replaced child retained");
        var alias=(JObject)before.DeepClone();
        ((JArray)alias["roots"][0]["items"]).Add(alias["roots"][0]["items"][0].DeepClone());
        Check(NativeSnapshotIndex.Build(alias,first).Nodes.Count==3,"equal shared entity projection");
        alias["roots"][0]["items"][1]["data"]["child"]["child"]["text"]="conflicting alias";
        Reject(()=>NativeSnapshotIndex.Build(alias,first),"conflicting shared identity");
        foreach(JToken id in new JToken[]{new JValue(0),new JValue(-1),new JValue((ulong)uint.MaxValue+1),new JValue("3")})
        {
            var invalid=Snapshot(); invalid["roots"][0]["items"][0]["data"]["entity"]=id;
            Reject(()=>NativeSnapshotIndex.Build(invalid,null),"invalid identity");
        }
        Reject(()=>NativeSnapshotIndex.Build(new JObject(),null),"missing roots");
        Reject(()=>NativeSnapshotIndex.Build(new JObject { ["roots"]=new JArray(Enumerable.Range(0,24).Select(_=>new JObject())) },null),"root limit");
        using(var stop=new CancellationTokenSource())
        {
            stop.Cancel();
            try { NativeSnapshotIndex.Build(before,first,stop.Token); throw new Exception("cancel ignored"); }
            catch(OperationCanceledException) { }
        }
        // Read-only previous state can be shared by independent preparation jobs.
        Task.WaitAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=> {
            for(int i=0;i<25;i++)
                Check(NativeSnapshotIndex.Build((JObject)before.DeepClone(),first).Unchanged.Count==3,"parallel immutable comparison");
        })).ToArray());
        var retained=PrepareWithoutRetainingPrevious(out var old);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!old.IsAlive && retained.Nodes.Count==3,"prepared index kept prior snapshot alive");
        var metadata=Snapshot();
        metadata["business"]=new JObject { ["health"]=new JObject { ["value"]=10 },["services"]=new JArray("rent"),["removed"]=1 };
        metadata["client"]=new JObject { ["name"]="顾客" };
        var metadataBaseline=NativeSnapshotIndex.Build(metadata,null);
        var metadataNext=(JObject)metadata.DeepClone();
        metadataNext["business"]["health"]["value"]=9;
        ((JObject)metadataNext["business"]).Remove("removed");
        var metadataIndex=NativeSnapshotIndex.Build(metadataNext,metadataBaseline);
        Check(metadataIndex.Unchanged.Count==3 && metadataIndex.ChangedTopLevel.SetEquals(new[]{"business"}) &&
            metadataIndex.ChangedBusiness.SetEquals(new[]{"health","removed"}),"unrelated business/UI state invalidated");
        Check(metadataBaseline.ChangedBusiness.Count==3 && metadataBaseline.ChangedTopLevel.Contains("client"),"first metadata snapshot incomplete");
        var customerUpdate=(JObject)metadata.DeepClone();
        customerUpdate["client"]["itemQueries"]=new JObject { ["items"]=new JArray(new JArray(3,64)) };
        var customerIndex=NativeSnapshotIndex.Build(customerUpdate,metadataBaseline);
        Check(customerIndex.ChangedClient.SetEquals(new[]{"itemQueries"}),"query refresh invalidated stable customer properties");
        var departing=(JObject)customerUpdate.DeepClone(); departing["client"]=JValue.CreateNull();
        Check(NativeSnapshotIndex.Build(departing,customerIndex).ChangedClient.SetEquals(new[]{"itemQueries","name"}),"old customer retained after departure");
        CheckComparisonParity();
        CheckTraversalBoundaries();
        BenchmarkComparison();
        Console.WriteLine("PASS native snapshot index: nested/property invalidation, item section isolation, null/removal, root moves, add/remove, duplicate identity validation, concurrent comparison, cancellation and prior-graph collection");
    }

    private static void CheckInventoryRootState()
    {
        JObject State(bool locked,bool hidden) => new JObject {
            ["fields"]=new JObject { ["identifier"]="equipment",["overrideLockInsert"]=locked,["overrideLockRemove"]=locked },
            ["tags"]=new JObject { ["slot-policy"]=new JObject { ["valueString"]="backpack",["valueBool"]=true } },
            ["steal"]=true,["down"]=false,["hidden"]=hidden };
        var before=Snapshot(); before["roots"][0]["inventoryState"]=State(false,true);
        var baseline=NativeSnapshotIndex.Build(before,null,separateRecords:true);
        foreach(var state in new[]{State(true,true),State(false,false)})
        {
            var current=Snapshot(); current["roots"][0]["inventoryState"]=state;
            var decoded=NativeSnapshotCodec.Unpack(NativeSnapshotBinary.Pack(current));
            var updated=NativeSnapshotIndex.Build(decoded,baseline,separateRecords:true);
            Check(updated.Unchanged.Count==3 && updated.ChangedTopLevel.Contains("roots"),
                "root slot mode/lock change was lost when every item record stayed unchanged");
            Check(JToken.DeepEquals(decoded["roots"][0]["inventoryState"],state),"inventory root contract was not preserved by the binary codec");
        }
    }
    private static void CheckItemAbilities()
    {
        string[] queries={"MayActivateSlotItem","CanActivateSlotItem","MayToggleSlotItem",
            "CanToggleSlotItem","MaySelectSlotItem","CanSelectSlotItem"};
        var snapshot=Snapshot();
        var item=(JObject)snapshot["roots"][0]["items"][0]["data"];
        for(int bits=0;bits<64;bits++)
        {
            var expected=Enumerable.Range(0,6).Select(i=>(bits & (1<<i))!=0).ToArray();
            item["abilities"]=ProbablyCoop.Core.ItemAbilityBits.Pack(expected[0],expected[1],expected[2],expected[3],expected[4],expected[5]);
            var decoded=NativeSnapshotBinary.Unpack(NativeSnapshotBinary.Pack(snapshot));
            int value=(int)NativeSnapshotIndex.Build(decoded,null).Nodes[1]["abilities"];
            for(int i=0;i<queries.Length;i++)
                Check(ProbablyCoop.Core.ItemAbilityBits.Has(value,queries[i])==expected[i],"native ability polarity or wire bit differs");
            Check(!ProbablyCoop.Core.ItemAbilityBits.Has(value,"unknown"),"unknown ability permitted");
        }
        foreach(var invalid in new JToken[]{-1,64,"1",JValue.CreateNull(),new JObject()})
        {
            item["abilities"]=invalid;
            Reject(()=>NativeSnapshotIndex.Build(snapshot,null),"invalid native ability bits");
        }
    }
    private static void CheckRootGrids()
    {
        var snapshot=Snapshot();
        var shape=new JObject { ["w"]=2,["h"]=2,["outside"]=1,["cells"]=new JArray(0,1,7,255) };
        snapshot["roots"][0]["shape"]=shape;
        var previous=NativeSnapshotIndex.Build(snapshot,null);
        var grid=previous.RootGrids[NativeActorInventory.RootKey.Shared(1)];
        Check(grid.Width==2 && grid.Height==2 && grid.Outside==1 && grid.Cells.SequenceEqual(new byte[]{0,1,7,255}),"decoded native grid lost geometry");
        shape["cells"][3]=32;
        Check(grid.Cells[3]==255 && NativeSnapshotIndex.Build(snapshot,previous).RootGrids[NativeActorInventory.RootKey.Shared(1)].Cells[3]==32,"decoded grids alias mutable JSON");
        foreach(var (property,value) in new (string,JToken)[]{("w",129),("h",0),("cells",new JArray(1)),("cells",new JArray(0,1,2,256)),("outside",-1),("outside","1")})
        {
            var malformed=(JObject)snapshot.DeepClone();
            malformed["roots"][0]["shape"][property]=value;
            Reject(()=>NativeSnapshotIndex.Build(malformed,null),"invalid native grid "+property);
        }
        var duplicate=(JObject)snapshot.DeepClone();
        ((JArray)duplicate["roots"]).Add(duplicate["roots"][0].DeepClone());
        Reject(()=>NativeSnapshotIndex.Build(duplicate,null),"duplicate native grid");
    }
    private static void CheckTraversalBoundaries()
    {
        var source=Snapshot();
        var item=(JObject)source["roots"][0]["items"][0]["data"];
        item["empty"]=new JObject();
        item["siblings"]=new JArray(new JObject(),new JArray(),null,7,
            new JArray(new JObject { ["entity"]=17,["value"]=new JArray(1,2) }),
            new JObject { ["entity"]=18,["value"]=new JObject { ["first"]=1,["second"]=2 } });
        // Data's parent/siblings and metadata have entity-looking fields but are
        // not native item subtrees and must never leak into the identity index.
        source["roots"][0]["items"][0]["ignored"]=new JObject { ["entity"]="not an item" };
        source["business"]=new JObject { ["entity"]="not an item" };
        var original=NativeSnapshotIndex.Build(source,null);
        var expectedIds=new uint[]{1,2,3,17,18};
        Check(original.Nodes.Keys.ToHashSet().SetEquals(expectedIds),"walk escaped data root or skipped nested array sibling");
        var copy=(JObject)source.DeepClone();
        var reordered=(JObject)copy["roots"][0]["items"][0]["data"]["siblings"][5]["value"];
        var first=reordered.Property("first"); first.Remove(); reordered.Add(first);
        var unchanged=NativeSnapshotIndex.Build(copy,original);
        Check(unchanged.Unchanged.SetEquals(expectedIds) && unchanged.ChangedTopLevel.Count==0,"aligned-property optimization treated key order as data");
        reordered["second"]=99;
        var different=NativeSnapshotIndex.Build(copy,original);
        Check(different.Unchanged.SetEquals(new uint[]{2,3,17}) && different.PropertyChanged(1,"siblings") && different.PropertyChanged(18,"value"),"late sibling mutation was skipped");
        Check(!different.PropertyChanged(1,"child"),"late sibling mutation invalidated unchanged independent subtree");
        var expected=LegacyBuild(copy,source,original.Nodes);
        Check(different.Unchanged.SetEquals(expected.unchanged) && different.ChangedTopLevel.SetEquals(expected.top) && different.ChangedBusiness.SetEquals(expected.business),"scoped linked traversal differs from original algorithm");
        Console.WriteLine("PASS linked snapshot traversal: empty nodes, arrays, late siblings, root boundaries, reordered keys and exact ancestor invalidation");
    }
    private static void CheckComparisonParity()
    {
        // Compare against the pre-memoization algorithm on parsed and in-memory
        // graphs, including reordered object keys, arrays, null vs missing,
        // replacements and duplicated projections. These must use exact JSON
        // equality rather than an entity ID or hash as a stand-in for content.
        var random=new Random(6384);
        var previous=DeepSnapshot(8,7);
        var oldIndex=NativeSnapshotIndex.Build(previous,null);
        for(int iteration=0;iteration<100;iteration++)
        {
            var current=iteration%2==0 ? (JObject)previous.DeepClone() : JObject.Parse(previous.ToString());
            var candidates=current.Descendants().OfType<JObject>().Where(n=>n["entity"]!=null).ToArray();
            for(int change=0;change<iteration%9;change++)
            {
                var target=candidates[random.Next(candidates.Length)];
                switch(random.Next(5))
                {
                    case 0: target["fields"]["label"]="changed "+iteration; break;
                    case 1: ((JObject)target["fields"]).Remove("f2"); break;
                    case 2: target["fields"]["f3"]=JValue.CreateNull(); break;
                    case 3: target["fields"]["array"]=new JArray(3,2,1); break;
                    default:
                        var property=((JObject)target["fields"]).Property("f1");
                        if(property!=null) { property.Remove(); ((JObject)target["fields"]).Add(property); }
                        break;
                }
            }
            var expected=LegacyBuild(current,previous,oldIndex.Nodes);
            var actual=NativeSnapshotIndex.Build(current,oldIndex);
            Check(actual.Unchanged.SetEquals(expected.unchanged),"memoized entity equality differs from Newtonsoft");
            Check(actual.ChangedTopLevel.SetEquals(expected.top) && actual.ChangedBusiness.SetEquals(expected.business),"memoized metadata differs from Newtonsoft");
            foreach(var pair in actual.Nodes)
                foreach(string property in pair.Value.Properties().Select(p=>p.Name).Concat(oldIndex.Nodes[pair.Key].Properties().Select(p=>p.Name)).Distinct())
                    Check(actual.PropertyChanged(pair.Key,property)==(expected.properties.TryGetValue(pair.Key,out var changes) && changes.Contains(property)),"memoized property differs: "+property);
        }
        Console.WriteLine("PASS memoized native comparison: 100 differential snapshots against prior DeepEquals algorithm");
    }
    private static (HashSet<uint> unchanged,Dictionary<uint,HashSet<string>> properties,HashSet<string> top,HashSet<string> business) LegacyBuild(JObject current,JObject previous,Dictionary<uint,JObject> oldNodes)
    {
        HashSet<string> Changes(JObject next,JObject old)
        {
            var changed=new HashSet<string>(StringComparer.Ordinal);
            if(next!=null) foreach(var p in next.Properties()) if(old==null || !JToken.DeepEquals(p.Value,old[p.Name])) changed.Add(p.Name);
            if(old!=null) foreach(var p in old.Properties()) if(next==null || next[p.Name]==null) changed.Add(p.Name);
            return changed;
        }
        var top=Changes(current,previous);
        var business=Changes(current["business"] as JObject,previous["business"] as JObject);
        var nodes=new Dictionary<uint,JObject>(); var unchanged=new HashSet<uint>(); var properties=new Dictionary<uint,HashSet<string>>();
        foreach(var root in (JArray)current["roots"])
            foreach(var item in (JArray)root["items"])
                foreach(var token in ((JObject)item["data"]).DescendantsAndSelf())
                {
                    if(token is not JObject node || node["entity"]==null) continue;
                    uint id=uint.Parse(node["entity"].ToString());
                    if(nodes.TryGetValue(id,out var alias))
                    { if(!JToken.DeepEquals(alias,node)) throw new InvalidDataException(); continue; }
                    nodes.Add(id,node); oldNodes.TryGetValue(id,out var old);
                    if(old!=null && JToken.DeepEquals(old,node)) unchanged.Add(id);
                    else properties.Add(id,Changes(node,old));
                }
        return (unchanged,properties,top,business);
    }
    private static JObject DeepSnapshot(int count,int depth)
    {
        uint id=0; var items=new JArray();
        for(int i=0;i<count;i++)
        {
            JObject child=null;
            for(int level=0;level<depth;level++)
            {
                var fields=new JObject { ["label"]="机器生产数据",["array"]=new JArray(1,2,3) };
                for(int field=0;field<32;field++) fields["f"+field]=field;
                var node=new JObject { ["entity"]=++id,["fields"]=fields };
                if(child!=null) node["child"]=child;
                child=node;
            }
            items.Add(new JObject { ["data"]=child });
        }
        return new JObject { ["roots"]=new JArray(new JObject { ["items"]=items }),["business"]=new JObject { ["day"]=6 } };
    }
    private static void BenchmarkComparison()
    {
        var previous=DeepSnapshot(64,10); var current=(JObject)previous.DeepClone(); var baseline=NativeSnapshotIndex.Build(previous,null);
        (double ms,long bytes) Measure(Action action)
        {
            for(int i=0;i<3;i++) action();
            long allocated=GC.GetAllocatedBytesForCurrentThread(); var timer=System.Diagnostics.Stopwatch.StartNew();
            for(int i=0;i<20;i++) action();
            return (timer.Elapsed.TotalMilliseconds/20,(GC.GetAllocatedBytesForCurrentThread()-allocated)/20);
        }
        var old=Measure(()=>LegacyBuild(current,previous,baseline.Nodes));
        var next=Measure(()=>NativeSnapshotIndex.Build(current,baseline));
        Console.WriteLine($"BENCH index 640 nested entities .NET9 only: old={old.ms:F3}ms/{old.bytes}B memoized={next.ms:F3}ms/{next.bytes}B; Unity timing required");
    }
    private static void CheckItemSections()
    {
        var snapshot=Snapshot();
        var item=(JObject)snapshot["roots"][0]["items"][0]["data"];
        item["content"]=item["child"].DeepClone(); item.Remove("child");
        item["fields"]=new JObject { ["name"]="机器",["count"]=1 };
        item["state"]=new JObject { ["on"]=true };
        item["modified"]=new JObject { ["heat"]=2 };
        item["shape"]=new JObject { ["width"]=2 };
        item["modifiedShape"]=new JObject { ["x"]=1 };
        item["types"]=new JArray("machine");
        item["features"]=new JArray(new JObject { ["condition"]=100 });
        item["examine"]=JValue.CreateNull();
        item["tooltip"]=new JObject { ["text"]="机器详情" };
        var baseline=NativeSnapshotIndex.Build(snapshot,null);
        var sections=new[]{"fields","state","modified","shape","modifiedShape","types","features","content","examine","tooltip"};
        foreach(string section in sections)
        {
            var next=(JObject)snapshot.DeepClone();
            // Removing a section also has to invalidate it. In the native item
            // contract explicit null detaches a formerly present window.
            next["roots"][0]["items"][0]["data"][section]=section=="examine" ? new JObject { ["entity"]=4,["text"]="new window" } : JValue.CreateNull();
            var index=NativeSnapshotIndex.Build(next,baseline);
            foreach(string other in sections)
                Check(index.PropertyChanged(1,other)==(other==section),"item section isolation: "+section+" changed "+other);
            if(section=="content") Check(!index.Nodes.ContainsKey(2) && !index.Nodes.ContainsKey(3),"removed window retained native descendants");
        }
        var removed=(JObject)snapshot.DeepClone();
        ((JObject)removed["roots"][0]["items"][0]["data"]).Remove("features");
        Check(NativeSnapshotIndex.Build(removed,baseline).PropertyChanged(1,"features"),"missing required property disguised as unchanged");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeSnapshotIndex PrepareWithoutRetainingPrevious(out WeakReference old)
    {
        var previous=Snapshot(); old=new WeakReference(previous);
        return NativeSnapshotIndex.Build((JObject)previous.DeepClone(),NativeSnapshotIndex.Build(previous,null));
    }
    private static JObject Snapshot()=>new JObject {
        ["roots"]=new JArray(new JObject { ["place"]=1,["entity"]=90,
            ["items"]=new JArray(new JObject { ["legacyId"]=1,["data"]=new JObject {
                ["entity"]=1,["name"]="机器",["child"]=new JObject {
                    ["entity"]=2,["kind"]="window",["child"]=new JObject { ["entity"]=3,["text"]="生产" } } } }) }) };
    private static void Reject(Action action,string message)
    { try { action(); } catch(InvalidDataException) { return; } throw new Exception("Accepted "+message); }
    private static void CheckFrontendMetadata()
    {
        var source=Snapshot(); source["schema"]=13;
        source["frontends"]=new JArray(new JObject { ["type"]=1,["serial"]=(1UL<<63)|4UL,["context"]=2UL,["id"]=1 });
        var decoded=NativeSnapshotCodec.Unpack(NativeSnapshotBinary.Pack(source));
        Check(JToken.DeepEquals(source,decoded),"typed frontend metadata changed during binary projection");
        var baseline=NativeSnapshotIndex.Build(decoded,null);
        Check(baseline.Nodes.Count==3,"frontend entity reference was counted as a new inventory node");
        var reopened=(JObject)decoded.DeepClone(); reopened["frontends"][0]["serial"]=5UL;
        var next=NativeSnapshotIndex.Build(reopened,baseline);
        Check(next.Unchanged.SetEquals(new uint[]{1,2,3}) && next.ChangedTopLevel.SetEquals(new[]{"frontends"}),
            "opening a new actor view invalidated unrelated native inventory renderers");
        var closed=(JObject)reopened.DeepClone(); closed["frontends"]=new JArray();
        var retired=NativeSnapshotIndex.Build(closed,next);
        Check(retired.Nodes.Count==3 && retired.Unchanged.Count==3 && retired.ChangedTopLevel.SetEquals(new[]{"frontends"}),
            "retiring a native view removed its still-live shared item");
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
}
