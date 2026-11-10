using System;
using System.Collections.Generic;

namespace ProbablyCoop.Core;

// Exact memo of the existing sort proof. Fresh native shape bytes, ordered
// child move proofs, flags, scene and undo state must all match on every hit.
internal sealed class SortProofCache
{
    internal const int MaxEntries=1024,MaxCells=512*1024,MaxRows=16384;
    internal readonly struct State : IEquatable<State>
    {
        internal readonly uint Id;
        internal readonly int Width,Height;
        internal readonly byte Outside;
        internal readonly string Scene;
        internal readonly bool Undo;
        internal State(uint id,int width,int height,byte outside,string scene,bool undo)
        { Id=id; Width=width; Height=height; Outside=outside; Scene=scene; Undo=undo; }
        public bool Equals(State other)=>Id==other.Id && Width==other.Width && Height==other.Height && Outside==other.Outside &&
            string.Equals(Scene,other.Scene,StringComparison.Ordinal) && Undo==other.Undo;
    }
    internal readonly struct Row : IEquatable<Row>
    {
        internal readonly string Move;
        internal readonly bool Pinned,Debris;
        internal Row(string move,bool pinned,bool debris) { Move=move; Pinned=pinned; Debris=debris; }
        public bool Equals(Row other)=>string.Equals(Move,other.Move,StringComparison.Ordinal) && Pinned==other.Pinned && Debris==other.Debris;
    }
    private sealed class Entry {internal State State; internal byte[] Cells; internal Row[] Rows; internal string Proof;}
    private readonly Dictionary<uint,Entry> entries=new Dictionary<uint,Entry>();
    internal int Count=>entries.Count;
    internal int CellCount {get;private set;}
    internal int RowCount {get;private set;}
    internal bool TryGet(State state,ReadOnlySpan<byte> cells,IReadOnlyList<Row> rows,out string proof)
    {
        proof=null;
        if(!entries.TryGetValue(state.Id,out var entry) || !entry.State.Equals(state) || rows==null ||
            rows.Count!=entry.Rows.Length || !cells.SequenceEqual(entry.Cells)) return false;
        for(int i=0;i<rows.Count;i++) if(!rows[i].Equals(entry.Rows[i])) return false;
        proof=entry.Proof; return true;
    }
    internal void Put(State state,ReadOnlySpan<byte> cells,IReadOnlyList<Row> rows,string proof)
    {
        if(state.Id==0 || state.Width<0 || state.Height<0 || state.Width>128 || state.Height>128 ||
            cells.Length!=state.Width*state.Height || state.Scene==null || rows==null || proof==null)
            throw new ArgumentException("Invalid sort proof entry");
        for(int i=0;i<rows.Count;i++) if(rows[i].Move==null) throw new ArgumentException("Missing sort move proof");
        Remove(state.Id);
        if(rows.Count>MaxRows) return;
        if(entries.Count>=MaxEntries || CellCount+cells.Length>MaxCells || RowCount+rows.Count>MaxRows) Clear();
        var copy=new Row[rows.Count]; for(int i=0;i<copy.Length;i++) copy[i]=rows[i];
        entries.Add(state.Id,new Entry {State=state,Cells=cells.ToArray(),Rows=copy,Proof=proof});
        CellCount+=cells.Length; RowCount+=copy.Length;
    }
    private void Remove(uint id)
    {
        if(!entries.TryGetValue(id,out var old)) return;
        CellCount-=old.Cells.Length; RowCount-=old.Rows.Length; entries.Remove(id);
    }
    internal void Retain(ISet<uint> live)
    {
        var removed=new List<uint>(); foreach(uint id in entries.Keys) if(!live.Contains(id)) removed.Add(id);
        foreach(uint id in removed) Remove(id);
    }
    internal void Clear() {entries.Clear(); CellCount=RowCount=0;}
}
