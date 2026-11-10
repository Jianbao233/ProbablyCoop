using Newtonsoft.Json.Linq;
using ProbablyCoop;

internal static class NativeRootProjectionTests
{
    internal static void Run()
    {
        var first = Snapshot();
        var index = NativeSnapshotIndex.Build(first, null, separateRecords: true);
        var projected = NativeRootProjection.Read((JArray)first["roots"]!, index);
        Check(projected.Roots.Length == 2 && projected.Roots[0].Entity == 100 &&
            projected.Roots[0].Items[0].Entity == 1 && projected.Roots[0].Items[0].LegacyId == 10 &&
            ReferenceEquals(projected.Roots[0].Items[0].Data, index.Nodes[1]), "valid prepared ownership");
        // Moves change ownership, not the stable item identity or item data.
        var moved = Snapshot();
        var row = moved["roots"]![0]!["items"]![0]!;
        row.Remove(); ((JArray)moved["roots"]![1]!["items"]!).Add(row);
        var movedIndex = NativeSnapshotIndex.Build(moved, index, separateRecords: true);
        var movedRoots = NativeRootProjection.Read((JArray)moved["roots"]!, movedIndex);
        Check(movedRoots.Roots[0].Items.Length == 0 && movedRoots.Roots[1].Items[1].Entity == 1 &&
            movedIndex.Unchanged.Contains(1), "ownership move preserves entity");

        Reject(s => s["roots"]![1]!["place"] = 1, "duplicate place");
        Reject(s => s["roots"]![1]!["entity"] = 100, "duplicate fixture identity");
        Reject(s => s["roots"]![1]!["entity"] = 1, "fixture/item collision");
        Reject(s => s["roots"]![1]!["place"] = 24, "unknown fixture");
        Reject(s => s["roots"]![1]!["entity"] = "101", "text identity");
        Reject(s => ((JObject)s["roots"]![0]!["items"]![0]!["data"]!["fields"]!).Remove("<identifier>k__BackingField"), "missing item definition field");
        Reject(s => s["roots"]![1]!["items"]![0]!["legacyId"] = 10, "legacy map collision");
        Reject(s => ((JObject)s["roots"]![1]!["inventoryState"]!).Remove("tags"), "incomplete final root");
        Reject(s => s["roots"]![1]!["shape"] = true, "invalid geometry");
        Reject(s => s["roots"]![1]!["inventoryState"]!["steal"] = true, "partial slot mode");
        Reject(s => ((JArray)s["roots"]![1]!["items"]!).Add(s["roots"]![0]!["items"]![0]!.DeepClone()), "same item in two roots");
        Reject(s =>
        {
            var state = s["roots"]![0]!["inventoryState"]!;
            state["steal"] = true; state["down"] = false; state["hidden"] = false;
            ((JArray)s["roots"]![0]!["items"]!).Add(s["roots"]![1]!["items"]![0]!.DeepClone());
        }, "overfull slot");

        // Equal render aliases are allowed by the index, but never turn into a
        // second owned root child. A shape-less slot is a valid fixture.
        var slot = Snapshot();
        slot["roots"]![0]!["shape"] = JValue.CreateNull();
        var mode = slot["roots"]![0]!["inventoryState"]!;
        mode["steal"] = true; mode["down"] = false; mode["hidden"] = true;
        var slotIndex = NativeSnapshotIndex.Build(slot, null, separateRecords: true);
        Check(NativeRootProjection.Read((JArray)slot["roots"]!, slotIndex).Roots[0].Items.Length == 1, "valid embedded slot");
        var malformedReference = (JArray)slot["roots"]!.DeepClone();
        malformedReference[0]!["items"]![0]!["data"] = 999;
        Throws(() => NativeRootProjection.Read(malformedReference, slotIndex), "missing prepared entity");
        // Vanilla CreateEmptyItem and copied/custom objects can legitimately
        // have no definition identifier. Network entity identity still exists.
        var unnamed = Snapshot();
        unnamed["roots"]![0]!["items"]![0]!["data"]!["fields"]!["<identifier>k__BackingField"] = JValue.CreateNull();
        var unnamedIndex = NativeSnapshotIndex.Build(unnamed, null, separateRecords: true);
        Check(NativeRootProjection.Read((JArray)unnamed["roots"]!, unnamedIndex).Roots[0].Items[0].Entity == 1, "native null definition identifier");
        CheckActors();
        Console.WriteLine("PASS native root projection: complete ownership preflight, fixture/item collisions, duplicate owners, slot capacity, moves, actor-qualified roots and missing records");
    }

