using ProbablyCoop.Core;

internal static class CommandProjectionTimingTests
{
    internal static void Run()
    {
        var samples=new List<double>();
        var slow=new CommandProjectionTiming();
        int traces=0;
        slow.Slow=(kind,serial,start,finish,reply,elapsed)=>{
            Check(kind=="trip" && serial==1 && start==3 && finish==5 && reply==350 && elapsed==320,"slow trace lost command or first projected revision");
            traces++;
        };
        slow.Sent(1,100,"trip",3); slow.Applied(1,420,5); slow.Applied(1,440,6); slow.Reply(1,true,450);
        Check(traces==1,"slow accepted command trace missing");
        var timing=new CommandProjectionTiming {Completed=samples.Add};
        timing.Sent(1,100); timing.Reply(1,true);
        timing.Applied(0,150);
        Check(samples.Count==0 && timing.Count==1,"pre-command snapshot counted after reply");
        timing.Applied(1,230); timing.Applied(1,300); timing.Reply(1,true);
        Check(samples.SequenceEqual(new[]{130d}) && timing.Count==0,"reply-first duplicate sample");
        timing.Sent(2,300); timing.Applied(2,400); timing.Applied(2,450);
        Check(samples.Count==1,"projection without accepted reply counted");
        timing.Reply(2,true);
        Check(samples.Last()==100,"late reply moved first projection timestamp");
        timing.Sent(3,500); timing.Applied(3,600); timing.Reply(3,false);
        Check(samples.Count==2 && timing.Count==0,"rejected mutation counted");
        timing.Sent(4,700); timing.Reply(4,true);
        timing.Sent(5,710); timing.Reply(5,true);
        timing.Sent(6,720); timing.Reply(6,true);
        timing.Applied(5,900);
        Check(timing.Count==1 && samples.Skip(2).Order().SequenceEqual(new[]{190d,200d}),"coalesced snapshot lost requests or completed newer command");
        timing.Expire(10721);
        Check(timing.Expired==1 && timing.Count==0 && samples.Count==4,"missing projection passed as success");
        for(ulong i=1;i<=CommandProjectionTiming.Capacity+1;i++) timing.Sent(i,11000);
        Check(timing.Count==CommandProjectionTiming.Capacity && timing.Overflowed==1,"timing memory unbounded");
        timing.Clear(); timing.Applied(100,12000); timing.Reply(1,true);
        Check(timing.Count==0 && timing.Expired==0 && timing.Overflowed==0 && samples.Count==4,"old lifetime survived clear");
        timing.Sent(1,13000); timing.Reply(1,true); timing.Applied(1,13100);
        Check(samples.Last()==100,"reused epoch request ID poisoned");
        Console.WriteLine("PASS command-to-projection timing: stale jobs, reply/apply reorder, coalescing, rejects, dedup, bounded expiry and epoch reset");
    }
    private static void Check(bool value,string message) {if(!value) throw new Exception(message);}
}
