using ProbablyCoop.Core;

internal static class ContentsProofCacheTests
{
    internal static void Run()
    {
        var cache=new ContentsProofCache();
        var original=new[]{new ContentsProofCache.Row("parent",false,false),new ContentsProofCache.Row("child",true,false)};
        var source=(ContentsProofCache.Row[])original.Clone(); cache.Put(1,source,"canonical");
        Check(cache.TryGet(1,original,out string proof) && proof=="canonical","exact state lost");
        Check(!cache.TryGet(2,original,out _),"different owner reused proof");
        Check(!cache.TryGet(1,original.Reverse().ToArray(),out _),"traversal order ignored");
        Check(!cache.TryGet(1,original.Take(1).ToArray(),out _),"descendant removal ignored");
        Check(!cache.TryGet(1,original.Append(original[0]).ToArray(),out _),"descendant insertion ignored");
        foreach(int index in new[]{0,1}) foreach(int property in new[]{0,1,2})
        {
            var changed=(ContentsProofCache.Row[])original.Clone(); var row=changed[index];
            changed[index]=new ContentsProofCache.Row(property==0 ? row.Move+"changed" : row.Move,
                property==1 ? !row.Pinned : row.Pinned,property==2 ? !row.Locked : row.Locked);
            Check(!cache.TryGet(1,changed,out _),"changed contents accepted");
        }
        source[0]=new ContentsProofCache.Row("mutated",true,true);
        Check(cache.TryGet(1,original,out _),"mutable caller buffer retained");
        cache.Put(2,source,"other"); cache.Retain(new HashSet<uint>{2});
        Check(cache.Count==1 && cache.RowCount==2 && !cache.TryGet(1,original,out _),"sweep failed");
        cache.Clear(); Check(cache.Count==0 && cache.RowCount==0,"clear failed");
        cache.Put(1,original,"A"); cache.Put(1,source,"B");
        Check(!cache.TryGet(1,original,out _),"old state retained after replacement");
        cache.Put(1,original,"A"); Check(cache.RowCount==2 && cache.TryGet(1,original,out proof) && proof=="A","restore proof contract changed");
        for(uint id=1;id<=ContentsProofCache.MaxEntries+1;id++) cache.Put(id,original,"x");
        Check(cache.Count<=ContentsProofCache.MaxEntries,"entry budget exceeded");
        var large=Enumerable.Repeat(original[0],ContentsProofCache.MaxRows).ToArray();
        cache.Put(3,large,"large"); Check(cache.RowCount==ContentsProofCache.MaxRows,"full budget cache failed");
        cache.Put(4,original,"small"); Check(cache.RowCount==2 && !cache.TryGet(3,large,out _),"row budget exceeded");
        cache.Put(4,large.Append(original[0]).ToArray(),"uncached");
        Check(cache.RowCount==0 && cache.Count==0,"oversized entry retained prior proof");
        Reject(()=>cache.Put(0,original,"x")); Reject(()=>cache.Put(1,Array.Empty<ContentsProofCache.Row>(),"x"));
        Reject(()=>cache.Put(1,new[]{new ContentsProofCache.Row()},"x")); Reject(()=>cache.Put(1,original,null));
        cache.Put(1,original,"canonical");
        // Warm the dictionary/interface/string paths before measuring steady
        // calls; first-use runtime/JIT initialization is not a cache allocation.
        for(int i=0;i<10000;i++) Check(cache.TryGet(1,original,out _),"warm lookup failed");
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++) Check(cache.TryGet(1,original,out _),"steady lookup failed");
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        if(allocated!=0) throw new Exception("steady lookups allocated "+allocated+" bytes");
        Console.WriteLine("PASS contents proof cache: exact ordered move/pin/lock state, insert/remove, source isolation, sweep, replacement, strict budgets and allocation-free hits");
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch(ArgumentException) { return; } throw new Exception("Invalid contents proof cache input accepted"); }
}
