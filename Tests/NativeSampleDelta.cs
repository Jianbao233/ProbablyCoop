using System.Security.Cryptography;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

// Offline experiment on authentic applied packets. A reference can only
// resolve against the named complete baseline. No new live wire format.
internal static class NativeSampleDelta
{
    internal static void Run(string directory,bool measure=false)
    {
        CheckRecordEquality();
        byte[][] raw=Enumerable.Range(0,3).Select(i=>File.ReadAllBytes(
            Path.Combine(directory,$"qa-native-sample-{i}.bin"))).ToArray();
        byte[][] samples=raw.Select((bytes,i)=>{
            if(!ProbablyCoop.Core.NativeRecordFrame.IsDelta(bytes)) return bytes;
            ulong revision=System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(4));
            var basis=File.ReadAllBytes(Path.Combine(directory,$"qa-native-sample-{i}-basis.bin"));
            return NativeRecordBasis.ExpandForAudit(ProbablyCoop.Core.NativeRecordFrame.Body(bytes,revision),basis);
        }).ToArray();
        for(int i=0;i<samples.Length;i++)
        {
            var recorded=NativeSnapshotCodec.Unpack(samples[i]);
            var prepared=NativeSnapshotIndex.Build(recorded,null,separateRecords:true);
            var roots=NativeRootProjection.Read((JArray)recorded["roots"],prepared);
            var receipts=NativeReceiptProjection.Read(recorded["commandReceipts"],
                (ulong)recorded["commandThrough"],(ushort)recorded["commandResult"]);
            Console.WriteLine($"PASS recorded projection preflight {i}: schema={recorded["schema"]} roots={roots.Roots.Length} entities={prepared.Nodes.Count} receipts={receipts.Receipts.Length}; offline replay only");
        }
        var baseWorld=NativeSnapshotCodec.Unpack(samples[0]);
        var baseFields=new NativeDeferredFields(); baseFields.Seal();
        var productionFull=baseFields.Pack((JObject)baseWorld.DeepClone(),default,true,null,0,out var wireBasis,out _);
        var baseline=Items(baseWorld);
        string digest=Convert.ToHexString(SHA256.HashData(samples[0]));
        var parts=NativeDetachedSample.Baseline(baseWorld);
        var originalBase=NativeSnapshotIndex.Build(baseWorld,null);
        var previousParts=parts; var previousFull=originalBase;
        var previousSeparated=NativeSnapshotIndex.Build((JObject)baseWorld.DeepClone(),null,separateRecords:true);
        var pinned=previousSeparated;
        // Both deltas refer to the first sample. Missing the middle packet
        // must not prevent reconstruction of the second edit.
        for(int i=1;i<samples.Length;i++)
        {
            var world=NativeSnapshotCodec.Unpack(samples[i]);
            var writer=new NativeDeferredFields(); writer.Seal();
            var incremental=writer.Pack((JObject)world.DeepClone(),default,true,wireBasis,41,
                out var unexpectedCheckpoint,out int wireReferences,auditBasis:productionFull);
            if(unexpectedCheckpoint!=null || wireReferences==0) throw new InvalidDataException("Native record writer skipped the real fixture");
            var sparse=NativeSnapshotCodec.Unpack(ProbablyCoop.Core.NativeRecordFrame.Body(incremental,41));
            var actualSparse=NativeSnapshotIndex.Build(sparse,previousSeparated,separateRecords:true,basis:pinned);
            var packet=Encode(world,baseline,digest,out int reused,out int total);
            var owned=EncodeOwned(world,baseline,digest);
            if(!packet.AsSpan().SequenceEqual(owned) || world.Parent!=null) throw new InvalidDataException("Owned encoder changed wire format or retained its input");
            var reconstructed=Apply(packet,baseline,digest);
            var full=NativeSnapshotBinary.Pack(reconstructed);
            if(reused==0 || reused==total || !JToken.DeepEquals(reconstructed,world) ||
                !Plain(full).SequenceEqual(Plain(samples[i])))
                throw new InvalidDataException("Incremental experiment differs from original game snapshot");
            // Explicitly reject both a wrong base and an unresolved reference.
            Reject(()=>Apply(packet,baseline,digest+"bad"));
            Reject(()=>Apply(packet,new Dictionary<uint,JObject>(),digest));
            var separate=NativeDetachedSample.Read(packet,parts,digest);
            separate.Check(world,originalBase,parts);
            NativeDetachedSample.Read(packet,parts,digest,previousParts).Check(world,previousFull,previousParts);
            previousFull=NativeSnapshotIndex.Build(world,previousFull); previousParts=separate;
            var separatedWorld=(JObject)world.DeepClone();
            previousSeparated=NativeSnapshotIndex.Build(separatedWorld,previousSeparated,separateRecords:true);
            if(!actualSparse.Unchanged.SetEquals(previousSeparated.Unchanged) ||
                !actualSparse.ChangedTopLevel.SetEquals(previousSeparated.ChangedTopLevel) ||
                actualSparse.Nodes.Count!=previousSeparated.Nodes.Count ||
                actualSparse.Nodes.Any(entry=>!SameRecord(entry.Value,previousSeparated.Nodes[entry.Key])))
                throw new InvalidDataException("Sparse production index changed native data");
            if(!previousFull.Unchanged.SetEquals(previousSeparated.Unchanged) ||
                !previousFull.ChangedTopLevel.SetEquals(previousSeparated.ChangedTopLevel) ||
                !previousFull.ChangedBusiness.SetEquals(previousSeparated.ChangedBusiness) ||
                !previousFull.ChangedClient.SetEquals(previousSeparated.ChangedClient) ||
                previousSeparated.Nodes.Count!=previousFull.Nodes.Count)
                throw new InvalidDataException("Production separated record index diverged");
            foreach(var (id,node) in previousFull.Nodes)
                if(!previousSeparated.Nodes.TryGetValue(id,out var actual) || !SameRecord(node,actual))
                    throw new InvalidDataException("Native separated entity differs");
            foreach(var row in Rows(separatedWorld))
                if(previousSeparated.Nodes[(uint)row["data"]].Parent!=null)
                    throw new InvalidDataException("Native separated item retains a world parent");
            Console.WriteLine($"PASS recorded edit {i}: identicalItems={reused}/{total} wireBytes={raw[i].Length} fullBytes={full.Length} experimentalBytes={packet.Length} exactReconstruction=true baseline={digest[..16]}");
            if(measure)
            {
                var index=NativeSnapshotIndex.Build(baseWorld,null);
                Measure("full_encode",()=>NativeSnapshotBinary.Pack(world));
                Measure("delta_encode",()=>Encode(world,baseline,digest,out _,out _));
                Measure("delta_owned_encode",()=>EncodeOwned(world,baseline,digest));
                Measure("full_decode_index",()=>NativeSnapshotIndex.Build(NativeSnapshotCodec.Unpack(full),index));
                Measure("delta_expand_index",()=>NativeSnapshotIndex.Build(Apply(packet,baseline,digest),index));
                Measure("delta_detached_index",()=>NativeDetachedSample.Read(packet,parts,digest));
            }
        }
    }
    private static byte[] Encode(JObject world,Dictionary<uint,JObject> baseline,string digest,out int reused,out int total)
    {
        reused=total=0;
        var delta=new JObject();
        foreach(var prop in world.Properties())
        {
            if(prop.Name!="roots") { delta.Add(prop.Name,prop.Value.DeepClone()); continue; }
            var roots=new JArray(); delta.Add(prop.Name,roots);
            foreach(JObject root in (JArray)prop.Value)
            {
                var compact=new JObject(); roots.Add(compact);
                foreach(var section in root.Properties())
                {
                    if(section.Name!="items") { compact.Add(section.Name,section.Value.DeepClone()); continue; }
                    var items=new JArray(); compact.Add(section.Name,items);
                    foreach(JObject row in (JArray)section.Value)
                    {
                        var data=(JObject)row["data"]; uint id=(uint)data["entity"]; total++;
                        bool same=baseline.TryGetValue(id,out var previous) && SameRecord(previous,data);
                        if(same) reused++;
                        var item=new JObject(); items.Add(item);
                        foreach(var field in row.Properties())
                            item.Add(field.Name,same && field.Name=="data" ? new JValue(id) : field.Value.DeepClone());
                    }
                }
            }
        }
        return NativeSnapshotBinary.Pack(new JObject { ["base"]=digest,["snapshot"]=delta });
    }
    private static void Measure(string name,Func<object> operation)
    {
        const int count=8;
        _=operation();
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        var timer=Stopwatch.StartNew();
        for(int i=0;i<count;i++) _=operation();
        Console.WriteLine(FormattableString.Invariant($"OFFLINE {name} n={count} meanMs={timer.Elapsed.TotalMilliseconds/count:F3} managedBytesPerOperation={(GC.GetAllocatedBytesForCurrentThread()-allocated)/count}"));
    }
    // A capture transferred exclusively to a worker needs neither a second
    // metadata tree nor copies of changed item records. Restore the local
    // fixture even if the bounded encoder rejects a malformed field.
    private static byte[] EncodeOwned(JObject world,Dictionary<uint,JObject> basis,string digest)
    {
        if(world.Parent!=null) throw new InvalidOperationException("Expected an exclusively owned world");
        var saved=new List<(JObject Row,JObject Data)>();
        var envelope=new JObject { ["base"]=digest };
        try
        {
            foreach(var row in Rows(world))
            {
                var data=(JObject)row["data"]; uint id=(uint)data["entity"];
                if(!basis.TryGetValue(id,out var original) || !SameRecord(original,data)) continue;
                saved.Add((row,data)); row["data"]=id;
            }
            envelope["snapshot"]=world;
            return NativeSnapshotBinary.Pack(envelope);
        }
        finally
        {
            if(world.Parent!=null) envelope["snapshot"]=JValue.CreateNull();
            foreach(var (row,data) in saved) row["data"]=data;
        }
    }
    private static Dictionary<uint,JObject> Items(JObject world)
    {
        var items=new Dictionary<uint,JObject>();
        foreach(var row in Rows(world))
        {
            var data=(JObject)row["data"]; uint id=(uint)data["entity"];
            if(items.TryGetValue(id,out var prior) && !SameRecord(prior,data))
                throw new InvalidDataException("Conflicting item in captured baseline");
            items[id]=data;
        }
        return items;
    }
    private static IEnumerable<JObject> Rows(JObject world) =>
        ((JArray)world["roots"]).Cast<JObject>().SelectMany(root=>((JArray)root["items"]).Cast<JObject>());
    // Delta references preserve the binary record, not just the JSON value.
    // In particular Newtonsoft's object/number equality ignores distinctions
    // that the native encoder retains, including property order and -0f.
    private static bool SameRecord(JToken a,JToken b)
    {
        if(ReferenceEquals(a,b)) return true;
        if(a==null || b==null || a.Type!=b.Type) return false;
        if(a is JContainer ca)
        {
            if(b is not JContainer cb || ca.Count!=cb.Count ||
                a is JProperty pa && pa.Name!=((JProperty)b).Name) return false;
            for(JToken x=ca.First,y=cb.First;x!=null;x=x.Next,y=y.Next)
                if(!SameRecord(x,y)) return false;
            return true;
        }
        object left=((JValue)a).Value,right=((JValue)b).Value;
        if(a.Type==JTokenType.Integer)
            return left is ulong u ? right is ulong v && u==v :
                right is not ulong && Convert.ToInt64(left)==Convert.ToInt64(right);
        if(a.Type==JTokenType.Float)
        {
            if(left is float f) return right is float g && BitConverter.SingleToInt32Bits(f)==BitConverter.SingleToInt32Bits(g);
            if(left is double d) return right is double e && BitConverter.DoubleToInt64Bits(d)==BitConverter.DoubleToInt64Bits(e);
            return left is decimal m && right is decimal n && decimal.GetBits(m).AsSpan().SequenceEqual(decimal.GetBits(n));
        }
        return Equals(left,right);
    }
    private static void CheckRecordEquality()
    {
        var pairs=new (JToken,JToken,bool)[] {
            (JObject.Parse("{'a':1,'b':2}"),JObject.Parse("{'b':2,'a':1}"),false),
            (new JValue(-0f),new JValue(0f),false),(new JValue((float)1),new JValue((double)1),false),
            (new JValue(Math.BitIncrement(1d)),new JValue(1d),false),
            (new JValue(1m),new JValue(1.00m),false),(new JValue(1UL),new JValue(1L),false),
            (new JValue(1),new JValue(1L),true),(new JValue((string)null),JValue.CreateNull(),false),
            (new JArray(1,2),new JArray(2,1),false) };
        foreach(var (a,b,same) in pairs)
        {
            bool bytes=Plain(NativeSnapshotBinary.Pack(new JObject { ["v"]=a })).SequenceEqual(
                Plain(NativeSnapshotBinary.Pack(new JObject { ["v"]=b })));
            if(bytes!=same || SameRecord(a,b)!=same) throw new InvalidDataException($"Record comparison differs: {a.Type} {a} vs {b}, binaryEqual={bytes}, recordEqual={SameRecord(a,b)}, expected={same}, first={((a as JValue)?.Value?.GetType().Name)} second={((b as JValue)?.Value?.GetType().Name)}");
        }
    }
    private static JObject Apply(byte[] bytes,Dictionary<uint,JObject> items,string digest)
    {
        var packet=NativeSnapshotCodec.Unpack(bytes);
        if((string)packet["base"]!=digest) throw new InvalidDataException("Wrong native baseline");
        var world=(JObject)packet["snapshot"];
        foreach(var row in Rows(world))
        {
            if(row["data"] is JObject) continue;
            if(row["data"]?.Type!=JTokenType.Integer || !items.TryGetValue((uint)row["data"],out var old))
                throw new InvalidDataException("Unavailable item in native baseline");
            row["data"]=old.DeepClone();
        }
        return world;
    }
    private static void Reject(Action run)
    {
        try { run(); }
        catch(InvalidDataException) { return; }
        throw new InvalidOperationException("Accepted invalid baseline reference");
    }
    private static byte[] Plain(byte[] bytes)
    {
        using var input=new MemoryStream(bytes,4,bytes.Length-4,false);
        using var zipped=new System.IO.Compression.GZipStream(input,System.IO.Compression.CompressionMode.Decompress);
        using var plain=new MemoryStream();
        zipped.CopyTo(plain); return plain.ToArray();
    }
}
