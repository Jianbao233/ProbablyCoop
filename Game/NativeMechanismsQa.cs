using System;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// A bounded, real-Unity supplement to the full operating-day regression.
// This branch never creates a full-flow acceptance receipt or reseeds a save.
internal sealed partial class NativeBackgroundQa
{
    private readonly bool mechanismsOnly = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-mechanisms-only") >= 0;
    private bool mechanismLogisticsComplete;
    private bool mechanismAppraisalComplete;
    private bool mechanismStorageComplete;
    private double mechanismsHostFinishAt;

    private void TickMechanismsHost(RoomSelector room,double now)
    {
        if(stage==0)
        {
            Require(NativeActorSession.Current!=null,"mechanism regression requires an ordinary two-actor checkpoint");
            NotebookUIManager.Instance.CloseUI();
            SeedWorkshopQa();
            SeedLogisticsQa();
            SeedAppraisalQa();
            SeedStorageTabsQa();
            SeedShootingQa();
            stage=1;
            ModHelper.WriteModDataFileText("probably_coop","qa-room.txt",room.Code);
            Record("FIXTURE mechanisms-only","original helper CustomUI and shooting workflows; separate from native multi-day acceptance");
        }
        bool connected=room.Session?.State==LinkState.Connected;
        observedConnection|=connected;
        CheckHostWorkshopQa();
        CheckHostLogisticsQa();
        CheckHostAppraisalQa();
        CheckHostStorageTabsQa();
        CheckHostPresetHopperQa();
        CheckHostShootingQa();
        CheckShootingQaConnection(connected,now);
        if(workshopHostDone && LogisticsQaHostDone && AppraisalQaHostDone && StorageTabsQaHostDone && ShootingQaHostDone)
        {
            if(mechanismsHostFinishAt==0) mechanismsHostFinishAt=now+3;
            if(now>=mechanismsHostFinishAt)
            {
                Record("PASS mechanisms-host","supplemental original interaction workflows finished on source");
                Finish("PASS host runtime");
            }
        }
    }

    private bool RunMechanismsGuest(ReplicaLink link,double now)
    {
        if(resumedQa)
        {
            if(!ResumeShootingQa(link,now)) return false;
        }
        else if(!RunGuestMechanismSetup(link,now)) return false;
        if(!RunGuestShootingQa(link,now)) return false;
        Record("PASS mechanisms-guest","supplemental original helper UI, personal effects and shooting completed over actual network");
        return true;
    }

    private bool RunGuestMechanismSetup(ReplicaLink link,double now)
    {
        if(!RunGuestWorkshopQa(link,now)) return false;
        if(!mechanismLogisticsComplete)
        {
            if(!RunGuestLogisticsQa(link,now)) return false;
            mechanismLogisticsComplete=true;
        }
        if(!mechanismAppraisalComplete)
        {
            if(!RunGuestAppraisalQa(link,now)) return false;
            mechanismAppraisalComplete=true;
        }
        if(!mechanismStorageComplete)
        {
            if(!RunGuestStorageTabsQa(link,now)) return false;
            mechanismStorageComplete=true;
        }
        return RunGuestPresetHopperQa(link,now);
    }
}
