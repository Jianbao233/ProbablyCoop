using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// Capture native scalar values on the game thread, then transfer ownership to
// the encoding worker. Blocks retain only immutable strings/boxed value types,
// fixed field names and their detached JSON destination, never native objects.
internal sealed class NativeDeferredFields
{
    internal const int MaxValues=500000;
    private readonly int totalBudget, recordBudget;
    private int RemainingValues=>Math.Min(MaxValues,totalBudget-values);
    private sealed class Contract
    {
        internal readonly string[] Names;
        internal Contract(FieldInfo[] fields,bool collections=false)
        {
            Names=new string[fields.Length];
            var names=new HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<fields.Length;i++)
            {
                var field=fields[i]; var type=field.FieldType;
                bool collection=collections && (type==typeof(List<string>) || type==typeof(List<int>) || type==typeof(Dictionary<string,int>));
                if(field.IsStatic || !(collection || type.IsPrimitive || type.IsEnum || type==typeof(string) || type==typeof(decimal)))
                    throw new InvalidOperationException("Deferred field contract must contain instance scalars only");
                // IntPtr/UIntPtr are primitive too, but not serializable state.
                if(type==typeof(IntPtr) || type==typeof(UIntPtr))
                    throw new InvalidOperationException("Native pointers cannot enter deferred fields");
                Names[i]=field.Name;
                if(!names.Add(field.Name)) throw new InvalidOperationException("Duplicate deferred field name");
            }
        }
    }
    internal sealed class Block
    {
        internal JContainer Target;
        internal string[] Names;
        internal object[] Values;
        internal string[] Keys;
        internal NativeTagRow[] TagRows;
        internal int[] TagColumns;
        internal byte[] Bytes;
        internal string[] Strings;
        internal Block TagBasis;
        internal bool InheritsRow(int row) => TagBasis!=null && row<TagBasis.Keys.Length &&
            Keys[row]==TagBasis.Keys[row] && TagRows[row].SameValues(in TagBasis.TagRows[row]);
    }
    private static readonly ConditionalWeakTable<FieldInfo[],Contract> Contracts=new ConditionalWeakTable<FieldInfo[],Contract>();
    private static readonly ConditionalWeakTable<FieldInfo[],Contract> CollectionContracts=new ConditionalWeakTable<FieldInfo[],Contract>();
    private readonly List<Block> blocks=new List<Block>();
    private readonly int ownerThread=Thread.CurrentThread.ManagedThreadId;
    private int values;
    private bool sealedCapture;
    private int consumed;
    private JObject proofTarget,upgradeRows;
    private int proofCash,proofFavor;
    private string proofOracle;
    private Func<JObject,string> hashProof;
    internal sealed class UpgradeProofCache
    {
        private int cash,favor;
        private string result;
        private (string Key,string[] Names,object[] Values)[] rows;
        internal string Match(JObject current,Dictionary<JToken,Block> lookup,int newCash,int newFavor)
        {
            if(result==null || cash!=newCash || favor!=newFavor || current.Count!=rows.Length) return null;
            int index=0;
            foreach(var entry in current.Properties())
            {
                var old=rows[index++];
                if(entry.Name!=old.Key || !lookup.TryGetValue(entry.Value,out var block) ||
                    !ReferenceEquals(block.Names,old.Names) || block.Values?.Length!=old.Values.Length) return null;
                for(int i=0;i<old.Values.Length;i++)
                    if(!NativeRecordBasis.ScalarEqual(old.Values[i],block.Values[i])) return null;
            }
            return result;
        }
        internal void Remember(JObject current,Dictionary<JToken,Block> lookup,int newCash,int newFavor,string proof)
        {
            var saved=new (string,string[],object[])[current.Count];
            int index=0;
            foreach(var entry in current.Properties())
            {
                var block=lookup[entry.Value];
                saved[index++]=(entry.Name,block.Names,block.Values);
            }
            rows=saved; cash=newCash; favor=newFavor; result=proof;
        }
    }
    private UpgradeProofCache proofCache;
    // Use the same captured upgrade fields for display and the stale purchase
    // check. The worker receives only immutable values and a static hash function.
    internal void DeferUpgradeProof(JObject target,JObject upgrades,int cash,int favor,Func<JObject,string> hash,string oracle,UpgradeProofCache cache)
    {
        RequireCaptureThread();
        if(sealedCapture || proofTarget!=null || target==null || upgrades==null || hash?.Target!=null || hash==null)
            throw new InvalidOperationException("Invalid deferred upgrade proof");
        proofTarget=target; upgradeRows=upgrades; proofCash=cash; proofFavor=favor; hashProof=hash; proofOracle=oracle; proofCache=cache;
    }

    private void FinishUpgradeProof(Dictionary<JToken,Block> lookup,CancellationToken cancellation)
    {
        if(proofTarget==null) return;
        string proof=proofCache?.Match(upgradeRows,lookup,proofCash,proofFavor);
        if(proof==null)
        {
            proof=HashUpgradeRows(lookup,cancellation);
            proofCache?.Remember(upgradeRows,lookup,proofCash,proofFavor,proof);
        }
        if(proofOracle!=null && proof!=proofOracle) throw new InvalidDataException("Worker upgrade quote differs from authority");
        proofTarget["proof"]=proof;
        proofTarget=upgradeRows=null; hashProof=null; proofOracle=null; proofCache=null;
    }
    private string HashUpgradeRows(Dictionary<JToken,Block> lookup,CancellationToken cancellation)
    {
        var upgrades=new JObject();
        foreach(var entry in upgradeRows.Properties())
        {
            cancellation.ThrowIfCancellationRequested();
            if(!lookup.TryGetValue(entry.Value,out var block) || block.Values==null || block.Names.Length!=block.Values.Length)
                throw new InvalidOperationException("Upgrade fields were not frozen");
            var row=new JObject();
            for(int i=0;i<block.Names.Length;i++) row.Add(block.Names[i],ThawField(block.Values[i]));
            upgrades.Add(entry.Name,row);
        }
        return hashProof(new JObject { ["cash"]=proofCash,["favor"]=proofFavor,["upgrades"]=upgrades });
    }
    // Owned by one authority. Weak keys allow destroyed native tables and
    // shapes to disappear; the values never point back to game objects.
    internal sealed class StockCache
    {
        internal sealed class TagTable
        {
            internal string[] Keys;
            internal NativeTagRow[] Rows;
        }
        internal sealed class CellTable { internal byte[] Bytes; }
        internal sealed class FieldTable { internal object[] Values; internal FieldInfo[] Contract; }
        internal readonly ConditionalWeakTable<object,TagTable> Tags=new ConditionalWeakTable<object,TagTable>();
        internal readonly ConditionalWeakTable<object,CellTable> Shapes=new ConditionalWeakTable<object,CellTable>();
        internal readonly ConditionalWeakTable<object,FieldTable> ItemFields=new ConditionalWeakTable<object,FieldTable>();
        internal byte[] Scratch=Array.Empty<byte>();
        internal bool ReadingShape;
    }
    private readonly StockCache tagCache,shapeCache,fieldCache;
    internal bool HasItemFieldCache=>fieldCache!=null;
    // The capture thread resolves a sprite into an exclusively owned data-only
    // identity. Layers retain no native renderer, callback, or Unity value.
    internal sealed class SpriteLayer
    {
        internal readonly JObject Image;
        internal readonly float X,Y,R,G,B,A;
        internal readonly int Rotation,Priority;
        internal readonly bool Flip,Mask;
        private int consumed;
        internal SpriteLayer(JObject image,float x,float y,int rotation,bool flip,int priority,bool mask,
            float r,float g,float b,float a)
        {
            if(image==null || image.Parent!=null) throw new InvalidOperationException("Sprite identity must be exclusively owned");
            Image=image; X=x; Y=y; Rotation=rotation; Flip=flip; Priority=priority; Mask=mask;
            R=r; G=g; B=b; A=a;
        }
        internal JObject Materialize()
        {
            if(Interlocked.Exchange(ref consumed,1)!=0 || Image.Parent!=null)
                throw new InvalidOperationException("Frozen sprite layer was already consumed");
            return new JObject {
                ["image"]=Image,["x"]=X,["y"]=Y,["rotation"]=Rotation,["flip"]=Flip,
                ["priority"]=Priority,["mask"]=Mask,["color"]=new JArray(R,G,B,A)
            };
        }
    }
    // Only the item values are collected on Unity's thread. Construct the
    // ordered JSON properties on the encoding worker after ownership transfer.
    internal sealed class ItemRow
    {
        internal readonly JObject Target;
        internal ItemRow(JObject target=null) { Target=target ?? new JObject(); }
        internal JObject Fields,State,Modified,Shape,ModifiedShape,Content,Examine;
        internal JArray Types,Features,SpriteModifiers;
        internal SpriteLayer[] FrozenSpriteModifiers;
        internal JToken Tooltip,Advanced;
        internal uint Owner;
        internal bool MayRemove;
        internal int MaxRemove,Abilities,FrontendKind;
        internal string Move,Contents;
        internal bool IsPlain=>Content==null && Examine==null && SpriteModifiers==null && FrozenSpriteModifiers==null && Features?.Count==0 && Fields!=null && State!=null &&
            Modified!=null && Shape!=null && ModifiedShape!=null && Types!=null && Tooltip!=null && Advanced!=null;
        internal void AddComponentRoots(HashSet<JToken> roots)
        {
            roots.Add(Fields); roots.Add(State); roots.Add(Modified); roots.Add(Shape); roots.Add(ModifiedShape);
            roots.Add(Types); roots.Add(Features); roots.Add(Tooltip); roots.Add(Advanced);
        }

        internal void Assemble()
        {
            var entity=Target.Property("entity");
            if(entity==null || Target.Count!=1) throw new InvalidOperationException("Native item identity was modified");
            if(FrozenSpriteModifiers!=null)
            {
                if(SpriteModifiers!=null || FrozenSpriteModifiers.Length>128)
                    throw new InvalidOperationException("Invalid frozen native sprite layers");
                var modifiers=new JArray();
                foreach(var layer in FrozenSpriteModifiers)
                {
                    if(layer==null) throw new InvalidOperationException("Missing frozen native sprite layer");
                    modifiers.Add(layer.Materialize());
                }
                SpriteModifiers=modifiers;
            }
            entity.Remove();
            Target.Add("fields",Fields); Target.Add("state",State); Target.Add("modified",Modified);
            Target.Add("shape",Shape); Target.Add("modifiedShape",ModifiedShape); Target.Add("types",Types);
            Target.Add("features",Features); Target.Add("content",Content); Target.Add("examine",Examine);
            if(SpriteModifiers!=null) Target.Add("spriteModifiers",SpriteModifiers);
            Target.Add(entity);
            if(Tooltip!=null)
            {
                Target.Add("tooltip",Tooltip); Target.Add("advanced",Advanced);
                Target.Add("mayRemove",MayRemove); Target.Add("maxRemove",MaxRemove);
                Target.Add("moveToken",Move); Target.Add("contentsToken",Contents);
                Target.Add("abilities",Abilities);
            }
            if(Owner!=0) Target.Add("owner",Owner);
            if(FrontendKind!=0) Target.Add("frontendKind",FrontendKind);
        }
    }
    private readonly List<ItemRow> itemRows=new List<ItemRow>();
    private readonly Dictionary<JObject,uint> borrowedRecords=new Dictionary<JObject,uint>();
    private readonly HashSet<JObject> borrowedNested=new HashSet<JObject>();
    private NativeRecordBasis borrowedBasis;
    internal readonly HashSet<uint> ConfirmedRecords=new HashSet<uint>();
    internal readonly HashSet<uint> AuditedRecords=new HashSet<uint>();
    internal readonly HashSet<uint> UntrackedRecords=new HashSet<uint>();
    internal JObject BorrowRecord(NativeRecordBasis basis,uint id,bool nested=false)
    {
        RequireCaptureThread();
        if(sealedCapture || basis==null || !basis.Contains(id) || borrowedBasis!=null && !ReferenceEquals(borrowedBasis,basis))
            throw new InvalidOperationException("Invalid native capture basis");
        if(borrowedRecords.Count>=recordBudget) throw new InvalidOperationException("Native record budget exceeded");
        borrowedBasis=basis;
        var marker=new JObject { ["entity"]=id }; borrowedRecords.Add(marker,id);
        if(nested) borrowedNested.Add(marker);
        return marker;
    }
    internal int CapturedItemRows=>itemRows.Count;
    internal bool WorkerItems { get; }
    internal bool EarlyPlain { get; set; }=true;
    internal int ElidedRows { get; private set; }
    // One capture owns the pool from the start of native reads until its
    // worker finishes. Only detached, empty markers of elided delta rows
    // can return. Checkpoints and materialized nodes keep their own objects.
    internal sealed class MarkerPool
    {
        internal readonly Stack<JObject> Objects=new Stack<JObject>();
        internal readonly Stack<JArray> Arrays=new Stack<JArray>();
        internal void Reclaim(JContainer marker)
        {
            if(marker.Parent!=null || marker.Count!=0) return;
            if(marker is JObject obj && Objects.Count<2048) Objects.Push(obj);
            else if(marker is JArray array && Arrays.Count<512) Arrays.Push(array);
        }
    }
    internal MarkerPool Markers { get; set; }
    internal int ReusedMarkers { get; private set; }
    private JObject ObjectMarker()
    {
        if(Markers==null || Markers.Objects.Count==0) return new JObject();
        ReusedMarkers++;
        return Markers.Objects.Pop();
    }
    private JArray ArrayMarker()
    {
        if(Markers==null || Markers.Arrays.Count==0) return new JArray();
        ReusedMarkers++;
        return Markers.Arrays.Pop();
    }
    internal JObject StageItem(ItemRow row)
    {
        RequireCaptureThread();
        if(!WorkerItems || sealedCapture || itemRows.Count>=recordBudget) throw new InvalidOperationException("Invalid staged item");
        itemRows.Add(row);
        return row.Target;
    }
    internal ItemRow CurrentItem(JObject target) =>
        itemRows.Count!=0 && ReferenceEquals(itemRows[itemRows.Count-1].Target,target) ? itemRows[itemRows.Count-1] : null;
    private void AssembleItems(CancellationToken cancellation)
    {
        foreach(var row in itemRows) { cancellation.ThrowIfCancellationRequested(); row.Assemble(); }
        itemRows.Clear();
    }
    internal bool PresentationRows { get; }
    internal NativeDeferredFields(bool presentationRows=true,StockCache tagCache=null,StockCache shapeCache=null,
        bool workerItems=false,StockCache fieldCache=null,bool pagedSnapshots=false)
    {
        PresentationRows=presentationRows; this.tagCache=tagCache; this.shapeCache=shapeCache;
        WorkerItems=workerItems; this.fieldCache=fieldCache;
        // A capture may span pages, while each individual copied component
        // remains bounded just as before. The encoder also checks each page
        // and the complete checkpoint's actual expansion cost.
        totalBudget=pagedSnapshots ? NativeSnapshotPages.MaxNodes : MaxValues;
        recordBudget=pagedSnapshots ? NativeSnapshotPages.MaxRecords : MaxValues;
    }
    private static readonly string[] TooltipNames={"nakedText","richTextForeground","richTextBackground","richTextUnderline",
        "richTextStrikethrough","recommendedWidth","recommendedHeight","isTruncated"};
    private static readonly string[] ShapeNames={"x","y","w","h","flip","rotation","outside","cells"};

    internal JObject CaptureTooltip(string text,string foreground,string background,string underline,string strikethrough,
        int width,int height,bool truncated)
    {
        RequireCaptureThread();
        if(sealedCapture || TooltipNames.Length>RemainingValues) throw new InvalidOperationException("Invalid deferred tooltip");
        // Strings are immutable; the worker owns these scalar values and
        // never needs to touch the native item, renderer, or RichText object.
        var target=ObjectMarker();
        blocks.Add(new Block { Target=target,Names=TooltipNames,
            Values=new object[]{text,foreground,background,underline,strikethrough,width,height,truncated} });
        values+=TooltipNames.Length;
        return target;
    }

    internal JObject Capture(object source,FieldInfo[] fields) => Capture<object>(source,fields,null);
    internal JObject CaptureCachedFields<T>(T source,FieldInfo[] fields,Func<T,object[],object[]> read) where T:class
    {
        RequireCaptureThread();
        if(sealedCapture || fieldCache==null || source==null || fields==null || fields.Length>RemainingValues)
            throw new InvalidOperationException("Invalid immutable item field capture");
        var contract=Contracts.GetValue(fields,key=>new Contract(key));
        var table=fieldCache.ItemFields.GetOrCreateValue(source);
        var frozen=read(source,ReferenceEquals(table.Contract,fields) ? table.Values : null);
        if(frozen==null || frozen.Length!=fields.Length) throw new InvalidOperationException("Invalid item field row");
        table.Values=frozen;
        table.Contract=fields;
        var target=ObjectMarker();
        blocks.Add(new Block { Target=target,Names=contract.Names,Values=frozen });
        values+=fields.Length;
        return target;
    }
    internal JObject Capture<T>(T source,FieldInfo[] fields,Action<T,object[]> read) where T:class
    {
        RequireCaptureThread();
        if(sealedCapture) throw new InvalidOperationException("Deferred capture already sealed");
        if(source==null || fields==null) throw new ArgumentNullException();
        if(fields.Length>RemainingValues) throw new InvalidOperationException("Deferred field budget exceeded");
        var contract=Contracts.GetValue(fields,key=>new Contract(key));
        var frozen=new object[fields.Length];
        if(read==null)
            for(int i=0;i<fields.Length;i++) frozen[i]=fields[i].GetValue(source);
        else read(source,frozen);
        var target=ObjectMarker();
        blocks.Add(new Block { Target=target,Names=contract.Names,Values=frozen });
        values+=fields.Length;
        return target;
    }
    internal JObject CaptureBusinessFields(object source,FieldInfo[] fields)
    {
        RequireCaptureThread();
        if(sealedCapture || source==null || fields==null || fields.Length>RemainingValues)
            throw new InvalidOperationException("Invalid deferred business fields");
        var contract=CollectionContracts.GetValue(fields,key=>new Contract(key,true));
        var frozen=new object[fields.Length];
        int count=fields.Length;
        for(int i=0;i<fields.Length;i++)
        {
            var value=fields[i].GetValue(source);
            int elements=value is System.Collections.ICollection list ? list.Count : 0;
            if(elements>RemainingValues-count) throw new InvalidOperationException("Deferred business value budget exceeded");
            count+=elements;
            frozen[i]=value switch {
                List<int> ints=>ints.ToArray(),List<string> strings=>strings.ToArray(),
                Dictionary<string,int> map=>CopyMap(map),_=>value
            };
        }
        var target=ObjectMarker();
        blocks.Add(new Block {Target=target,Names=contract.Names,Values=frozen});
        values+=count;
        return target;
    }
    private static KeyValuePair<string,int>[] CopyMap(Dictionary<string,int> map)
    {
        var rows=new KeyValuePair<string,int>[map.Count]; int i=0;
        foreach(var row in map) rows[i++]=row;
        return rows;
    }
    private static JToken ThawField(object value)
    {
        if(value is byte[] cells)
        {
            var array=new JArray();
            foreach(byte cell in cells) array.Add((long)cell);
            return array;
        }
        if(value is KeyValuePair<string,int>[] rows)
        {
            var map=new JObject();
            foreach(var row in rows) map.Add(row.Key,row.Value);
            return map;
        }
        if(value is int[] || value is string[]) return JArray.FromObject(value);
        return NativeSnapshotCodec.Scalar(value);
    }
    internal JObject CaptureMap<T>(IReadOnlyDictionary<string,T> source,FieldInfo[] fields) where T:class
    {
        RequireCaptureThread();
        if(sealedCapture) throw new InvalidOperationException("Deferred capture already sealed");
        if(source==null || fields==null) throw new ArgumentNullException();
        if(fields.Length==0 || source.Count>RemainingValues/fields.Length)
            throw new InvalidOperationException("Deferred field map budget exceeded");
        var contract=Contracts.GetValue(fields,key=>new Contract(key));
        var keys=new string[source.Count];
        var frozen=new object[source.Count*fields.Length];
        int row=0,offset=0;
        foreach(var pair in source)
        {
            if(row>=keys.Length || pair.Key==null || pair.Value==null)
                throw new InvalidOperationException("Invalid or changing native field map");
            keys[row++]=pair.Key;
            for(int i=0;i<fields.Length;i++) frozen[offset++]=fields[i].GetValue(pair.Value);
        }
        if(row!=keys.Length) throw new InvalidOperationException("Native field map changed during capture");
        // One frozen table replaces a JObject + deferred block + value array
        // for every tag. Keys/values preserve native enumeration/field order.
        var target=ObjectMarker();
        blocks.Add(new Block { Target=target,Names=contract.Names,Values=frozen,Keys=keys });
        values+=frozen.Length;
        return target;
    }
    internal void Seal()
    {
        RequireCaptureThread();
        if(sealedCapture) throw new InvalidOperationException("Deferred capture already sealed");
        sealedCapture=true;
    }
    internal JArray CaptureBytes(ReadOnlySpan<byte> source)
    {
        RequireCaptureThread();
        if(sealedCapture) throw new InvalidOperationException("Deferred capture already sealed");
        if(source.Length>RemainingValues) throw new InvalidOperationException("Deferred byte array budget exceeded");
        var target=ArrayMarker();
        blocks.Add(new Block {Target=target,Bytes=source.ToArray()});
        values+=source.Length;
        return target;
    }
    internal JArray CaptureStrings(List<string> source)
    {
        RequireCaptureThread();
        if(sealedCapture || source==null || source.Count>RemainingValues)
            throw new InvalidOperationException("Invalid native string list");
        if(source.Count==0) return new JArray();
        var target=ArrayMarker();
        blocks.Add(new Block { Target=target,Strings=source.ToArray() });
        values+=source.Count;
        return target;
    }
    internal delegate void FillCells<T>(T source,Span<byte> destination);
    private byte[] ReadCells<T>(T source,int count,FillCells<T> fill)
    {
        RequireCaptureThread();
        if(sealedCapture || fill==null || count<0 || count>RemainingValues)
            throw new InvalidOperationException("Invalid deferred shape cells");
        if(shapeCache!=null && !shapeCache.ReadingShape && source is object key && !typeof(T).IsValueType)
        {
            var table=shapeCache.Shapes.GetOrCreateValue(key);
            if(shapeCache.Scratch.Length<count) shapeCache.Scratch=new byte[count];
            var slice=shapeCache.Scratch.AsSpan(0,count);
            slice.Clear();
            shapeCache.ReadingShape=true;
            try { fill(source,slice); }
            finally { shapeCache.ReadingShape=false; }
            if(table.Bytes==null || !slice.SequenceEqual(table.Bytes)) table.Bytes=slice.ToArray();
            return table.Bytes;
        }
        var bytes=new byte[count];
        fill(source,bytes);
        return bytes;
    }
    internal JObject CaptureShape<T>(T source,int x,int y,int width,int height,bool flipped,int rotation,byte outside,
        FillCells<T> fill,JObject basis)
    {
        if(width<0 || height<0 || width>128 || height>128)
            throw new InvalidOperationException("Invalid deferred native shape");
        if(width*height+ShapeNames.Length>RemainingValues)
            throw new InvalidOperationException("Deferred native checkpoint capture budget exceeded");
        int count=width*height;
        var bytes=ReadCells(source,count,fill);
        object cells=bytes;
        if(basis!=null)
        {
            var prior=blocks.Count>0 ? blocks[blocks.Count-1] : null;
            if(prior?.Names!=ShapeNames || !ReferenceEquals(prior.Target,basis) || prior.Values[7] is not byte[] original)
                throw new InvalidOperationException("Invalid base native shape");
            if(bytes.AsSpan().SequenceEqual(original)) cells=false;
        }
        var target=ObjectMarker();
        blocks.Add(new Block { Target=target,Names=ShapeNames,Values=new object[]{x,y,width,height,flipped,rotation,outside,cells} });
        values+=count+ShapeNames.Length;
        return target;
    }
    internal JToken CaptureCells<T>(T source,int count,FillCells<T> fill,JArray basis=null)
    {
        var bytes=ReadCells(source,count,fill);
        if(basis!=null)
        {
            var prior=blocks.Count>0 ? blocks[blocks.Count-1] : null;
            if(prior?.Bytes==null || !ReferenceEquals(prior.Target,basis)) throw new InvalidOperationException("Invalid base shape cells");
            if(bytes.AsSpan().SequenceEqual(prior.Bytes)) { values+=count; return (JToken)false; }
        }
        var target=ArrayMarker();
        blocks.Add(new Block{Target=target,Bytes=bytes});
        values+=count;
        return target;
    }
    internal JObject CaptureTags<T>(IReadOnlyDictionary<string,T> source,Func<T,NativeTagRow> freeze,NativeTagRow.Layout layout=null,JObject basis=null) where T:class
    {
        RequireCaptureThread();
        if(sealedCapture) throw new InvalidOperationException("Deferred capture already sealed");
        if(source==null || freeze==null) throw new ArgumentNullException();
        layout=layout ?? NativeTagRow.Canonical;
        if(source.Count>RemainingValues/NativeTagRow.Names.Length)
            throw new InvalidOperationException("Deferred tag table budget exceeded");
        var table=tagCache?.Tags.GetOrCreateValue(source);
        int count=source.Count;
        bool matching=table?.Rows?.Length==count;
        var keys=matching ? table.Keys : new string[count];
        var rows=matching ? table.Rows : new NativeTagRow[count];
        int row=0;
        foreach(var pair in source)
        {
            if(row>=keys.Length || pair.Key==null || pair.Value==null)
                throw new InvalidOperationException("Invalid or changing native tag table");
            var value=freeze(pair.Value);
            if(matching && (keys[row]!=pair.Key || !rows[row].SameValues(in value)))
            {
                // Earlier rows matched byte for byte. Never overwrite arrays
                // already owned by a sealed snapshot or a running worker.
                var newKeys=new string[count]; var newRows=new NativeTagRow[count];
                Array.Copy(keys,newKeys,row); Array.Copy(rows,newRows,row);
                keys=newKeys; rows=newRows; matching=false;
            }
            if(!matching) { keys[row]=pair.Key; rows[row]=value; }
            row++;
        }
        if(row!=keys.Length) throw new InvalidOperationException("Native tag table changed during capture");
        if(table!=null && !matching) { table.Keys=keys; table.Rows=rows; }
        var target=ObjectMarker();
        var prior=basis!=null && blocks.Count>0 ? blocks[blocks.Count-1] : null;
        if(basis!=null && (prior?.TagRows==null || !ReferenceEquals(prior.Target,basis) || prior.TagBasis!=null ||
            !ReferenceEquals(prior.TagColumns,layout.Columns))) throw new InvalidOperationException("Invalid base tag block");
        blocks.Add(new Block {Target=target,Names=layout.Names,Keys=keys,TagRows=rows,TagColumns=layout.Columns,TagBasis=prior});
        values+=rows.Length*NativeTagRow.Names.Length;
        return target;
    }
    internal void Materialize(JObject root,CancellationToken cancellation=default)
    {
        if(borrowedRecords.Count!=0) throw new InvalidOperationException("Borrowed records require the record encoder");
        if(!sealedCapture || Interlocked.Exchange(ref consumed,1)!=0)
            throw new InvalidOperationException("Deferred capture is not available for encoding");
        try
        {
            AssembleItems(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if(proofTarget!=null)
            {
                var lookup=new Dictionary<JToken,Block>(ReferenceComparer.Instance);
                foreach(var block in blocks) lookup.Add(block.Target,block);
                FinishUpgradeProof(lookup,cancellation);
            }
            foreach(var block in blocks)
            {
                if(!ReferenceEquals(block.Target.Root,root))
                    throw new InvalidOperationException("Deferred field destination left its captured graph");
                if(block.Target.Count!=0) throw new InvalidOperationException("Deferred field destination was modified");
                if(block.Bytes!=null)
                {
                    for(int i=0;i<block.Bytes.Length;i++)
                    {
                        if((i&127)==0) cancellation.ThrowIfCancellationRequested();
                        block.Target.Add(new JValue((long)block.Bytes[i]));
                    }
                }
                else if(block.Strings!=null)
                {
                    foreach(var value in block.Strings)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        block.Target.Add(NativeSnapshotCodec.Scalar(value));
                    }
                }
                else if(block.Keys!=null)
                {
                    int offset=0;
                    foreach(var key in block.Keys)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if(block.InheritsRow(offset/block.Names.Length))
                        {
                            ((JObject)block.Target).Add(key,false);
                            offset+=block.Names.Length;
                            continue;
                        }
                        var row=new JObject();
                        for(int i=0;i<block.Names.Length;i++)
                        {
                            if((offset&127)==0) cancellation.ThrowIfCancellationRequested();
                            var value=block.TagRows==null ? block.Values[offset] : block.TagRows[offset/block.Names.Length].Value(block.TagColumns[i]);
                            row.Add(block.Names[i],NativeSnapshotCodec.Scalar(value)); offset++;
                        }
                        ((JObject)block.Target).Add(key,row);
                    }
                }
                else
                {
                    for(int i=0;i<block.Values.Length;i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        ((JObject)block.Target).Add(block.Names[i],ThawField(block.Values[i]));
                    }
                }
            }
        }
        finally { blocks.Clear(); }
    }
    // Encode the frozen scalars directly. The placeholder graph remains empty;
    // only this single consumer can access the frozen blocks. Do not materialize
    // thousands of temporary JSON properties only to discard them after packing.
    internal byte[] Pack(JObject root,CancellationToken cancellation=default) =>
        Pack(root,cancellation,false,null,0,out _,out _);
    internal byte[] Pack(JObject root,CancellationToken cancellation,bool records,NativeRecordBasis basis,
        ulong revision,out NativeRecordBasis checkpoint,out int referenced,byte[] auditBasis=null) =>
        PackFrame(root,cancellation,records,basis,revision,out checkpoint,out referenced,auditBasis,false).Single;

    internal Core.NativeSnapshotFrame PackFrame(JObject root,CancellationToken cancellation,bool records,NativeRecordBasis basis,
        ulong revision,out NativeRecordBasis checkpoint,out int referenced,byte[] auditBasis=null,
        bool allowPages=true,bool preferPages=false)
    {
        if(!sealedCapture || Interlocked.Exchange(ref consumed,1)!=0)
            throw new InvalidOperationException("Deferred capture is not available for encoding");
        try
        {
            checkpoint=null; referenced=0;
            cancellation.ThrowIfCancellationRequested();
            var lookup=new Dictionary<JToken,Block>(ReferenceComparer.Instance);
            foreach(var block in blocks)
            {
                cancellation.ThrowIfCancellationRequested();
                if(block.Target.Count!=0) throw new InvalidOperationException("Deferred field destination was modified");
                lookup.Add(block.Target,block);
            }
            int itemCount=0;
            if(records) foreach(JObject parent in (JArray)root["roots"]) itemCount+=((JArray)parent["items"]).Count;
            bool incremental=records && basis!=null && Math.Abs(itemCount-basis.Count)<=Math.Max(8,basis.Count/3);
            if(borrowedRecords.Count!=0 && (!records || !ReferenceEquals(basis,borrowedBasis)))
                throw new InvalidOperationException("Borrowed records require their exact acknowledged basis");
            HashSet<JToken> verified=null,components=null;
            if(incremental && EarlyPlain && itemRows.Count!=0)
            {
                var top=new Dictionary<uint,JObject>();
                foreach(var slot in NativeRecordBasis.Rows(root))
                {
                    var target=(JObject)slot["data"]; uint id=(uint)target["entity"];
                    if(!top.TryAdd(id,target)) top[id]=null;
                }
                verified=new HashSet<JToken>(lookup.Comparer);
                components=new HashSet<JToken>(lookup.Comparer);
                foreach(var row in itemRows)
                {
                    cancellation.ThrowIfCancellationRequested();
                    uint id=(uint)row.Target["entity"];
                    if(top.TryGetValue(id,out var target) && ReferenceEquals(row.Target,target) && basis.MatchesPlain(id,row,lookup))
                    {
                        verified.Add(target);
                        if(auditBasis!=null) row.Assemble();
                        else row.AddComponentRoots(components);
                    }
                    else row.Assemble();
                }
                ElidedRows=auditBasis==null ? verified.Count : 0;
            }
            else foreach(var row in itemRows) { cancellation.ThrowIfCancellationRequested(); row.Assemble(); }
            var restoredRecords=new HashSet<JObject>();
            void RestoreRecord(JObject target,uint id)
            {
                if(!restoredRecords.Add(target)) return;
                int first=itemRows.Count;
                borrowedBasis.Restore(id,target,lookup,itemRows,cancellation);
                for(int i=first;i<itemRows.Count;i++) ConfirmedRecords.Add((uint)itemRows[i].Target["entity"]);
            }
            foreach(var target in borrowedNested) RestoreRecord(target,borrowedRecords[target]);
            foreach(var record in borrowedRecords) ConfirmedRecords.Add(record.Value);
            // Nested records have independent capture stamps. A changed outer
            // machine/container must not force recapturing all its siblings.
            if(basis!=null)
                foreach(var row in itemRows)
                {
                    uint id=(uint)row.Target["entity"];
                    if(ConfirmedRecords.Contains(id) || row.Target.Count==1) continue;
                    if(basis.MatchesRecord(id,row.Target,lookup)) ConfirmedRecords.Add(id);
                    else if(AuditedRecords.Contains(id)) UntrackedRecords.Add(id);
                }
            foreach(var block in blocks)
            {
                var owner=block.Target.Root;
                if(!ReferenceEquals(owner,root) && components?.Contains(owner)!=true)
                    throw new InvalidOperationException("Deferred field destination left its captured graph");
            }
            FinishUpgradeProof(lookup,cancellation);
            if(!records) return Single(NativeSnapshotBinary.Pack(root,cancellation,lookup));
            bool restored=false;
            void RestoreBorrowed()
            {
                if(restored) return;
                foreach(var record in borrowedRecords) RestoreRecord(record.Key,record.Value);
                restored=true;
            }
            if(incremental)
            {
                if(borrowedRecords.Count!=0)
                {
                    verified??=new HashSet<JToken>(lookup.Comparer);
                    foreach(var record in borrowedRecords) if(!borrowedNested.Contains(record.Key)) verified.Add(record.Key);
                    if(auditBasis!=null) RestoreBorrowed();
                }
                byte[] expected=auditBasis==null ? null : NativeSnapshotBinary.Pack(root,cancellation,lookup);
                var reused=basis.ReferenceUnchanged(root,lookup,cancellation,verified);
                if(reused.Count>0)
                {
                    bool published=false;
                    try
                    {
                        var delta=NativeSnapshotBinary.Pack(root,cancellation,lookup);
                        if(delta.Length<=Core.ReplicaLink.MaxBytes-12)
                        {
                            if(expected!=null && !expected.AsSpan().SequenceEqual(NativeRecordBasis.ExpandForAudit(delta,auditBasis,cancellation)))
                                throw new InvalidDataException("Native delta differs from complete authority capture");
                            referenced=reused.Count;
                            foreach(var entry in reused) ConfirmedRecords.Add((uint)entry.Data["entity"]);
                            if(Markers!=null && components!=null && components.Count!=0)
                                foreach(var block in blocks)
                                    if(components.Contains(block.Target)) Markers.Reclaim(block.Target);
                            var frame=Single(Core.NativeRecordFrame.Delta(delta,revision));
                            published=true;
                            return frame;
                        }
                    }
                    catch(InvalidDataException error) when(allowPages && NativeSnapshotBinary.IsCapacity(error)) { }
                    finally { if(!published) foreach(var entry in reused) entry.Row["data"]=entry.Data; }
                }
                if(verified!=null && auditBasis==null)
                    foreach(var row in itemRows) if(verified.Contains(row.Target)) row.Assemble();
            }
            RestoreBorrowed();
            Core.NativeSnapshotFrame full=null;
            if(!preferPages || itemRows.Count==0)
            {
                try { full=Single(NativeSnapshotBinary.Pack(root,cancellation,lookup)); }
                catch(InvalidDataException error) when(allowPages && NativeSnapshotBinary.IsCapacity(error)) { }
            }
            full??=NativeSnapshotPages.Pack(root,itemRows,lookup,cancellation);
            checkpoint=new NativeRecordBasis(root,lookup,itemRows);
            return full;
        }
        finally { itemRows.Clear(); blocks.Clear(); }
    }
    private static Core.NativeSnapshotFrame Single(byte[] bytes)=>new Core.NativeSnapshotFrame(new[] { bytes });
    internal sealed class ReferenceComparer : IEqualityComparer<JToken>
    {
        internal static readonly ReferenceComparer Instance=new ReferenceComparer();
        public bool Equals(JToken x,JToken y)=>ReferenceEquals(x,y);
        public int GetHashCode(JToken value)=>RuntimeHelpers.GetHashCode(value);
    }
    private void RequireCaptureThread()
    {
        if(Thread.CurrentThread.ManagedThreadId!=ownerThread)
            throw new InvalidOperationException("Native values must be read on the capture thread");
    }
}
