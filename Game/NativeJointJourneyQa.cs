using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly string[] journeyLocations={AfterhourLocation.DUMPING_GROUND,AfterhourLocation.COMMISSARY,AfterhourLocation.DR_JACKSON};
    private readonly Dictionary<string,int> beforeVisits=new Dictionary<string,int>();
    private int journeyStep,journeyIndex;
    private double journeyUntil,journeyNext;
    private string hostObservedScene="";
    private bool eveningFixture;
    private bool blockedTripRetryVerified;
    private const string TravelPackTag="PROBABLY_COOP_QA_TRAVEL_PACK";
    private const string TravelHostPackTag="PROBABLY_COOP_QA_TRAVEL_HOST_PACK";
    private GameItem travelBackpack;
    private GameItem travelHostBackpack;
    private uint travelBackpackId;
    private uint travelHostBackpackId;
    private readonly uint[] journeyMarkers=new uint[2];
    private readonly Dictionary<uint,(int NativeId,int Count,uint Parent)>[] journeyReturnContents=
        new Dictionary<uint,(int NativeId,int Count,uint Parent)>[2];
    private Dictionary<string,int> journeyReturnStock;
    private double journeyCloseUntil;
    private bool journeyCloseBellPending,journeyClosePrepared;
    private StoreClientInstance journeyCloseObserved;
    private readonly HashSet<StoreClientInstance> journeyCloseCustomers=new HashSet<StoreClientInstance>();
    private string journeyCloseBlocked="";
    private void SeedTravelAccess()
    {
        var store=PlayerStore.Instance;
        store.isAfterhoursIntroduced=true; store.isStampIntroduced=true;
        store.storeClientData.isJacksonIntroduced=true; store.visitLeftTonight=3;
        store.commissaryData.redeemedCredit=100;
        store.commissaryData.GetItemData("processed_meat").StockCount=2;
        store.dumpingGroundMinorWoundImmunitySearchesLeft=10;
        store.dumpingGroundMajorWoundImmunitySearchesLeft=20;
        travelBackpack=DirectoryMaster.Item("backpack_small");
        GeneralHelper.SetItemOwned(travelBackpack,true);
        travelBackpack.EnableTag(TravelPackTag);
        var juice=DirectoryMaster.Item("processed_juice"); GeneralHelper.SetItemOwned(juice,true);
        Require(GraphUtils.TryAcceptAll(travelBackpack,juice)==0 &&
            GraphUtils.TryAcceptAll(EmporiumEntry.Instance.invElement,travelBackpack)==0,"travel backpack fixture could not be placed");
        travelHostBackpack=DirectoryMaster.Item("backpack_medium");
        GeneralHelper.SetItemOwned(travelHostBackpack,true);
        travelHostBackpack.EnableTag(TravelHostPackTag);
        // The earlier original PC request consumes processed_meat recursively,
        // including bags. Use a distinct non-food marker so that legitimately
        // fulfilling that order cannot empty the later travel assertion.
        var hostMarker=DirectoryMaster.Item("toilet_paper"); GeneralHelper.SetItemOwned(hostMarker,true);
        hostMarker.EnableTag(TravelHostPackTag+"_CONTENT");
        Require(GraphUtils.TryAcceptAll(travelHostBackpack,hostMarker)==0 &&
            GraphUtils.TryAcceptAll(EmporiumEntry.Instance.invElement,travelHostBackpack)==0,"host travel backpack fixture could not be placed");
        foreach(string id in journeyLocations) beforeVisits[id]=store.GetAfterhourState(id).visits;
        Record("FIXTURE afterhours","three visits and vanilla discoveries; Partner small bag/juice and Host medium bag/tagged non-food marker are different physical entities in disposable memory");
    }
    private void CheckHostJourney()
    {
        var world=NativeWorld.Host; if(world==null) return;
        if(!eveningFixture && PlayerStore.Instance.storeState==PlayerStore.StoreState.CLOSED)
        { eveningFixture=true; PlayerStore.Instance.visitLeftTonight=3; Record("FIXTURE afterhours-quota","three destinations per evening for coverage; native close normally grants one"); }
        if(world.ProposedTrip.Length>0 && world.TripReady==2)
        {
            bool returning=world.ProposedTrip=="return";
            if(world.ProposedTrip==AfterhourLocation.DUMPING_GROUND)
            {
                Require(world.Objects.TryGetValue(world.ProposedBackpack,out var selected) && ReferenceEquals(selected,travelBackpack) &&
                    travelBackpack.parentInventory==EmporiumEntry.Instance.invElement,
                    "shared trip proposal lost Partner selected backpack or moved it before consent");
                var actors=NativeActorSession.Current;
                Require(actors!=null && world.HasActorProjection && travelHostBackpack.parentInventory==EmporiumEntry.Instance.invElement,
                    "joint journey requires separate actor carry and untouched Host bag");
                using(actors.Enter(NativeActorRun.Slot.Host))
                {
                    // Host keeps its own original selectedBackpack draft. It
                    // must not copy the initiating Partner's proposal.
                    EmporiumEntry.Instance.selectedBackpack=travelHostBackpack;
                    Require(ReferenceEquals(actors.Carry.Selected(NativeActorRun.Slot.Host),travelHostBackpack),
                        "Host native selection did not remain actor-local");
                }
            }
            Require(MapUIManager.Instance.IsOutside==returning,"travel occurred before host consent");
            if(!blockedTripRetryVerified && world.ProposedTrip==AfterhourLocation.DUMPING_GROUND)
            {
                var store=PlayerStore.Instance;
                bool wasArresting=store.isClientBeingArrested;
                string destination=world.ProposedTrip;
                try
                {
                    // Exercise the native trade lock on the consent frame,
                    // without starting an arrest or modifying the checkpoint.
                    store.isClientBeingArrested=true;
                    world.ProposeTrip(destination,true);
                    Require(world.ProposedTrip==destination && world.TripReady==2 && !MapUIManager.Instance.IsInTransit,
                        "a refused Host confirmation discarded the Partner's consent or began travel");
                }
                finally { store.isClientBeingArrested=wasArresting; }
                blockedTripRetryVerified=true;
                Record("PASS shared-trip-blocked-retry","native trade lock refused Host consent, retained Partner readiness; retry uses same proposal");
                return;
            }
            Record("TRACE actor-travel-graph","before consent "+TravelCarryEvidence());
            Record("PASS shared-trip-barrier","waiting destination="+world.ProposedTrip+" scene="+MapUIManager.Instance.Current?.Id);
            world.ProposeTrip(world.ProposedTrip,true);
        }
        string scene=MapUIManager.Instance.Current?.Id ?? "";
        if(scene==hostObservedScene || (bool)AccessTools.Field(typeof(MapUIManager),"inTransit").GetValue(MapUIManager.Instance)) return;
        hostObservedScene=scene;
        if(scene.Length>0)
        {
            if(scene==AfterhourLocation.DUMPING_GROUND)
            {
                var carry=NativeActorSession.Current?.Carry;
                Record("TRACE actor-travel-graph","after arrival "+TravelCarryEvidence());
                Require(carry!=null && ReferenceEquals(EmporiumEntry.Instance.afterhourPocketSlotInvBackpack.childItem,travelHostBackpack) &&
                    ReferenceEquals(carry.Get(NativeActorRun.Slot.Host,NativeActorInventory.CarrySlot.Backpack).childItem,travelHostBackpack) &&
                    ReferenceEquals(carry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.Backpack).childItem,travelBackpack) &&
                    GraphUtils.FindAllChildrenType<GameItem>(travelHostBackpack).Count==1 && GraphUtils.FindAllChildrenType<GameItem>(travelBackpack).Count==1,
                    "host joint travel did not preserve both distinct personal backpacks and contents");
            }
            Require(PlayerStore.Instance.GetAfterhourState(scene).visits==beforeVisits[scene]+1,"shared trip duplicated native visit");
            Record("PASS host-joint-arrival",scene+" visits exactly once");
        }
        else
        {
            CheckHostReturnedCarry();
            Record("PASS host-joint-return","store scene restored");
        }
    }
    private bool RunJointJourneys(CoopSession session,double now)
    {
        if(journeyIndex>=journeyLocations.Length) return true;
        if(now<journeyNext) return false;
        if(journeyStep==0)
        {
            Require(PlayerStore.Instance.storeState==PlayerStore.StoreState.OPEN,"journey expected open business before closing");
            if(!PrepareJointClose(session,now)) return false;
            StoreShutterButton.Instance.OnClick();
            if(StoreUIManager.Instance.closeShutterConfirmationPanel.activeSelf)
            {
                var buttons=StoreUIManager.Instance.closeShutterConfirmationPanel.GetComponentsInChildren<UnityEngine.UI.Button>();
                bool clicked=false;
                foreach(var button in buttons) for(int i=0;i<button.onClick.GetPersistentEventCount();i++)
                    if(button.onClick.GetPersistentMethodName(i)=="EndAndClose") { button.onClick.Invoke(); clicked=true; break; }
                Require(clicked,"native close confirmation button binding missing");
            }
            Require(QaWaiting(session.Native),"native close button did not send request");
            journeyStep=1; journeyUntil=now+15; return false;
        }
        if(journeyStep==1)
        {
            Require(now<journeyUntil,"store did not close before joint outing; result="+QaResult(session.Native));
            if(QaWaiting(session.Native) || PlayerStore.Instance.storeState!=PlayerStore.StoreState.CLOSED) return false;
            Record("PASS native-business-close","original confirmation entered CLOSED on both replicas"); journeyStep=2; journeyNext=now+3; return false;
        }
        if(journeyStep==2)
        {
            if(journeyIndex==0)
            {
                var world=NativeWorld.Guest;
                travelBackpack=world.Objects.Values.OfType<GameItem>().Single(item=>item.IsTag(TravelPackTag));
                travelHostBackpack=world.Objects.Values.OfType<GameItem>().Single(item=>item.IsTag(TravelHostPackTag));
                travelBackpackId=world.PointerIdentity(travelBackpack);
                travelHostBackpackId=world.PointerIdentity(travelHostBackpack);
                Require(travelBackpackId!=0 && travelHostBackpackId!=0 && travelBackpackId!=travelHostBackpackId,
                    "joint journey bags do not have distinct network identities");
                ulong before=QaIssued(session.Native);
                StoreResponse.ShowAfterhourResponse();
                var choices=(GameGridScrollableInventory)AccessTools.Field(typeof(OverlayHandler),"itemChooseInventory").GetValue(OverlayHandler.current);
                var window=(PixelWindow)AccessTools.Field(typeof(OverlayHandler),"itemChooseWindow").GetValue(OverlayHandler.current);
                var choice=choices.childItems.OfType<GameItemElement>().Single(item=>ReferenceEquals(NativeLocalItemChooser.SourceOf(item),travelBackpack));
                Require(window.IsVisible() && !QaWaiting(session.Native) && QaIssued(session.Native)==before &&
                    world.PointerIdentity(choice)==0 && travelBackpack.parentInventory==EmporiumEntry.Instance.invElement,
                    "opening travel choices changed the shared graph or waited for the host");
                NativeOffscreenCapture.Capture("qa-travel-backpack-picker");
                RecipeSelectHandler.current.callback(choice);
                Require(ReferenceEquals(EmporiumEntry.Instance.selectedBackpack,travelBackpack) && TransitUI.IsOpen && !window.IsVisible(),
                    "original choice callback did not retain the local backpack draft and open transit");
                Record("PASS local-travel-preparation","native item picker and selection callback ran locally; only data-only icons created, shared backpack untouched until joint travel");
            }
            MapUIManager.Instance.Travel(AfterhourLocation.Get(journeyLocations[journeyIndex]));
            Require(QaWaiting(session.Native),"original travel did not request shared scene");
            journeyStep=3; journeyUntil=now+20; return false;
        }
        if(journeyStep==3)
        {
            Require(now<journeyUntil,"shared arrival timed out: "+journeyLocations[journeyIndex]+" result="+QaResult(session.Native));
            if(MapUIManager.Instance.Current?.Id!=journeyLocations[journeyIndex]) return false;
            Require(!EmporiumEntry.Instance.storePhysical.activeSelf,"guest retained shop scene on outing");
            var location=MapUIManager.Instance.Current;
            if(journeyIndex==0)
            {
                var slot=EmporiumEntry.Instance.afterhourPocketSlotInvBackpack;
                Require(ReferenceEquals(slot.childItem,travelBackpack) && NativeWorld.Guest.PointerIdentity(slot.childItem)==travelBackpackId &&
                    NativeReplicaSlotPresentation.Matches(slot) && GraphUtils.FindAllChildrenType<GameItem>(travelBackpack).Count==1,
                    "guest did not embed its own backpack and contents in the original equipment slot");
                var carry=NativeWorld.Guest.ActorCarry;
                Require(carry!=null && ReferenceEquals(carry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.Backpack),slot) &&
                    ReferenceEquals(carry.Get(NativeActorRun.Slot.Host,NativeActorInventory.CarrySlot.Backpack).childItem,travelHostBackpack) &&
                    NativeWorld.Guest.PointerIdentity(travelHostBackpack)==travelHostBackpackId && GraphUtils.FindAllChildrenType<GameItem>(travelHostBackpack).Count==1,
                    "guest mixed Host carried bag with its original Partner UI");
                journeyMarkers[0]=NativeWorld.Guest.PointerIdentity(GraphUtils.FindAllChildrenType<GameItem>(travelHostBackpack).Single());
                journeyMarkers[1]=NativeWorld.Guest.PointerIdentity(GraphUtils.FindAllChildrenType<GameItem>(travelBackpack).Single());
                Require(journeyMarkers.All(id=>id!=0) && journeyMarkers[0]!=journeyMarkers[1],"travel markers lack distinct native identities");
                Record("PASS shared-travel-backpack","same shared destination, distinct Host/Partner bags and contents; original local equipment slot renders only Partner bag");
                VerifyCarryWindowDrag(session.Native);
            }
            Require(NativePointer.CurrentLocation()==(ushort)(journeyIndex+2),"guest cursor identified the wrong shared destination");
            Require(location.StageObject==null || location.StageObject.activeSelf,"native destination background not active");
            bool creditVisible=StoreUIManager.Instance.storeCreditDisplay.creditDisplayText.gameObject.activeSelf;
            Require(creditVisible==(location is DrJacksonLocation),
                "guest storefront credit display leaked into the wrong outside stage");
            Record("PASS guest-joint-arrival",location.Id+" native stage, local native panel and correct cash display");
            journeyStep=4; journeyNext=now+2; return false;
        }
        if(journeyStep==4)
        {
            if(!RunDestinationActions(session,now)) return false;
            NativeOffscreenCapture.Capture("qa-trip-"+journeyLocations[journeyIndex]);
            if(journeyLocations[journeyIndex]!=AfterhourLocation.DR_JACKSON)
                Require(EmporiumEntry.Instance.afterhourInventory.childItems.Count==0,"roundtrip fixture requires explicit recovery of ground items");
            if(journeyIndex==0) CaptureJourneyReturn();
            MapUIManager.Instance.Return(); Require(QaWaiting(session.Native),"original return did not request shared scene");
            journeyStep=5; journeyUntil=now+20; return false;
        }
        Require(now<journeyUntil,"shared return timed out: "+journeyLocations[journeyIndex]+" result="+QaResult(session.Native));
        if(MapUIManager.Instance.IsOutside || NativeWorld.Guest.HasPendingWork || QaWaiting(session.Native)) return false;
        if(journeyIndex==0)
            VerifyJourneyReturn();
        Require(EmporiumEntry.Instance.storePhysical.activeSelf,"guest shop did not reappear after return");
        Require(PlayerStore.Instance.visitLeftTonight==2-journeyIndex,"visit quota consumed more than once");
        if(journeyLocations[journeyIndex]==AfterhourLocation.DR_JACKSON)
        {
            Require(NativeWorld.Guest.Objects.TryGetValue(engineerEntity,out var product) && product is GameItem item && GeneralHelper.IsItemOwned(item) &&
                item.parentInventory!=null && item.parentInventory!=EmporiumEntry.Instance.afterhourInventory,"native engineer return lost purchased goods");
            Record("PASS shared-engineer-recovery","native DrJackson.Leave recovered the purchased product into the shop");
        }
        Require(StoreUIManager.Instance.storeCreditDisplay.creditDisplayText.gameObject.activeSelf,
            "guest storefront credit display was not restored on return");
        Record("PASS guest-joint-return",journeyLocations[journeyIndex]+" quota="+PlayerStore.Instance.visitLeftTonight);
        journeyIndex++; destinationStep=0; journeyStep=2; journeyNext=now+2; return journeyIndex==journeyLocations.Length;
    }

    // The visit can add legitimate cargo to the selected bag. Freeze its real
    // physical contents immediately before Return, rather than expecting the
    // one-item departure fixture after scavenging. The world quantity check
    // also covers pocket stacks that the native return is allowed to merge.
    private void CaptureJourneyReturn()
    {
        var world=NativeWorld.Guest;
        var bags=new[]{travelHostBackpack,travelBackpack};
        for(int actor=0;actor<2;actor++)
        {
            Require(ReferenceEquals(world.ActorCarry.Get((NativeActorRun.Slot)actor,NativeActorInventory.CarrySlot.Backpack).childItem,bags[actor]),
                "pre-return actor no longer holds its original travel bag");
            journeyReturnContents[actor]=JourneyContents(world,bags[actor]);
            Require(journeyReturnContents[actor].ContainsKey(journeyMarkers[actor]),"travel lost its initial marker before return");
        }
        journeyReturnStock=JourneyOwnedStock(world);
        Record("TRACE journey-return-expected","hostContents="+journeyReturnContents[0].Count+" partnerContents="+journeyReturnContents[1].Count+" "+TravelCarryEvidence());
    }

    private void VerifyJourneyReturn()
    {
        var world=NativeWorld.Guest;
        var bags=new[]{travelHostBackpack,travelBackpack};
        var ids=new[]{travelHostBackpackId,travelBackpackId};
        for(int actor=0;actor<2;actor++)
        {
            var bag=bags[actor];
            Require(world.PointerIdentity(bag)==ids[actor] && bag.parentInventory!=null && GeneralHelper.IsItemOwned(bag),
                "return lost original actor bag identity/ownership: "+actor);
            Require(TryJourneyRoot(world,bag,out var root) && (root.Personal
                ? root.Owner==(NativeActorInventory.ActorSlot)actor && root.Place==(int)NativeActorInventory.CarrySlot.Backpack
                : root.Place is 1 or 2 or 7),"return moved actor bag into an unrelated or foreign root: "+actor);
            var slot=world.ActorCarry.Get((NativeActorRun.Slot)actor,NativeActorInventory.CarrySlot.Backpack);
            Require(root.Personal ? ReferenceEquals(slot.childItem,bag) && ReferenceEquals(bag.parentInventory,slot) : slot.childItem==null,
                "return duplicated actor bag or detached retained carry: "+actor);
            var actual=JourneyContents(world,bag); var expected=journeyReturnContents[actor];
            Require(expected!=null && actual.Count==expected.Count && expected.All(row=>actual.TryGetValue(row.Key,out var value) && value.Equals(row.Value)) &&
                actual.ContainsKey(journeyMarkers[actor]),"return changed bag content identities, quantities or physical parents: "+actor);
        }
        var stock=JourneyOwnedStock(world);
        Require(journeyReturnStock!=null && stock.Count==journeyReturnStock.Count &&
            journeyReturnStock.All(row=>stock.TryGetValue(row.Key,out int count) && count==row.Value),
            "native return lost/duplicated owned quantities, including recovered pocket cargo");
        Record("PASS shared-travel-return-contents","both original bags and initial markers retained; exact pre-return nested IDs/quantities/parents and all owned quantities conserved; capacity overflow may remain in its own carry");
    }

    private static Dictionary<uint,(int NativeId,int Count,uint Parent)> JourneyContents(NativeWorld world,GameItem bag)
    {
        var result=new Dictionary<uint,(int NativeId,int Count,uint Parent)>();
        foreach(var item in GraphUtils.FindAllChildrenType<GameItem>(bag).Distinct())
        {
            uint id=world.PointerIdentity(item),parent=world.PointerIdentity(item.parentInventory);
            Require(id!=0 && parent!=0 && item.uniqueId>0 && item.unitCount>0 && GeneralHelper.IsItemOwned(item),"travel content lacks an owned physical identity");
            result.Add(id,(item.uniqueId,item.unitCount,parent));
        }
        return result;
    }

    private static bool TryJourneyRoot(NativeWorld world,GameItem item,out NativeActorInventory.RootKey root)
    {
        var args=new object[]{item.parentInventory,default(NativeActorInventory.RootKey)};
        bool found=(bool)AccessTools.Method(typeof(NativeWorld),"TryPhysicalRoot").Invoke(world,args);
        root=(NativeActorInventory.RootKey)args[1]; return found;
    }

    private static Dictionary<string,int> JourneyOwnedStock(NativeWorld world) => world.Objects.Values.OfType<GameItem>().Distinct()
        .Where(item=>item.parentInventory!=null && GeneralHelper.IsItemOwned(item) && TryJourneyRoot(world,item,out _))
        .GroupBy(item=>item.identifier).ToDictionary(group=>group.Key,group=>group.Sum(item=>item.unitCount));

    private void CheckHostReturnedCarry()
    {
        var actors=NativeActorSession.Current;
        Require(actors!=null,"return capacity oracle has no actor session");
        int retained=0;
        foreach(var actor in new[]{NativeActorRun.Slot.Host,NativeActorRun.Slot.Partner})
        using(actors.Enter(actor))
        foreach(var place in new[]{NativeActorInventory.CarrySlot.Backpack,NativeActorInventory.CarrySlot.RightPocket,NativeActorInventory.CarrySlot.LeftPocket})
        {
            var slot=actors.Carry.Get(actor,place); var item=slot.childItem;
            if(item==null) continue;
            retained++;
            Require(ReferenceEquals(item.parentInventory,slot),"return retained cargo outside its original actor slot");
            foreach(var destination in new[]{EmporiumEntry.Instance.backInvinvElement,EmporiumEntry.Instance.invElement,EmporiumEntry.Instance.showcaseElement})
            {
                var target=destination.TryFindOneValidInventorySlot(item);
                Require(target?.IsValid()!=true || target.numTransfer<item.unitCount,
                    "return left actor cargo despite a valid native shared destination: "+actor+"/"+place);
            }
        }
        Record("PASS host-return-capacity","retainedRoots="+retained+"; any remainder keeps its physical actor parent and has no full native placement in shop/counter/showcase");
    }

    private string TravelCarryEvidence()
    {
        var carry=NativeActorSession.Current?.Carry ?? NativeWorld.Guest?.ActorCarry;
        object Describe(GameItem item)=>item==null ? null : new {
            id=item.uniqueId, kind=item.identifier, count=item.unitCount,
            parent=item.parentInventory?.GetType().Name,
            hostSlot=carry!=null && ReferenceEquals(item.parentInventory,carry.Get(NativeActorRun.Slot.Host,NativeActorInventory.CarrySlot.Backpack)),
            partnerSlot=carry!=null && ReferenceEquals(item.parentInventory,carry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.Backpack)),
            children=GraphUtils.FindAllChildrenType<GameItem>(item).Select(child=>new { id=child.uniqueId, kind=child.identifier, count=child.unitCount }).ToArray()
        };
        return Newtonsoft.Json.JsonConvert.SerializeObject(new {
            actor=carry?.CurrentActor.ToString(), local=Describe(EmporiumEntry.Instance.afterhourPocketSlotInvBackpack.childItem),
            host=Describe(carry?.Get(NativeActorRun.Slot.Host,NativeActorInventory.CarrySlot.Backpack).childItem),
            partner=Describe(carry?.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.Backpack).childItem),
            expectedHost=Describe(travelHostBackpack), expectedPartner=Describe(travelBackpack)
        });
    }

    // A native day cannot close while an undiscovered required customer remains
    // in the source queue. Play their real dialogue rather than changing flags
    // or treating an intentionally disabled shutter as a transport failure.
    private bool PrepareJointClose(CoopSession session,double now)
    {
        if(journeyClosePrepared) return true;
        if(journeyCloseUntil==0) journeyCloseUntil=now+100;
        var world=NativeWorld.Guest; var store=PlayerStore.Instance;
        var client=store.currentClientInstance?.GetClientBlueprint();
        Require(now<journeyCloseUntil,"native pre-close queue timed out: client="+client?.identifier+
            " intent="+client?.clientIntent+" introduced="+client?.isIntroduced+" dismissable="+client?.dismissable+
            " queue="+world?.QueuedClients+" canClose="+world?.CanClose+" dialogue="+world?.HasDialogue+
            " pendingDialogue="+world?.PendingMainDialogue+" bellLock="+store.lockNextBellFlag+" shutterLock="+store.lockShutterButtonFlag);
        if(world?.Ready!=true || QaWaiting(session.Native) || session.Shop.Waiting || world.HasPendingWork) return false;
        if(journeyCloseBellPending)
        {
            ushort result=QaResult(session.Native);
            Require(result==(ushort)ShopResult.Applied || result==(ushort)ShopResult.Stale,
                "native pre-close bell failed: result="+result+" client="+client?.identifier);
            journeyCloseBellPending=false;
            if(result==(ushort)ShopResult.Stale)
            { Record("TRACE pre-close-bell-stale","native customer context advanced before the queued click; retry against current facts"); journeyNext=now+0.3; return false; }
        }
        if(!ReferenceEquals(journeyCloseObserved,store.currentClientInstance))
        {
            journeyCloseObserved=store.currentClientInstance;
            if(journeyCloseObserved!=null)
            {
                journeyCloseCustomers.Add(journeyCloseObserved);
                Require(journeyCloseCustomers.Count<=12,"native pre-close queue exceeded twelve distinct customers");
                Record("TRACE pre-close-native-client",client.identifier+" intent="+client.clientIntent+
                    " dismissable="+client.dismissable+" queued="+world.QueuedClients);
            }
        }
        if(world.HasDialogue)
        {
            var dialog=DialogUIManager.Instance;
            var choice=dialog.dialogChoiceBubbles.FirstOrDefault(b=>b.gameObject.activeSelf && b.IsInteractable());
            if(choice!=null)
            {
                Record("TRACE pre-close-native-choice","client="+client?.identifier+" text="+choice.TMP.text);
                choice.OnDialogChoiceClick();
            }
            else dialog.OnAdvancePressed();
            journeyNext=now+0.65; return false;
        }
        // Native arrival starts the main dialogue via a scheduled callback.
        // Do not dismiss a customer or close between its approach and first line.
        if(world.PendingMainDialogue || client!=null && !store.isClientArrived || store.lockNextBellFlag>0 ||
            store.lockShutterButtonFlag>0 || store.isClientBeingArrested || store.isHandlingShootout ||
            Interactable.isPaused || NegociationUIManager.IsTradeLocked())
        { journeyNext=now+0.25; return false; }
        if(world.CanClose)
        {
            journeyClosePrepared=true;
            Record("PASS native-pre-close-queue","original dialogue/Bell resolved required queue; host CanEndDay now permits shutter; observed="+
                string.Join(",",journeyCloseCustomers.Select(instance=>instance.GetClientBlueprint().identifier))+
                " remainingOptional="+world.QueuedClients+"; no customer, dismissal flag or cash changes by QA");
            return true;
        }
        if(client==null ? world.QueuedClients>0 : store.IsClientDimissable())
        {
            NegociationUIManager.Instance.CloseUI();
            UnityEngine.Object.FindObjectOfType<StoreBell>().GetComponent<Interactable>().OnClick();
            Require(QaWaiting(session.Native),"original pre-close bell emitted no authenticated request");
            journeyCloseBellPending=true; journeyNext=now+1.5; return false;
        }
        // A non-dismissible customer with no active dialogue can require a real
        // service or trade. Never force past that interaction; leave its exact
        // offer and state in the bounded failure diagnostic.
        string blocked=client?.identifier ?? "<empty>";
        if(journeyCloseBlocked!=blocked)
        {
            journeyCloseBlocked=blocked;
            Record("TRACE pre-close-required-action","client="+blocked+" intent="+client?.clientIntent+
                " introduced="+client?.isIntroduced+" complete="+client?.isBusinessComplete+" offers="+
                string.Join(",",EmporiumEntry.Instance.GetAllNonOwnedItem().Select(item=>item.identifier+":"+item.GetNegociatedValue())));
        }
        journeyNext=now+0.4; return false;
    }

    private int destinationStep,previousAttempts,previousTokens,previousStock;
    private uint recoveredEntity,recoveredTarget;
    private int recoveredCount,recoveredTargetBefore;
    private double destinationNext,destinationDeadline;
    private bool RunDestinationActions(CoopSession session,double now)
    {
        if(now<destinationNext) return false;
        var world=NativeWorld.Guest; var store=PlayerStore.Instance; var entry=EmporiumEntry.Instance;
        string location=journeyLocations[journeyIndex];
        if(location==AfterhourLocation.DR_JACKSON) return RunEngineerPurchase(session,now);
        if(destinationStep==0)
        {
            if(location==AfterhourLocation.DUMPING_GROUND)
            {
                previousAttempts=store.scavengingAttempts;
                MapUIManager.Instance.ScavengeButtonClick();
            }
            else
            {
                previousTokens=store.commissaryData.redeemedCredit;
                previousStock=store.commissaryData.GetStock("processed_meat");
                var product=CommissaryUIManager.Instance.panel.GetComponentsInChildren<CommissaryElement>(true).Single(e=>e.id=="processed_meat");
                product.buyButton.onClick.Invoke();
            }
            Require(QaWaiting(session.Native),"native destination button did not issue command: "+location);
            destinationStep=1; destinationDeadline=now+15; return false;
        }
        if(destinationStep==1 || destinationStep==3)
        {
            Require(now<destinationDeadline,"destination command timed out: "+location);
            if(QaWaiting(session.Native)) return false;
            Require(QaResult(session.Native)==(ushort)ShopResult.Applied,"native destination command rejected: "+location+" step="+destinationStep+" result="+QaResult(session.Native));
            destinationStep++; destinationNext=now+1; return false;
        }
        if(destinationStep==2)
        {
            if(location==AfterhourLocation.DUMPING_GROUND)
            {
                Require(store.scavengingAttempts==previousAttempts+1,"search was not committed exactly once");
                Require(ScavHelper.GetScavTimeLeft()==ScavHelper.GetMaxScavAttempts()-store.scavengingAttempts,"search counter projection differs");
                Record("PASS shared-scavenging","original search button; attempts exactly +1, host-generated ground loot");
            }
            else
            {
                Require(store.commissaryData.redeemedCredit==previousTokens-25 && store.commissaryData.GetStock("processed_meat")==previousStock-1,"commissary charged/stock count differs");
                Require(entry.afterhourInventory.childItems.Any(i=>i.identifier=="processed_meat"),"commissary lost purchased item");
                Record("PASS shared-commissary-buy","original product button; tokens -25, stock -1 and purchased item on shared ground");
            }
            destinationStep=4;
        }
        if(recoveredEntity!=0)
        {
            bool hasSource=world.Objects.TryGetValue(recoveredEntity,out var obj) && obj is GameItem;
            var source=hasSource ? (GameItem)obj : null;
            if(recoveredTarget!=0)
            {
                Require(world.Objects.TryGetValue(recoveredTarget,out var targetObject) && targetObject is GameItem target &&
                    target.unitCount==recoveredTargetBefore+recoveredCount && target.parentInventory!=null &&
                    TryJourneyRoot(world,target,out var targetRoot) && targetRoot.Personal && targetRoot.Owner==NativeActorInventory.ActorSlot.Partner &&
                    (source==null || source.parentInventory==null && source.unitCount==0),
                    "native ground stack failed to conserve source and target quantities");
                Record("PASS shared-ground-stack","source="+recoveredEntity+" target="+recoveredTarget+" transferred="+recoveredCount+"; native whole-stack merge conserved quantity");
            }
            else
                Require(source!=null && source.unitCount==recoveredCount && GeneralHelper.IsItemOwned(source) &&
                    TryJourneyRoot(world,source,out var root) && root.Personal && root.Owner==NativeActorInventory.ActorSlot.Partner,
                    "recovered item did not enter the Partner's own carrying root with its original quantity");
            Record("PASS shared-ground-recovery","entity="+recoveredEntity+" native personal carrying insertion, no local duplication");
            recoveredEntity=recoveredTarget=0;
        }
        if(entry.afterhourInventory.childItems.Count==0) return true;
        var loot=entry.afterhourInventory.childItems.First();
        SlotMarker slot=null;
        var ownBackpack=entry.afterhourPocketSlotInvBackpack.childItem;
        var ownContents=ownBackpack?.contentWindow?.childElement as GameInventory;
        foreach(var inventory in new GameInventory[]{entry.afterhourPocketSlotInvLeft,entry.afterhourPocketSlotInvRight,ownContents})
        {
            if(inventory==null) continue;
            var candidate=inventory.TryFindOneValidInventorySlot(loot);
            if(candidate?.IsValid()==true && candidate.numTransfer==loot.unitCount) { slot=candidate; break; }
        }
        Require(slot!=null,"QA ground recovery needs more native carrying space: "+loot.identifier);
        Require(world.TryData(loot,out var data),"loot missing authoritative identity"); recoveredEntity=(uint)data["entity"];
        recoveredCount=loot.unitCount; recoveredTarget=world.PointerIdentity(slot.targetItem);
        recoveredTargetBefore=slot.targetItem?.unitCount ?? 0;
        Require(slot.targetItem==null || recoveredTarget!=0 && recoveredTarget!=recoveredEntity,"ground stack target lacks a distinct authoritative identity");
        slot.TryAcceptOnce(); Require(QaWaiting(session.Native),"native ground recovery did not issue move");
        destinationStep=3; destinationDeadline=now+15; return false;
    }

    private uint engineerEntity;
    private int engineerCash,engineerPrice;
    private bool RunEngineerPurchase(CoopSession session,double now)
    {
        var world=NativeWorld.Guest; var entry=EmporiumEntry.Instance; var store=PlayerStore.Instance;
        if(destinationStep==0)
        {
            var product=entry.frontInvinvElement.childItems.Where(i=>!GeneralHelper.IsItemOwned(i) && i.GetNegociatedValue()>0 && i.GetNegociatedValue()<=store.playerCash)
                .OrderBy(i=>i.GetNegociatedValue()).FirstOrDefault();
            Require(product!=null,"native engineer shop has no affordable fixture; cash="+store.playerCash);
            Require(world.TryData(product,out var data),"engineer product identity missing"); engineerEntity=(uint)data["entity"];
            var slot=entry.backInvinvElement.TryFindOneValidInventorySlot(product);
            Require(slot?.IsValid()==true && slot.numTransfer==product.unitCount,"engineer product cannot fit offer counter");
            slot.TryAcceptOnce(); Require(QaWaiting(session.Native),"engineer selection did not send native move");
            destinationStep=1; destinationDeadline=now+15; return false;
        }
        if(destinationStep==1 || destinationStep==3)
        {
            Require(now<destinationDeadline,"engineer command reply timed out");
            if(QaWaiting(session.Native)) return false;
            Require(QaResult(session.Native)==(ushort)ShopResult.Applied,"engineer command failed: "+QaResult(session.Native)+" step="+destinationStep);
            destinationStep++; destinationNext=now+1; return false;
        }
        Require(world.Objects.TryGetValue(engineerEntity,out var obj) && obj is GameItem,"engineer selected product disappeared");
        var selected=(GameItem)obj;
        if(destinationStep==2)
        {
            Require(selected.parentInventory==entry.backInvinvElement,"engineer selected product not in original offer counter");
            var ui=NegociationUIManager.Instance;
            store.currentNegociatedItem=selected; ui.UpdateUI(selected); ui.OpenUI();
            destinationStep=6; destinationNext=now+1; return false;
        }
        if(destinationStep==6)
        {
            engineerCash=store.playerCash; engineerPrice=(int)selected.GetNegociatedValue();
            var button=(TabletButton)AccessTools.Field(typeof(NegociationUIManager),"primaryButton").GetValue(NegociationUIManager.Instance);
            Require(button?.Interactable==true,"engineer native purchase button unavailable");
            button.button.onClick.Invoke(); Require(QaWaiting(session.Native),"engineer native buy button did not send trade");
            destinationStep=3; destinationDeadline=now+15; return false;
        }
        if(!GeneralHelper.IsItemOwned(selected) || store.playerCash!=engineerCash-engineerPrice)
        { Require(now<destinationDeadline,"engineer purchase ownership/cash failed to converge"); return false; }
        Record("PASS shared-engineer-buy","original offer counter and tablet button; cash="+engineerCash+"->"+store.playerCash+" entity="+engineerEntity+" now owned");
        return true;
    }
}
