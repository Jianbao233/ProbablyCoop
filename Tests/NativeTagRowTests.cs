using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

internal static class NativeTagRowTests
{
    private sealed class Tag
    {
        public bool valueEnabled=true;
        private string internalValueString;
        public int valueInt=int.MinValue;
        public float valueFloat=float.NegativeInfinity;
        public long valueLong=long.MaxValue;
        public double valueDouble=double.NaN;
        public bool valueBool=true;
        public string identifier {get;set;}="identifier😀";
        public string identifierName {get;set;}="标记";
        public string Text {get=>internalValueString;set=>internalValueString=value;}
    }
    private static readonly FieldInfo[] Fields=typeof(Tag).GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);
    private static readonly Func<Tag,NativeTagRow> Freeze=tag=>new NativeTagRow(tag.valueEnabled,tag.Text,tag.valueInt,tag.valueFloat,
        tag.valueLong,tag.valueDouble,tag.valueBool,tag.identifier,tag.identifierName);
    internal static void Run()
    {
        CheckModifiedTable();
        CheckStockCache();
        CheckFrozenRecordFrames();
        var compact=DetachedCheckpoint(out var discardedWorld);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!discardedWorld.IsAlive && compact.Count==1,"checkpoint retained unrelated world metadata");
        Check(NativeTagRow.GetLayout(Fields)!=null,"exact declared tag contract not recognized");
        Check(NativeTagRow.GetLayout(Fields.Skip(1).ToArray())==null && NativeTagRow.GetLayout(null)==null &&
            NativeTagRow.GetLayout(Enumerable.Repeat(Fields[0],9).ToArray())==null,"mismatched tag layout accepted");
        var rows=new Dictionary<string,Tag> { ["a"]=new Tag(),[""]=new Tag {Text="中文\n",valueFloat=-0f,valueDouble=double.NegativeInfinity,valueLong=long.MinValue,identifier=null,identifierName=""} };
        var generic=new NativeDeferredFields(); var oldRoot=new JObject { ["tags"]=generic.CaptureMap(rows,Fields) }; generic.Seal();
        var typed=new NativeDeferredFields(); var root=new JObject { ["tags"]=typed.CaptureTags(rows,Freeze) }; typed.Seal();
        rows["a"].Text="changed"; rows[""].valueInt=400; rows.Clear();
        var old=generic.Pack(oldRoot); var next=typed.Pack(root);
        Check(Plain(old).SequenceEqual(Plain(next)),"typed tag binary bytes changed");
        Check(JToken.DeepEquals(NativeSnapshotBinary.Unpack(old),NativeSnapshotBinary.Unpack(next)),"typed tag value types changed");
        var reversed=Fields.Reverse().ToArray(); var layout=NativeTagRow.GetLayout(reversed);
        Check(layout!=null,"actual metadata order cannot be mapped");
        var reorderedRows=new Dictionary<string,Tag>{{"row",new Tag()}};
        var reorderedOld=new NativeDeferredFields(); var reorderedOldRoot=new JObject { ["tags"]=reorderedOld.CaptureMap(reorderedRows,reversed) }; reorderedOld.Seal();
        var reorderedNew=new NativeDeferredFields(); var reorderedNewRoot=new JObject { ["tags"]=reorderedNew.CaptureTags(reorderedRows,Freeze,layout) }; reorderedNew.Seal();
        Check(Plain(reorderedOld.Pack(reorderedOldRoot)).SequenceEqual(Plain(reorderedNew.Pack(reorderedNewRoot))),"reordered metadata changed wire column order");
        Reject(()=>typed.CaptureTags(rows,Freeze),"sealed capture"); Reject(()=>typed.Pack(root),"double consumption");
        var materialized=new NativeDeferredFields(); var m=new JObject { ["tags"]=materialized.CaptureTags(new Dictionary<string,Tag>{{"t",new Tag()}},Freeze) }; materialized.Seal(); materialized.Materialize(m);
        var expected=new JObject(); var source=new Tag(); foreach(var field in Fields) expected.Add(field.Name,NativeSnapshotCodec.Scalar(field.GetValue(source)));
        Check(m["tags"]["t"].ToString()==expected.ToString(),"typed tag materialization changed order or fields");
        var capture=new NativeDeferredFields();
        Task.Run(()=>Reject(()=>capture.CaptureTags(rows,Freeze),"worker native read")).GetAwaiter().GetResult();
        Reject(()=>capture.CaptureTags(new Dictionary<string,Tag>{{"null",null}},Freeze),"null native row");
        var oversized=Enumerable.Range(0,NativeDeferredFields.MaxValues/9+1).ToDictionary(i=>i.ToString(),i=>source);
        Reject(()=>capture.CaptureTags(oversized,Freeze),"typed value budget");
        var frozen=Detached(out var weak,out var detached);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!weak.IsAlive,"typed capture retained native row/delegate closure");
        Check((int)NativeSnapshotBinary.Unpack(frozen.Pack(detached))["tags"]["t"]["valueInt"]==int.MinValue,"collected source lost values");
        var cancelled=new NativeDeferredFields(); var cancelledRoot=new JObject { ["t"]=cancelled.CaptureTags(rows,Freeze) }; cancelled.Seal();
        using(var stop=new CancellationTokenSource())
        { stop.Cancel(); try { cancelled.Pack(cancelledRoot,stop.Token); throw new Exception("typed cancellation ignored"); } catch(OperationCanceledException) { } }
        var many=Enumerable.Range(0,2000).ToDictionary(i=>"tag"+i,i=>new Tag {valueInt=i});
        var reflected=Measure(()=> { var d=new NativeDeferredFields(); d.CaptureMap(many,Fields); d.Seal(); });
        var unboxed=Measure(()=> { var d=new NativeDeferredFields(); d.CaptureTags(many,Freeze); d.Seal(); });
        Check(unboxed.Bytes<reflected.Bytes,"typed capture did not reduce allocation");
        Console.WriteLine($"PASS typed tag table: exact contract/order/wire bits, null/NaN/infinity, frozen source, no native retention, cancellation/thread/value bounds; .NET9 capture 2000 rows reflection={reflected.Ms:F3}ms/{reflected.Bytes}B typed={unboxed.Ms:F3}ms/{unboxed.Bytes}B");
    }
    private static void CheckModifiedTable()
    {
        var baseRows=new Dictionary<string,Tag>{{"same",new Tag()},{"signed",new Tag{valueFloat=-0f}},{"base only",new Tag()}};
        var altered=new Dictionary<string,Tag>{{"same",new Tag()},{"signed",new Tag{valueFloat=0f}},{"added",new Tag{Text=""}}};
        JObject Capture(bool materialize)
        {
            var fields=new NativeDeferredFields();
            var state=fields.CaptureTags(baseRows,Freeze);
            var root=new JObject { ["state"]=state,["modified"]=fields.CaptureTags(altered,Freeze,basis:state) };
            fields.Seal();
            if(materialize) { fields.Materialize(root); return root; }
            return NativeSnapshotBinary.Unpack(fields.Pack(root));
        }
        var data=Capture(false);
        Check(JToken.DeepEquals(data,Capture(true)),"materialized and binary sparse tags differ");
        var modified=(JObject)data["modified"];
        Check(modified["same"].Type==JTokenType.Boolean && !(bool)modified["same"] &&
            modified["signed"] is JObject && modified["added"] is JObject && modified["base only"]==null,"signed zero, insert or removal was aliased");
        var plain=new NativeDeferredFields(); var baseline=new JObject{["modified"]=plain.CaptureTags(altered,Freeze)};
        plain.Seal(); plain.Materialize(baseline);
        var resolved=new JObject();
        foreach(var property in modified.Properties()) resolved[property.Name]=NativeTagRow.Resolve(property,(JObject)data["state"]).DeepClone();
        Check(JToken.DeepEquals(baseline["modified"],resolved),"sparse map changed native values");
        baseRows["same"].Text=altered["same"].Text="new base and modified value";
        var updated=Capture(false);
        Check(JToken.DeepEquals(modified,updated["modified"]) && !JToken.DeepEquals(NativeTagRow.Resolve(modified.Property("same"),(JObject)data["state"]),
            NativeTagRow.Resolve(((JObject)updated["modified"]).Property("same"),(JObject)updated["state"])),"unchanged alias did not follow changing base row");
        try { NativeTagRow.Resolve(modified.Property("same"),new JObject()); throw new Exception("Accepted missing base"); }
        catch(InvalidDataException) { }
        Console.WriteLine("PASS sparse native tag rows: exact binary/materialized values, independent modified order and changes to inherited base");
    }
    private static void CheckStockCache()
    {
        var cache=new NativeDeferredFields.StockCache();
        var tags=new Dictionary<string,Tag>{{"x",new Tag()},{"y",new Tag{Text="B"}}};
        var cells=new byte[]{0,1};
        var captures=new List<(NativeDeferredFields Fields,JObject Root,byte[] Expected)>();
        (NativeDeferredFields,JObject) FreezeFrame(NativeDeferredFields.StockCache reuse)
        {
            var fields=new NativeDeferredFields(true,reuse,reuse);
            var root=new JObject { ["tags"]=fields.CaptureTags(tags,Freeze),
                ["shape"]=fields.CaptureShape(cells,0,0,2,1,false,0,0,(source,span)=>source.AsSpan().CopyTo(span),null) };
            fields.Seal();
            return (fields,root);
        }
        void Capture()
        {
            var (plain,world)=FreezeFrame(null);
            var (cached,copy)=FreezeFrame(cache);
            captures.Add((cached,copy,plain.Pack(world)));
        }
        Capture(); Capture();
        var originalKeys=cache.Tags.GetOrCreateValue(tags).Keys;
        var originalCells=cache.Shapes.GetOrCreateValue(cells).Bytes;
        Capture();
        Check(ReferenceEquals(originalKeys,cache.Tags.GetOrCreateValue(tags).Keys) &&
            ReferenceEquals(originalCells,cache.Shapes.GetOrCreateValue(cells).Bytes),"unchanged immutable buffers were copied");
        tags["y"].valueFloat=-0f; cells[0]=2; Capture();
        tags["y"].valueFloat=0f; cells[0]=0; Capture();
        tags.Clear(); tags.Add("y",new Tag()); tags.Add("x",new Tag()); Capture();
        tags.Remove("x"); Capture(); tags.Clear(); Capture();
        foreach(var (fields,root,expected) in captures.AsEnumerable().Reverse())
            Check(fields.Pack(root).SequenceEqual(expected),"later capture modified an earlier tag table or shape");
        Console.WriteLine("PASS native stock cache: delayed reverse-order workers, changed/reverted cells and floats, unchanged reuse, reorder, add/remove, empty tables");
    }
    private static void CheckFrozenRecordFrames()
    {
        var baseTags=new Dictionary<string,Tag>{{"shared",new Tag{valueFloat=-0f}},{"changed",new Tag()}};
        var modified=new Dictionary<string,Tag>{{"shared",new Tag{valueFloat=-0f}},{"changed",new Tag{Text="custom"}}};
        var cache=new NativeDeferredFields.StockCache();
        var cells=new byte[]{7,8};
        (NativeDeferredFields Fields,JObject Root) Capture()
        {
            var f=new NativeDeferredFields(true,cache,cache);
            var tags=f.CaptureTags(baseTags,Freeze);
            var mod=f.CaptureTags(modified,Freeze,basis:tags);
            var shape=f.CaptureShape(cells,0,0,2,1,false,0,0,(source,span)=>source.AsSpan().CopyTo(span),null);
            var item=new JObject { ["entity"]=1,["state"]=tags,["modified"]=mod,["shape"]=shape };
            var fixedItem=new JObject { ["entity"]=2,["tooltip"]=f.CaptureTooltip(null,"","色","",null,12,2,false) };
            var root=new JObject { ["roots"]=new JArray(new JObject { ["items"]=new JArray(
                new JObject { ["data"]=item },new JObject { ["data"]=fixedItem }) }) };
            f.Seal(); return (f,root);
        }
        var (first,origin)=Capture();
        var full=first.Pack(origin,default,true,null,0,out var basis,out _);
        void Verify(int expected)
        {
            var (next,world)=Capture();
            var bytes=next.Pack(world,default,true,basis,3,out _,out int refs,auditBasis:full);
            Check(refs==expected && ProbablyCoop.Core.NativeRecordFrame.HasBase(bytes,3),"wrong frozen item reference count");
        }
        Verify(2);
        modified["shared"].valueFloat=0f; Verify(1);
        modified["shared"].valueFloat=-0f; Verify(2);
        cells[1]=9; Verify(1);
        cells[1]=8; baseTags["changed"].Text="custom"; Verify(1);
        Console.WriteLine("PASS incremental native frozen records: normal and inherited tag values, signed zero, immutable geometry, tooltip layers and revert checked against complete bytes");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeRecordBasis DetachedCheckpoint(out WeakReference discardedWorld)
    {
        var fields=new NativeDeferredFields();
        var item=new JObject { ["entity"]=3, ["tags"]=fields.CaptureTags(new Dictionary<string,Tag>{{"f",new Tag()}},Freeze) };
        var root=new JObject {
            ["roots"]=new JArray(new JObject {["items"]=new JArray(new JObject {["data"]=item})}),
            ["metadata"]=fields.CaptureStrings(new List<string>{"unrelated"}) };
        discardedWorld=new WeakReference(root); fields.Seal();
        fields.Pack(root,default,true,null,0,out var basis,out _);
        return basis;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeDeferredFields Detached(out WeakReference weak,out JObject root)
    {
        var source=new Tag(); weak=new WeakReference(source); var rows=new Dictionary<string,Tag>{{"t",source}};
        var fields=new NativeDeferredFields(); root=new JObject { ["tags"]=fields.CaptureTags(rows,t=>Freeze(t)) }; fields.Seal(); return fields;
    }
    private static byte[] Plain(byte[] data)
    { using var input=new MemoryStream(data,4,data.Length-4,false); using var zip=new System.IO.Compression.GZipStream(input,System.IO.Compression.CompressionMode.Decompress); using var output=new MemoryStream(); zip.CopyTo(output); return output.ToArray(); }
    private static (double Ms,long Bytes) Measure(Action action)
    { for(int i=0;i<3;i++) action(); long before=GC.GetAllocatedBytesForCurrentThread(); var timer=System.Diagnostics.Stopwatch.StartNew(); for(int i=0;i<20;i++) action(); return (timer.Elapsed.TotalMilliseconds/20,(GC.GetAllocatedBytesForCurrentThread()-before)/20); }
    private static void Check(bool value,string message) {if(!value) throw new Exception(message);}
    private static void Reject(Action action,string message)
    {try {action();} catch(InvalidOperationException) {return;} throw new Exception("Accepted "+message);}
}
