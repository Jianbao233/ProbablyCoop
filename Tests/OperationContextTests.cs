using ProbablyCoop.Core;

internal static class OperationContextTests
{
    internal static void Run()
    {
        var context = new OperationContext();
        ulong first=context.Observe(6,1,"shop",false);
        if(first==0 || context.Observe(6,1,"shop",false)!=first) throw new Exception("unchanged context invalidated pending inputs");
        ulong departed=context.Observe(6,1,"shop",true);
        ulong arrived=context.Observe(6,1,"engineer",false);
        ulong returned=context.Observe(6,1,"shop",false);
        ulong closed=context.Observe(6,2,"shop",false);
        ulong nextDay=context.Observe(7,1,"shop",false);
        if(new[]{first,departed,arrived,returned,closed,nextDay}.Distinct().Count()!=6)
            throw new Exception("day/phase/location ABA reused an old command generation");
        Console.WriteLine("PASS operation context: stable pending proofs, departure/arrival, return-location ABA, store phase and next day");
    }
}
