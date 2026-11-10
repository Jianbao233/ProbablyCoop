using System;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Targeted replay of the existing native lifecycle assertions. This mode uses
// the ordinary disposable Host stage-zero fixtures and actor gameplay probe;
// its result must never substitute for the separate complete gameplay suite.
internal sealed partial class NativeBackgroundQa
{
    private readonly bool lifecycleOnly = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-lifecycle-only") >= 0;
    private const double LifecycleTimeoutSeconds = 840;
    private bool lifecycleStarted, lifecycleGuestComplete;
    private int lifecycleInitialDay, lifecycleGuestStep;
    private double lifecycleUntil, lifecycleOpenUntil, lifecycleDisconnectedAt;

    private void BeginLifecycle(double now)
    {
        Require(Enabled && lifecycleOnly && saveRoundTrip && (host ? IsHostProduct : IsGuestProduct),
            "lifecycle-only requires isolated native QA with real save verification");
        if(lifecycleStarted) return;
        lifecycleStarted=true;
        lifecycleInitialDay=StoreStation.Instance.dayCounter;
        lifecycleUntil=now+LifecycleTimeoutSeconds;
        Record("TRACE lifecycle-only",
            (host ? "Host" : "Guest")+" targeted actor/travel/three-night replay from day="+lifecycleInitialDay+
            "; reuses disposable full-suite fixtures; NOT complete-gameplay acceptance; timeout="+LifecycleTimeoutSeconds);
    }

    // Called after the usual Host fixture initialization, instead of the full
    // suite's per-frame helpers. The runner separately requires Guest PASS;
    // a disconnect alone is not evidence of successful guest gameplay.
    private void TickLifecycleHost(RoomSelector room,double now)
    {
        BeginLifecycle(now);
        Require(host && actorGameplaySeeded,"lifecycle Host requires its original actor fixtures");
        Require(now<lifecycleUntil,"lifecycle-only Host timed out at day="+StoreStation.Instance.dayCounter);
        var actors=NativeActorSession.Current;
        Require(actors!=null && !actors.Faulted && !actors.Terminal,
            "lifecycle-only Host lost its playable actor session");
        bool connected=room.Session?.State==LinkState.Connected;
        observedConnection|=connected;
        if(!connected)
        {
            if(!observedConnection) return;
            if(lifecycleDisconnectedAt==0) lifecycleDisconnectedAt=now;
            // Leave the normal room/hand cleanup one player-loop opportunity.
            if(now-lifecycleDisconnectedAt<1) return;
            Require(actorGameplayHostDone && hostDayVerified &&
                StoreStation.Instance.dayCounter==lifecycleInitialDay+3 &&
                CoopRun.VerifyCheckpoint(PlayerStore.Instance,lifecycleInitialDay+3),
                "lifecycle guest departed before three verified native nights");
            var world=NativeWorld.Host;
            Require(world!=null && NativeShop.Gate?.HeldForInteraction==null &&
                world.TripReady==0 && world.ProposedTrip.Length==0 && !world.RemotePhoneOwned &&
                !world.PartnerHasHand && !world.HasFrontendBindings,
                "lifecycle disconnect retained partner input or activity ownership");
            Record("PASS lifecycle-only-host",
                "original actor consumption, joint travel and three source checkpoints; day="+lifecycleInitialDay+
                "->"+StoreStation.Instance.dayCounter+"; partner cleanup complete; full gameplay suite still required");
            Finish("PASS host runtime");
            return;
        }
        lifecycleDisconnectedAt=0;
        actors.Store.modData.TryGetValue(NativeActorPartnerSetup.SaveKey,out string setup);
        if(!NativeActorPartnerSetup.IsComplete(setup,actors.Run.Run)) return;
        CheckActorToolFixture();
        CheckActorGameplayHost();
        CheckHostPhoneIndependence();
        // The Guest reads the original documents before opening for business.
        // The existing helper retains the dated procurement paper for all nights.
        RetireReadDocumentFixtures();
        SeedBudgetConfirmation();
        CheckHostJourney();
        CheckHostDay(room.Session);
    }

    // Called only after RunActorGameplayGuest has completed. Returning true
    // lets the ordinary QA caller Finish("PASS guest runtime") and quit.
    private bool RunLifecycleGuest(CoopSession session,double now)
    {
        BeginLifecycle(now);
        Require(!host && actorGameplayGuestStage==6,"lifecycle Guest requires completed original actor gameplay");
        if(lifecycleGuestComplete) return true;
        Require(now<lifecycleUntil,"lifecycle-only Guest timed out step="+lifecycleGuestStep+
            " journey="+journeyStep+" night="+dayStep+" day="+StoreStation.Instance.dayCounter);
        Require(session?.State==LinkState.Connected,"lifecycle-only Guest lost its room");
        var world=NativeWorld.Guest;
        if(world?.Ready!=true || QaWaiting(session.Native) || session.Shop.Waiting || world.HasPendingWork) return false;
        var store=PlayerStore.Instance;
        if(lifecycleGuestStep==0)
        {
            Require(store.storeState==PlayerStore.StoreState.MORNING,
                "lifecycle-only must begin before opening, without skipping native business state");
            if(!RunDocumentsQa(session.Native,now)) return false;
            lifecycleGuestStep=1;
        }
        if(lifecycleGuestStep==1)
        {
            // This existing short flow includes the original bulk/PC interaction
            // and real telephone order. Later delivery assertions remain intact.
            if(!RunServicesQa(session.Native,now)) return false;
            Record("PASS lifecycle-only-prerequisites",
                "original document reads and real supplier phone order; no injected delivery or skipped multiday assertions");
            lifecycleGuestStep=2;
            lifecycleOpenUntil=now+30;
            return false;
        }
        if(lifecycleGuestStep==2)
        {
            Require(now<lifecycleOpenUntil,"lifecycle original morning shutter did not become available");
            Require(store.storeState==PlayerStore.StoreState.MORNING,"lifecycle business opened outside the original shutter request");
            if(!world.CanOpen || store.lockShutterButtonFlag>0) return false;
            StoreShutterButton.Instance.OnClick();
            Require(QaWaiting(session.Native),"lifecycle original shutter emitted no authenticated request");
            lifecycleGuestStep=3;
            lifecycleOpenUntil=now+30;
            return false;
        }
        if(lifecycleGuestStep==3)
        {
            Require(now<lifecycleOpenUntil,"lifecycle native shutter did not finish opening");
            Require(QaResult(session.Native)==(ushort)ShopResult.Applied,
                "lifecycle original shutter rejected: "+QaResult(session.Native));
            if(store.storeState!=PlayerStore.StoreState.OPEN || store.lockShutterButtonFlag>0 ||
                store.lockNextBellFlag>0) return false;
            Record("PASS lifecycle-only-open","original shutter request reached OPEN and native animation locks cleared");
            lifecycleGuestStep=4;
        }
        if(!RunJointJourneys(session,now)) return false;
        if(completedDays==0 && !RunGuestDay(session,now)) return false;
        if(!RunAdditionalDays(session,now)) return false;
        Require(completedDays==3 && StoreStation.Instance.dayCounter==lifecycleInitialDay+3 && store.saveSlotId==-1,
            "lifecycle-only did not complete three native nights with a save-free Guest");
        Record("PASS lifecycle-only-guest",
            "original actor operations, supplier order, three shared destinations and three nightly reports; day="+
            lifecycleInitialDay+"->"+StoreStation.Instance.dayCounter+
            "; all existing mandatory-event assertions retained; NOT complete-gameplay acceptance");
        lifecycleGuestComplete=true;
        return true;
    }
}
