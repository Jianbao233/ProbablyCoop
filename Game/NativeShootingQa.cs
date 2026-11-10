using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

// Disposable mechanism fixtures use original arrival/dismiss/shoot/SEC methods.
// They are not part of the natural operating-day acceptance receipt.
internal sealed partial class NativeBackgroundQa
{
    private const string ShootingLedger="QA_SHOOTING_LEDGER",ShootingOffer="QA_SHOOTING_OFFER";
    private GameItem shootingQaLedger;
    private bool shootingQaQueued;
    private bool ShootingQaHostDone { get; set; }
    private int shootingQaHostRound,shootingQaGuestRound,shootingQaStep,shootingQaPhase;
    private readonly int[] shootingQaDismissed=new int[4];
    private readonly GameItemElement[] shootingQaOffers=new GameItemElement[4];
    private readonly uint[] shootingQaOfferIds=new uint[4],shootingQaGuestOfferIds=new uint[4];
    private readonly bool[] shootingQaHostAimCaptured=new bool[4];
    private int shootingQaCash,shootingQaAug,shootingQaCrime;
    private double shootingQaNext,shootingQaUntil,shootingQaHostNext;
    private byte[] shootingQaOld;
    private ulong shootingQaReplay;
    private Vector3 shootingQaArm;
    private bool shootingQaSawMode,shootingQaSawWound,shootingQaSawBody;
    private int shootingQaExpectedDrop,shootingQaObservedDrops,shootingQaRecoilOnRejoin,shootingQaFlashOnRejoin;
    private double shootingQaRejoinBy;
    private bool shootingQaResumeVerified;
    private ulong shootingQaResumeRequest;

    private void CheckShootingQaConnection(bool connected,double now)
    {
        if(ShootingQaHostDone) return;
        var world=NativeWorld.Host;
        if(connected)
        {
            if(shootingQaRejoinBy!=0)
            {
                shootingQaRejoinBy=0;
                Record("PASS host-shooting-rejoin", "fresh peer joined after shooting restart " + shootingQaObservedDrops);
            }
            if(world.ShootingEpisode==1 && world.ShootingState==NativeWorld.ShootingPhase.Aiming)
                shootingQaExpectedDrop=Math.Max(1,shootingQaExpectedDrop);
            if(world.ShootingEpisode==2 && world.ShootingState is NativeWorld.ShootingPhase.Fired or NativeWorld.ShootingPhase.Leaving or NativeWorld.ShootingPhase.Police)
                shootingQaExpectedDrop=2;
            return;
        }
        if(!observedConnection) return;
        if(shootingQaRejoinBy==0)
        {
            Require(shootingQaExpectedDrop==shootingQaObservedDrops+1,"mechanism Guest departed outside a shooting restart boundary");
            shootingQaObservedDrops++;
            shootingQaRejoinBy=now+65;
            Record("PASS host-shooting-disconnect", "source observed loss of peer at shooting boundary " + shootingQaObservedDrops);
        }
        Require(now<shootingQaRejoinBy,"shooting Guest did not rejoin the same Host");
    }

    private void RestartShootingGuest(string boundary,int code)
    {
        ModHelper.WriteModDataFileText("probably_coop","qa-shooting-rejoin.json",new JObject {
            ["command"]=Encoding.UTF8.GetString(shootingQaOld), ["offer"]=shootingQaGuestOfferIds[shootingQaGuestRound]
        }.ToString(Formatting.None));
        Record("PASS shooting-restart-"+boundary,"guest requested process exit during native " + NativeWorld.Guest.ShootingState);
        Finish("PASS guest shooting restart requested",code);
    }

