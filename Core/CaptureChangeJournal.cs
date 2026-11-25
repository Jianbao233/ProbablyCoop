using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ProbablyCoop.Core;

// Main-thread ownership of dirtiness. Encoder completions carry a stamp, so
// an old completion cannot clear a newer mutation or a new session's work.
internal sealed class CaptureChangeJournal<TKey> where TKey:class
{
    private sealed class Entry
    {
        internal ulong Version;
        internal readonly HashSet<object> Sources=new HashSet<object>(References.Instance);
    }
    private sealed class References:IEqualityComparer<object>
    {
        internal static readonly References Instance=new References();
        public new bool Equals(object a,object b)=>ReferenceEquals(a,b);
        public int GetHashCode(object value)=>RuntimeHelpers.GetHashCode(value);
    }
    private readonly Dictionary<TKey,Entry> entries=new Dictionary<TKey,Entry>(References.Instance);
    private readonly Dictionary<object,HashSet<TKey>> owners=new Dictionary<object,HashSet<TKey>>(References.Instance);
    private readonly List<TKey> propagation=new List<TKey>();
    private ulong serial;
    internal int Count=>entries.Count;
    internal int SourceCount=>owners.Count;
    internal ulong Version(TKey key)
    {
        if(!entries.TryGetValue(key,out var entry)) entries.Add(key,entry=new Entry {Version=++serial});
        return entry.Version;
    }
    internal bool IsCurrent(TKey key,ulong version)=>entries.TryGetValue(key,out var entry) && entry.Version==version;
    internal bool Tracks(object source)=>source!=null && owners.ContainsKey(source);
    internal void Observe(TKey key,object source)
    {
        if(source==null) return;
        Version(key);
        if(!entries[key].Sources.Add(source)) return;
        if(!owners.TryGetValue(source,out var list)) owners.Add(source,list=new HashSet<TKey>(References.Instance));
        list.Add(key);
    }
    internal bool Changed(object source)
    {
        if(source==null || !owners.TryGetValue(source,out var list)) return false;
        ulong stamp=++serial;
        propagation.AddRange(list);
        for(int i=0;i<propagation.Count;i++)
        {
            var key=propagation[i]; var entry=entries[key];
            if(entry.Version==stamp) continue;
            entry.Version=stamp;
            if(owners.TryGetValue(key,out var parents)) propagation.AddRange(parents);
        }
        propagation.Clear();
        return true;
    }
    internal void InvalidateAll()
    { foreach(var entry in entries.Values) entry.Version=++serial; }
    internal void Forget(TKey key)
    {
        if(!entries.TryGetValue(key,out var entry)) return;
        foreach(var source in entry.Sources)
            if(owners.TryGetValue(source,out var list)) { list.Remove(key); if(list.Count==0) owners.Remove(source); }
        entries.Remove(key);
    }
    internal void Clear() { entries.Clear(); owners.Clear(); propagation.Clear(); ++serial; }
}
