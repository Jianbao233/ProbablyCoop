using System.Collections.Generic;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private void VerifyScavengeVisit(NativeActorSession actors)
    {
        Require(Enabled && IsHostProduct && saveRoundTrip, "scavenge verification requires an isolated Host checkpoint");
        var store=actors.Store;
        var partner=actors.Run.Partner;
        var hostPerks=store.startingPerks;
        var partnerPerks=partner.startingPerks;
        int hostAttempts=store.scavengingAttempts, partnerAttempts=partner.scavengingAttempts;
        var station=StoreStation.Instance;
        int day=station.dayCounter;
        var dumpingGround=AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND);
        try
        {
            var proficient=new StartingPerk { id="proficient_scavenger" };
            var careful=new StartingPerk { id="careful_scavenger" };
            store.startingPerks=new List<StartingPerk> { proficient };
            partner.startingPerks=new List<StartingPerk> { careful };
            Verify("01-split",NativeActorRun.Slot.Partner,NativeActorRun.Slot.Host);
            store.startingPerks=new List<StartingPerk> { proficient,careful };
            partner.startingPerks=new List<StartingPerk> { proficient,careful };
            Verify("06-both",NativeActorRun.Slot.Host,NativeActorRun.Slot.Partner);
        }
        finally
        {
            station.dayCounter=day;
            store.startingPerks=hostPerks;
            partner.startingPerks=partnerPerks;
            store.scavengingAttempts=hostAttempts;
            partner.scavengingAttempts=partnerAttempts;
        }

        void Verify(string label,NativeActorRun.Slot first,NativeActorRun.Slot second)
        {
            station.dayCounter=day;
            using(actors.Enter(first)) dumpingGround.Arrive();
            int hostFirst=ReadOrSpend(NativeActorRun.Slot.Host,true);
            int partnerFirst=ReadOrSpend(NativeActorRun.Slot.Partner,true);
            station.dayCounter=day+1;
            using(actors.Enter(second)) dumpingGround.Arrive();
            int hostSecond=ReadOrSpend(NativeActorRun.Slot.Host,false);
            int partnerSecond=ReadOrSpend(NativeActorRun.Slot.Partner,false);
            Record("PASS bug-"+label+"-scavenge",
                $"native arrival {first} then {second}; Host {hostFirst}->0->{hostSecond}, Partner {partnerFirst}->0->{partnerSecond}; attempts seeded as spent between arrivals");
        }

        int ReadOrSpend(NativeActorRun.Slot actor,bool spend)
        {
            using(actors.Enter(actor))
            {
                int max=ScavHelper.GetMaxScavAttempts();
                int left=ScavHelper.GetScavTimeLeft();
                Require(max>0 && left==max, "cooperative arrival did not reset native scavenging: "+actor+" left="+left+" max="+max);
                if(actor==NativeActorRun.Slot.Partner)
                    Require(ScavHelper.GetMinorWoundChance()==0 && ScavHelper.GetMajorWoundChance()==0, "careful scavenger lost native wound protection");
                if(spend)
                {
                    if(actor==NativeActorRun.Slot.Partner) partner.scavengingAttempts=max;
                    else store.scavengingAttempts=max;
                    Require(ScavHelper.GetScavTimeLeft()==0, "scavenging fixture failed to exhaust attempts: "+actor);
                }
                return left;
            }
        }
    }
}
