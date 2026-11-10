using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

/// <summary>Opt-in Unity runtime acceptance runner for disposable QA profiles only.</summary>
internal sealed partial class NativeBackgroundQa
{
    internal static bool IsHostProduct => IsProduct("Host");
    internal static bool IsGuestProduct => IsProduct("Guest");
    // Legacy low-level probes still submit directly to ReplicaLink. Original
    // gameplay UI now owns independent ids/results in NativeWorld instead.
    private static bool QaWaiting(ReplicaLink link)=>link.Waiting || NativeWorld.Guest?.HasUiRequests==true;
    private static bool QaWaiting(ShopLink link)=>link.Waiting;
    private static ulong QaIssued(ReplicaLink link)=>Math.Max(link.PendingRequest,NativeWorld.Guest?.LastSubmittedUiRequest ?? 0);
    private static ulong QaCompleted(ReplicaLink link)=>Math.Max(link.CompletedRequest,NativeWorld.Guest?.LastUiCompletion ?? 0);
    private static ushort QaResult(ReplicaLink link)=>NativeWorld.Guest?.LastUiCompletion>link.CompletedRequest
        ? NativeWorld.Guest.LastUiResult : link.Result;
    private static ShopResult QaResult(ShopLink link)=>link.Result;
    private static bool QaReceiptFromProjection(ReplicaLink link)=>NativeWorld.Guest?.LastUiCompletion>link.CompletedRequest
        ? NativeWorld.Guest.LastUiResultFromProjection : link.ResultFromProjection;
    // Cleanup/renew receipts are independent transport traffic too. They must
    // not replace the result of the actual user gesture being asserted.
    private static ushort QaLatestResult(ReplicaLink link) => NativeWorld.Guest?.LastGestureCompletion > QaCompleted(link)
        ? NativeWorld.Guest.LastGestureResult : QaResult(link);

    private static bool IsProduct(string role)
    {
        string product = Application.productName;
        return product == "Probably Stolen Coop QA " + role ||
            System.Text.RegularExpressions.Regex.IsMatch(product,
                "^Probably Stolen Coop Regression[A-Za-z0-9-]* " + role + "$");
    }
    private readonly bool host;
    private int profileCheckpointSlot = -1;
    private double profileReadyAt;
    private bool profileTooltipObserved;
    private bool profilePointerObserved;
    private readonly int frameTarget=Application.isBatchMode ? 30 : 60;
    private bool checkedLocalFramePolicy;
    private bool checkedPlatformIsolation;
    private bool checkedFillInstallation;
    private bool checkedConnectedCap, checkedWaitingCap, checkedDepartedCap;
    private void CheckOriginalBackgroundCap(int expected)
    {
        var options=EmporiumOption.Instance;
        var focus=AccessTools.Field(typeof(EmporiumOption),"appHasFocus");
        bool oldFocus=(bool)focus.GetValue(options),oldLimit=options.fpsCapWhileMinimized;
        try
        {
            // Probe the original option branch on the private desktop only.
            // No OS focus, preference save or display-mode change is needed.
            focus.SetValue(options,false);
            options.fpsCapWhileMinimized=true;
            options.ApplyFpsCap();
            Require(Application.runInBackground && QualitySettings.vSyncCount==0 && Application.targetFrameRate==expected,
                "unexpected room/background frame policy "+Application.targetFrameRate+" vs "+expected);
            Record("PASS original-focus-cap","original unfocused limit with room traffic; target="+expected+" background=true");
        }
        finally
        {
            focus.SetValue(options,oldFocus);
            options.fpsCapWhileMinimized=oldLimit;
            options.ApplyFpsCap();
        }
    }
    private readonly double started = Time.realtimeSinceStartupAsDouble;
    private readonly List<string> evidence = new List<string>();
    private bool loaded, done, observedConnection;
    private readonly bool actorFieldProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-actor-fields") >= 0;
    private bool actorFieldsChecked;
    private int stage, attempts;
    private double next, deadline, disconnectedAt;
    private double progressAt;
    private bool waiting;
    private bool nativeWaiting, nativeTabletOpened;
    private double nativeTabletAt;
    private uint capturedOffer,pendingOffer;
    private ShopAction pending;
    private uint pendingEntity;
    private ShopPlacement pendingPlacement, original;
    private ShopItem chosen;
    private int initialCash, initialCount, initialQuantity;
    private int tradeCash, tradePrice;

    private NativeBackgroundQa(bool host,int soakSeconds) { this.host=host; this.soakSeconds=soakSeconds; }

