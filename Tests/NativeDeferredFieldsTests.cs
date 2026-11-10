using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

internal static class NativeDeferredFieldsTests
{
    private enum Mode : ulong { Last=ulong.MaxValue }
    private sealed class Source
    {
        public string Text="中文😀\n<color=#fff>机器</color>";
        public string Empty=null;
        public char Character='中';
        public bool Enabled=true;
        public byte Byte=255;
        public short Short=short.MinValue;
        public int Count=37;
        public uint Unsigned=uint.MaxValue;
        public long Big=long.MinValue;
        public ulong UBig=ulong.MaxValue;
        public double Heat=12.5;
        public float Fraction=float.PositiveInfinity;
        public decimal Money=decimal.MaxValue;
        public Mode State=Mode.Last;
    }
    private sealed class MutableSource { public List<int> Values=new List<int>{1,2}; }
    private sealed class Business
    {
        public List<string> Names=new(){"a",null,"中文",string.Empty};
        public List<int> Counts=new(){int.MinValue,17,int.MaxValue};
        public Dictionary<string,int> Prices=new(){{"后柜",7},{"前柜",-9}};
        public List<string> Missing=null;
        public Mode State=Mode.Last;
    }
    private sealed class PointerSource { public IntPtr Pointer=IntPtr.Zero; }
    private static readonly FieldInfo[] Contract=typeof(Source).GetFields(BindingFlags.Public|BindingFlags.Instance);

    private static void CheckTypeList()
    {
        var source=new List<string>{"机器","",null,"重复","重复"};
        var expected=new JObject { ["types"]=JArray.FromObject(source),["empty"]=new JArray() };
        foreach(bool materialize in new[]{false,true})
        {
            var list=new List<string>(source);
            var fields=new NativeDeferredFields();
            var root=new JObject { ["types"]=fields.CaptureStrings(list),["empty"]=fields.CaptureStrings(new List<string>()) };
            list.Clear(); fields.Seal();
            Reject(()=>fields.CaptureStrings(list),"string capture after seal");
            if(materialize)
            {
                fields.Materialize(root);
                Check(JToken.DeepEquals(root,expected),"item types changed through materialization");
            }
            else
            {
                var bytes=fields.Pack(root);
                Check(bytes.SequenceEqual(NativeSnapshotBinary.Pack(expected)),"frozen item types changed native wire");
                Check(JToken.DeepEquals(NativeSnapshotBinary.Unpack(bytes),expected),"item type order, nulls or duplicates lost");
            }
        }
    }
    internal static void Run()
    {
        BusinessProjection();
        CheckDeferredQuotes();
        PairedShapeCells();
        NativeTagRowTests.Run();
        CheckByteArrays();
        CheckTooltip();
        CheckShapeRows();
        CheckTypeList();
        CheckStagedItem();
        CheckPlainReferences();
        var directSource=new Source();
        var direct=new NativeDeferredFields();
        var directRoot=new JObject { ["row"]=direct.Capture(directSource,Contract,(source,row)=>{
            for(int i=0;i<Contract.Length;i++) row[i]=Contract[i].GetValue(source);
        }) };
        var expectedRow=new JObject { ["row"]=Immediate(directSource) };
        directSource.Text="mutated"; direct.Seal();
        Check(direct.Pack(directRoot).SequenceEqual(NativeSnapshotBinary.Pack(expectedRow)),"direct field reader lost ordered scalar contract");
        CheckDirectEncoding();
        CheckMaps();
        var source=new Source(); var expected=Immediate(source);
        var deferred=new NativeDeferredFields();
        var root=new JObject { ["fields"]=deferred.Capture(source,Contract) };
        Check(((JObject)root["fields"]).Count==0,"JSON scalar construction remained on capture thread");
        source.Text="changed after capture"; source.Count=999; source.Heat=double.NaN;
        deferred.Seal();
        Reject(()=>deferred.Capture(source,Contract),"capture after ownership transfer");
        Reject(()=>deferred.Seal(),"double seal");
        Task.Run(()=>deferred.Materialize(root)).GetAwaiter().GetResult();
        Check(root["fields"].ToString(Formatting.None)==expected.ToString(Formatting.None),"native values were read after capture or scalar wire changed");
        var oldRoot=new JObject { ["fields"]=expected };
        Check(JToken.DeepEquals(NativeSnapshotCodec.Unpack(NativeSnapshotCodec.Pack(root)),
            NativeSnapshotCodec.Unpack(NativeSnapshotCodec.Pack(oldRoot))),"codec path differs from immediate fields");
        Reject(()=>deferred.Materialize(root),"double materialization");

        var wrongThread=new NativeDeferredFields();
        Task.Run(()=>Reject(()=>wrongThread.Capture(source,Contract),"native read on worker")).GetAwaiter().GetResult();
        Reject(()=>wrongThread.Materialize(new JObject()),"unsealed worker access");
        Reject(()=>wrongThread.Capture(new MutableSource(),typeof(MutableSource).GetFields()),"mutable source escaped to worker");
        Reject(()=>wrongThread.Capture(new PointerSource(),typeof(PointerSource).GetFields()),"native pointer escaped to worker");
        Reject(()=>wrongThread.Capture(source,Enumerable.Repeat(Contract[0],NativeDeferredFields.MaxValues+1).ToArray()),"value budget");

        var modified=new NativeDeferredFields(); var fields=modified.Capture(source,Contract);
        fields["intruder"]=1; modified.Seal();
        Reject(()=>modified.Materialize(new JObject { ["fields"]=fields }),"modified destination");
        var cloned=new NativeDeferredFields(); var original=new JObject { ["fields"]=cloned.Capture(source,Contract) };
        cloned.Seal();
        Reject(()=>cloned.Materialize((JObject)original.DeepClone()),"clone lost deferred destination");

        var cancelled=new NativeDeferredFields(); var cancelledRoot=new JObject { ["fields"]=cancelled.Capture(source,Contract) };
        cancelled.Seal(); using var stop=new CancellationTokenSource(); stop.Cancel();
        try { cancelled.Materialize(cancelledRoot,stop.Token); throw new Exception("cancellation ignored"); }
        catch(OperationCanceledException) { }
        Check(((JObject)cancelledRoot["fields"]).Count==0,"pre-cancel produced partially visible fields");

        var frozen=WithoutRetainingSource(out var weak,out var frozenRoot);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!weak.IsAlive,"deferred worker retained native source object");
        Task.Run(()=>frozen.Materialize(frozenRoot)).GetAwaiter().GetResult();
        Check((int)frozenRoot["fields"]["Count"]==37,"collected owner lost captured values");

