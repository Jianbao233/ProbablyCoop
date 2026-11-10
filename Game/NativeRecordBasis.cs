using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// An authority worker owns both the captured tree and its frozen blocks.
// A reference may only replace a record with exactly the same wire content.
internal sealed class NativeRecordBasis
{
    private readonly Dictionary<uint,JObject> items=new Dictionary<uint,JObject>();
    private readonly Dictionary<uint,NativeDeferredFields.ItemRow> plain=new Dictionary<uint,NativeDeferredFields.ItemRow>();
    private readonly Dictionary<JToken,NativeDeferredFields.Block> fields;
    internal int Count=>items.Count;

    internal NativeRecordBasis(JObject world,Dictionary<JToken,NativeDeferredFields.Block> fields,List<NativeDeferredFields.ItemRow> staged=null)
    {
        foreach(var row in Rows(world))
        {
            var item=(JObject)row["data"];
            uint id=(uint)item["entity"];
            row["data"]=id;
            // Ambiguous top-level identities can always travel inline.
            if(id!=0 && !items.TryAdd(id,item)) items[id]=null;
        }
        // Keep just the records that may be referenced. Metadata and its
        // parent world tree need not be held until the next checkpoint.
        var roots=new HashSet<JToken>(fields.Comparer);
        foreach(var item in items.Values) if(item!=null) roots.Add(item);
        this.fields=new Dictionary<JToken,NativeDeferredFields.Block>(fields.Comparer);
        foreach(var pair in fields)
            if(roots.Contains(pair.Key.Root)) this.fields.Add(pair.Key,pair.Value);
        if(staged!=null) foreach(var row in staged)
        {
            if(!row.IsPlain || !roots.Contains(row.Target)) continue;
            plain.Add((uint)row.Target["entity"],row);
        }
    }

    internal bool MatchesPlain(uint id,NativeDeferredFields.ItemRow row,Dictionary<JToken,NativeDeferredFields.Block> captured)
    {
        if(!plain.TryGetValue(id,out var previous) || !row.IsPlain || row.Target.Count!=1 ||
            previous.Owner!=row.Owner || previous.MayRemove!=row.MayRemove || previous.MaxRemove!=row.MaxRemove ||
            previous.Abilities!=row.Abilities || previous.FrontendKind!=row.FrontendKind ||
            previous.Move!=row.Move || previous.Contents!=row.Contents) return false;
        return Equal(row.Target["entity"],previous.Target["entity"],captured) &&
            Equal(row.Features,previous.Features,captured) && Equal(row.Fields,previous.Fields,captured) && Equal(row.State,previous.State,captured) &&
            Equal(row.Modified,previous.Modified,captured) && Equal(row.Shape,previous.Shape,captured) &&
            Equal(row.ModifiedShape,previous.ModifiedShape,captured) && Equal(row.Types,previous.Types,captured) &&
            Equal(row.Tooltip,previous.Tooltip,captured) && Equal(row.Advanced,previous.Advanced,captured);
    }

    internal static IEnumerable<JObject> Rows(JObject world)
    {
        if(world["roots"] is not JArray roots || roots.Count>NativeRootProjection.MaximumRoots) throw new InvalidDataException("Invalid native inventory roots");
        foreach(var root in roots)
        {
            if(root["items"] is not JArray rows) throw new InvalidDataException("Invalid native inventory");
            foreach(var value in rows)
                if(value is JObject row && row["data"] is JObject) yield return row;
                else throw new InvalidDataException("Invalid native item record");
        }
    }

    // Used only to compare the incremental packet against an independently
    // encoded complete capture in isolated QA, and to inspect recorded packets.
    internal static byte[] ExpandForAudit(byte[] body,byte[] full,CancellationToken cancellation=default)
    {
        var basis=NativeSnapshotCodec.Unpack(full,cancellation);
        var items=new Dictionary<uint,JObject>();
        foreach(var row in Rows(basis))
        {
            var item=(JObject)row["data"];
            uint id=(uint)item["entity"];
            if(!items.TryAdd(id,item)) items[id]=null;
        }
        var expanded=NativeSnapshotCodec.Unpack(body,cancellation);
        foreach(JObject root in (JArray)expanded["roots"])
        foreach(JObject row in (JArray)root["items"])
        {
            cancellation.ThrowIfCancellationRequested();
            if(row["data"] is JObject) continue;
            if(row["data"]?.Type!=JTokenType.Integer || !items.TryGetValue((uint)row["data"],out var item) || item==null)
                throw new InvalidDataException("Unresolvable native reference in audit");
            row["data"]=item.DeepClone();
        }
        return NativeSnapshotBinary.Pack(expanded,cancellation);
    }