    internal static NativeBackgroundQa Create()
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "--probably-coop-qa") < 0) return null;
        bool host = IsHostProduct;
        bool guest = IsGuestProduct;
        if ((!host && !guest) || (!Application.isBatchMode && Array.IndexOf(args,"--probably-coop-qa-windowed")<0) ||
            Array.IndexOf(args, "--probably-coop-local") < 0)
        {
            Debug.LogError("[probably_coop QA] refused: requires isolated QA product/profile and explicit QA player mode");
            return null;
        }
        Application.runInBackground = true;
        Application.targetFrameRate = Application.isBatchMode ? 30 : 60;
        Debug.Log($"[probably_coop QA] player mode batch={Application.isBatchMode} target={Application.targetFrameRate} graphics={SystemInfo.graphicsDeviceType}");
        Enabled=true;
        int index=Array.IndexOf(args,"--probably-coop-qa-soak-seconds"),seconds=0;
        if(index>=0 && (index+1>=args.Length || !int.TryParse(args[index+1],out seconds) || seconds is <1 or >1200))
            throw new ArgumentException("Invalid isolated QA duration");
        var runner = new NativeBackgroundQa(host,seconds);
        int profile = Array.IndexOf(args,"--probably-coop-qa-profile-checkpoint");
        if(profile>=0 && (Application.isBatchMode || !NativePerformance.Profiling || seconds<30 ||
            profile+1>=args.Length || !int.TryParse(args[profile+1],out runner.profileCheckpointSlot) || runner.profileCheckpointSlot<0))
            throw new ArgumentException("Checkpoint profiling requires a rendered isolated QA pair, slot and duration");
        int dense = Array.IndexOf(args,"--probably-coop-qa-dense-items");
        if(dense >= 0 && (dense+1 >= args.Length || seconds < 30 ||
            !int.TryParse(args[dense+1],out runner.denseCount) || runner.denseCount is < 1 or > 96))
            throw new ArgumentException("Invalid isolated dense fixture");
        bool compareStock=Array.IndexOf(args,"--probably-coop-qa-compare-stock")>=0;
        bool compareItems=Array.IndexOf(args,"--probably-coop-qa-compare-items")>=0;
        bool compareFields=Array.IndexOf(args,"--probably-coop-qa-compare-fields")>=0;
        bool comparePlain=Array.IndexOf(args,"--probably-coop-qa-compare-plain")>=0;
        bool compareMarkers=Array.IndexOf(args,"--probably-coop-qa-compare-markers")>=0;
        if(host && (compareMarkers || comparePlain || compareStock || compareItems || compareFields || Array.IndexOf(args,"--probably-coop-qa-compare-capture")>=0))
        {
            if(runner.denseCount==0 || seconds<100 || Application.isBatchMode)
                throw new ArgumentException("Capture comparison requires a sustained native dense window");
            NativeCaptureComparison.Arm(compareStock,compareItems,compareFields,comparePlain,compareMarkers);
        }
        return runner;
    }
    internal static bool Enabled;
    internal static void PerturbRootGrid(GameGridInventory grid,System.Reflection.PropertyInfo property,NativeSnapshotIndex.RootGrid shape)
    {
        var cells=(byte[])shape.Cells.Clone();
        cells[cells.Length-1]^=1;
        property.SetValue(grid,new GridShapeBuilder(shape.Width,shape.Height).SetData(cells,shape.Width).SetDataOutside(shape.Outside));
    }

    internal void Tick(RoomSelector room, NativeShop shop, NativeGuest guest, bool inMenu)
    {
        if (done) return;
        // Native option loading may overwrite Create's cap when scenes load.
        // These already-validated disposable batch profiles must not run an
        // uncapped render loop while the user's visible clients are open.
        if(!NativeRoomPacing.PacingLocalPair)
        {
            if(Application.targetFrameRate!=frameTarget) Application.targetFrameRate=frameTarget;
            if(QualitySettings.vSyncCount!=0) QualitySettings.vSyncCount=0;
        }
        try
        {
            if (!checkedPlatformIsolation)
            {
                if (AchievementTracker.Instance == null) return;
                NativeQaPlatformWrites.Verify();
                Record("PASS qa-platform-write-isolation", "both original write entrypoints intercepted; probe guarded independently by statsReady=false");
                checkedPlatformIsolation = true;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if(now>=progressAt && PlayerStore.HasInstance && EmporiumEntry.Instance!=null)
            {
                progressAt=now+20;
                var current=PlayerStore.Instance;
                Record("TRACE progress","stage="+stage+" day="+StoreStation.Instance.dayCounter+" client="+
                    current.currentClientInstance?.GetClientBlueprint()?.identifier+" arrived="+current.isClientArrived+
                    " business="+NativeActorSession.Current?.Activities.HasActiveBusiness+" localDialogue="+
                    (DialogUIManager.Instance?.currentDialog!=null)+" journey="+journeyStep+" night="+dayStep);
            }
            if(!checkedFillInstallation)
            {
                Record("TRACE native-fill-installation",NativeTooltipTiming.HasBenchmarkFillPatch() ? "benchmark" : "native");
                checkedFillInstallation=true;
            }
            if(!Application.isBatchMode && EmporiumOption.Instance!=null && room.HasLobby)
            {
                bool connected=room.Session?.State==LinkState.Connected;
                if(connected && !checkedConnectedCap)
                {
                    CheckOriginalBackgroundCap(room.IsLoopback ? 60 : 30);
                    checkedConnectedCap=true;
                }
                if(host && !connected && (!checkedWaitingCap || observedConnection && !checkedDepartedCap))
                {
                    CheckOriginalBackgroundCap(10);
                    checkedWaitingCap=true;
                    if(observedConnection) checkedDepartedCap=true;
                }
            }
            if (NativeRoomPacing.PacingLocalPair && !checkedLocalFramePolicy && EmporiumOption.Instance != null)
            {
                EmporiumOption.Instance.ApplyFpsCap();
                Require(Application.runInBackground && Application.targetFrameRate == 60 && QualitySettings.vSyncCount == 0,
                    "native focus/option update overrode the local pair frame policy");
                checkedLocalFramePolicy = true;
                Record("PASS dual-window-pacing", "original option reapply; connected loopback uses 60 Hz with background simulation");
            }
            if (now - started > 900+soakSeconds) throw new InvalidOperationException($"timeout stage={stage}");
            if(waterMerchantQa) { TickWaterMerchant(room,shop,inMenu,now); return; }
            if(TickBoundaryGuest(room,guest,inMenu,now)) return;
            if (host)
            {
                if (!loaded && inMenu && room.IsHost)
                {
                    bool profile=profileCheckpointSlot>=0;
                    var checkpoints = CoopRun.ListCheckpoints(onlySlot:profile ? profileCheckpointSlot : (int?)null);
                    if(!freshRun && !profile) checkpoints=checkpoints.Where(CoopRun.CanResume).ToList();
                    if(actorRestoreOnly) checkpoints=new List<CoopRun.Checkpoint> { SelectActorReloadCheckpoint(checkpoints) };
                    if (checkpoints.Count != 1 || !freshRun && !(profile ? CoopRun.PrepareResume(checkpoints[0]) : CoopRun.CanResume(checkpoints[0])))
                        throw new InvalidOperationException("expected one registered isolated checkpoint");
                    loaded = true;
                    Record("PASS checkpoint-identity", $"slot={checkpoints[0].Slot} day={checkpoints[0].Day}");
                    if(!profile) NewGameData.HardReset();
                    if(freshRun)
                    {
                        CoopRun.Armed=true; NewGameData.Instance.saveSlot=-1;
                        NewGameData.Instance.skipIntro=false; NewGameData.Instance.disableTutorialHint=false;
                        var menu=MainMenuUIController.Instance;
                        menu.OnNewGameClick();
                        menu.OnScenarioContinue(StartingScenario.GetByStartType(NewGameData.StartType.APPRENTICE));
                        menu.skipToggle.isOn=false;
                        menu.OnPerkFinishedClick();
                        return;
                    }
                    else if(!profile) NewGameData.Instance.saveSlot = checkpoints[0].Slot;
                    MainMenuUIController.Instance.StartNewGameClick();
                    return;
                }
                if (shop == null) return;
                if(profileCheckpointSlot>=0)
                {
                    ProfileCheckpoint(room,now,room.Session?.Native?.Revision>=2 && NativeWorld.Host?.PartnerSetupRequiredForQa==false);
                    return;
                }
                WriteActorRunIdentity();
                if(actorRestoreOnly) { TickActorReloadHost(room); return; }
                if(mechanismsOnly) { TickMechanismsHost(room,now); return; }
                if (actorFieldProbe && !actorFieldsChecked)
                {
                    foreach (string actorDetail in NativeActorQa.Run(PlayerStore.Instance))
                        Record("PASS native-actor-field-check", actorDetail);
                    foreach (string checkpointDetail in NativeActorCheckpointQa.Run())
                        Record("PASS native-actor-checkpoint-memory-check", checkpointDetail);
                    Record("PASS native-actor-checkpoint-memory-complete", "real ES3 reader; synthetic bytes only; no native save or load");
                    actorFieldsChecked = true;
                    Record("PASS native-actor-fields-complete", "explicit QA installation, real native queries, scoped partner edits and complete unpatch; host state unchanged");
                    if(NativeWorld.ActorProjectionQaRequested)
                        NativeWorld.Host.EnableQaActorProjection(PlayerStore.Instance);
                }
                if(freshRun) { TickFreshHost(room,room.Session,now); return; }
                if(scratchOnly)
                {
                    if(stage==0)
                    {
                        NotebookUIManager.Instance.CloseUI(); SeedMachine(); SeedScratchControl(); SeedScratchFrontends(); stage=1;
                        ModHelper.WriteModDataFileText("probably_coop","qa-room.txt",room.Code);
                        Record("FIXTURE scratcher-only","isolated native GPU scratch workflow; no day or full-play acceptance scope");
                    }
                    bool connected=room.Session?.State==LinkState.Connected; observedConnection|=connected;
                    CheckHostScratchFrontends();
                    if(scratchHostDone)
                    {
                        if(scratchHostFinishAt==0) scratchHostFinishAt=now+3;
                        if(now>=scratchHostFinishAt) Finish("PASS host runtime");
                    }
                    else if(observedConnection && !connected) throw new InvalidOperationException("scratch guest departed before source assertions completed");
                    return;
                }
                if(stripOnly)
                {
                    if(stage==0)
                    {
                        NotebookUIManager.Instance.CloseUI(); SeedMachine(); SeedStripControl(); SeedStripFrontends(); stage=1;
                        ModHelper.WriteModDataFileText("probably_coop","qa-room.txt",room.Code);
                        Record("FIXTURE water-strip-only","isolated native strip workflow; no full-play or day acceptance scope");
                    }
                    bool connected=room.Session?.State==LinkState.Connected; observedConnection|=connected;
                    CheckHostStripFrontends();
                    if(stripHostDone)
                    {
                        if(stripHostFinishAt==0) stripHostFinishAt=now+3;
                        if(now>=stripHostFinishAt) Finish("PASS host runtime");
                    }
                    else if(observedConnection && !connected) throw new InvalidOperationException("strip guest departed before source assertions completed");
                    return;
                }
                if(typewriterOnly)
                {
                    if(stage==0)
                    {
                        NotebookUIManager.Instance.CloseUI(); SeedMachine(); SeedBubbleQa(); stage=1;
                        ModHelper.WriteModDataFileText("probably_coop","qa-room.txt",room.Code);
                        Record("FIXTURE typewriter-only","isolated source shop and native input/line tests; no day, inventory interaction or full-play acceptance scope");
                    }
                    bool connected=room.Session?.State==LinkState.Connected;
                    observedConnection|=connected;
                    CheckHostBubbleQa();
                    if(bubbleHostPhase==3)
                    {
                        if(bubbleQaHostFinishAt==0) bubbleQaHostFinishAt=now+3;
                        if(now>=bubbleQaHostFinishAt) Finish("PASS host runtime");
                    }
                    else if(observedConnection && !connected) throw new InvalidOperationException("typewriter guest departed before source assertions completed");
                    return;
                }
                if (stage == 0)
                {
                    NotebookUIManager.Instance.CloseUI();
                    if(NativeActorSession.Current!=null && saveRoundTrip)
                    {
                        // The full-play scenario deliberately starts at the
                        // rent boundary. The separate fresh-run scenario owns
                        // acceptance of native day-one creation and tutorial.
                        StoreStation.Instance.dayCounter=6;
                        PlayerStore.Instance.dayUntilRent=1;
                        Record("FIXTURE full-play-calendar","disposable actor run starts at day 6/rent due next morning; three real native nights must then save days 7, 8 and 9");
                        // Start this bounded mechanism suite from known stock.
                        // Its freshly played source file is backed up by the
                        // runner; only this disposable in-memory scene changes.
                        var entry=EmporiumEntry.Instance;
                        var baseline=entry.invElement.childItems.Concat(entry.backInvinvElement.childItems)
                            .Concat(entry.showcaseElement.childItems).Distinct().ToArray();
                        foreach(var item in baseline) item.Destroy();
                        for(int i=0;i<2;i++)
                        {
                            var item=DirectoryMaster.Item("processed_meat"); GeneralHelper.SetItemOwned(item,true);
                            Require(GraphUtils.TryAcceptAll(entry.invElement,item)==0,"baseline sale fixture did not fit");
                        }
                        Record("FIXTURE full-play-stock","replaced "+baseline.Length+" source stock roots with two native sale goods before adding mechanism fixtures; no source save overwritten");
                    }
                    SeedMachine();
                    SeedTools();
                    SeedTravelAccess();
                    SeedServices();
                    SeedUnload();
                    SeedButcher();
                    SeedNativeFrontends();
                    SeedInspectionControl();
                    SeedBubbleQa();
                    SeedDenseStock();
                    if(NativeActorSession.Current!=null) SeedActorGameplayHost();
                    // A real vanilla item with the opposite ownership state tests both tooltip branches.
                    // This fixture exists only in this disposable run and is never saved.
                    var fixture = DirectoryMaster.Item("processed_meat");
                    GeneralHelper.SetItemOwned(fixture, false);
                    var slot = EmporiumEntry.Instance.backInvinvElement.TryFindOneValidInventorySlot(fixture);
                    Require(slot?.IsValid() == true && slot.TryAcceptOnce() == fixture.unitCount,
                        "could not seed the isolated unowned fixture");
                    Record("PASS isolated-vanilla-fixture", "processed_meat unowned on back counter; no save");
                    if(Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-input-only")>=0) SeedCoupleInputScene();
                    Record("PASS native-authority", $"room={room.Code} day={shop.Capture().Day}");
                    ModHelper.WriteModDataFileText("probably_coop", "qa-room.txt", room.Code);
                    stage = 1;
                }
                if (room.Session?.State == LinkState.Connected)
                {
                    observedConnection = true;
                    if(NativeWorld.Host.PartnerHandCount==2) observedRestartGroup=true;
                    CheckRestartContention(false);
                    if(hostAwaitingRejoin) { hostAwaitingRejoin=false; hostRejoined=true; disconnectedAt=0; Record("PASS host-native-rejoin","same original host process accepted fresh guest handshake and retained live world"); }
                }
                if(Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-input-only")>=0)
                {
                    TickCoupleInputScene(room.Session.Native);
                    return;
                }
                if(lifecycleOnly) { TickLifecycleHost(room,now); return; }
                CheckActorToolFixture();
                CheckActorGameplayHost();
                CheckHostMachine();
                CheckHostButcher();
                CheckHostNativeFrontends();
                CheckHostBubbleQa();
                CheckHostScannerConfront();
                RetireReadDocumentFixtures();
                CheckHostInspectionQa();
                CheckHostContainerHand(room.Session?.Native,now);
                ReleaseDenseStockAfterProbe();
                CheckHostMachinePointer();
                CheckHostDynamicChoices();
                CheckHostNestedEquipment();
                if(saveRoundTrip) SeedBudgetConfirmation();
                CheckHostPhoneIndependence();
                CheckHostInformation(now);
                CheckHostJourney();
                CheckHostDay(room.Session);
                CheckHostEnding(shop,now);
                if (!hostDayStarted && PlayerStore.Instance.currentNegociatedItem != null && shop.Capture().OfferAction == ShopAction.Sell)
                {
                    if (!NegociationUIManager.Instance.IsOpen) { NegociationUIManager.Instance.OpenUI(); nativeTabletAt=now+0.5; }
                    uint offer=shop.Capture().OfferId;
                    if(pendingOffer!=offer) { pendingOffer=offer; nativeTabletAt=now+0.7; }
                    if(now>=nativeTabletAt && capturedOffer!=offer)
                    { CaptureNativeUi("host",PlayerStore.Instance.currentNegociatedItem); capturedOffer=offer; }
                }
                if (observedConnection && room.Session?.State != LinkState.Connected)
                {
                    // Room.Tick detects disconnect after NativeShop.Tick in the same frame.
                    // Give the regular next-frame cleanup a bounded opportunity to run.
                    if (disconnectedAt == 0) disconnectedAt = now;
                    if (shop.Capture().HeldItem != 0 && now - disconnectedAt < 1) return;
                    Require(shop.Capture().HeldItem == 0, "disconnect retained item hold");
                    Require(NativeWorld.Host.TripReady==0 && NativeWorld.Host.ProposedTrip.Length==0 &&
                        !NativeWorld.Host.RemotePhoneOwned && !NativeWorld.Host.PartnerHasHand && !NativeWorld.Host.HasFrontendBindings,
                        "disconnect retained partner consent, hand, phone or frontend ownership");
                    if(!hostAwaitingRejoin) Record("PASS disconnect-cleanup", $"held={shop.Capture().HeldItem}");
                    if(reconnectQa && !hostRejoined)
                    {
                        CheckRestartContention(true);
                        Require(observedRestartGroup,"guest disconnected without the confirmed native group reservation");
                        if(!hostAwaitingRejoin) { hostAwaitingRejoin=true; hostRejoinUntil=now+60; }
                        Require(now<hostRejoinUntil,"native guest did not rejoin the existing host within 60 seconds");
                        return;
                    }
                    Require(hostPointerChecked,"real guest entity cursor was not validated on host");
                    Finish("PASS host runtime");
                }
                return;
            }
            var link = room.Session?.Shop;
            if (guest == null || link?.View.Active != true || now < next) return;
            if (NativeWorld.Guest?.Ready != true) return;
            if(profileCheckpointSlot>=0) { ProfileCheckpoint(room,now,true); return; }
            if(actorRestoreOnly) { if(RunActorReloadGuest(room.Session)) Finish("PASS guest runtime"); return; }
            if(mechanismsOnly) { if(RunMechanismsGuest(room.Session.Native,now)) Finish("PASS guest runtime"); return; }
            if(!RunActorNetworkQa((float)now)) return;
            if(typewriterOnly) { if(RunBubbleQa(room.Session.Native,now)) Finish("PASS guest runtime"); return; }
            if(stripOnly) { if(RunStripFrontends(room.Session.Native,now)) Finish("PASS guest runtime"); return; }
            if(scratchOnly) { if(RunScratchFrontends(room.Session.Native,now)) Finish("PASS guest runtime"); return; }
            if(freshRun) { if(RunFreshGuest(room.Session,now)) Finish("PASS guest runtime"); return; }
            // The input-only Host never runs the actor-gameplay driver.
            if(!resumedQa && Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-input-only")<0 &&
                !RunActorGameplayGuest(room.Session,now)) return;
            if(resumedQa && !ResumeActorGameplayGuest()) return;
            if(lifecycleOnly) { if(RunLifecycleGuest(room.Session,now)) Finish("PASS guest runtime"); return; }
            ResumeNativeQa(link);
            if (nativeWaiting)
            {
                if (QaWaiting(room.Session.Native)) { if(now>deadline) throw new TimeoutException("native button reply"); return; }
                Require(QaResult(room.Session.Native)==(ushort)ShopResult.Applied,"original tablet command rejected: "+QaResult(room.Session.Native));
                Record("PASS original-tablet-command","native primary Button.onClick -> authenticated native command -> host native sale");
                nativeWaiting=false; stage++; next=now+0.8; return;
            }
            if (waiting)
            {
                if (QaWaiting(link))
                {
                    if (now > deadline) throw new InvalidOperationException($"ack timeout {pending}");
                    return;
                }
                if (QaResult(link) == ShopResult.Stale && attempts++ < 4)
                {
                    next = now + 0.5;
                    link.Submit(pending, pendingEntity, now, pendingPlacement);
                    return;
                }
                bool reject = stage == 3;
                if (reject ? QaResult(link) is not (ShopResult.NoSpace or ShopResult.Unavailable) :
                    QaResult(link) != ShopResult.Applied)
                    throw new InvalidOperationException($"{pending} result={QaResult(link)} stage={stage}");
                Record("PASS authoritative-" + pending, $"stage={stage} result={QaResult(link)} revision={link.Revision}");
                waiting = false;
                stage++;
                next = now + 0.5;
                return;
            }
            if (stage == 0 && !RunRapidHandsQa(room.Session.Native, now)) return;
            if(stage==0 && Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-input-only")>=0)
            {
                Require(NativeWorld.Guest.AuditProjection(out string inputAudit),inputAudit);
                Finish("PASS rapid input runtime");
                return;
            }
            if (!guest.AuditProjection(link.View, out string detail))
                throw new InvalidOperationException(detail);
            switch (stage)
            {
                case 0:
                    Require(Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-native-guest")<0 &&
                        PlayerStore.Instance.saveSlotId==-1,"normal guest entry depends on a development switch or loaded a save slot");
                    Record("PASS normal-native-join","joined native scene without guest development switch; replica slot=-1");
                    Record("PASS native-projection", detail);
                    chosen = link.View.Items.FirstOrDefault(x => x.Owned && x.Movable && x.Place == 1 &&
                        x.Width <= 2 && x.Height <= 2);
                    if (chosen == null) throw new InvalidOperationException("no movable fixture in saved stock");
                    original = new ShopPlacement(chosen.Place, chosen.X, chosen.Y, chosen.Transform);
                    initialCash = link.View.Cash;
                    initialCount = link.View.Items.Count;
                    initialQuantity = link.View.Items.Sum(x => x.Quantity);
                    stage++;
                    break;
                case 1:
                    Require(NativeGuest.Active, "guest native mutation guard inactive");
                    Record("PASS guest-replica-mode", $"slot={PlayerStore.Instance.saveSlotId}");
                    stage++;
                    break;
                case 2: Send(link, ShopAction.PickUp, chosen.Id); break;
                case 3: Send(link, ShopAction.Place, chosen.Id, new ShopPlacement(7, 23, 9, 0)); break;
                case 4:
                    Require(link.View.HeldItem == chosen.Id, "invalid place lost hold");
                    Send(link, ShopAction.Release, chosen.Id);
                    break;
                case 5: Send(link, ShopAction.PickUp, chosen.Id); break;
                case 6: PlaceIn(guest, link, 2); break;
                case 7:
                    Require(Selected(link).Place == 2, "back counter move missing");
                    Send(link, ShopAction.PickUp, chosen.Id);
                    break;
                case 8: PlaceIn(guest, link, 7); break;
                case 9:
                    Require(Selected(link).Place == 7, "showcase move missing");
                    Send(link, ShopAction.PickUp, chosen.Id);
                    break;
                case 10: Send(link, ShopAction.Place, chosen.Id, original); break;
                case 11:
                    if(!stockVerified)
                    {
                    var item = Selected(link);
                    Require(item.Place == original.Container && item.X == original.X && item.Y == original.Y &&
                        item.Owned && item.Quantity == chosen.Quantity && link.View.HeldItem == 0 &&
                        link.View.Cash == initialCash && link.View.Items.Count == initialCount &&
                        link.View.Items.Sum(x => x.Quantity) == initialQuantity, "conservation/roundtrip mismatch");
                    Record("PASS stock-back-showcase-stock", detail + $" cash={link.View.Cash}");
                    stockVerified=true;
                    }
                    if(!RunMutableLayoutsQa(now)) return;
                    if(!RunSustainedOperations(room.Session.Native,now)) return;
                    if(!PrepareMachine(room.Session.Native,now)) return;
                    if(!RunDynamicChoicesQa(room.Session.Native,now)) return;
                    if(!RunNestedEquipmentQa(room.Session.Native,now)) return;
                    if(!CheckGuestMachinePointer(room.Session)) return;
                    if(!RunNativeTools(room.Session.Native,now)) return;
                    if(!RunNativeFrontends(room.Session.Native,now)) return;
                    if(!RunBubbleQa(room.Session.Native,now)) return;
                    if(!RunIndependentUiQa(room.Session.Native,now)) return;
                    if(!RunContinuousControlQa(room.Session.Native,now)) return;
                    if(!RunAuxiliaryQa(room.Session.Native,now)) return;
                    if(!RunDocumentsQa(room.Session.Native,now)) return;
                    if(!RunRapidHandsQa(room.Session.Native,now)) return;
                    if(!RunGroupQa(room.Session.Native,now)) return;
                    if(!RunSortQa(room.Session.Native,now)) return;
                    if(!RunUnloadQa(room.Session.Native,now)) return;
                    if(!RunGuestButcher(room.Session.Native,now)) return;
                    if(!RunServicesQa(room.Session.Native,now)) return;
                    if(!RunPhoneEdges(room.Session.Native,now)) return;
                    if(!RunWildQa(room.Session.Native,now)) return;
                    if (link.View.Shutter == 0) Send(link, ShopAction.ToggleShutter, 0);
                    else { stage++; next = now + 4; }
                    break;
                case 12:
                    Require(link.View.Shutter == 1, "shutter did not open");
                    Record("PASS native-shutter", "open on guest");
                    if (link.View.Customer.Length == 0) Send(link, ShopAction.RingBell, 0);
                    else stage++;
                    next = now + 8;
                    break;
                case 13:
                    if(!RunScannerConfront(room.Session.Native,now)) return;
                    if(!RunInspectionQa(room.Session.Native,now)) return;
                    if(!PrepareOpeningSale(room.Session,guest,now)) return;
                    Require(link.View.Customer.Length>0,"native sale regression has no customer");
                    Record("PASS native-customer", link.View.Customer);
                    Record(link.View.Items.Any(x => !x.Owned) ? "PASS unowned-projection" : "NOT_RUN unowned-projection", detail);
                    var saleClient=PlayerStore.Instance.currentClientInstance.GetClientBlueprint();
                    chosen=link.View.Items.FirstOrDefault(x=>x.Owned && x.Movable && x.Place==1 &&
                        guest.Replica(x.Id) is GameItem saleItem && saleClient.IsClientBuyingThisItem(saleItem) && !saleClient.IsClientRefusingItem(saleItem) && saleClient.HasBudgetLeftToBuy(saleItem));
                    Require(chosen!=null,"generated first customer has no affordable matching sale fixture");
                    Send(link, ShopAction.PickUp, chosen.Id);
                    break;
                case 14: PlaceIn(guest, link, 2); break;
                case 15: Send(link, ShopAction.SelectOffer, chosen.Id); break;
                case 16:
                    if (link.View.OfferAction != ShopAction.Sell)
                    {
                        throw new InvalidOperationException("native sale mandatory regression offer unavailable");
                    }
                    tradeCash = link.View.Cash;
                    tradePrice = link.View.Price;
                    if(!nativeTabletOpened)
                    {
                        var replica=guest.Replica(chosen.Id);
                        PlayerStore.Instance.currentNegociatedItem=replica;
                        NegociationUIManager.Instance.UpdateUI(replica);
                        NegociationUIManager.Instance.OpenUI(); nativeTabletOpened=true; next=now+1;
                        break;
                    }
                    CaptureNativeUi("guest",guest.Replica(chosen.Id));
                    var button=(TabletButton)AccessTools.Field(typeof(NegociationUIManager),"primaryButton").GetValue(NegociationUIManager.Instance);
                    Require(button?.Interactable==true,"original primary button unavailable");
                    button.button.onClick.Invoke();
                    Require(QaWaiting(room.Session.Native),"original button did not emit a network request");
                    nativeWaiting=true; deadline=now+12;
                    break;
                case 17:
                    var sold = link.View.Items.FirstOrDefault(x => x.Id == chosen.Id);
                    Require(link.View.Cash == tradeCash + tradePrice && (sold == null || !sold.Owned),
                        "sale did not change cash/ownership together");
                    Record("PASS native-sale", $"cash={tradeCash}->{link.View.Cash} price={tradePrice} " +
                        $"sold-item={(sold == null ? "removed-by-native-customer" : "unowned-on-counter")}; " + detail);
                    VerifyMachineProgress();
                    stage++;
                    break;
                case 18:
                    if(!RunRejoinedContentionPickup(now)) return;
                    if(RequestNativeQaRestart()) return;
                    if(!RunJointJourneys(room.Session,now)) return;
                    if(completedDays==0 && !RunGuestDay(room.Session,now)) return;
                    if(!RunAdditionalDays(room.Session,now)) return;
                    var other = link.View.Items.FirstOrDefault(x => x.Owned && x.Movable && x.Id != chosen.Id);
                    Require(other != null, "no second item for disconnect hold test");
                    Send(link, ShopAction.PickUp, other.Id);
                    break;
                case 19:
                    Require(link.View.HeldItem != 0, "missing hold before disconnect");
                    if(!RunGuestEnding(room.Session,now)) return;
                    Record("PASS disconnect-with-active-hold", $"held={link.View.HeldItem}; host checks release");
                    Finish("PASS guest runtime");
                    break;
            }
        }
        catch (Exception ex)
        {
            Record("FAIL native-runtime", ex.ToString());
            Finish("FAIL runtime");
        }
    }

    private ShopItem Selected(ShopLink link) => link.View.Items.First(x => x.Id == chosen.Id);

    private void PlaceIn(NativeGuest guest, ShopLink link, byte container)
    {
        if (!guest.FindQaPlacement(Selected(link), container, out var placement))
            throw new InvalidOperationException($"fixture does not fit container {container}");
        Send(link, ShopAction.Place, chosen.Id, placement);
    }

    private void Send(ShopLink link, ShopAction action, uint entity, ShopPlacement placement = default)
    {
        pending = action;
        pendingEntity = entity;
        pendingPlacement = placement;
        attempts = 0;
        waiting = true;
        deadline = Time.realtimeSinceStartupAsDouble + 12;
        link.Submit(action, entity, Time.realtimeSinceStartupAsDouble, placement);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void CaptureNativeUi(string role, GameItem item)
    {
        var ui=NegociationUIManager.Instance;
        // Call after the fixture has stayed stable across frames. A forced Rebuild
        // here would capture native measurement probes queued for end-of-frame destruction.
        // Capture the rendered frame first. Native tag plates and scroll controls
        // finalise their activation during canvas rendering, after Update.
        if(SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)
            NativeOffscreenCapture.Capture("qa-native-ui");
        var data=new JObject { ["role"]=role,["cash"]=PlayerStore.Instance.playerCash,
            ["item"]=item.identifier,["tooltip"]=JObject.FromObject(NativeMirrorProbe.Tooltip(((GameItemElement)item).GetTooltip())),
            ["tablet"]=NativeNegotiationProbe.Capture(ui.uiObject.transform),
            ["bounds"]=JObject.FromObject(NativeOffscreenCapture.Bounds(ui.uiObject.transform as RectTransform)) };
        ModHelper.WriteModDataFileText("probably_coop","qa-native-ui.json",data.ToString(Formatting.Indented));
    }

    private void Record(string name, string detail)
    {
        string line = name + " " + detail;
        evidence.Add(line);
        Debug.Log("[probably_coop QA] " + line);
        ModHelper.WriteModDataFileText("probably_coop", "qa-results.txt", string.Join("\n", evidence));
    }

    private void ProfileCheckpoint(RoomSelector room,double now,bool ready)
    {
        if(!ready || room.Session?.State!=LinkState.Connected)
        {
            Require(profileReadyAt==0,"profiling peer disconnected before completing the sample");
            return;
        }
        if(profileReadyAt==0)
        {
            profileReadyAt=now;
            Record("TRACE checkpoint-profile-ready",$"day={StoreStation.Instance.dayCounter} native_revision={room.Session.Native.Revision}");
        }
        CheckCheckpointPointer(room.Session,now);
        if(!host && !profileTooltipObserved && now-profileReadyAt>=12)
        {
            var item=EmporiumEntry.Instance.invElement.childItems.OfType<GameItemElement>()
                .FirstOrDefault(i=>i.name?.Length>0 && GeneralHelper.IsItemOwned(i));
            Require(item!=null,"checkpoint profile requires an owned native inventory item");
            // The same native read that drives the pointer tooltip. This probe
            // verifies the focused read path, not physical mouse input.
            var normal=item.GetTooltip(); var advanced=item.GetTooltipAdvanced();
            var focused=NativeWorld.Guest.CurrentFastTooltip(item,false);
            if(focused!=null && ReferenceEquals(normal,focused) && normal.nakedText?.Length>item.name.Length &&
                ReferenceEquals(advanced,NativeWorld.Guest.CurrentFastTooltip(item,true)))
            {
                profileTooltipObserved=true;
                Record("PASS checkpoint-focused-tooltip","normal and advanced native reads use the fresh authoritative descriptions");
            }
        }
        // The host gets a few extra seconds to complete the final telemetry window.
        if(now-profileReadyAt>=soakSeconds+(host ? 7 : 0))
        {
            if(host) Finish("PASS checkpoint-profile-host");
            else if(!done)
            {
                Require(profileTooltipObserved,"no full focused tooltip received during the checkpoint profile");
                Require(profilePointerObserved,"no complete localized remote cursor label rendered during the checkpoint profile");
                Require(NativeWorld.Guest.AuditProjection(out var detail),detail);
                Record("PASS checkpoint-profile-guest", "native projection active through profiling interval"); done=true;
            }
        }
    }

    private void Finish(string result,int? exitCode=null)
    {
        done = true;
        // Close live native panels while their audio/UI services still exist, before
        // Unity begins destroying scene components in an unspecified order.
        StoreUIManager.Instance?.CloseAllUI();
        QaStopping=true;
        foreach(var registration in UnityEngine.Object.FindObjectsOfType<StoreUI>(true)) registration.enabled=false;
        Record(result,$"Unity {(Application.isBatchMode ? "batch" : "windowed")} target={frameTarget}; native calls and authenticated network; no system input; GPU frames recorded when enabled");
        Application.Quit(exitCode ?? (result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1));
    }
    internal static bool QaStopping;
    // Native StoreUI.OnDisable calls back into audio during Unity process teardown.
    // QA has already closed all panels while services are alive. This only suppresses
    // duplicate teardown callbacks in the explicitly launched disposable QA process.
    [HarmonyPatch(typeof(StoreUI),nameof(StoreUI.OnDisable))]
    private static class QaTeardown
    { private static bool Prefix() => !QaStopping; }
}
