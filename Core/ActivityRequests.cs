using System;
using System.Collections.Generic;
using System.Linq;

namespace ProbablyCoop.Core;

// Local bookkeeping for independent native UI requests. A receipt and the graph
// which contains that attempt are separate facts; neither can stand in for the
// other. Retired views still drain their receipts without touching a newer UI.
internal sealed class ActivityRequests
{
    internal const int Limit=16;
    internal sealed class Entry
    {
        internal ulong Serial;
        internal string Kind,Activity;
        internal double Started;
        internal ushort? Result;
        internal bool Active=true;
    }
    private readonly Dictionary<ulong,Entry> entries=new Dictionary<ulong,Entry>();
    private ulong through;
    internal int Count=>entries.Count;
    internal bool HasRoom=>Count<Limit;
    internal IEnumerable<Entry> Entries=>entries.Values;
    internal bool Pending(string kind)=>entries.Values.Any(entry=>entry.Active && entry.Kind==kind);
    internal bool PendingActivity(string activity)=>entries.Values.Any(entry=>entry.Active && entry.Activity==activity);
    internal bool Track(ulong serial,string kind,string activity,double started)
    {
        if(serial==0 || string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(activity) ||
            !HasRoom || entries.ContainsKey(serial)) return false;
        entries.Add(serial,new Entry { Serial=serial,Kind=kind,Activity=activity,Started=started });
        return true;
    }
    internal bool Reply(ulong serial,ushort result,out Entry entry)
    {
        if(result is 0 or > (ushort)ShopResult.Failed) throw new ArgumentOutOfRangeException(nameof(result));
        if(!entries.TryGetValue(serial,out entry) || entry.Result.HasValue) return false;
        entry.Result=result; return true;
    }
    internal void Project(ulong serial)=>through=Math.Max(through,serial);
    internal void Collect()
    {
        foreach(var entry in entries.Values.Where(entry=>entry.Result.HasValue && entry.Serial<=through).ToArray())
            entries.Remove(entry.Serial);
    }
    internal void Retire() { foreach(var entry in entries.Values) entry.Active=false; }
    internal void Clear() { entries.Clear(); through=0; }
}
