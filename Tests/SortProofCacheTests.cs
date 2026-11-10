using ProbablyCoop.Core;

internal static class SortProofCacheTests
{
    internal static void Run()
    {
        var cache=new SortProofCache();
        var state=new SortProofCache.State(1,3,2,255,"store",true);
        var cells=new byte[]{0,1,2,3,4,255};
        var rows=new[]{new SortProofCache.Row("move-a",false,false),new SortProofCache.Row("move-b",true,true)};
        cache.Put(state,cells,rows,"original-json-sha");
        Check(cache.TryGet(state,cells,rows,out var proof) && proof=="original-json-sha","exact sort state missed");
        foreach(var different in new[]{
            new SortProofCache.State(2,3,2,255,"store",true),new SortProofCache.State(1,2,3,255,"store",true),
            new SortProofCache.State(1,3,2,0,"store",true),new SortProofCache.State(1,3,2,255,"outside",true),
            new SortProofCache.State(1,3,2,255,"store",false)})
            Check(!cache.TryGet(different,cells,rows,out _),"sort scalar change not invalidated");
        for(int i=0;i<cells.Length;i++)
        {
            cells[i]^=1; Check(!cache.TryGet(state,cells,rows,out _),"shape cell change not invalidated"); cells[i]^=1;
        }
        foreach(var changed in new[]{rows.Reverse().ToArray(),rows.Skip(1).ToArray(),rows.Append(rows[0]).ToArray(),
            new[]{new SortProofCache.Row("different",false,false),rows[1]},
            new[]{new SortProofCache.Row("move-a",true,false),rows[1]},
            new[]{new SortProofCache.Row("move-a",false,true),rows[1]}})
            Check(!cache.TryGet(state,cells,changed,out _),"ordered move/pin/debris list change not invalidated");
        var expectedCells=cells.ToArray(); var expectedRows=rows.ToArray(); cells[0]=7; rows[0]=default;
        Check(cache.TryGet(state,expectedCells,expectedRows,out _),"mutable caller buffers retained");
        Check(!cache.TryGet(state,expectedCells.AsSpan(1),expectedRows,out _) && !cache.TryGet(state,expectedCells,null,out _),"invalid lookup matched");
        for(int i=0;i<10000;i++) cache.TryGet(state,expectedCells,expectedRows,out _);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++) if(!cache.TryGet(state,expectedCells,expectedRows,out _)) throw new Exception("warm sort miss");
        Check(GC.GetAllocatedBytesForCurrentThread()==before,"steady sort lookup allocates");
        cache.Put(state,expectedCells,Array.Empty<SortProofCache.Row>(),"empty");
        Check(cache.Count==1 && cache.RowCount==0 && cache.CellCount==6 && cache.TryGet(state,expectedCells,Array.Empty<SortProofCache.Row>(),out proof) && proof=="empty","empty inventory or replacement counts broken");
        cache.Retain(new HashSet<uint>());
        Check(cache.Count==0 && cache.CellCount==0 && cache.RowCount==0,"dead inventory retained");
        var full=new byte[128*128];
        for(uint i=1;i<=SortProofCache.MaxCells/full.Length+1;i++) cache.Put(new SortProofCache.State(i,128,128,0,"",false),full,Array.Empty<SortProofCache.Row>(),"p");
        Check(cache.Count==1 && cache.CellCount==full.Length,"retained shape budget exceeded");
        cache.Clear();
        for(uint i=1;i<=SortProofCache.MaxEntries+1;i++) cache.Put(new SortProofCache.State(i,0,0,0,"",false),Array.Empty<byte>(),Array.Empty<SortProofCache.Row>(),"p");
        Check(cache.Count==1,"entry budget exceeded by empty shapes");
        var hugeRows=Enumerable.Repeat(new SortProofCache.Row("p",false,false),SortProofCache.MaxRows+1).ToArray();
        cache.Put(state,expectedCells,hugeRows,"p");
        Check(!cache.TryGet(state,expectedCells,hugeRows,out _) && cache.RowCount<=SortProofCache.MaxRows,"oversized sort rows retained");
        Reject(()=>cache.Put(default,Array.Empty<byte>(),Array.Empty<SortProofCache.Row>(),"p"));
        Reject(()=>cache.Put(state,Array.Empty<byte>(),expectedRows,"p"));
        Reject(()=>cache.Put(state,expectedCells,new[]{default(SortProofCache.Row)},"p"));
        cache.Clear(); Check(cache.Count==0 && cache.CellCount==0 && cache.RowCount==0,"close retained sort proof");
        Console.WriteLine("PASS exact sort proof cache: all scalars/cells/ordered rows, flags, source ownership, empty inventory, replacement/sweep, bounded memory and zero-allocation hits");
    }
    private static void Check(bool value,string message) {if(!value) throw new Exception(message);}
    private static void Reject(Action action) {try {action();} catch(ArgumentException) {return;} throw new Exception("invalid sort cache state accepted");}
}
