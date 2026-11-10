using System;
using System.Collections.Generic;

namespace ProbablyCoop.Core;

// A cache of an existing proof, not a replacement validation or hash. Every
// lookup compares all source scalars and every shape byte. Only its owner on
// the game thread uses it; no native object or mutable caller buffer is retained.
internal sealed class MoveProofCache
{
    internal readonly struct State : IEquatable<State>
    {
        internal readonly uint Id,Parent;
        internal readonly int Quantity,X,Y,Rotation,Width,Height;
        internal readonly bool Owned,Flip;
        internal readonly byte Outside;
        internal State(uint id,uint parent,int quantity,bool owned,int x,int y,int rotation,bool flip,int width,int height,byte outside)
        { Id=id; Parent=parent; Quantity=quantity; Owned=owned; X=x; Y=y; Rotation=rotation; Flip=flip; Width=width; Height=height; Outside=outside; }
        public bool Equals(State other)=>Id==other.Id && Parent==other.Parent && Quantity==other.Quantity && Owned==other.Owned &&
            X==other.X && Y==other.Y && Rotation==other.Rotation && Flip==other.Flip && Width==other.Width && Height==other.Height && Outside==other.Outside;
    }
    private sealed class Entry
    {
        internal State State;
        internal byte[] Cells;
        internal string Proof;
    }
    private readonly Dictionary<uint,Entry> entries=new Dictionary<uint,Entry>();
    internal int Count=>entries.Count;
    internal bool TryGet(State state,ReadOnlySpan<byte> cells,out string proof)
    {
        proof=null;
        if(!entries.TryGetValue(state.Id,out var entry) || !entry.State.Equals(state) || !cells.SequenceEqual(entry.Cells)) return false;
        proof=entry.Proof; return true;
    }
    internal void Put(State state,ReadOnlySpan<byte> cells,string proof)
    {
        if(state.Id==0 || state.Width<0 || state.Height<0 || state.Width>128 || state.Height>128 || cells.Length!=state.Width*state.Height || proof==null)
            throw new ArgumentException("Invalid move proof cache entry");
        entries[state.Id]=new Entry { State=state,Cells=cells.ToArray(),Proof=proof };
    }
    internal void Retain(ISet<uint> live)
    {
        var removed=new List<uint>();
        foreach(uint id in entries.Keys) if(!live.Contains(id)) removed.Add(id);
        foreach(uint id in removed) entries.Remove(id);
    }
    internal void Clear()=>entries.Clear();
}