    private static JObject ActorSnapshot()
    {
        var snapshot=Snapshot(); snapshot["actors"]=new JObject();
        var roots=(JArray)snapshot["roots"]!;
        foreach(JObject root in roots) root["key"]=NativeActorInventory.RootKey.Shared((int)root["place"]!).ToString();
        uint fixture=200;
        foreach(var owner in new[]{NativeActorInventory.Host,NativeActorInventory.Partner})
            foreach(var place in new[]{4,5,8})
                roots.Add(new JObject { ["key"]=NativeActorInventory.RootKey.Carried(owner,(NativeActorInventory.CarrySlot)place).ToString(),
                    ["place"]=place,["entity"]=fixture++,["shape"]=null,["items"]=new JArray(),
                    ["inventoryState"]=new JObject { ["fields"]=new JObject(),["tags"]=new JObject(),["steal"]=false,["down"]=false,["hidden"]=false } });
        return snapshot;
    }
    private static void CheckActors()
    {
        var snapshot=ActorSnapshot();
        var index=NativeSnapshotIndex.Build(snapshot,null,separateRecords:true);
        var projection=NativeRootProjection.Read((JArray)snapshot["roots"]!,index);
        Check(projection.Roots.Length==8 && !projection.Roots[2].Key.Equals(projection.Roots[5].Key),"two actors share native slot numbers without sharing roots");
        void RejectActor(Action<JObject> change,string name)
        {
            var value=ActorSnapshot(); change(value);
            Throws(()=>NativeRootProjection.Read((JArray)value["roots"]!,NativeSnapshotIndex.Build(value,null,separateRecords:true)),name);
        }
        RejectActor(s=>s["roots"]![7]!.Remove(),"omitted empty partner backpack");
        RejectActor(s=>s["roots"]![5]!["key"]=s["roots"]![2]!["key"]!.DeepClone(),"aliased personal roots");
        RejectActor(s=>s["roots"]![5]!["place"]=5,"root key/place mismatch");
        RejectActor(s=>s["roots"]![5]!["key"]="world/4","personal slot labelled shared");
        RejectActor(s=>((JObject)s["roots"]![5]!).Remove("key"),"missing actor owner key");
        RejectActor(s=>s["roots"]![5]!["entity"]=s["roots"]![2]!["entity"]!.DeepClone(),"physical slot shared by actors");
    }

    private static JObject Snapshot() => new JObject { ["roots"] = new JArray(Root(1, 100, 1, 10), Root(2, 101, 2, 11)) };
    private static JObject Root(int place, uint fixture, uint item, uint legacy) => new JObject
    {
        ["place"] = place, ["entity"] = fixture,
        ["inventoryState"] = new JObject { ["fields"] = new JObject(), ["tags"] = new JObject() },
        ["shape"] = new JObject { ["w"] = 2, ["h"] = 2, ["outside"] = 1, ["cells"] = new JArray(0, 0, 0, 0) },
        ["items"] = new JArray(new JObject { ["legacyId"] = legacy, ["data"] = new JObject
        {
            ["entity"] = item, ["fields"] = new JObject { ["<identifier>k__BackingField"] = "test_item" }
        } })
    };
    private static void Reject(Action<JObject> change, string name)
    {
        var value = Snapshot(); change(value);
        Throws(() => NativeRootProjection.Read((JArray)value["roots"]!,
            NativeSnapshotIndex.Build(value, null, separateRecords: true)), name);
    }
    private static void Throws(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Native root projection accepted " + name);
    }
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("Native root projection: " + name); }
}
