using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Newtonsoft.Json.Linq;
using RootKey = ProbablyCoop.NativeActorInventory.RootKey;

namespace ProbablyCoop;

// Built on the decode worker. The input and returned nodes are read-only after
// construction; they contain no native object, delegate or Unity reference.
internal sealed class NativeSnapshotIndex
{
    internal readonly Dictionary<uint,JObject> Nodes=new Dictionary<uint,JObject>();
    private readonly Dictionary<uint,List<KeyValuePair<uint,JObject>>> records=new Dictionary<uint,List<KeyValuePair<uint,JObject>>>();
    internal readonly HashSet<uint> Unchanged=new HashSet<uint>();
    private readonly Dictionary<uint,HashSet<string>> changedProperties=new Dictionary<uint,HashSet<string>>();
    internal readonly HashSet<string> ChangedTopLevel;
    internal readonly HashSet<string> ChangedBusiness;
    internal readonly HashSet<string> ChangedClient;
    // Root geometry is decoded before any native inventory or UI mutation.
    // The buffers belong to this decoded projection, independent of Unity.
    internal readonly Dictionary<RootKey,RootGrid> RootGrids=new Dictionary<RootKey,RootGrid>();
    internal readonly bool ActorRoots;
    internal sealed class RootGrid
    {
        internal readonly int Width,Height;
        internal readonly byte Outside;
        internal readonly byte[] Cells;

        internal RootGrid(JObject shape)
        {
            Width=ReadInteger(shape["w"],1,128);
            Height=ReadInteger(shape["h"],1,128);
            Outside=(byte)ReadInteger(shape["outside"],0,255);
            if(shape["cells"] is not JArray cells || cells.Count!=Width*Height)
                throw new InvalidDataException("Invalid native inventory shape cells");
            Cells=new byte[cells.Count];
            int index=0;
            for(var cell=cells.First;cell!=null;cell=cell.Next)
                Cells[index++]=(byte)ReadInteger(cell,0,255);
        }

        internal static int ReadInteger(JToken token,int min,int max)
        {
            if(token is JValue value && token.Type==JTokenType.Integer)
            {
                long number=value.Value switch { long l=>l,int i=>i,byte b=>b,ulong u when u<=int.MaxValue=>(long)u,_=>-1 };
                if(number>=min && number<=max) return (int)number;
            }
            throw new InvalidDataException("Invalid native inventory shape integer");
        }
    }
    private readonly JObject source;
    // A changed child window need not invalidate the owning item's tags,
    // shape or sprite. Comparisons still happen only on the decode worker.
    internal bool PropertyChanged(uint id,string property) => !Nodes.ContainsKey(id) ||
        changedProperties.TryGetValue(id,out var changed) && changed.Contains(property);
    private NativeSnapshotIndex(JObject source,NativeSnapshotIndex previous,Comparison comparison,bool separateRecords)
    {
        this.source=source;
        ActorRoots=source["actors"] is JObject;
        if(source["actors"]!=null && source["actors"].Type!=JTokenType.Null && !ActorRoots)
            throw new InvalidDataException("Invalid actor projection envelope");
        ChangedTopLevel=Changes(source,previous?.source,comparison,separateRecords ? "roots" : null);
        ChangedBusiness=Changes(source["business"] as JObject,previous?.source["business"] as JObject,comparison);
        ChangedClient=Changes(source["client"] as JObject,previous?.source["client"] as JObject,comparison);
    }
    private static HashSet<string> Changes(JObject current,JObject previous,Comparison comparison,string skip=null)
    {
        var changed=new HashSet<string>(StringComparer.Ordinal);
        if(current!=null) for(var token=current.First;token!=null;token=token.Next)
        {
            var property=(JProperty)token;
            if(property.Name!=skip && (previous==null || !comparison.Equal(property.Value,previous[property.Name]))) changed.Add(property.Name);
        }
        if(previous!=null) for(var token=previous.First;token!=null;token=token.Next)
        {
            var property=(JProperty)token;
            if(property.Name!=skip && (current==null || current[property.Name]==null)) changed.Add(property.Name);
        }
        return changed;
    }

