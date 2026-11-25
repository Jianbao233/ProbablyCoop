using ProbablyCoop.Core;

internal static class RunClockTests
{
    internal static void Run()
    {
        var host=new RunClockSync(); var guest=new RunClockSync();
        host.Sample(104.5f,73,true); guest.Receive(host.Serial,host.Seconds,3);
        for(int i=1;i<=2400;i++)
        {
            float elapsed=i*0.25f;
            host.Sample(104.5f+elapsed,73+elapsed,true);
            guest.Receive(host.Serial,host.Seconds,3+elapsed);
            Check(host.Serial==1 && guest.At(3+elapsed)==104.5f+elapsed,"unrelated snapshot reset display clock");
        }
        host.Sample(400,674,false); Check(host.Serial==1,"clock correction published an otherwise idle world");
        host.Sample(400,674,true); Check(host.Serial==2 && host.Seconds==400,"host clock discontinuity ignored");
        guest.Receive(host.Serial,host.Seconds,604); guest.Receive(1,104.5f,604.2);
        Check(guest.At(604.5)==400.5f,"older calibration rewound clock");
        host.Reset(); guest.Reset(); host.Sample(12,501,false); guest.Receive(host.Serial,host.Seconds,1001);
        Check(host.Serial==1 && guest.At(1003)==14,"old epoch clock retained");
        Console.WriteLine("PASS run clock: native elapsed time, duplicate packets, correction, new peer and unrelated process clock origins");
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
}
