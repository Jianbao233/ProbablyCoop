using System;
using System.Collections.Generic;

namespace ProbablyCoop.Core;

// Dispatch -> first fully applied authoritative projection containing that
// dispatch. Reply/chunk arrival/worker completion alone is not a UI apply.
// Native coroutines may finish later; their outcomes need separate assertions.
internal sealed class CommandProjectionTiming
{
    internal const int Capacity=64;
    internal const double TimeoutMilliseconds=10000;
    private sealed class Entry
    {
        internal ulong Serial;
        internal double Sent,Applied;
        internal bool Replied,Projected;
        internal string Kind;
        internal double ReplyTime;
        internal ulong StartRevision,EndRevision;
    }
    private readonly List<Entry> entries=new List<Entry>();
    internal int Count=>entries.Count;
    internal int Expired {get;private set;}
    internal int Overflowed {get;private set;}
    internal int CompletedCount {get;private set;}
    internal Action<double> Completed;
    internal Action<string,ulong,ulong,ulong,double,double> Slow;
    internal void Sent(ulong serial,double now,string kind=null,ulong revision=0)
    {
        if(serial==0) throw new ArgumentOutOfRangeException(nameof(serial));
        foreach(var entry in entries) if(entry.Serial==serial) throw new InvalidOperationException("Duplicate timing request");
        Expire(now);
        if(entries.Count==Capacity) { entries.RemoveAt(0); Overflowed++; }
        entries.Add(new Entry {Serial=serial,Sent=now,Kind=kind,StartRevision=revision});
    }
    internal void Reply(ulong serial,bool accepted,double now=0)
    {
        for(int i=0;i<entries.Count;i++)
        {
            var entry=entries[i]; if(entry.Serial!=serial) continue;
            if(!accepted) { entries.RemoveAt(i); return; }
            entry.Replied=true; entry.ReplyTime=now; Complete(i,entry); return;
        }
    }
    internal void Applied(ulong throughSerial,double now,ulong revision=0)
    {
        for(int i=entries.Count-1;i>=0;i--)
        {
            var entry=entries[i]; if(entry.Serial>throughSerial) continue;
            if(!entry.Projected) { entry.Projected=true; entry.Applied=now; entry.EndRevision=revision; }
            Complete(i,entry);
        }
    }
    private void Complete(int index,Entry entry)
    {
        if(!entry.Replied || !entry.Projected) return;
        entries.RemoveAt(index);
        CompletedCount++;
        double elapsed=entry.Applied-entry.Sent;
        Completed?.Invoke(elapsed);
        if(elapsed>=300) Slow?.Invoke(entry.Kind,entry.Serial,entry.StartRevision,entry.EndRevision,entry.ReplyTime-entry.Sent,elapsed);
    }
    internal void Expire(double now)
    {
        for(int i=entries.Count-1;i>=0;i--)
            if(now-entries[i].Sent>TimeoutMilliseconds) { entries.RemoveAt(i); Expired++; }
    }
    internal void Clear() { entries.Clear(); Expired=Overflowed=CompletedCount=0; }
}
