using System.Diagnostics;
using ProbablyCoop.Core;

internal static class DirectCaptureOrderTests
{
    internal static void Run()
    {
        var clock=Stopwatch.StartNew();
        using var host=new LoopbackLink(new byte[Wire.FingerprintSize],0);
        using var guest=new LoopbackLink(new byte[Wire.FingerprintSize],0,host.Code);
        int executed=0,captured=0,callbacks=0;
        host.Session.Native.Execute=_=> {executed++; return (ushort)ShopResult.Applied;};
        void Capture()
        {
            callbacks++;
            if(host.Session.State!=LinkState.Connected) return;
            captured=executed;
            host.Session.Native.Publish(new[]{(byte)captured});
        }
        void HostTick()
        {
            int before=callbacks;
            host.Tick(clock.Elapsed.TotalSeconds,Capture);
            Check(callbacks==before+1,"capture callback repeated or skipped");
            Check(captured==executed,"capture lagged received mutation by one transport tick");
        }
        void Until(Func<bool> ready)
        {
            while(!ready())
            {
                Check(clock.Elapsed.TotalSeconds<5,"direct capture ordering timed out");
                guest.Tick(clock.Elapsed.TotalSeconds); HostTick(); Thread.Sleep(1);
            }
        }
        Until(()=>guest.Session.Native.Revision!=0);
        Check(guest.Session.Native.Submit(new byte[]{9},clock.Elapsed.TotalSeconds),"cannot submit capture ordering command");
        // No guest tick between local input and the host observing the command.
        while(executed==0)
        {
            Check(clock.Elapsed.TotalSeconds<5,"local input waited for a guest networking tick");
            HostTick(); Thread.Sleep(1);
        }
        Until(()=>!guest.Session.Native.Waiting && guest.Session.Native.State[0]==1);
        Check(executed==1 && captured==1,"post-command projection not sent");
        // No second session/native tick is added: retries and the existing
        // per-tick chunk limit remain owned by the one Session.Tick call.
        host.Tick(clock.Elapsed.TotalSeconds,()=>host.Session.DropPeer());
        Check(host.Session.State==LinkState.Waiting && host.Session.Native.State==null,"callback epoch reset was ignored");
        Console.WriteLine("PASS direct capture scheduling: real UDP receive -> mutation -> same-tick capture -> bounded session send; handshake and epoch reset");
    }
    private static void Check(bool value,string message) {if(!value) throw new Exception(message);}
}
