using ProbablyCoop.Core;

internal static class MoveProofCacheTests
{
    internal static void Run()
    {
        MoveProofCache.State State(int field=-1)=>new MoveProofCache.State(
            field==0 ? 2u : 1u,field==1 ? 4u : 3u,field==2 ? 4 : 3,field!=3,
            field==4 ? 1 : 0,field==5 ? 1 : 0,field==6 ? 1 : 0,field==7,
            field==8 ? 3 : 2,field==9 ? 3 : 2,field==10 ? (byte)1 : (byte)0);
        var cache=new MoveProofCache(); var cells=new byte[]{1,0,0,1}; var original=State();
        cache.Put(original,cells,"original");
        Check(cache.TryGet(original,cells,out string proof) && proof=="original","identical state missed");
        for(int field=0;field<11;field++)
            Check(!cache.TryGet(State(field),cells,out _),"stale proof accepted after field change "+field);
        for(int cell=0;cell<cells.Length;cell++)
        {
            var changed=(byte[])cells.Clone(); changed[cell]^=1;
            Check(!cache.TryGet(original,changed,out _),"shape mutation accepted");
        }
        Check(!cache.TryGet(original,new byte[]{1,0,0},out _),"short shape accepted");
        cells[0]=0;
        Check(!cache.TryGet(original,cells,out _) && cache.TryGet(original,new byte[]{1,0,0,1},out _),"caller buffer aliased cached shape");
        cache.Put(State(0),cells,"second");
        cache.Retain(new HashSet<uint>{2});
        Check(cache.Count==1 && !cache.TryGet(original,cells,out _) && cache.TryGet(State(0),cells,out _),"swept entity retained or independent state lost");
        cache.Clear(); Check(cache.Count==0,"session close retained proofs");
        // Preserve the opaque original proof, including returning to an earlier
        // state. The cache does not invent revisions or weaken existing checks.
        var known=new byte[]{1,0,0,1}; cache.Put(original,known,"A");
        cache.Put(State(2),known,"B");
        Check(!cache.TryGet(original,known,out _),"ABA reused incompatible cached entry");
        cache.Put(original,known,"A");
        Check(cache.TryGet(original,known,out proof) && proof=="A","restored state changed existing proof contract");
        foreach(var invalid in new[]{new MoveProofCache.State(),new MoveProofCache.State(1,1,1,true,0,0,0,false,129,1,0)})
            Reject(()=>cache.Put(invalid,known,"invalid"));
        Reject(()=>cache.Put(original,known,null));
        Reject(()=>cache.Put(original,new byte[]{1},"invalid"));
        long bytes=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++) Check(cache.TryGet(original,known,out _),"repeat lookup changed");
        long allocations=GC.GetAllocatedBytesForCurrentThread()-bytes;
        Check(allocations==0,"steady proof lookups allocate");
        Console.WriteLine("PASS move proof cache: all 11 scalars and every shape byte invalidate; owned snapshot buffers, entity sweep, close, ABA contract and allocation-free steady lookup");
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch(ArgumentException) { return; } throw new Exception("invalid proof cache input accepted"); }
}
