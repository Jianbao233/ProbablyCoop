using ProbablyCoop.Core;

internal static class ChoiceLeaseTests
{
    internal static void Run()
    {
        var lease=new ChoiceLease();
        var first=lease.Open(7,2,false);
        Require(!lease.Take(first,7,-1) && !lease.Take(first,7,2) && !lease.Take(first,8,0),"invalid input must not consume");
        Require(lease.Take(first,7,1) && !lease.Take(first,7,1),"callback consumed exactly once even if it throws");
        var second=lease.Open(7,2,true);
        var nested=lease.Open(7,1,true);
        Require(second>first && nested>second && !lease.Take(second,7,0),"replacement invalidates earlier callbacks");
        Require(lease.Take(nested,7,-1) && !lease.Take(nested,7,-1),"cancel once");
        var scene=lease.Open(8,0,true); lease.Clear();
        var reconnect=lease.Open(8,0,true);
        Require(reconnect>scene && !lease.Take(scene,8,-1) && lease.Take(reconnect,8,-1),"retirement never reuses identity");
        foreach(int count in new[]{-1,257})
        {
            try { lease.Open(8,count,true); throw new Exception("unbounded options accepted"); }
            catch(ArgumentOutOfRangeException) { }
        }
        Console.WriteLine("PASS native choice leases: replay, nested replacement, cancellation, context, retirement and bounded options");
    }
    private static void Require(bool value,string message) { if(!value) throw new Exception(message); }
}
