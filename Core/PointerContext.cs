namespace ProbablyCoop.Core;

// Cursor context lives in the existing UInt64 packet field. Entity identities
// are scoped by the authenticated session/epoch, just like native snapshots.
internal readonly struct PointerContext
{
    internal const ushort NativeElement=53;
    internal readonly ushort Location,Surface;
    internal readonly uint Entity;
    internal PointerContext(ushort location,ushort surface,uint entity=0)
    { Location=location; Surface=surface; Entity=entity; }
    internal ulong Encode()=>((ulong)Entity<<32)|((ulong)Location<<16)|Surface;
    internal static bool TryDecode(ulong value,out PointerContext context)
    {
        context=new PointerContext((ushort)(value>>16),(ushort)value,(uint)(value>>32));
        if(context.Location>5) return false;
        if(context.Surface==NativeElement) return context.Entity!=0;
        return context.Entity==0 && context.Surface is (32 or 41 or 42 or 43 or 46 or 47 or 50 or 51 or 52);
    }
}
