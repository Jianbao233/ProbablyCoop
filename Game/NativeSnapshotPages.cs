using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Full captures can carry more than one binary graph budget. The first page
// holds the world and ownership slots. Later pages hold item records, including
// records of items inside a container. The original graph is restored on the
// worker before it becomes a delta basis.
internal static class NativeSnapshotPages
{
    private const string Items = "pageItems";
    private const int ItemsPerPage = 64, MaxExpanded = 64*1024*1024;
    internal const int MaxNodes = 2000000, MaxRecords = ItemsPerPage*(NativeSnapshotFrame.MaxPages-1);

    internal static NativeSnapshotFrame Pack(JObject root,IReadOnlyList<NativeDeferredFields.ItemRow> rows,
        IReadOnlyDictionary<JToken,NativeDeferredFields.Block> fields,CancellationToken cancellation)
    {
        if (rows.Count is 0 or >MaxRecords)
            throw NativeSnapshotBinary.Capacity("Invalid native snapshot item page count");
        var references=new (JObject Record,JObject Marker)[rows.Count];
        var markers=new Dictionary<JToken,int>(NativeDeferredFields.ReferenceComparer.Instance);
        var pages=new List<byte[]>();
        int detached=0,packedBytes=0,nodes=0,expanded=0;
        try
        {
            foreach(var row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                var marker=new JObject();
                if(!ReferenceEquals(row.Target.Root,root))
                    throw new InvalidDataException("Item record is outside its native snapshot");
                row.Target.Replace(marker);
                markers.Add(marker,detached);
                references[detached++]=(row.Target,marker);
            }
            Add(Encode(root));
            for(int start=0;start<detached;start+=ItemsPerPage)
                AddRecords(start,Math.Min(ItemsPerPage,detached-start));
            return new NativeSnapshotFrame(pages.ToArray());
        }
        finally
        {
            for(int index=0;index<detached;index++)
                references[index].Marker.Replace(references[index].Record);
        }

        (byte[] Bytes,(int Nodes,int Bytes) Cost) Encode(JObject piece)
        {
            var bytes=NativeSnapshotBinary.Pack(piece,cancellation,fields,out var cost,markers);
            return (bytes,cost);
        }
        void Add((byte[] Bytes,(int Nodes,int Bytes) Cost) piece)
        {
            if(pages.Count>=NativeSnapshotFrame.MaxPages)
                throw NativeSnapshotBinary.Capacity("Native snapshot exceeds checkpoint page budget");
            Charge(piece.Cost,ref nodes,ref expanded);
            if(piece.Bytes.Length>NativeSnapshotFrame.MaxPackedBytes-packedBytes)
                throw NativeSnapshotBinary.Capacity("Native snapshot exceeds packed checkpoint budget");
            packedBytes+=piece.Bytes.Length;
            pages.Add(piece.Bytes);
        }
        void AddRecords(int start,int count)
        {
            var items=new JArray();
            for(int i=start;i<start+count;i++) items.Add(references[i].Record);
            (byte[] Bytes,(int Nodes,int Bytes) Cost) piece=default;
            try { piece=Encode(new JObject { [Items]=items }); }
            catch(InvalidDataException error) when(count>1 && NativeSnapshotBinary.IsCapacity(error)) { }
            finally { items.RemoveAll(); }
            if(piece.Bytes!=null) { Add(piece); return; }
            int half=count/2;
            AddRecords(start,half);
            AddRecords(start+half,count-half);
        }
    }

    internal static JObject ReadMetadata(NativeSnapshotFrame frame,CancellationToken cancellation) =>
        NativeSnapshotBinary.Unpack(frame.First,cancellation,out _,new List<(JObject,int)>());

    internal static JObject Unpack(NativeSnapshotFrame frame,CancellationToken cancellation)
    {
        if(frame.Count==1) return NativeSnapshotCodec.Unpack(frame.First,cancellation);
        int nodes=0,expanded=0;
        var rootReferences=new List<(JObject Marker,int Index)>();
        var root=Read(frame.First,rootReferences);
        var records=new List<JObject>();
        var links=new List<(JObject Marker,int Index,int Owner)>();
        for(int page=1;page<frame.Count;page++)
        {
            var references=new List<(JObject Marker,int Index)>();
            var batch=Read(frame[page],references);
            if(batch.Count!=1 || batch[Items] is not JArray items || items.Count is <1 or >ItemsPerPage)
                throw new InvalidDataException("Invalid native item page");
            var owners=new Dictionary<JToken,int>(NativeDeferredFields.ReferenceComparer.Instance);
            foreach(var item in items)
            {
                if(item is not JObject record)
                    throw new InvalidDataException("Invalid native item page record");
                owners.Add(record,records.Count);
                records.Add(record);
            }
            foreach(var reference in references)
            {
                var parent=reference.Marker.Parent;
                while(parent!=null && !owners.TryGetValue(parent,out _)) parent=parent.Parent;
                if(parent==null) throw new InvalidDataException("Invalid native item page reference owner");
                links.Add((reference.Marker,reference.Index,owners[parent]));
            }
            items.RemoveAll();
        }
        var slots=new JObject[records.Count];
        foreach(var reference in rootReferences) Register(reference.Marker,reference.Index,records.Count);
        foreach(var reference in links) Register(reference.Marker,reference.Index,reference.Owner);
        for(int index=0;index<records.Count;index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if(slots[index]==null) throw new InvalidDataException("Unowned native item page record");
            slots[index].Replace(records[index]);
        }
        // Each page has its own parser depth limit. Bound the assembled graph
        // before passing it to native inventory validation as well.
        ValidateDepth(root,0,cancellation);
        return root;

        JObject Read(byte[] bytes,List<(JObject Marker,int Index)> references)
        {
            var value=NativeSnapshotBinary.Unpack(bytes,cancellation,out var cost,references);
            Charge(cost,ref nodes,ref expanded);
            return value;
        }
        void Register(JObject marker,int index,int owner)
        {
            cancellation.ThrowIfCancellationRequested();
            if(marker.Parent==null || index<0 || index>=owner || slots[index]!=null)
                throw new InvalidDataException("Invalid or duplicate native item page reference");
            slots[index]=marker;
        }
    }

    private static void Charge((int Nodes,int Bytes) cost,ref int nodes,ref int expanded)
    {
        if(cost.Nodes>MaxNodes-nodes || cost.Bytes>MaxExpanded-expanded)
            throw NativeSnapshotBinary.Capacity("Native snapshot exceeds total checkpoint budget");
        nodes+=cost.Nodes;
        expanded+=cost.Bytes;
    }

    private static void ValidateDepth(JToken token,int depth,CancellationToken cancellation)
    {
        if(depth>128) throw new InvalidDataException("Native paged graph exceeds depth budget");
        if(token is not JContainer container) return;
        cancellation.ThrowIfCancellationRequested();
        foreach(var child in container.Children()) ValidateDepth(child,depth+1,cancellation);
    }
}