    private bool ResumeShootingQa(ReplicaLink link,double now)
    {
        if(shootingQaResumeVerified) return true;
        if(shootingQaUntil==0) shootingQaUntil=now+230;
        Require(now<shootingQaUntil,"rejoined shooting incident did not finish");
        var world=NativeWorld.Guest;
        if(QaWaiting(link)) return false;
        if(shootingQaResumeRequest!=0)
        {
            Require(link.CompletedRequest==shootingQaResumeRequest && link.Result==(ushort)ShopResult.Stale,
                "rejoin accepted a prior shooting proof");
            shootingQaGuestRound=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-shooting-after-fire")>=0 ? 2 : 1;
            shootingQaRecoilOnRejoin=NativeShootingQaEvidence.LocalRecoil;
            shootingQaFlashOnRejoin=NativeShootingQaEvidence.LocalFlash;
            shootingQaResumeVerified=true;
            Record("PASS guest-shooting-rejoin", "native camera and retired offer clear; stale pre-disconnect action rejected on new link, boundary " + shootingQaGuestRound);
            return true;
        }
        if(world.HasDialogue) { DialogUIManager.Instance.OnAdvancePressed(); return false; }
        if(world.ShootingState!=NativeWorld.ShootingPhase.Complete || StoreUIManager.Instance.isShootingMode) return false;
        var state=JObject.Parse(ModHelper.ReadModDataFileText("probably_coop","qa-shooting-rejoin.json"));
        var command=JObject.Parse((string)state["command"]);
        var ui=StoreUIManager.Instance;
        Require((ulong)command["episode"]==world.ShootingEpisode && !world.Objects.ContainsKey((uint)state["offer"]) &&
            !ui.isShot && !ui.body.activeSelf && ui.shotClientInstance==null && ui.mainCamera.activeSelf,
            "rejoin kept the old offer or native aiming/body state");
        Require(link.Submit(Encoding.UTF8.GetBytes((string)state["command"]),now),"cannot replay pre-disconnect shooting proof");
        shootingQaResumeRequest=link.PendingRequest;
        return false;
    }

