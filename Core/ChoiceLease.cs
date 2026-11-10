using System;

namespace ProbablyCoop.Core;

// A single actor's native chooser. Taking a choice consumes its generation
// before a callback can throw, replace the menu, or send another request.
internal sealed class ChoiceLease
{
    internal const int Limit=256;
    private ulong next;
    internal ulong Serial { get; private set; }
    internal ulong Context { get; private set; }
    internal int Count { get; private set; }
    internal bool Cancelable { get; private set; }
    internal ulong Open(ulong context,int count,bool cancelable)
    {
        if(context==0 || count<0 || count>Limit) throw new ArgumentOutOfRangeException();
        Serial=checked(++next); Context=context; Count=count; Cancelable=cancelable;
        return Serial;
    }
    internal bool Take(ulong serial,ulong context,int index)
    {
        if(Serial==0 || serial!=Serial || context!=Context || index < -1 ||
            index>=Count || index==-1 && !Cancelable) return false;
        Clear(); return true;
    }
    internal void Clear() { Serial=Context=0; Count=0; Cancelable=false; }
}
