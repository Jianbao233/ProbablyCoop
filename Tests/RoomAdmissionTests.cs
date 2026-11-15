using ProbablyCoop.Core;

internal static class RoomAdmissionTests
{
    internal static void Run()
    {
        var toHost=new Queue<byte[]>();
        var toGuest=new Queue<byte[]>();
        var fingerprint=new byte[Wire.FingerprintSize];
        var host=new CoopSession(true,fingerprint,(bytes,_)=>{toGuest.Enqueue(bytes);return true;},requireApproval:true);
        var guest=new CoopSession(false,fingerprint,(bytes,_)=>{toHost.Enqueue(bytes);return true;});
        byte[] oldRejection;

        Start(0);
        Check(host.JoinPending && guest.JoinPending && guest.State==LinkState.Handshake &&
            host.Native.Revision==0 && guest.Native.Snapshot==null,"pending applicant received a session");
        host.DeclineJoin(.1);
        oldRejection=toGuest.Dequeue(); // Simulate losing the initial datagram.
        guest.Tick(1); Drain(1);
        Check(host.State==LinkState.JoinDeclined && guest.State==LinkState.JoinDeclined,"lost decline did not recover on Hello retry");

        Start(2);
        Check(!guest.Receive(oldRejection,false,2.1) && guest.JoinPending,"refusal from a previous attempt affected a new applicant");
        host.ApproveJoin();
        Check(guest.Receive(toGuest.Dequeue(),false,2.1) && toHost.Count==1,"approved applicant did not receive a Welcome");
        host.SetAcceptingJoins(false,2.2);
        Drain(2.2); // Ready from the earlier Welcome must lose to the pause.
        Check(host.State==LinkState.JoinClosed && guest.State==LinkState.JoinClosed,"paused join accepted a late Ready");

        Start(3);
        Check(host.State==LinkState.JoinClosed && guest.State==LinkState.JoinClosed && !host.JoinPending,"closed room offered a pending admission");
        host.SetAcceptingJoins(true,3.5);
        Start(4);
        host.ApproveJoin(); Drain(4.1);
        Check(host.State==LinkState.Connected && guest.State==LinkState.Connected,"reopened room did not admit an approved new attempt");
        host.SetAcceptingJoins(false,4.2);
        guest.Tick(4.3); host.Tick(4.3); Drain(4.3);
        Check(host.State==LinkState.Connected && guest.State==LinkState.Connected,
            "pausing new joins interrupted an admitted guest");

        using var localHost=new LoopbackLink(fingerprint,0,requireApproval:true);
        using var localGuest=new LoopbackLink(fingerprint,0,localHost.Code);
        DriveUntil(()=>localGuest.Session.JoinPending && localHost.Session.JoinPending);
        Check(localHost.Session.State==LinkState.Handshake && localGuest.Session.State==LinkState.Handshake,
            "direct port connected before room approval");
        localHost.Session.ApproveJoin();
        DriveUntil(()=>localHost.Session.State==LinkState.Connected && localGuest.Session.State==LinkState.Connected);
        Console.WriteLine("PASS room admission: pending without snapshot, reject and packet loss, fresh retries, pause vs late Ready, existing guest retained, real UDP approval");

        void Start(double now)
        {
            host.DropPeer(); host.Connect(now); guest.Connect(now);
            guest.Tick(now); Drain(now);
        }
        void Drain(double now)
        {
            for(int round=0;round<12 && toHost.Count+toGuest.Count>0;round++)
            {
                while(toHost.TryDequeue(out byte[] bytes)) host.Receive(bytes,false,now);
                while(toGuest.TryDequeue(out byte[] bytes)) guest.Receive(bytes,false,now);
            }
        }
        void DriveUntil(Func<bool> condition)
        {
            double now=0;
            for(int i=0;i<40 && !condition();i++)
            {
                now+=.05;
                localGuest.Tick(now); localHost.Tick(now); localGuest.Tick(now);
                Thread.Sleep(5);
            }
            Check(condition(),"real UDP did not reach the expected admission state");
        }
    }

    private static void Check(bool pass,string message)
    { if(!pass) throw new Exception(message); }
}