    private void SeedShootingQa()
    {
        Require(Enabled && mechanismsOnly,"shooting fixtures require explicit isolated mechanism QA");
        shootingQaLedger=SeedFrontendItem("red_beer",ShootingLedger);
        Require(shootingQaLedger.identifier=="red_beer","shooting ledger factory returned a fallback item");
        shootingQaLedger.ModifyTag(ShootingLedger,t=>t.Enable().SetInt(0));
        NativeShootingQaEvidence.Reset();
        Record("FIXTURE shooting","queued after independent logistics/appraisal/tab tests; four disposable native customers, unlock through original Shotgun.Unlock; no save");
    }
    private static int ShootingCrime(PlayerStore store)
    {int i=store.secData.GetCrimeIndex("MANSLAUGHTER");return i>=0 && i<store.secData.crimeAmountTotal.Count ? store.secData.crimeAmountTotal[i] : 0;}
    private static string ShootingOfferRound(int round)=>ShootingOffer+"_"+round;
    private void CheckHostShootingQa()
    {
        if(ShootingQaHostDone || !ExtendedLogisticsQaHostDone) return;
        var store=PlayerStore.Instance; var world=NativeWorld.Host; var ui=StoreUIManager.Instance;
        if(store.storeState==PlayerStore.StoreState.MORNING)
        {if(store.CanStartDay()) StoreShutterButton.Instance.OnClick();return;}
        Require(store.storeState==PlayerStore.StoreState.OPEN,"shooting QA expected original open store");
        if(!shootingQaQueued)
        {
            Shotgun.Unlock();
            for(int i=3;i>=0;i--)
            {
                int round=i;
                var client=new StoreClient {identifier="QA_SHOOTING_"+i,displayName="QA shotgun "+i,
                    spriteName=SpriteDict.GetRandomScavSpriteName(),clientIntent=StoreClient.ClientIntent.SELL,mainDialogue=null,
                    isIntroduced=true,isAug=i==1,arrestable=true,dismissable=true,clientFaction=StoreClient.FACTION_LOWER};
                Require(SpriteDict.Instance.spriteDictionary.ContainsKey(client.spriteName),"shooting fixture portrait absent from original sprite pool");
                client.OnArrival=()=>
                {
                    var offer=DirectoryMaster.Item("red_beer");
                    Require(offer is GameItemElement && offer.identifier=="red_beer" && shootingQaOffers[round]==null,
                        "shooting native offer factory/arrival invalid or repeated");
                    offer.EnableTag(ShootingOffer);offer.EnableTag(ShootingOfferRound(round));
                    shootingQaOffers[round]=(GameItemElement)offer;
                    NativeShootingQaEvidence.TrackOffer((GameItemElement)offer,round);
                    store.AddDirectSellingItemToTable(offer,false);
                    Require(offer.parentInventory==EmporiumEntry.Instance.frontInvinvElement && !GeneralHelper.IsItemOwned(offer),
                        "shooting offer fixture has no original front-counter space/ownership");
                    Record("TRACE shooting-offer-arrival","round="+round+" native="+offer.identifier+" next-customer offers are independent entities");
                };
                client.OnDismiss=()=>shootingQaDismissed[round]++;
                store.storeClientManager.AddNextClient(client);
            }
            shootingQaCash=store.playerCash; shootingQaAug=store.runStats.augsShot; shootingQaCrime=ShootingCrime(store);
            shootingQaQueued=true; shootingQaLedger.ModifyTag(ShootingLedger,t=>t.Enable().SetInt(1));
            return;
        }
        for(int i=0;i<shootingQaOffers.Length;i++)
            if(shootingQaOffers[i]!=null && shootingQaOfferIds[i]==0)
                shootingQaOfferIds[i]=world.PointerIdentity(shootingQaOffers[i]);
        if(world.ShootingState==NativeWorld.ShootingPhase.Aiming && world.ShootingEpisode is >=1 and <=4)
        {
            int aiming=(int)world.ShootingEpisode-1;
            if(!shootingQaHostAimCaptured[aiming] && ui.isShootingMode && ui.shootingUI.activeSelf)
            {NativeOffscreenCapture.Capture("qa-shooting-host-aim-"+aiming);shootingQaHostAimCaptured[aiming]=true;}
        }
        if(shootingQaHostRound==3 && store.currentClientInstance?.GetClientBlueprint().identifier=="QA_SHOOTING_3" &&
            store.isClientArrived && !ui.isAboutToStartMainDialog)
        {
            if(shootingQaPhase==0)
            {ui.shotgun.GetComponent<Shotgun>().OnClick();shootingQaPhase=1;shootingQaHostNext=Time.realtimeSinceStartupAsDouble+2;}
            else if(shootingQaPhase==1 && world.ShootingState==NativeWorld.ShootingPhase.Aiming && Time.realtimeSinceStartupAsDouble>=shootingQaHostNext)
            {ui.ExitShootingMode();shootingQaPhase=2;}
        }
        if(shootingQaHostRound>=4 || shootingQaDismissed[shootingQaHostRound]==0 || world.ShootingState!=NativeWorld.ShootingPhase.Complete ||
            store.isHandlingShootout || store.lockNextBellFlag!=0 || ui.secGameObject.gameObject.activeSelf || ui.isShootingMode) return;
        int r=shootingQaHostRound;
        Require(shootingQaDismissed[r]==1,"shooting customer dismissed more than once: "+r);
        var retiredOffer=shootingQaOffers[r];
        var removal=NativeShootingQaEvidence.OfferRemoval(retiredOffer);
        int expectedCalls=r is 1 or 2 ? 2 : 1;
        Require(retiredOffer!=null && shootingQaOfferIds[r]!=0 && retiredOffer.IsDestroyed() && retiredOffer.parentInventory==null &&
            !EmporiumEntry.Instance.GetAllNonOwnedItem().Any(item=>ReferenceEquals(item,retiredOffer)) &&
            removal.Removals==1 && removal.Destroys==1 && removal.DestroyedByRemove &&
            NativeShootingQaEvidence.IncidentRemovalCalls((ulong)r+1)==expectedCalls,
            "native shooting/dismiss did not destroy the exact prior offer once: round="+r+" id="+shootingQaOfferIds[r]+" "+removal);
        if(r+1<shootingQaOffers.Length && shootingQaOffers[r+1] is GameItemElement followingOffer)
            Require(!ReferenceEquals(retiredOffer,followingOffer) && !followingOffer.IsDestroyed() && !GeneralHelper.IsItemOwned(followingOffer) &&
                EmporiumEntry.Instance.GetAllNonOwnedItem().Any(item=>ReferenceEquals(item,followingOffer)),
                "prior shooting cleanup destroyed the automatic next customer's independent offer");
        if(r==0) Require(store.playerCash==shootingQaCash && store.runStats.augsShot==shootingQaAug && ShootingCrime(store)==shootingQaCrime,
            "unfired holster executed kill consequence");
        if(r==1) Require(store.runStats.augsShot==shootingQaAug+1 && store.playerCash>shootingQaCash && ShootingCrime(store)==shootingQaCrime &&
            NativeShootingQaEvidence.GoodKills==1 && NativeShootingQaEvidence.GoodActor==NativeActorRun.Slot.Partner,"native augmented kill/reward actor or once count wrong");
        if(r==2) Require(store.runStats.augsShot==shootingQaAug+1 && ShootingCrime(store)==shootingQaCrime+(StoreStation.Instance.dayCounter>=50?750:StoreStation.Instance.dayCounter>=25?500:250) &&
            NativeShootingQaEvidence.BadKills==1 && NativeShootingQaEvidence.BadActor==NativeActorRun.Slot.Partner,"native civilian crime actor or once count wrong");
        shootingQaHostRound++;
        NativeOffscreenCapture.Capture("qa-shooting-host-finished-"+r);
        shootingQaLedger.ModifyTag(ShootingLedger,t=>t.Enable().SetInt(shootingQaHostRound+1));
        Record("PASS source-shooting-round",r+": offerId="+shootingQaOfferIds[r]+" destroyed once by native RemoveNotOwnedItemFromTable; nativeRemoveCalls="+
            expectedCalls+"; next-offer-retained="+(r+1<shootingQaOffers.Length && shootingQaOffers[r+1]!=null)+
            "; native dismiss once; flags zero; good="+NativeShootingQaEvidence.GoodKills+" bad="+NativeShootingQaEvidence.BadKills);
        if(shootingQaHostRound==4)
        {
            Require(shootingQaObservedDrops==2 && shootingQaRejoinBy==0,"both shooting guest reconnects must be observed by the source");
            Require(NativeShootingQaEvidence.SourceShots==2 && NativeShootingQaEvidence.SourceShootouts==2 &&
                NativeShootingQaEvidence.SourceAchievements==2,"source native shooting count differs from two accepted shots");
            ShootingQaHostDone=true;
            Record("PASS host-native-shooting","unfired guest holster, augmented and civilian native aftermath, Host input propagation; exact original counts and actor continuation; no pending police flags");
        }
    }
    private bool RunGuestShootingQa(ReplicaLink link,double now)
    {
        if(shootingQaStep==99) return true;
        if(shootingQaUntil==0) shootingQaUntil=now+230;
        Require(now<shootingQaUntil,"native shooting QA timeout round="+shootingQaGuestRound+" step="+shootingQaStep+" phase="+NativeWorld.Guest?.ShootingState);
        var world=NativeWorld.Guest; var store=PlayerStore.Instance;var ui=StoreUIManager.Instance;
        if(world?.Ready!=true || QaWaiting(link) || now<shootingQaNext) return false;
        var marker=world.Objects.Values.OfType<GameItem>().SingleOrDefault(item=>item.IsTag(ShootingLedger));
        if(marker==null || marker.GetTagReadonly(ShootingLedger).GetInt()==0) return false;
        if(shootingQaStep==0)
        {
            if(world.PendingMainDialogue) return false;
            string id=store.currentClientInstance?.GetClientBlueprint().identifier;
            if(id!="QA_SHOOTING_"+shootingQaGuestRound)
            {
                if(world.HasDialogue) {DialogUIManager.Instance.OnAdvancePressed();shootingQaNext=now+0.2;return false;}
                if(store.lockNextBellFlag>0 || StoreClientMono.Instance.IsMoving()) return false;
                if(store.currentClientInstance!=null) UnityEngine.Object.FindObjectOfType<StoreDismissButton>().OnClick();
                else UnityEngine.Object.FindObjectOfType<StoreBell>().OnClick();
                shootingQaNext=now+0.3;return false;
            }
            if(!store.isClientArrived) return false;
            var roundOffer=EmporiumEntry.Instance.GetAllNonOwnedItem().SingleOrDefault(item=>item.IsTag(ShootingOfferRound(shootingQaGuestRound)));
            if(roundOffer==null) return false;
            uint offerId=world.PointerIdentity(roundOffer);
            Require(offerId!=0 && !shootingQaGuestOfferIds.Contains(offerId),"shooting rounds reused an old offer identity");
            shootingQaGuestOfferIds[shootingQaGuestRound]=offerId;
            shootingQaSawMode=shootingQaSawWound=shootingQaSawBody=false;
            if(shootingQaGuestRound==3) {shootingQaStep=8;return false;}
            if(shootingQaGuestRound==0)
            {
                var offer=roundOffer;
                var slot=EmporiumEntry.Instance.frontInvinvElement.TryInventorySlot(offer,offer.unitCount,offer.modifiedShape,offer.modifiedState);
                Require(slot?.IsValid()==true,"original offer pickup target unavailable");
                Require(offer is GameItemElement,"shooting offer has no original draggable view");
                BeginAtNativeSlot((GameItemElement)offer,slot,holdAcrossFrames:true);shootingQaStep=1;return false;
            }
            ui.shotgun.GetComponent<Shotgun>().OnClick();shootingQaStep=3;return false;
        }
        if(shootingQaStep==1)
        {
            if(!world.HasConfirmedGesture) return false;
            ui.shotgun.GetComponent<Shotgun>().OnClick();Require(QaWaiting(link),"held shotgun attempt emitted no request");shootingQaStep=2;return false;
        }
        if(shootingQaStep==2)
        {
            Require(QaResult(link)==(ushort)ShopResult.Busy && !ui.isShootingMode &&
                EmporiumEntry.Instance.GetAllNonOwnedItem().Any(item=>world.PointerIdentity(item)==shootingQaGuestOfferIds[0]),
                "shotgun destroyed held offer or started an unapproved local incident");
            ReleaseNativeDrag();shootingQaStep=20;return false;
        }
        if(shootingQaStep==20)
        {if(world.HasPendingWork)return false;ui.shotgun.GetComponent<Shotgun>().OnClick();shootingQaStep=3;Record("PASS shooting-held-offer","original guest pickup reserves non-owned counter offer; authoritative gun entry Busy; original release keeps item");return false;}
        if(shootingQaStep==3)
        {
            if(world.ShootingState!=NativeWorld.ShootingPhase.Aiming || !ui.isShootingMode || !ui.shootingUI.activeSelf) return false;
            Require(ui.arm.activeSelf && ui.shootingCamera.activeSelf && !ui.mainCamera.activeSelf && ui.CanShoot(),"original guest aiming frontend/permission wrong");
            shootingQaArm=ui.arm.transform.localPosition;shootingQaNext=now+0.25;shootingQaStep=4;return false;
        }
        if(shootingQaStep==4)
        {
            Require(Vector3.Distance(shootingQaArm,ui.arm.transform.localPosition)>0.00001f,"native Arm breathing did not tick locally");
            NativeOffscreenCapture.Capture("qa-shooting-aim-"+shootingQaGuestRound);
            string action=shootingQaGuestRound==0 ? "exit" : "fire";
            shootingQaOld=Encoding.UTF8.GetBytes(world.ShootingCommand(action).ToString(Formatting.None));
            if(shootingQaGuestRound==0) { RestartShootingGuest("aim",78); return false; }
            ui.Shoot();
            Require(QaWaiting(link),"original aiming action did not submit shared operation");
            shootingQaStep=5;return false;
        }
        if(shootingQaStep==5)
        {
            Require(QaResult(link)==(ushort)ShopResult.Applied,"original shooting action rejected");
            if(shootingQaGuestRound==1)
            {
                if(world.ShootingState==NativeWorld.ShootingPhase.Aiming) return false;
                Require(world.ShootingState is NativeWorld.ShootingPhase.Fired or NativeWorld.ShootingPhase.Leaving or NativeWorld.ShootingPhase.Police,
                    "guest missed the live confirmed shot boundary");
                RestartShootingGuest("fire",79); return false;
            }
            Require(link.Submit(shootingQaOld,now),"cannot resend consumed shooting proof as new request");
            shootingQaReplay=link.PendingRequest;shootingQaStep=6;return false;
        }
        if(shootingQaStep==6)
        {
            Require(link.CompletedRequest==shootingQaReplay && link.Result==(ushort)ShopResult.Stale,"consumed fire/exit proof repeated original action");
            Record("PASS shooting-stale-command","round="+shootingQaGuestRound+" duplicate semantic operation with new transport request ID rejected Stale");
            shootingQaStep=7;
        }
        if(shootingQaStep is 7 or 8)
        {
            shootingQaSawMode|=ui.isShootingMode;
            shootingQaSawWound|=ui.shootingWoundRenderer.gameObject.activeSelf;
            shootingQaSawBody|=ui.shootingBodyRenderer.gameObject.activeSelf || ui.body.activeSelf;
            if(shootingQaStep==8 && ui.isShootingMode) Require(!ui.CanShoot(),"guest observer can fire Host's incident");
            if(world.HasDialogue) {DialogUIManager.Instance.OnAdvancePressed();shootingQaNext=now+0.2;return false;}
            if(marker.GetTagReadonly(ShootingLedger).GetInt()<shootingQaGuestRound+2 || ui.isShootingMode || world.ShootingState!=NativeWorld.ShootingPhase.Complete) return false;
            Require(!world.Objects.ContainsKey(shootingQaGuestOfferIds[shootingQaGuestRound]) &&
                !EmporiumEntry.Instance.GetAllNonOwnedItem().Any(item=>item.IsTag(ShootingOfferRound(shootingQaGuestRound))),
                "retired shooting offer identity survived in replica graph");
            if(shootingQaGuestRound==2) Require(NativeShootingQaEvidence.LocalRecoil>shootingQaRecoilOnRejoin &&
                NativeShootingQaEvidence.LocalFlash>shootingQaFlashOnRejoin && shootingQaSawBody,"native local recoil/flash/body presentation did not execute");
            if(shootingQaGuestRound==3) Require(shootingQaSawMode,"Host Enter/Exit never reached observer native camera");
            NativeOffscreenCapture.Capture("qa-shooting-finished-"+shootingQaGuestRound);
            shootingQaGuestRound++;shootingQaStep=0;
            if(shootingQaGuestRound<4)return false;
            Require(NativeShootingQaEvidence.SourceShots==0 && NativeShootingQaEvidence.SourceShootouts==0 &&
                NativeShootingQaEvidence.SourceAchievements==0 && NativeShootingQaEvidence.GoodKills==0 && NativeShootingQaEvidence.BadKills==0,
                "replica executed original shared shooting aftermath");
            Require(NativeShootingQaEvidence.LocalRecoil==shootingQaRecoilOnRejoin+1 && NativeShootingQaEvidence.LocalFlash==shootingQaFlashOnRejoin+1,
                "replica omitted or replayed an accepted shot animation");
            Require(store.lockNextBellFlag==0 && !store.isHandlingShootout && !ui.isShootingMode && !ui.isShot && ui.shotClientInstance==null && !ui.body.activeSelf,
                "shooting left native lock/camera/corpse behind");
            Record("PASS guest-native-shooting","original aiming and recoil/flash/body, real process reconnects, stale operations and Host observer camera; replica world callbacks never ran");
            shootingQaStep=99;return true;
        }
        return false;
    }
}