    internal static NativeSnapshotIndex Build(JObject snapshot,NativeSnapshotIndex previous,CancellationToken cancellation=default,
        bool separateRecords=false,NativeSnapshotIndex basis=null)
    {
        cancellation.ThrowIfCancellationRequested();
        // One build-local memo prevents repeatedly walking the same complete
        // window/item subtree for roots, entities and changed properties. It
        // is not retained by the result and never substitutes hashes for equality.
        var comparison=new Comparison(cancellation);
        var index=new NativeSnapshotIndex(snapshot,previous,comparison,separateRecords);
        var references=basis==null ? null : new HashSet<uint>();
        if(snapshot["roots"] is not JArray roots || roots.Count>NativeRootProjection.MaximumRoots)
            throw new InvalidDataException("Invalid native inventory roots");
        foreach(var entry in roots)
        {
            cancellation.ThrowIfCancellationRequested();
            if(entry is not JObject root || root["items"] is not JArray items)
                throw new InvalidDataException("Invalid native inventory items");
            if(root["shape"] is JObject shape)
            {
                var key=NativeRootProjection.ReadKey(root,index.ActorRoots);
                if(index.RootGrids.ContainsKey(key)) throw new InvalidDataException("Duplicate native grid root");
                index.RootGrids.Add(key,new RootGrid(shape));
            }
            foreach(var row in items)
            {
                if(row is not JObject item) throw new InvalidDataException("Invalid native item row");
                if(item["data"] is not JObject data)
                {
                    uint id=EntityId(item["data"]);
                    if(!separateRecords || basis==null || !references.Add(id) || !basis.records.TryGetValue(id,out var inherited) || inherited==null)
                        throw new InvalidDataException("Invalid native item reference");
                    foreach(var entryNode in inherited)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        index.Register(entryNode.Key,entryNode.Value,previous,comparison);
                    }
                    if(!index.Unchanged.Contains(id)) index.ChangedTopLevel.Add("roots");
                    continue;
                }
                if(data.Count==0 && separateRecords)
                    throw new InvalidDataException("Invalid native item projection");
                uint recordId=0;
                List<KeyValuePair<uint,JObject>> members=null;
                if(separateRecords)
                {
                    // The root inventory owns placement and order. One item's
                    // immutable data must not retain an entire world by Parent.
                    recordId=EntityId(data["entity"]);
                    item["data"]=recordId;
                    if(basis==null)
                    {
                        members=new List<KeyValuePair<uint,JObject>>();
                        if(!index.records.TryAdd(recordId,members)) index.records[recordId]=null;
                    }
                }
                // JToken already has parent/sibling links. Walking them avoids
                // a recursive iterator allocation for every field/tag container.
                for(JToken token=data;token!=null;token=Next(token,data))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(token is not JObject node || node["entity"]==null) continue;
                    if(node.TryGetValue("abilities",out var abilities))
                        RootGrid.ReadInteger(abilities,0,ProbablyCoop.Core.ItemAbilityBits.MaxValue);
                    uint id=EntityId(node["entity"]);
                    members?.Add(new KeyValuePair<uint,JObject>(id,node));
                    index.Register(id,node,previous,comparison);
                }
                if(separateRecords && !index.Unchanged.Contains(recordId)) index.ChangedTopLevel.Add("roots");
            }
        }
        if(separateRecords && !index.ChangedTopLevel.Contains("roots") && !comparison.Equal(roots,previous?.source["roots"]))
            index.ChangedTopLevel.Add("roots");
        return index;
    }

    private void Register(uint id,JObject node,NativeSnapshotIndex previous,Comparison comparison)
    {
        if(Nodes.TryGetValue(id,out var other))
        {
            if(!comparison.Equal(node,other)) throw new InvalidDataException("Conflicting native entity projection");
            return;
        }
        Nodes.Add(id,node);
        JObject old=null;
        previous?.Nodes.TryGetValue(id,out old);
        if(old!=null && comparison.Equal(node,old)) Unchanged.Add(id);
        else changedProperties.Add(id,Changes(node,old,comparison));
    }

    private static uint EntityId(JToken value)
    {
        if(value?.Type!=JTokenType.Integer || !uint.TryParse(value.ToString(),out uint id) || id==0)
            throw new InvalidDataException("Invalid native entity identity");
        return id;
    }

    private static JToken Next(JToken node,JToken root)
    {
        if(node is JContainer container && container.First!=null) return container.First;
        while(!ReferenceEquals(node,root))
        {
            if(node.Next!=null) return node.Next;
            node=node.Parent;
        }
        return null;
    }

    private sealed class Comparison
    {
        private readonly CancellationToken cancellation;
        private readonly Dictionary<JToken,Entry> memo=new Dictionary<JToken,Entry>(ReferenceComparer.Instance);
        private int visits;
        private readonly struct Entry
        {
            internal readonly JToken Previous;
            internal readonly bool Equal;
            internal Entry(JToken previous,bool equal) { Previous=previous; Equal=equal; }
        }
        private sealed class ReferenceComparer : IEqualityComparer<JToken>
        {
            internal static readonly ReferenceComparer Instance=new ReferenceComparer();
            public bool Equals(JToken x,JToken y)=>ReferenceEquals(x,y);
            public int GetHashCode(JToken value)=>RuntimeHelpers.GetHashCode(value);
        }
        internal Comparison(CancellationToken cancellation) { this.cancellation=cancellation; }
        internal bool Equal(JToken current,JToken previous,bool entryPoint=true)
        {
            if((++visits & 127)==0) cancellation.ThrowIfCancellationRequested();
            if(ReferenceEquals(current,previous)) return true;
            if(current==null || previous==null || current.Type!=previous.Type) return false;
            // Keep Newtonsoft's numeric/date/raw/null comparison semantics.
            if(current is not JObject && current is not JArray) return JToken.DeepEquals(current,previous);
            // Only entity nodes, their direct sections and externally queried
            // sections are compared again by the index. Caching every tag row
            // costs a large dictionary without eliminating a second visit.
            bool entity=current is JObject candidate && candidate["entity"]!=null;
            bool remember=entryPoint || entity;
            if(remember && memo.TryGetValue(current,out var cached) && ReferenceEquals(cached.Previous,previous)) return cached.Equal;
            bool equal;
            if(current is JObject obj)
            {
                var old=(JObject)previous;
                equal=obj.Count==old.Count;
                if(equal)
                {
                    var oldToken=old.First;
                    for(var token=obj.First;token!=null;token=token.Next,oldToken=oldToken.Next)
                    {
                        var property=(JProperty)token; var oldProperty=(JProperty)oldToken;
                        // Preserve JObject's order-insensitive semantics. Native
                        // captures normally keep order, so that path needs no
                        // dictionary lookup for each field in each tag row.
                        var other=property.Name==oldProperty.Name ? oldProperty.Value : old[property.Name];
                        if(!Equal(property.Value,other,entity)) { equal=false; break; }
                    }
                }
            }
            else
            {
                var array=(JArray)current; var old=(JArray)previous;
                equal=array.Count==old.Count;
                if(equal)
                {
                    var other=old.First;
                    for(var item=array.First;item!=null;item=item.Next,other=other.Next)
                        if(!Equal(item,other,false)) { equal=false; break; }
                }
            }
            if(remember) memo[current]=new Entry(previous,equal);
            return equal;
        }
    }
}
