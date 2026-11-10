using System;
using System.Collections.Generic;

namespace ProbablyCoop.Core;

// Direct tooltip replies can arrive after a complete world projection. If
// every outstanding reply still describes exactly what that projection shows,
// its sequence need not cause another complete world to cross the connection.
internal sealed class TooltipSnapshotWatermark<TKey,TValue>
{
    internal const int MaxTargets=16;
    private readonly Dictionary<TKey,TValue> pending=new Dictionary<TKey,TValue>();
    private ulong latest,through;
    private bool differingReplies;
    internal void Sent(ulong sequence,TKey target,TValue value)
    {
        if(sequence<=latest) throw new ArgumentException("Tooltip observations must increase");
        latest=sequence;
        if(differingReplies) return;
        if(pending.TryGetValue(target,out var previous))
            differingReplies=!EqualityComparer<TValue>.Default.Equals(value,previous);
        else if(pending.Count==MaxTargets) differingReplies=true;
        else pending.Add(target,value);
    }
    internal ulong Capture(Func<TKey,TValue,bool> matches)
    {
        if(!differingReplies)
            foreach(var row in pending)
                if(!matches(row.Key,row.Value)) { differingReplies=true; break; }
        if(differingReplies) Reset();
        return through;
    }
    internal void Reset()
    {
        through=latest;
        differingReplies=false;
        pending.Clear();
    }
}