internal static class NativeShootingQaEvidence
{
    internal static int SourceShots,SourceShootouts,SourceAchievements,GoodKills,BadKills,LocalRecoil,LocalFlash;
    internal static NativeActorRun.Slot GoodActor,BadActor;
    internal sealed class Offer
    {
        internal int Round,Removals,Destroys;
        internal bool DestroyedByRemove;
        public override string ToString()=>"round="+Round+" containing-removals="+Removals+" native-destroys="+Destroys+" removed-destroyed="+DestroyedByRemove;
    }
    internal sealed class Removal
    {internal ulong Episode;internal GameItemElement[] Items;}
    private static readonly Dictionary<GameItemElement,Offer> offers=new();
    private static readonly Dictionary<ulong,int> removalCalls=new();
    internal static bool Enabled=>NativeBackgroundQa.Enabled && Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-mechanisms-only")>=0;
    internal static void Reset(){SourceShots=SourceShootouts=SourceAchievements=GoodKills=BadKills=LocalRecoil=LocalFlash=0;offers.Clear();removalCalls.Clear();}
    internal static void TrackOffer(GameItemElement item,int round)=>offers.Add(item,new Offer {Round=round});
    internal static Offer OfferRemoval(GameItemElement item)=>item!=null && offers.TryGetValue(item,out var result) ? result : new Offer {Round=-1};
    internal static int IncidentRemovalCalls(ulong episode)=>removalCalls.TryGetValue(episode,out int count) ? count : 0;
    internal static Removal BeforeRemove()
    {
        var world=NativeWorld.Host;
        if(!Enabled || NativeGuest.Active || world?.ShootingOwnsCustomer!=true) return null;
        return new Removal {Episode=world.ShootingEpisode,Items=EmporiumEntry.Instance.GetAllNonOwnedItem()
            .OfType<GameItemElement>().Where(offers.ContainsKey).ToArray()};
    }
    internal static void AfterRemove(Removal call)
    {
        if(call==null)return;
        removalCalls[call.Episode]=IncidentRemovalCalls(call.Episode)+1;
        foreach(var item in call.Items)
        {
            var state=offers[item];state.Removals++;
            state.DestroyedByRemove=item.IsDestroyed() && item.parentInventory==null;
        }
    }
    internal static void Destroyed(GameItemElement item)
    {if(Enabled && !NativeGuest.Active && offers.TryGetValue(item,out var state))state.Destroys++;}
}

