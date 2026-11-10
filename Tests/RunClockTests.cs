using ProbablyCoop.Core;

internal static class RunClockTests
{
    internal static void Run()
    {
        var host=new RunClockSync(); var guest=new RunClockSync();
        host.Sample(104.5f,73); guest.Receive(host.Serial,host.Seconds,3);
        for(int i=1;i<=19;i++)
        {
            float elapsed=i*0.25f;
            host.Sample(104.5f+elapsed,73+elapsed);
            guest.Receive(host.Serial,host.Seconds,3+elapsed);
            Check(host.Serial==1 && guest.At(3+elapsed)==104.5f+elapsed,"unrelated snapshot reset display clock");
        }
        host.Sample(109.5f,78); Check(host.Serial==2,"missing regular clock calibration");
        guest.Receive(host.Serial,host.Seconds,8); guest.Receive(1,104.5f,8.2);
        Check(guest.At(8.5)==110,"older calibration rewound clock");
        host.Sample(400,78.1); Check(host.Serial==3 && host.Seconds==400,"host clock discontinuity ignored");
        host.Reset(); guest.Reset(); host.Sample(12,501); guest.Receive(host.Serial,host.Seconds,1001);
        Check(host.Serial==1 && guest.At(1003)==14,"old epoch clock retained");
        Console.WriteLine("PASS run clock: native elapsed time, duplicate packets, correction, new peer and unrelated process clock origins");
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
}