    internal List<(JObject Row,JObject Data)> ReferenceUnchanged(JObject current,Dictionary<JToken,NativeDeferredFields.Block> currentFields,CancellationToken cancellation,HashSet<JToken> verified=null)
    {
        var reused=new List<(JObject,JObject)>();
        var seen=new HashSet<uint>();
        var duplicate=new HashSet<uint>();
        foreach(var row in Rows(current))
        {
            uint id=(uint)row["data"]["entity"];
            if(!seen.Add(id)) duplicate.Add(id);
        }
        foreach(var row in Rows(current))
        {
            cancellation.ThrowIfCancellationRequested();
            var item=(JObject)row["data"];
            uint id=(uint)item["entity"];
            if(!duplicate.Contains(id) && items.TryGetValue(id,out var original) && original!=null &&
                (verified?.Contains(item)==true || Equal(item,original,currentFields)))
            {
                reused.Add((row,item));
                row["data"]=id;
            }
        }
        return reused;
    }

    private bool Equal(JToken current,JToken original,Dictionary<JToken,NativeDeferredFields.Block> currentFields)
    {
        if(ReferenceEquals(current,original)) return true;
        if(current.Type!=original.Type) return false;
        if(current is JValue value) return ScalarEqual(value.Value,((JValue)original).Value);
        if(current is JProperty p)
            return p.Name==((JProperty)original).Name && Equal(p.Value,((JProperty)original).Value,currentFields);
        var container=(JContainer)current; var previous=(JContainer)original;
        if(container.Count!=previous.Count) return false;
        if(container.Count==0)
        {
            currentFields.TryGetValue(current,out var a);
            fields.TryGetValue(original,out var b);
            return a==null ? b==null : b!=null && BlockEqual(a,b);
        }
        for(JToken next=current.First,prior=original.First;next!=null;next=next.Next,prior=prior.Next)
            if(!Equal(next,prior,currentFields)) return false;
        return true;
    }

    private static bool BlockEqual(NativeDeferredFields.Block a,NativeDeferredFields.Block b)
    {
        // Name/column arrays are immutable schema contracts. A different
        // representation is safe to send inline even if its values coincide.
        if(!ReferenceEquals(a.Names,b.Names) || !ReferenceEquals(a.TagColumns,b.TagColumns) ||
            !ArrayEqual(a.Keys,b.Keys) || !ArrayEqual(a.Bytes,b.Bytes) || !ArrayEqual(a.Strings,b.Strings)) return false;
        if(a.TagRows!=null || b.TagRows!=null)
        {
            if(a.TagRows==null || b.TagRows==null || a.TagRows.Length!=b.TagRows.Length) return false;
            for(int i=0;i<a.TagRows.Length;i++)
            {
                bool inherited=a.InheritsRow(i);
                if(inherited!=b.InheritsRow(i) || !inherited && !a.TagRows[i].SameValues(in b.TagRows[i])) return false;
            }
        }
        if(ReferenceEquals(a.Values,b.Values)) return true;
        if(a.Values==null || b.Values==null || a.Values.Length!=b.Values.Length) return false;
        for(int i=0;i<a.Values.Length;i++) if(!ScalarEqual(a.Values[i],b.Values[i])) return false;
        return true;
    }

    private static bool ArrayEqual<T>(T[] x,T[] y) where T:IEquatable<T> =>
        ReferenceEquals(x,y) || x!=null && y!=null && x.AsSpan().SequenceEqual(y);
    internal static bool ScalarEqual(object x,object y)
    {
        if(ReferenceEquals(x,y)) return true;
        if(x==null || y==null || x.GetType()!=y.GetType()) return false;
        if(x is float f) return BitConverter.SingleToInt32Bits(f)==BitConverter.SingleToInt32Bits((float)y);
        if(x is double d) return BitConverter.DoubleToInt64Bits(d)==BitConverter.DoubleToInt64Bits((double)y);
        if(x is decimal m) return decimal.GetBits(m).AsSpan().SequenceEqual(decimal.GetBits((decimal)y));
        if(x is byte[] bytes) return bytes.AsSpan().SequenceEqual((byte[])y);
        if(x is int[] ints) return ints.AsSpan().SequenceEqual((int[])y);
        if(x is string[] strings) return strings.AsSpan().SequenceEqual((string[])y);
        if(x is KeyValuePair<string,int>[] map)
        {
            var other=(KeyValuePair<string,int>[])y;
            if(map.Length!=other.Length) return false;
            for(int i=0;i<map.Length;i++) if(!map[i].Equals(other[i])) return false;
            return true;
        }
        return x.Equals(y);
    }
}
