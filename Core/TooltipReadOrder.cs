using System;

namespace ProbablyCoop.Core;

// Only tracks reads. No business command, money or item ownership depends on it.
internal sealed class TooltipReadOrder
{
    internal uint Interest { get; private set; }
    private ulong received,applied;
    private double renewAfter;
    internal void Select(double now)
    {
        if(++Interest==0) Interest=1;
        renewAfter=now+0.4;
    }
    internal bool Renew(double now,bool hasCurrentRead)
    {
        if(hasCurrentRead || now<renewAfter) return false;
        Select(now);
        return true;
    }
    internal bool Accepts(uint interest,ulong observation) => interest!=0 && interest==Interest && observation>received && observation>applied;
    internal void Received(ulong observation) => received=Math.Max(received,observation);
    internal void Applied(ulong through) => applied=Math.Max(applied,through);
}