[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.RemoveNotOwnedItemFromTable))]
internal static class NativeShootingQaRemovalObserver
{
    private static void Prefix(out NativeShootingQaEvidence.Removal __state)=>__state=NativeShootingQaEvidence.BeforeRemove();
    private static void Postfix(bool __runOriginal,NativeShootingQaEvidence.Removal __state)
    {if(__runOriginal)NativeShootingQaEvidence.AfterRemove(__state);}
}
[HarmonyPatch(typeof(GameItemElement),nameof(GameItemElement.Destroy))]
internal static class NativeShootingQaDestroyObserver
{
    private static void Postfix(GameItemElement __instance,bool __runOriginal)
    {if(__runOriginal)NativeShootingQaEvidence.Destroyed(__instance);}
}
[HarmonyPatch]
internal static class NativeShootingQaObserver
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(StoreUIManager),nameof(StoreUIManager.Shoot));
        yield return AccessTools.Method(typeof(StoreUIManager),nameof(StoreUIManager.StartShootoutSequence));
        yield return AccessTools.Method(typeof(AchievementTracker),nameof(AchievementTracker.CheckAugHunter));
        yield return AccessTools.Method(typeof(AugHelper),nameof(AugHelper.OnGoodKill));
        yield return AccessTools.Method(typeof(AugHelper),nameof(AugHelper.CleanupKill));
        yield return AccessTools.Method(typeof(Arm),nameof(Arm.TriggerRecoil));
        yield return AccessTools.Method(typeof(Arm),nameof(Arm.TriggerMuzzleFlash));
    }
    private static void Postfix(MethodBase __originalMethod,bool __runOriginal)
    {
        if(!__runOriginal || !NativeShootingQaEvidence.Enabled)return;
        string name=__originalMethod.Name;
        if(name==nameof(Arm.TriggerRecoil)){if(NativeGuest.Active)NativeShootingQaEvidence.LocalRecoil++;return;}
        if(name==nameof(Arm.TriggerMuzzleFlash)){if(NativeGuest.Active)NativeShootingQaEvidence.LocalFlash++;return;}
        if(name==nameof(StoreUIManager.Shoot)){if(!NativeGuest.Active)NativeShootingQaEvidence.SourceShots++;return;}
        if(name==nameof(StoreUIManager.StartShootoutSequence))NativeShootingQaEvidence.SourceShootouts++;
        if(name==nameof(AchievementTracker.CheckAugHunter))NativeShootingQaEvidence.SourceAchievements++;
        if(name==nameof(AugHelper.OnGoodKill)){NativeShootingQaEvidence.GoodKills++;NativeShootingQaEvidence.GoodActor=NativeActorSession.Current?.Run.CurrentActor??NativeActorRun.Slot.Host;}
        if(name==nameof(AugHelper.CleanupKill)){NativeShootingQaEvidence.BadKills++;NativeShootingQaEvidence.BadActor=NativeActorSession.Current?.Run.CurrentActor??NativeActorRun.Slot.Host;}
    }
}