        var exclusive=new NativeDeferredFields(); var exclusiveRoot=new JObject { ["fields"]=exclusive.Capture(new Source(),Contract) };
        exclusive.Seal(); int winners=0,refused=0;
        Parallel.For(0,2,_=> {
            try { exclusive.Materialize(exclusiveRoot); Interlocked.Increment(ref winners); }
            catch(InvalidOperationException) { Interlocked.Increment(ref refused); }
        });
        Check(winners==1 && refused==1,"two workers concurrently consumed one snapshot");

        const int blocks=300;
        var old=Measure(()=> { var array=new JArray(); for(int n=0;n<blocks;n++) array.Add(Immediate(source)); });
        // Capture cost only. Worker materialization is checked separately above,
        // not mislabeled as removed work or as measured Unity/Mono frame time.
        var capture=Measure(()=> { var d=new NativeDeferredFields(); var array=new JArray(); for(int n=0;n<blocks;n++) array.Add(d.Capture(source,Contract)); d.Seal(); });
        Console.WriteLine($"PASS deferred native fields: scalar/Unicode parity, immutable capture, worker ownership, mutable/pointer rejection, cancellation, no native retention and codec; .NET9 capture old={old.Ms:F3}ms/{old.Bytes}B deferred={capture.Ms:F3}ms/{capture.Bytes}B ({blocks} blocks; excludes worker)");
    }
    private static void CheckStagedItem()
    {
        var fields=new NativeDeferredFields(workerItems:true);
        var expected=new JObject {
            ["fields"]=new JObject(), ["state"]=new JObject(), ["modified"]=new JObject(),
            ["shape"]=new JObject(), ["modifiedShape"]=new JObject(), ["types"]=new JArray("food"),
            ["features"]=new JArray(), ["content"]=null, ["examine"]=null, ["entity"]=7u,
            ["tooltip"]=new JObject(), ["advanced"]=false, ["mayRemove"]=true, ["maxRemove"]=12,
            ["moveToken"]=(string)null, ["contentsToken"]="proof", ["abilities"]=47, ["owner"]=7u, ["frontendKind"]=2 };
        var row=new NativeDeferredFields.ItemRow { Fields=new JObject(),State=new JObject(),Modified=new JObject(),
            Shape=new JObject(),ModifiedShape=new JObject(),Types=fields.CaptureStrings(new List<string>{"food"}),
            Features=new JArray(),Content=null,Examine=null,Tooltip=new JObject(),Advanced=false,MayRemove=true,MaxRemove=12,Move=null,
            Contents="proof",Abilities=47,Owner=7,FrontendKind=2 };
        var item=fields.StageItem(row); item["entity"]=7u;
        var world=new JObject { ["item"]=item };
        fields.Seal();
        Check(item.Count==1 && fields.CurrentItem(item)==row,"native item materialized before worker");
        var actual=Task.Run(()=>fields.Pack(world)).GetAwaiter().GetResult();
        Check(actual.SequenceEqual(NativeSnapshotBinary.Pack(new JObject {["item"]=expected})),"staged native item field order, null string, ability or ownership mismatch");
        var invalid=new NativeDeferredFields(workerItems:true);
        var changed=invalid.StageItem(new NativeDeferredFields.ItemRow());
        changed["entity"]=7u; changed["frontendKind"]=2;
        invalid.Seal();
        Reject(()=>invalid.Pack(new JObject {["item"]=changed}),"item placeholder metadata changed before assembly");
    }
    private static void CheckPlainReferences()
    {
        (NativeDeferredFields Fields,JObject World) Frame(string type,bool duplicate=false,NativeDeferredFields.MarkerPool pool=null,int frontend=0,string overlay=null)
        {
            var fields=new NativeDeferredFields(workerItems:true){Markers=pool};
            var items=new JArray();
            foreach(uint id in duplicate ? new uint[]{1,1,2} : new uint[]{1,2})
            {
                var item=new NativeDeferredFields.ItemRow {
                    Fields=new JObject(),State=new JObject(),Modified=new JObject(),Shape=new JObject(),ModifiedShape=new JObject(),
                    Types=fields.CaptureStrings(new List<string>{id==1 ? type : "unchanged"}),
                    Features=new JArray(),Tooltip=fields.CaptureTooltip("title",null,"","","",4,5,false),
                    Advanced=false,Owner=id,Contents="bag",Move="slot",FrontendKind=id==1 ? frontend : 0,
                    SpriteModifiers=id==1 && overlay!=null ? new JArray(new JObject {
                        ["image"]=new JObject { ["atlas"]="Items/water_container3",["sprite"]=overlay }
                    }) : null
                };
                var target=fields.StageItem(item); target["entity"]=id;
                items.Add(new JObject { ["data"]=target });
            }
            fields.Seal();
            return (fields,new JObject { ["roots"]=new JArray(new JObject { ["items"]=items }) });
        }
        var first=Frame("old");
        var full=first.Fields.Pack(first.World,default,true,null,0,out var basis,out _);
        // A reviewed callback may be added, replaced or removed while every
        // scalar/tag remains equal. Never reuse the old capability annotation.
        foreach(var transition in new[]{(0,2),(2,3),(3,0)})
        {
            var original=Frame("old",frontend:transition.Item1);
            var source=original.Fields.Pack(original.World,default,true,null,0,out var oldBasis,out _);
            var updated=Frame("old",frontend:transition.Item2);
            var delta=updated.Fields.Pack(updated.World,default,true,oldBasis,8,out _,out int refs);
            Check(refs==1 && updated.Fields.ElidedRows==1,"frontend capability edit reused stale item record");
            var expected=Frame("old",frontend:transition.Item2);
            Check(NativeRecordBasis.ExpandForAudit(ProbablyCoop.Core.NativeRecordFrame.Body(delta,8),source)
                .AsSpan().SequenceEqual(expected.Fields.Pack(expected.World)),"frontend capability transition lost in deferred delta");
        }
        foreach(var transition in new[]{((string)null,"large_blue_100"),("large_blue_100","large_blue_50"),("large_blue_50",null)})
        {
            var original=Frame("old",overlay:transition.Item1);
            var source=original.Fields.Pack(original.World,default,true,null,0,out var oldBasis,out _);
            var updated=Frame("old",overlay:transition.Item2);
            var delta=updated.Fields.Pack(updated.World,default,true,oldBasis,8,out _,out int refs);
            var expanded=NativeRecordBasis.ExpandForAudit(ProbablyCoop.Core.NativeRecordFrame.Body(delta,8),source);
            var record=NativeSnapshotBinary.Unpack(expanded)["roots"][0]["items"][0]["data"];
            Check(refs==1 && (string)record["spriteModifiers"]?[0]["image"]["sprite"]==transition.Item2,
                "item layer appeared, changed, or cleared without invalidating its old record");
        }
        foreach(bool duplicate in new[]{false,true})
        foreach(string type in new[]{"old","edited"})
        {
            var current=Frame(type,duplicate);
            var packet=current.Fields.Pack(current.World,default,true,basis,4,out _,out int refs);
            int expectedRefs=duplicate || type!="old" ? 1 : 2;
            Check(current.Fields.ElidedRows==expectedRefs && refs==expectedRefs,"wrong number of elided complete item rows");
            var expected=Frame(type,duplicate);
            var expanded=NativeRecordBasis.ExpandForAudit(ProbablyCoop.Core.NativeRecordFrame.Body(packet,4),full);
            Check(expanded.AsSpan().SequenceEqual(expected.Fields.Pack(expected.World)),"plain item reference lost data or row order");
        }
        Console.WriteLine("PASS staged native item references: unchanged, edits, duplicate IDs and expanded full binary parity");
        var markers=new NativeDeferredFields.MarkerPool();
        foreach(var type in new[]{"old","changed","old","changed"})
        {
            var frame=Frame(type,pool:markers);
            int reused=frame.Fields.ReusedMarkers;
            var packet=frame.Fields.Pack(frame.World,default,true,basis,4,out _,out _);
            if(type=="changed" && reused==0) throw new Exception("Detached native markers not reused");
            var reference=Frame(type);
            Check(NativeRecordBasis.ExpandForAudit(ProbablyCoop.Core.NativeRecordFrame.Body(packet,4),full).AsSpan()
                .SequenceEqual(reference.Fields.Pack(reference.World)),"reused marker changed live record or pinned basis");
        }
        var keyframe=Frame("rebase",pool:markers);
        var newFull=keyframe.Fields.Pack(keyframe.World,default,true,null,0,out var refreshed,out _);
        Check(markers.Objects.Count==0 && markers.Arrays.Count==0,"checkpoint marker returned to circulation");
        var after=Frame("rebase",pool:markers);
        after.Fields.Pack(after.World,default,true,refreshed,9,out _,out _,auditBasis:newFull);
    }
    private static int quoteCalls;
    private static string QuoteText(JObject values)
    {
        quoteCalls++;
        return values.ToString(Formatting.None);
    }
    private static void CheckDeferredQuotes()
    {
        var cache=new NativeDeferredFields.UpgradeProofCache();
        var contract=typeof(Business).GetFields();
        var upgrade=new Business();
        string Capture(int cash,int favor,bool materialize=false)
        {
            var frozen=new NativeDeferredFields();
            var upgrades=new JObject { ["机械"]=frozen.CaptureBusinessFields(upgrade,contract) };
            var wild=new JObject { ["proof"]=null };
            var root=new JObject { ["wild"]=wild,["upgrades"]=upgrades };
            var fresh=new JObject();
            foreach(var field in contract) fresh[field.Name]=NativeSnapshotCodec.FieldValue(field.GetValue(upgrade));
            var original=new JObject{["cash"]=cash,["favor"]=favor,["upgrades"]=new JObject { ["机械"]=fresh }}.ToString(Formatting.None);
            frozen.DeferUpgradeProof(wild,upgrades,cash,favor,QuoteText,original,cache);
            frozen.Seal();
            if(materialize) frozen.Materialize(root); else root=NativeSnapshotCodec.Unpack(frozen.Pack(root));
            return (string)root["wild"]["proof"];
        }
        quoteCalls=0;
        var first=Capture(505,2);
        if(Capture(505,2,true)!=first || quoteCalls!=1) throw new Exception("Stable quote was recomputed");
        upgrade.Names.Reverse(); var changed=Capture(505,2);
        if(changed==first || Capture(506,2)==changed || Capture(505,3)==changed || quoteCalls!=4)
            throw new Exception("Upgrade, funds or favor did not invalidate cached quote");
        upgrade.Names.Reverse(); Check(Capture(505,2)==first,"reverted quote differs from original");
        Console.WriteLine("PASS authoritative upgrade proofs: frozen fields, funds, favor, ordered nullable lists, root encoding and materialization");
    }
    private static void CheckTooltip()
    {
        var capture=new NativeDeferredFields();
        var root=new JObject { ["tip"]=capture.CaptureTooltip("中文🙂","<b>水</b>",null,"","-\n-",17,3,true) };
        Check(((JObject)root["tip"]).Count==0,"tooltip dictionary expanded on the capture thread");
        var exact=new JObject { ["tip"]=new JObject {
            ["nakedText"]="中文🙂",["richTextForeground"]="<b>水</b>",["richTextBackground"]=JValue.CreateNull(),
            ["richTextUnderline"]="",["richTextStrikethrough"]="-\n-",["recommendedWidth"]=17,["recommendedHeight"]=3,["isTruncated"]=true
        }};
        capture.Seal();
        var packed=Task.Run(()=>capture.Pack(root)).GetAwaiter().GetResult();
        Check(BinaryPlain(packed).SequenceEqual(BinaryPlain(NativeSnapshotBinary.Pack(exact))),
            "worker tooltip changed text, null/empty, size, truncation, or field order");
        Reject(()=>capture.CaptureTooltip("","","","","",0,0,false),"sealed tooltip capture");
    }
    private static void CheckShapeRows()
    {
        static void Fill(byte[] source,Span<byte> into) => source.AsSpan().CopyTo(into);
        static JObject Eager(int x,int y,bool flipped,int rotation,int outside,JToken cells) => new JObject {
            ["x"]=x,["y"]=y,["w"]=2,["h"]=2,["flip"]=flipped,["rotation"]=rotation,["outside"]=outside,["cells"]=cells };

        foreach(bool identical in new[]{true,false})
        foreach(bool materialize in new[]{true,false})
        {
            var cells=new byte[]{1,0,2,255};
            var moved=(byte[])cells.Clone();
            if(!identical) moved[2]=7;
            var expected=new JObject { ["basis"]=Eager(-1,1,false,0,0,new JArray(1,0,2,255)),
                ["placed"]=Eager(7,3,true,2,255,identical ? (JToken)false : new JArray(1,0,7,255)) };
            var fields=new NativeDeferredFields();
            var basis=fields.CaptureShape(cells,-1,1,2,2,false,0,0,Fill,null);
            var placed=fields.CaptureShape(moved,7,3,2,2,true,2,255,Fill,basis);
            var root=new JObject { ["basis"]=basis,["placed"]=placed };
            Check(basis.Count==0 && placed.Count==0,"geometry objects expanded during native capture");
            Array.Clear(cells); Array.Clear(moved); fields.Seal();
            if(materialize)
            {
                Task.Run(()=>fields.Materialize(root)).GetAwaiter().GetResult();
                Check(JToken.DeepEquals(root,expected),"materialized shape transforms or cell values changed");
            }
            else
            {
                var packed=Task.Run(()=>fields.Pack(root)).GetAwaiter().GetResult();
                Check(BinaryPlain(packed).SequenceEqual(BinaryPlain(NativeSnapshotBinary.Pack(expected))),
                    "frozen shape changed wire bytes or reused mutable source");
            }
        }
        var bad=new NativeDeferredFields();
        Reject(()=>bad.CaptureShape(new byte[1],0,0,1,1,false,0,0,Fill,new JObject()),"unrelated frozen shape");
        Reject(()=>bad.CaptureShape(new byte[1],0,0,int.MaxValue,int.MaxValue,false,0,0,Fill,null),"shape too large");
    }
    private static void CheckByteArrays()
    {
        var bytes=Enumerable.Range(0,256).Select(i=>(byte)i).ToArray();
        var expected=new JObject { ["cells"]=new JArray(bytes.Select(b=>new JValue(b))),["empty"]=new JArray(),["fields"]=Immediate(new Source()) };
        var fields=new NativeDeferredFields();
        var root=new JObject { ["cells"]=fields.CaptureBytes(bytes),["empty"]=fields.CaptureBytes(Array.Empty<byte>()),["fields"]=fields.Capture(new Source(),Contract) };
        Array.Fill(bytes,(byte)42); fields.Seal();
        var packed=Task.Run(()=>fields.Pack(root)).GetAwaiter().GetResult();
        Check(BinaryPlain(packed).SequenceEqual(BinaryPlain(NativeSnapshotBinary.Pack(expected))),"frozen bytes changed wire values/order or retained source buffer");
        Check(JToken.DeepEquals(NativeSnapshotBinary.Unpack(packed),NativeSnapshotBinary.Unpack(NativeSnapshotBinary.Pack(expected))),"frozen byte array value types changed");
        Check(((JArray)root["cells"]).Count==0,"frozen bytes allocated JSON nodes");
        Reject(()=>fields.CaptureBytes(bytes),"byte capture after seal");
        Reject(()=>fields.Pack(root),"byte double consumption");
        var materialized=new NativeDeferredFields(); var mr=new JObject { ["a"]=materialized.CaptureBytes(bytes) }; materialized.Seal(); materialized.Materialize(mr);
        Check(JToken.DeepEquals(mr["a"],new JArray(bytes.Select(b=>new JValue(b)))),"byte materialization differs");
        var wrongThread=new NativeDeferredFields();
        Task.Run(()=>Reject(()=>wrongThread.CaptureBytes(bytes),"byte capture from worker")).GetAwaiter().GetResult();
        Reject(()=>wrongThread.CaptureBytes(new byte[NativeDeferredFields.MaxValues+1]),"byte capture budget");
        var combined=new NativeDeferredFields(); combined.Capture(new Source(),Contract);
        Reject(()=>combined.CaptureBytes(new byte[NativeDeferredFields.MaxValues]),"mixed field/array budget");
        foreach(bool clone in new[]{false,true})
        {
            var invalid=new NativeDeferredFields(); var r=new JObject { ["a"]=invalid.CaptureBytes(bytes) }; invalid.Seal();
            if(clone) r=(JObject)r.DeepClone(); else ((JArray)r["a"]).Add(1);
            Reject(()=>invalid.Pack(r),"frozen array clone/modified placeholder");
        }
        var deep=new NativeDeferredFields(); var dr=new JObject(); var tail=dr;
        for(int i=0;i<63;i++) { var next=new JObject(); tail["n"]=next; tail=next; }
        tail["a"]=deep.CaptureBytes(new byte[]{1}); deep.Seal();
        RejectData(()=>deep.Pack(dr),"frozen array virtual depth");
        var huge=new NativeDeferredFields(); var hr=new JObject { ["a"]=huge.CaptureBytes(new byte[NativeDeferredFields.MaxValues]) }; huge.Seal();
        RejectData(()=>huge.Pack(hr),"frozen array virtual node count");
        var cancelled=new NativeDeferredFields(); var cr=new JObject { ["a"]=cancelled.CaptureBytes(bytes) }; cancelled.Seal();
        using(var stop=new CancellationTokenSource()) {
            stop.Cancel(); try { cancelled.Pack(cr,stop.Token); throw new Exception("array cancellation ignored"); } catch(OperationCanceledException) { }
        }
        var cells=new byte[10000];
        var old=Measure(()=> {var a=new JArray(); foreach(byte cell in cells) a.Add(cell);});
        var nextCapture=Measure(()=> {var d=new NativeDeferredFields(); d.CaptureBytes(cells); d.Seal();});
        Check(nextCapture.Bytes<old.Bytes/4,"frozen cells failed to reduce allocation");
        Console.WriteLine($"PASS frozen shape cells: exact mixed wire/values, source isolation, thread/seal, identity, cancellation and virtual depth/node budgets; .NET9 10000 cells capture JSON={old.Ms:F3}ms/{old.Bytes}B frozen={nextCapture.Ms:F3}ms/{nextCapture.Bytes}B (encoding excluded)");
    }
    private static void CheckDirectEncoding()
    {
        // Two equal empty placeholders must be addressed by identity, not value.
        var rows=new Dictionary<string,Source> { ["tag😀"]=new Source(),[""]=new Source { Count=-1,Heat=double.NaN,Fraction=float.NegativeInfinity } };
        var expected=new JObject { ["before"]=new JArray(true,new JValue((string)null),"tag😀") };
        var expectedRows=new JObject(); foreach(var pair in rows) expectedRows.Add(pair.Key,Immediate(pair.Value));
        expected["map"]=expectedRows; expected["fields"]=Immediate(new Source { Count=999 }); expected["empty"]=new JObject();
        var fields=new NativeDeferredFields();
        var root=new JObject { ["before"]=expected["before"].DeepClone(),["map"]=fields.CaptureMap(rows,Contract),
            ["fields"]=fields.Capture(new Source { Count=999 },Contract),["empty"]=fields.CaptureMap(new Dictionary<string,Source>(),Contract) };
        rows["tag😀"].Count=100; rows.Clear(); fields.Seal();
        var direct=Task.Run(()=>fields.Pack(root)).GetAwaiter().GetResult();
        var ordinary=NativeSnapshotBinary.Pack(expected);
        Check(BinaryPlain(direct).SequenceEqual(BinaryPlain(ordinary)),"direct field encoding differs byte-for-byte from materialized encoder");
        Check(JToken.DeepEquals(NativeSnapshotBinary.Unpack(direct),NativeSnapshotBinary.Unpack(ordinary)),"direct encoding changed value types");
        Check(((JObject)root["map"]).Count==0 && ((JObject)root["fields"]).Count==0,"direct encoding materialized JSON nodes");
        Reject(()=>fields.Pack(root),"double direct consumption"); Reject(()=>fields.Materialize(root),"materialize after direct consumption");
        var unsealed=new NativeDeferredFields(); Reject(()=>unsealed.Pack(new JObject()),"unsealed direct access");
        Reject(()=>unsealed.Capture(new Source(),new[]{Contract[0],Contract[0]}),"duplicate field names");
        foreach(bool clone in new[]{false,true})
        {
            var invalid=new NativeDeferredFields(); var invalidRoot=new JObject { ["f"]=invalid.Capture(new Source(),Contract) }; invalid.Seal();
            if(clone) invalidRoot=(JObject)invalidRoot.DeepClone(); else invalidRoot["f"]["intruder"]=1;
            Reject(()=>invalid.Pack(invalidRoot),"direct clone/modified destination");
        }
        var cancelled=new NativeDeferredFields(); var cancelledRoot=new JObject { ["f"]=cancelled.Capture(new Source(),Contract) }; cancelled.Seal();
        using(var stop=new CancellationTokenSource())
        {
            stop.Cancel(); try { cancelled.Pack(cancelledRoot,stop.Token); throw new Exception("direct cancellation ignored"); } catch(OperationCanceledException) { }
        }
        Reject(()=>cancelled.Pack(cancelledRoot),"cancelled capture reused");
        var collected=MapWithoutNativeSource(out var weak,out var collectedRoot);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Check(!weak.IsAlive,"direct encoding retains native owner");
        Check((int)NativeSnapshotBinary.Unpack(collected.Pack(collectedRoot))["map"]["tag"]["Count"]==37,"direct collected owner data lost");
        var exclusive=new NativeDeferredFields(); var exclusiveRoot=new JObject { ["f"]=exclusive.Capture(new Source(),Contract) }; exclusive.Seal();
        int winners=0,refused=0;
        Parallel.For(0,2,i=> { try { if(i==0) exclusive.Pack(exclusiveRoot); else exclusive.Materialize(exclusiveRoot); Interlocked.Increment(ref winners); }
            catch(InvalidOperationException) { Interlocked.Increment(ref refused); } });
        Check(winners==1 && refused==1,"direct and materialize consumed same snapshot");
        // Budgets must count the virtual properties/rows, not just placeholders.
        var deep=new NativeDeferredFields(); var deepRoot=new JObject(); var tail=deepRoot;
        for(int i=0;i<64;i++) { var next=new JObject(); tail["n"]=next; tail=next; }
        tail["f"]=deep.Capture(new Source(),Contract); deep.Seal();
        RejectData(()=>deep.Pack(deepRoot),"virtual field depth");
        var oversized=new NativeDeferredFields(); var hugeRows=Enumerable.Range(0,18000).ToDictionary(i=>"t"+i,i=>new Source());
        var oversizedRoot=new JObject { ["map"]=oversized.CaptureMap(hugeRows,Contract) }; oversized.Seal();
        RejectData(()=>oversized.Pack(oversizedRoot),"virtual node budget");
        var expanded=new NativeDeferredFields(); var repeated=new string('x',100000);
        var expandedRoot=new JObject { ["map"]=expanded.CaptureMap(Enumerable.Range(0,200).ToDictionary(i=>"t"+i,i=>new Source {Text=repeated}),Contract) }; expanded.Seal();
        RejectData(()=>expanded.Pack(expandedRoot),"virtual repeated-string expanded budget");
        var many=Enumerable.Range(0,500).ToDictionary(i=>"tag"+i,i=>new Source {Count=i});
        var old=MeasureEncoding(many,false); var streaming=MeasureEncoding(many,true);
        Check(streaming.Bytes<old.Bytes,"direct encoder did not reduce allocations");
        Console.WriteLine($"PASS direct frozen encoding: exact wire bytes/types/order, source lifetime, identity, single consumption, cancellation and virtual depth/nodes/expanded budgets; .NET9 worker 500 rows materialize={old.Ms:F3}ms/{old.Bytes}B direct={streaming.Ms:F3}ms/{streaming.Bytes}B (capture excluded)");
    }
    private static byte[] BinaryPlain(byte[] packed)
    {
        using var input=new MemoryStream(packed,4,packed.Length-4,false);
        using var gzip=new System.IO.Compression.GZipStream(input,System.IO.Compression.CompressionMode.Decompress);
        using var output=new MemoryStream(); gzip.CopyTo(output); return output.ToArray();
    }
    private static void RejectData(Action action,string message)
    { try { action(); } catch(InvalidDataException) { return; } throw new Exception("Accepted "+message); }
    private static (double Ms,long Bytes) MeasureEncoding(Dictionary<string,Source> source,bool direct)
    {
        long ticks=0,bytes=0;
        for(int i=0;i<21;i++)
        {
            var fields=new NativeDeferredFields(); var root=new JObject { ["map"]=fields.CaptureMap(source,Contract) }; fields.Seal();
            long before=GC.GetAllocatedBytesForCurrentThread(),start=System.Diagnostics.Stopwatch.GetTimestamp();
            if(direct) fields.Pack(root); else { fields.Materialize(root); NativeSnapshotBinary.Pack(root); }
            if(i>0) { ticks+=System.Diagnostics.Stopwatch.GetTimestamp()-start; bytes+=GC.GetAllocatedBytesForCurrentThread()-before; }
        }
        return ((double)ticks/System.Diagnostics.Stopwatch.Frequency*1000/20,bytes/20);
    }
    private static void CheckMaps()
    {
        var source=new Dictionary<string,Source> { ["中文😀"]=new Source(),[""]=new Source { Count=-1,Heat=double.NegativeInfinity } };
        var expected=new JObject(); foreach(var pair in source) expected.Add(pair.Key,Immediate(pair.Value));
        var deferred=new NativeDeferredFields(); var root=new JObject { ["map"]=deferred.CaptureMap(source,Contract),["fields"]=deferred.Capture(new Source(),Contract) };
        Check(((JObject)root["map"]).Count==0,"map rows constructed on capture thread");
        source["中文😀"].Count=100; source[""].Text="changed"; source.Clear();
        deferred.Seal(); Task.Run(()=>deferred.Materialize(root)).GetAwaiter().GetResult();
        Check(root["map"].ToString(Formatting.None)==expected.ToString(Formatting.None),"table order/scalars changed or mutable source retained");
        Check(JToken.DeepEquals(NativeSnapshotCodec.Unpack(NativeSnapshotBinary.Pack(root))["map"],expected),"map binary roundtrip differs");
        Reject(()=>deferred.CaptureMap(source,Contract),"map capture after seal");
        var wrongThread=new NativeDeferredFields();
        Task.Run(()=>Reject(()=>wrongThread.CaptureMap(source,Contract),"map native read on worker")).GetAwaiter().GetResult();
        Reject(()=>wrongThread.CaptureMap(new Dictionary<string,MutableSource>{{"x",new MutableSource()}},typeof(MutableSource).GetFields()),"mutable map source escaped");
        Reject(()=>wrongThread.CaptureMap(new Dictionary<string,PointerSource>{{"x",new PointerSource()}},typeof(PointerSource).GetFields()),"pointer map escaped");
        Reject(()=>wrongThread.CaptureMap(new Dictionary<string,Source>{{"x",null}},Contract),"null map row accepted");
        Reject(()=>wrongThread.CaptureMap(new Dictionary<string,Source>{{"x",new Source()}},Array.Empty<FieldInfo>()),"empty map contract accepted");
        Reject(()=>wrongThread.CaptureMap(new Dictionary<string,Source>{{"x",new Source()}},Enumerable.Repeat(Contract[0],NativeDeferredFields.MaxValues+1).ToArray()),"map value budget");
        var empty=new NativeDeferredFields(); var emptyRoot=new JObject { ["map"]=empty.CaptureMap(source,Contract) }; empty.Seal(); empty.Materialize(emptyRoot);
        Check(((JObject)emptyRoot["map"]).Count==0,"empty map invented rows");
        var modified=new NativeDeferredFields(); var modifiedMap=modified.CaptureMap(source,Contract); modifiedMap["intruder"]=1; modified.Seal();
        Reject(()=>modified.Materialize(new JObject { ["map"]=modifiedMap }),"modified map destination");
        var frozen=MapWithoutNativeSource(out var weak,out var frozenRoot);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!weak.IsAlive,"deferred map retained native dictionary/rows");
        Task.Run(()=>frozen.Materialize(frozenRoot)).GetAwaiter().GetResult();
        Check((int)frozenRoot["map"]["tag"]["Count"]==37,"map source collection lost frozen data");
        var cancelled=new NativeDeferredFields(); var cancelledRoot=new JObject { ["map"]=cancelled.CaptureMap(new Dictionary<string,Source>{{"x",new Source()}},Contract) };
        cancelled.Seal(); using var stop=new CancellationTokenSource(); stop.Cancel();
        try { cancelled.Materialize(cancelledRoot,stop.Token); throw new Exception("map cancellation ignored"); } catch(OperationCanceledException) { }
        Check(((JObject)cancelledRoot["map"]).Count==0,"cancelled map changed destination");
        var many=Enumerable.Range(0,500).ToDictionary(i=>"tag"+i,i=>new Source { Count=i });
        var old=Measure(()=> { var d=new NativeDeferredFields(); var map=new JObject(); foreach(var pair in many) map.Add(pair.Key,d.Capture(pair.Value,Contract)); d.Seal(); });
        var table=Measure(()=> { var d=new NativeDeferredFields(); var map=d.CaptureMap(many,Contract); d.Seal(); });
        Console.WriteLine($"PASS deferred field maps: exact ordered scalars, immutable transfer, bounds, empty/invalid rows, thread gate, cancellation and native lifetime; .NET9 capture 500 rows old={old.Ms:F3}ms/{old.Bytes}B table={table.Ms:F3}ms/{table.Bytes}B (worker excluded)");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeDeferredFields MapWithoutNativeSource(out WeakReference weak,out JObject root)
    {
        var source=new Dictionary<string,Source>{{"tag",new Source()}};
        weak=new WeakReference(source["tag"]); var deferred=new NativeDeferredFields();
        root=new JObject { ["map"]=deferred.CaptureMap(source,Contract) }; deferred.Seal(); return deferred;
    }
    private static JObject Immediate(Source source)
    {
        var result=new JObject();
        foreach(var field in Contract) result[field.Name]=NativeSnapshotCodec.Scalar(field.GetValue(source));
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeDeferredFields WithoutRetainingSource(out WeakReference weak,out JObject root)
    {
        var source=new Source(); weak=new WeakReference(source); var deferred=new NativeDeferredFields();
        root=new JObject { ["fields"]=deferred.Capture(source,Contract) }; deferred.Seal(); return deferred;
    }
    private static (double Ms,long Bytes) Measure(Action action)
    {
        action(); long before=GC.GetAllocatedBytesForCurrentThread(); var timer=System.Diagnostics.Stopwatch.StartNew();
        const int repetitions=20; for(int i=0;i<repetitions;i++) action();
        return (timer.Elapsed.TotalMilliseconds/repetitions,(GC.GetAllocatedBytesForCurrentThread()-before)/repetitions);
    }
    private static void BusinessProjection()
    {
        var source=new Business();
        var fields=typeof(Business).GetFields();
        var expected=new JObject();
        foreach(var field in fields) expected[field.Name]=NativeSnapshotCodec.FieldValue(field.GetValue(source));
        var baseline=new JObject { ["row"]=expected };
        var encoded=new NativeDeferredFields(); var materialized=new NativeDeferredFields();
        var a=new JObject { ["row"]=encoded.CaptureBusinessFields(source,fields) };
        var b=new JObject { ["row"]=materialized.CaptureBusinessFields(source,fields) };
        encoded.Seal(); materialized.Seal(); source.Prices.Clear(); source.Names.Reverse(); source.Counts[0]=42;
        materialized.Materialize(b);
        Check(JToken.DeepEquals(b,baseline),"materialized business values changed after capture");
        var wire=encoded.Pack(a);
        Check(JToken.DeepEquals(NativeSnapshotBinary.Unpack(wire),NativeSnapshotBinary.Unpack(NativeSnapshotBinary.Pack(baseline))),"frozen business wire differs from original");
        source.Counts=new List<int>(new int[NativeDeferredFields.MaxValues]);
        Reject(()=>new NativeDeferredFields().CaptureBusinessFields(source,fields),"oversized business list");
        Reject(()=>new NativeDeferredFields().CaptureBusinessFields(new PointerSource(),typeof(PointerSource).GetFields()),"pointer in business projection");
        Console.WriteLine("PASS frozen business lists/maps: original JSON, null entries, native order, immutable capture and shared value budget");
    }
    private static void PairedShapeCells()
    {
        static void Copy(byte[] source,Span<byte> destination) => source.CopyTo(destination);
        foreach(bool equal in new[]{true,false})
        {
            var sample=new byte[]{0,1,255};
            var placed=(byte[])sample.Clone();
            if(!equal) placed[2]=128;
            var fields=new NativeDeferredFields();
            var shape=(JArray)fields.CaptureCells(sample,sample.Length,Copy);
            var other=fields.CaptureCells(placed,placed.Length,Copy,shape);
            var root=new JObject { ["base"]=shape,["placed"]=other };
            Array.Fill<byte>(sample,6); Array.Fill<byte>(placed,6); fields.Seal();
            var data=NativeSnapshotBinary.Unpack(fields.Pack(root));
            Check(data["base"].ToObject<byte[]>().SequenceEqual(new byte[]{0,1,255}),"owned base shape changed");
            if(equal) Check(data["placed"].Type==JTokenType.Boolean && !(bool)data["placed"],"matching cells expanded");
            else Check(data["placed"].ToObject<byte[]>().SequenceEqual(new byte[]{0,1,128}),"differing cells collapsed");
        }
        var bad=new NativeDeferredFields();
        Reject(()=>bad.CaptureCells(new byte[1],1,Copy,new JArray()),"unrelated shape basis");
    }
    private static void Reject(Action action,string message)
    { try { action(); } catch(InvalidOperationException) { return; } throw new Exception("Accepted "+message); }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
}
