using ProbablyCoop.Core;

internal static class TooltipSnapshotWatermarkTests
{
    internal static void Run()
    {
        var w=new TooltipSnapshotWatermark<int,string>();
        var world=new Dictionary<int,string>{{1,"A"},{2,"B"}};
        ulong Capture()=>w.Capture((id,v)=>world.TryGetValue(id,out var shown) && v==shown);
        w.Sent(1,1,"A"); w.Sent(2,2,"B"); w.Sent(3,1,"A");
        Check(Capture()==0 && Capture()==0,"equivalent delayed replies dirtied world");
        world[1]="C";
        Check(Capture()==3 && Capture()==3,"new snapshot did not supersede older different read");
        w.Sent(4,1,"A"); w.Sent(5,1,"C");
        Check(Capture()==5,"older differing reply could overwrite newer matching text");
        w.Sent(6,2,"B"); world.Remove(2);
        Check(Capture()==6,"removed entity retained an unfenced reply");
        for(int i=0;i<=TooltipSnapshotWatermark<int,string>.MaxTargets;i++) w.Sent((ulong)(7+i),100+i,"same");
        Check(w.Capture((_,_)=>true)==23,"overflow left outdated replies eligible");
        w.Sent(24,1,"old"); w.Reset();
        Check(w.Capture((_,_)=>throw new Exception("native key survived reset"))==24,"epoch retained outstanding reads");
        Console.WriteLine("PASS tooltip watermark: equal pending observations do not dirty the graph; change, reorder, removal, overflow and epoch retire old replies");
    }
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
}
