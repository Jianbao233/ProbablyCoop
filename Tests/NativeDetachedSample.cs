using Newtonsoft.Json.Linq;
using ProbablyCoop;

// Offline read model. Each top-level item owns only its own token tree.
// Unchanged records are shared by reference; no old whole-world parent chain.
internal sealed class NativeDetachedSample
{
    private readonly Dictionary<uint,NativeSnapshotIndex> records=new();
    private readonly Dictionary<uint,JObject> entities=new();
    private readonly HashSet<uint> unchanged=new();
    private readonly Dictionary<uint,HashSet<string>> properties=new();
    private readonly NativeSnapshotIndex metadata;

    internal static NativeDetachedSample Baseline(JObject data)=>new((JObject)data.DeepClone(),null,null);
    internal static NativeDetachedSample Read(byte[] bytes,NativeDetachedSample basis,string digest,NativeDetachedSample previous=null)
    {
        var packet=NativeSnapshotCodec.Unpack(bytes);
        if((string)packet["base"]!=digest || packet["snapshot"] is not JObject world) throw new InvalidDataException("Wrong native basis");
        return new(world,basis,previous ?? basis);
    }
    private NativeDetachedSample(JObject world,NativeDetachedSample basis,NativeDetachedSample previous)
    {
        foreach(var root in (JArray)world["roots"])
        foreach(JObject row in (JArray)root["items"])
        {
            NativeSnapshotIndex record;
            uint id;
            if(row["data"] is JObject item)
            {
                id=(uint)item["entity"];
                row["data"]=new JObject { ["entity"]=id };
                if(item.Parent!=null) throw new InvalidOperationException("Item retains whole-world parent");
                var fragment=new JObject { ["roots"]=new JArray(new JObject { ["items"]=new JArray(new JObject { ["data"]=item }) }) };
                record=NativeSnapshotIndex.Build(fragment,null);
            }
            else
            {
                if(row["data"]?.Type!=JTokenType.Integer || basis==null || !basis.records.TryGetValue(id=(uint)row["data"],out record))
                    throw new InvalidDataException("Missing native record");
                row["data"]=new JObject { ["entity"]=id };
            }
            if(!records.TryAdd(id,record) && !JToken.DeepEquals(records[id].Nodes[id],record.Nodes[id]))
                throw new InvalidDataException("Conflicting top level item");
            foreach(var entry in record.Nodes)
            {
                if(!entities.TryAdd(entry.Key,entry.Value) && !JToken.DeepEquals(entities[entry.Key],entry.Value))
                    throw new InvalidDataException("Conflicting nested entity");
            }
        }
        metadata=NativeSnapshotIndex.Build(world,previous?.metadata);
        foreach(var (id,node) in entities)
        {
            JObject old=null; previous?.entities.TryGetValue(id,out old);
            if(ReferenceEquals(old,node) || old!=null && JToken.DeepEquals(old,node)) { unchanged.Add(id); continue; }
            var changed=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in node.Properties()) if(old==null || !JToken.DeepEquals(field.Value,old[field.Name])) changed.Add(field.Name);
            if(old!=null) foreach(var field in old.Properties()) if(node[field.Name]==null) changed.Add(field.Name);
            properties[id]=changed;
        }
        if(previous==null || records.Keys.Any(id=>!unchanged.Contains(id))) metadata.ChangedTopLevel.Add("roots");
    }
    internal void Check(JObject world,NativeSnapshotIndex prior,NativeDetachedSample priorParts)
    {
        var expected=NativeSnapshotIndex.Build(world,prior);
        if(!expected.Unchanged.SetEquals(unchanged) || entities.Count!=expected.Nodes.Count ||
            !metadata.ChangedTopLevel.SetEquals(expected.ChangedTopLevel) ||
            !metadata.ChangedBusiness.SetEquals(expected.ChangedBusiness) ||
            !metadata.ChangedClient.SetEquals(expected.ChangedClient)) throw new InvalidDataException("Detached world change sets diverged");
        foreach(var (id,node) in entities)
        {
            if(!expected.Nodes.TryGetValue(id,out var match) || !JToken.DeepEquals(node,match)) throw new InvalidDataException("Wrong detached node");
            priorParts.entities.TryGetValue(id,out var old);
            foreach(var name in node.Properties().Select(p=>p.Name).Concat(old?.Properties().Select(p=>p.Name) ?? Array.Empty<string>()))
                if(expected.PropertyChanged(id,name)!=(properties.TryGetValue(id,out var changed) && changed.Contains(name)))
                    throw new InvalidDataException("Wrong detached property: "+name);
        }
    }
}
