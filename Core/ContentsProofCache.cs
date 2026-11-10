using System;
using System.Collections.Generic;

namespace ProbablyCoop.Core;

// Reuse only the exact existing proof of the ordered descendant state. This
// never substitutes a checksum/TTL for a fresh inspection of native contents.
internal sealed class ContentsProofCache
{
    internal const int MaxRows=16384, MaxEntries=1024;
    internal readonly struct Row : IEquatable<Row>
    {
        internal readonly string Move;
        internal readonly bool Pinned,Locked;
        internal Row(string move,bool pinned,bool locked) { Move=move; Pinned=pinned; Locked=locked; }
        public bool Equals(Row other)=>string.Equals(Move,other.Move,StringComparison.Ordinal) && Pinned==other.Pinned && Locked==other.Locked;
    }
    private sealed class Entry { internal Row[] Rows; internal string Proof; }
    private readonly Dictionary<uint,Entry> entries=new Dictionary<uint,Entry>();
    private int retainedRows;
    internal int Count=>entries.Count;
    internal int RowCount=>retainedRows;
    internal bool TryGet(uint id,IReadOnlyList<Row> rows,out string proof)
    {
        proof=null;
        if(!entries.TryGetValue(id,out var entry) || rows==null || rows.Count!=entry.Rows.Length) return false;
        for(int i=0;i<rows.Count;i++) if(!rows[i].Equals(entry.Rows[i])) return false;
        proof=entry.Proof; return true;
    }
    internal void Put(uint id,IReadOnlyList<Row> rows,string proof)
    {
        if(id==0 || rows==null || rows.Count==0 || proof==null) throw new ArgumentException("Invalid contents proof cache entry");
        for(int i=0;i<rows.Count;i++) if(rows[i].Move==null) throw new ArgumentException("Missing contents move proof");
        if(entries.TryGetValue(id,out var old)) { retainedRows-=old.Rows.Length; entries.Remove(id); }
        // Large inventories remain fully validated; they merely skip caching.
        if(rows.Count>MaxRows) return;
        if(entries.Count>=MaxEntries || retainedRows+rows.Count>MaxRows) Clear();
        var copy=new Row[rows.Count]; for(int i=0;i<copy.Length;i++) copy[i]=rows[i];
        entries.Add(id,new Entry { Rows=copy,Proof=proof }); retainedRows+=copy.Length;
    }
    internal void Retain(ISet<uint> live)
    {
        var removed=new List<uint>();
        foreach(uint id in entries.Keys) if(!live.Contains(id)) removed.Add(id);
        foreach(uint id in removed) { retainedRows-=entries[id].Rows.Length; entries.Remove(id); }
    }
    internal void Clear() { entries.Clear(); retainedRows=0; }
}
