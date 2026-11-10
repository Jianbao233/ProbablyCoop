using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

[HarmonyPatch(typeof(DumpingGroundLocation), nameof(DumpingGroundLocation.Arrive))]
internal static class NativeSharedScavengingArrival
{
    private static void Prefix()
    {
        var session=NativeActorSession.Current;
        if(session==null || session.Terminal || session.Faulted || NativeGuest.Active) return;
        var other=session.Run.CurrentActor==NativeActorRun.Slot.Host
            ? NativeActorRun.Slot.Partner : NativeActorRun.Slot.Host;
        // The original arrival resets the initiator and runs the shared
        // story event once. Only the other character needs a separate reset.
        using(session.Enter(other)) ScavHelper.ResetScavenging();
    }
}

internal sealed partial class NativeWorld
{
    private string proposedTrip="", drawnTrip="", shownScene="";
    private string tripProof="",remoteTripProof="";
    private byte tripReady;
    private byte tripInitiator;
    private readonly uint[] tripBags=new uint[2];
    private readonly string[] tripBagProofs={"",""};
    private CustomUIWindow tripUi;
    internal static bool NativeTravelExecution;
    internal string ProposedTrip => proposedTrip;
    internal byte TripReady => tripReady;
    private Dictionary<string,bool> discovered=new Dictionary<string,bool>();
    internal bool IsDiscovered(string id,out bool value)=>discovered.TryGetValue(id,out value);
    private JObject CaptureSharedScene()
    {
        var store=PlayerStore.Instance;
        var places=new JObject();
        foreach(var id in new[]{AfterhourLocation.DUMPING_GROUND,AfterhourLocation.COMMISSARY,AfterhourLocation.DR_JACKSON})
            places[id]=AfterhourLocation.Get(id)?.IsDiscovered()==true;
        var data=new JObject { ["location"]=MapUIManager.Instance.Current?.Id ?? "",["playerLocation"]=(int)store.playerLocation,
            ["proposed"]=proposedTrip,["ready"]=tripReady,["proof"]=TravelProof(),["visits"]=store.visitLeftTonight,["discovered"]=places,
            ["backpack"]=proposedBackpack,["backpackProof"]=proposedBackpackProof,
            ["actorBackpacks"]=new JArray(tripBags),["actorBackpackProofs"]=new JArray(tripBagProofs),["initiator"]=tripInitiator,
            ["searches"]=ScavHelper.GetScavTimeLeft(),["minor"]=ScavHelper.GetMinorWoundChance(),["major"]=ScavHelper.GetMajorWoundChance(),
            ["wounded"]=store.healthData.isWoundedFresh };
        if(HasActorProjection) foreach(string field in new[]{"searches","minor","major","wounded"}) data.Remove(field);
        return data;
    }
    internal void ProposeTrip(string destination,bool acceptProposal=false)
    {
        uint backpack=0; string contents="";
        if(destination is not ("return" or "cancel"))
        {
            if(acceptProposal && !HasActorProjection) { backpack=proposedBackpack; contents=proposedBackpackProof; }
            else if(EmporiumEntry.Instance.selectedBackpack is GameItem selected)
            {
                backpack=host ? model.Id(selected) : model.ReplicaId(selected);
                contents=ContentsProof(selected,!host);
            }
        }
        if(host)
        {
            var result=(ShopResult)TravelRequest(destination,1,null,backpack,contents);
            if(result!=ShopResult.Applied)
                CommandNotice(result switch {
                    ShopResult.Busy=>"请先完成当前操作，再确认出行。",
                    ShopResult.NoSpace=>"随身栏放不下选定的背包，请重新选择。",
                    _=>"出行条件已变化，请检查目的地和背包。"
                });
        }
        else Submit(new JObject { ["kind"]="travel",["destination"]=destination,["proof"]=remoteTripProof,
            ["backpack"]=backpack,["backpackProof"]=contents });
    }
    private string TravelProof() => HashData(new JObject { ["scene"]=MapUIManager.Instance.Current?.Id ?? "",
        ["day"]=StoreStation.Instance.dayCounter,["state"]=(int)PlayerStore.Instance.storeState,["visits"]=PlayerStore.Instance.visitLeftTonight,
        ["ground"]=new JArray(EmporiumEntry.Instance.afterhourInventory.childItems.Select(MoveToken)) });
    private void ValidateTripReadiness()
    {
        if(HasActorProjection)
        {
            for(int index=0;index<2;index++)
                if((tripReady&(1<<index))!=0 && proposedTrip!="return")
                {
                    using var scope=actorRun.Enter((NativeActorRun.Slot)index);
                    if(!ValidTravelBackpack(tripBags[index],tripBagProofs[index],out _))
                        tripReady=(byte)(tripReady&~(1<<index));
                }
            if(proposedTrip.Length>0 && tripProof!=TravelProof()) { tripReady=0; tripProof=TravelProof(); nextCapture=0; }
            return;
        }
        if(proposedTrip.Length>0 && proposedTrip!="return" && !ValidTravelBackpack(proposedBackpack,proposedBackpackProof,out _))
        { proposedTrip=""; proposedBackpack=0; proposedBackpackProof=""; tripReady=0; nextCapture=0; return; }
        if(proposedTrip.Length>0 && tripProof!=TravelProof()) { tripReady=0; tripProof=TravelProof(); nextCapture=0; }
    }
    private ushort TravelRequest(string destination,byte actor,string expected=null,uint backpack=0,string backpackProof="")
    {
        if(HasActorProjection) return ActorTravelRequest(destination,actor,expected,backpack,backpackProof);
        if(EndingActive || shop.Settling || string.IsNullOrEmpty(destination)) return (ushort)ShopResult.Busy;
        string proof=TravelProof();
        if(actor==2 && expected!=proof) return (ushort)ShopResult.Stale;
        var map=MapUIManager.Instance; var store=PlayerStore.Instance;
        if((bool)AccessTools.Field(typeof(MapUIManager),"inTransit").GetValue(map)) return (ushort)ShopResult.Busy;
        if(destination=="cancel") { proposedTrip=""; proposedBackpack=0; proposedBackpackProof=""; tripReady=0; nextCapture=0; return (ushort)ShopResult.Applied; }
        GameItem selectedBackpack=null;
        if(destination=="return")
        { if(!map.IsOutside) return (ushort)ShopResult.Unavailable; }
        else
        {
            var location=AfterhourLocation.Get(destination);
            if(map.IsOutside || location==null || !location.IsDiscovered() || !location.IsAvailable() ||
                !store.isAfterhoursIntroduced || store.storeState!=PlayerStore.StoreState.CLOSED || store.visitLeftTonight<1 ||
                HealthData.IsSeriouslyWounded() || StartingPerkContent.IsAgoraphobiaResting() ||
                StoreStation.Instance.storeEventManager.IsEventActive("crackdown")) return (ushort)ShopResult.Unavailable;
            if(!ValidTravelBackpack(backpack,backpackProof,out selectedBackpack)) return (ushort)ShopResult.Stale;
        }
        if(proposedTrip!=destination || proposedBackpack!=backpack || proposedBackpackProof!=backpackProof)
        { proposedTrip=destination; proposedBackpack=backpack; proposedBackpackProof=backpackProof; tripReady=0; }
        if(tripProof!=proof) { tripReady=0; tripProof=proof; }
        tripReady|=actor; nextCapture=0;
        if(tripReady!=3) return (ushort)ShopResult.Applied;
        if(PartnerHasHand || ItemMultiSelectHandler.current?.IsGroupDragging==true ||
            ItemMouseDragHandler.current?.IsDraggingItem==true || shop.Capture().HeldItem!=0 || NegociationUIManager.IsTradeLocked())
        { tripReady=(byte)(tripReady&~actor); return (ushort)ShopResult.Busy; }
        NativeTravelExecution=true;
        EndSharedPhone();
        // The joint confirmation displays the original leave warning to both actors.
        // Once both consent, run the native confirmed branch once; Return() would
        // open a second host-only confirmation and strand the guest in this scene.
        try
        {
            if(destination=="return") map.ReturnNow();
            else { EmporiumEntry.Instance.selectedBackpack=selectedBackpack; map.Travel(AfterhourLocation.Get(destination)); }
        }
        finally { NativeTravelExecution=false; proposedTrip=""; proposedBackpack=0; proposedBackpackProof=""; tripReady=0; }
        return (ushort)ShopResult.Applied;
    }

    private ushort ActorTravelRequest(string destination,byte actor,string expected,uint backpack,string backpackProof)
    {
        if(actor is not (1 or 2) || EndingActive || shop.Settling || string.IsNullOrEmpty(destination)) return (ushort)ShopResult.Busy;
        var map=MapUIManager.Instance;
        var session=NativeActorSession.Current;
        if(session==null || (bool)AccessTools.Field(typeof(MapUIManager),"inTransit").GetValue(map)) return (ushort)ShopResult.Busy;
        string proof=TravelProof();
        if(actor==2 && expected!=proof) return (ushort)ShopResult.Stale;
        if(destination=="cancel") { ResetActorTrip(); return (ushort)ShopResult.Applied; }
        bool first=proposedTrip!=destination || tripInitiator==0;
        var initiator=first ? actor : tripInitiator;
        var store=PlayerStore.Instance;
        var location=destination=="return" ? null : AfterhourLocation.Get(destination);
        if(destination=="return") { if(!map.IsOutside) return (ushort)ShopResult.Unavailable; }
        else
        {
            // Only the player proposing this departure contributes personal
            // travel restrictions. The partner follows the shared scene.
            using(session.Enter((NativeActorRun.Slot)(initiator-1)))
                if(map.IsOutside || location==null || !location.IsDiscovered() || !location.IsAvailable() ||
                    !store.isAfterhoursIntroduced || store.storeState!=PlayerStore.StoreState.CLOSED || store.visitLeftTonight<1 ||
                    HealthData.IsSeriouslyWounded() || StartingPerkContent.IsAgoraphobiaResting() ||
                    StoreStation.Instance.storeEventManager.IsEventActive("crackdown")) return (ushort)ShopResult.Unavailable;
            using(session.Enter((NativeActorRun.Slot)(actor-1)))
                if(!ValidTravelBackpack(backpack,backpackProof,out _)) return (ushort)ShopResult.Stale;
        }
        if(first) { ResetActorTrip(); proposedTrip=destination; tripInitiator=actor; }
        if(tripProof!=proof) { tripReady=0; tripProof=proof; }
        int index=actor-1,other=1-index;
        if(backpack!=0 && (tripReady&(1<<other))!=0 && tripBags[other]==backpack) return (ushort)ShopResult.Busy;
        tripBags[index]=backpack; tripBagProofs[index]=backpackProof;
        proposedBackpack=tripBags[tripInitiator-1]; proposedBackpackProof=tripBagProofs[tripInitiator-1];
        nextCapture=0;
        if((tripReady|actor)!=3)
        {
            tripReady|=actor;
            return (ushort)ShopResult.Applied;
        }
        // Keep the first player's consent while the second player's attempt
        // is blocked. A retry should not require another click from the peer.
        tripReady=(byte)(tripReady&~actor);
        if(PartnerHasHand || ItemMultiSelectHandler.current?.IsGroupDragging==true || ItemMouseDragHandler.current?.IsDraggingItem==true ||
            shop.Capture().HeldItem!=0 || NegociationUIManager.IsTradeLocked() || session.Activities.HasActiveBusiness)
            return (ushort)ShopResult.Busy;
        if(destination!="return")
        {
            for(int i=0;i<2;i++)
            {
                using var scope=session.Enter((NativeActorRun.Slot)i);
                if(!ValidTravelBackpack(tripBags[i],tripBagProofs[i],out var selected)) return (ushort)ShopResult.Stale;
                var slot=session.Carry.Get((NativeActorRun.Slot)i,NativeActorInventory.CarrySlot.Backpack);
                if(selected!=null && !ReferenceEquals(slot.childItem,selected))
                {
                    var target=slot.TryFindOneValidInventorySlot(selected);
                    if(slot.childItem!=null || target?.IsValid()!=true || target.numTransfer!=selected.unitCount)
                        return (ushort)ShopResult.NoSpace;
                }
                session.Carry.SetSelected((NativeActorRun.Slot)i,selected);
            }
            if(location.UsesAfterhourBag && !PrepareActorBackpacks(session)) return (ushort)ShopResult.NoSpace;
        }
        session.Activities.RetireScene();
        var activity=session.Activities.Begin(map,"joint-travel",(NativeActorRun.Slot)(tripInitiator-1),
            ()=>ReferenceEquals(NativeActorSession.Current,session) && !session.Terminal && !session.Faulted);
        EndSharedPhone(); NativeTravelExecution=true;
        try { activity.Wrap(()=> { if(destination=="return") map.ReturnNow(); else map.Travel(location); })(); }
        finally { NativeTravelExecution=false; ResetActorTrip(); }
        return (ushort)ShopResult.Applied;
    }

    private static bool PrepareActorBackpacks(NativeActorSession session)
    {
        var moves=new List<(NativeActorRun.Slot Actor,GameItem Item,GameInventory Parent,GridShape Shape,int Count)>();
        for(int i=0;i<2;i++)
        {
            var actor=(NativeActorRun.Slot)i;
            var item=session.Carry.Selected(actor);
            if(item!=null && !ReferenceEquals(item.parentInventory,session.Carry.Get(actor,NativeActorInventory.CarrySlot.Backpack)))
                moves.Add((actor,item,item.parentInventory,item.modifiedShape.Clone(),item.unitCount));
        }
        bool complete=false;
        try
        {
            foreach(var move in moves)
            {
                using var scope=session.Enter(move.Actor);
                if(!session.Carry.TransferSelectedBackpack(move.Actor).Complete) return false;
            }
            complete=true; return true;
        }
        finally
        {
            if(!complete)
                foreach(var move in moves.AsEnumerable().Reverse())
                {
                    using var scope=session.Enter(move.Actor);
                    if(move.Item.unitCount!=move.Count || !ReferenceEquals(move.Item.parentInventory,move.Parent) &&
                        !(bool)AccessTools.Method(typeof(GraphUtils),"PlaceAtShape").Invoke(null,new object[]{move.Item,move.Parent,move.Shape}))
                    {
                        var error=new InvalidOperationException("Could not restore a refused joint backpack transfer");
                        session.MarkFault(error); throw error;
                    }
                    session.Carry.SetSelected(move.Actor,move.Item);
                }
        }
    }

    private void ResetActorTrip()
    {
        proposedTrip=""; proposedBackpack=0; proposedBackpackProof=""; tripReady=tripInitiator=0;
        Array.Clear(tripBags,0,tripBags.Length); tripBagProofs[0]=tripBagProofs[1]=""; nextCapture=0;
    }
    private ushort ScavengeRequest()
    {
        if(MapUIManager.Instance.Current?.Id!=AfterhourLocation.DUMPING_GROUND || !ScavHelper.CanScavenge() || shop.Settling)
            return (ushort)ShopResult.Unavailable;
        ScavHelper.ScavengeDumpingGrounds(); nextCapture=0; return (ushort)ShopResult.Applied;
    }
    internal void RequestScavenge() => Submit(new JObject { ["kind"]="scavenge" });
    private void TickTripUi()
    {
        string key=proposedTrip+":"+tripReady+":"+proposedBackpack+":"+proposedBackpackProof+":"+string.Join(",",tripBags);
        if(drawnTrip==key) return; drawnTrip=key; tripUi?.Close(); tripUi=null;
        if(proposedTrip.Length==0) return;
        string destination=proposedTrip;
        var panel=CustomUIManager.Instance.CreateWindow("coop_joint_trip","共同出行","meta");
        panel.AddLabel(destination=="return" ? "一起返回当铺" : "一起前往："+AfterhourLocation.Get(destination).GetTitle());
        if(destination=="return" && EmporiumEntry.Instance.afterhourInventory.childItems.Count>0)
            panel.AddLabel(MapUIManager.Instance.Current.GetLeaveWarning());
        panel.AddLabel(NativeDayUi.ReadinessText(tripReady, !host));
        if(destination!="return")
        {
            if(HasActorProjection)
            {
                for(int i=0;i<2;i++) panel.AddLabel((i==0 ? "房主" : "伙伴")+"背包："+
                    (tripBags[i]!=0 && model.Objects.TryGetValue(tripBags[i],out var value) && value is GameItem selected ? selected.GetDisplayName() : "不携带"));
                panel.AddButton("选择我的背包",OpenTravelPreparation);
            }
            else panel.AddLabel("携带背包："+(proposedBackpack!=0 && model.Objects.TryGetValue(proposedBackpack,out var bag) && bag is GameItem item ? item.GetDisplayName() : "不携带"));
        }
        bool ready=(tripReady & (host ? 1 : 2))!=0;
        if(!ready || destination!="return")
            panel.AddButton(ModHelper.GetLocalized("probably_coop", ready ? "trip_refresh" : "trip_confirm"),
                ()=>ProposeTrip(destination,true));
        panel.AddButton("取消出行",()=>ProposeTrip("cancel"));
        tripUi=panel.Show();
    }
    private void ApplySharedSceneFacts(JObject data)
    {
        proposedTrip=(string)data["proposed"]; tripReady=(byte)data["ready"]; remoteTripProof=(string)data["proof"];
        proposedBackpack=(uint)data["backpack"]; proposedBackpackProof=(string)data["backpackProof"];
        tripInitiator=(byte)data["initiator"];
        if(data["actorBackpacks"] is not JArray bags || bags.Count!=2 || data["actorBackpackProofs"] is not JArray proofs || proofs.Count!=2)
            throw new System.IO.InvalidDataException("Invalid actor travel preparation");
        for(int i=0;i<2;i++) { tripBags[i]=(uint)bags[i]; tripBagProofs[i]=(string)proofs[i]; }
        foreach(var entry in ((JObject)data["discovered"]).Properties()) discovered[entry.Name]=(bool)entry.Value;
        var store=PlayerStore.Instance; store.visitLeftTonight=(int)data["visits"];
        if(!HasActorProjection)
        {
            store.healthData.isWoundedFresh=(bool)data["wounded"];
            GuestScavLeft=(int)data["searches"]; GuestScavMinor=(float)data["minor"]; GuestScavMajor=(float)data["major"];
        }
        store.playerLocation=(PlayerStore.PlayerLocation)(int)data["playerLocation"];
    }
    private void PresentSharedScene(JObject data)
    {
        var store=PlayerStore.Instance;
        string scene=(string)data["location"];
        if(scene!=shownScene)
        {
            var map=MapUIManager.Instance; var e=EmporiumEntry.Instance;
            e.selectedBackpack=null;
            TransitUI.Close(); NegociationUIManager.Instance.CloseUI();
            e.CloseItemWindowUIWindow();
            AccessTools.Method(typeof(MapUIManager),"CloseLocationPanel").Invoke(map,null);
            AccessTools.Method(typeof(MapUIManager),"HideStage").Invoke(map,null);
            var location=string.IsNullOrEmpty(scene) ? null : AfterhourLocation.Get(scene);
            if(!string.IsNullOrEmpty(scene) && location==null) throw new InvalidOperationException("Unknown shared scene");
            AccessTools.Property(typeof(MapUIManager),nameof(MapUIManager.Current)).SetValue(map,location);
            e.storePhysical.SetActive(location==null); e.storeInteractables.SetActive(location==null);
            StoreUIManager.Instance.storeCalendar.dayTMP.gameObject.SetActive(location==null);
            StoreUIManager.Instance.clientCounter.SetActive(location==null);
            StoreUIManager.Instance.storeCreditDisplay.creditDisplayText.gameObject
                .SetActive(location==null || location is DrJacksonLocation);
            foreach(string name in new[]{"trashInvWindow","faucetWindow","drainInvWindow","cassettePlayerWindow","showcaseWindow","docInvWindow","invWindow","vendingMachineWindow","vendingFountainWindow"})
            {
                var window=AccessTools.Property(typeof(EmporiumEntry),name)?.GetValue(e) as PixelWindow;
                window?.rectTransform.gameObject.SetActive(location==null);
            }
            e.frontInvWindow.rectTransform.gameObject.SetActive(location?.ShowsStoreCounters==true || location==null && store.storeState==PlayerStore.StoreState.OPEN);
            e.backInvWindow.rectTransform.gameObject.SetActive(location==null || location.ShowsStoreCounters);
            StoreShutterButton.Instance.blockerGameObject.transform.parent.gameObject.SetActive(location==null);
            if(location!=null)
            {
                AccessTools.Method(typeof(MapUIManager),"ShowStage").Invoke(map,new object[]{location});
                AccessTools.Method(typeof(MapUIManager),"OpenLocationPanel").Invoke(map,new object[]{location});
                if(location.UsesAfterhourBag) e.ShowAfterhourInv(); else e.HideAfterhourInv();
                if(location is DrJacksonLocation)
                {
                    AccessTools.Method(typeof(DrJacksonLocation),"EnsureSheetTablet").Invoke(location,null);
                    StoreUIManager.Instance.SetClientPositionToDrJackson(); map.SetCreditDisplayPositionToDrJackson();
                }
            }
            else { e.HideAfterhourInv(); StoreUIManager.Instance.ResetClientPosition(); map.ResetCreditDisplayPosition(); }
            shownScene=scene; e.Validate();
        }
        TickTripUi();
    }
    internal int GuestScavLeft;
    internal float GuestScavMinor,GuestScavMajor;
}

[HarmonyPatch(typeof(MapUIManager),nameof(MapUIManager.Travel))]
internal static class SharedTravelRequest
{
    private static bool Prefix(AfterhourLocation location)
    {
        if(NativeWorld.NativeTravelExecution) return true;
        var world=NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host;
        if(world==null) return true; world.ProposeTrip(location?.Id); return false;
    }
}
[HarmonyPatch(typeof(MapUIManager),nameof(MapUIManager.Return))]
internal static class SharedReturnRequest
{
    private static bool Prefix()
    { if(NativeWorld.NativeTravelExecution) return true; var w=NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host; if(w==null) return true; w.ProposeTrip("return"); return false; }
}
[HarmonyPatch(typeof(MapUIManager),nameof(MapUIManager.ReturnNow))]
internal static class SharedConfirmedReturn
{
    private static bool Prefix()
    { if(NativeWorld.NativeTravelExecution) return true; var w=NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host; if(w==null) return true; w.ProposeTrip("return"); return false; }
}
[HarmonyPatch(typeof(MapUIManager),nameof(MapUIManager.OpenUI))]
internal static class LocalTransitView
{
    private static bool Prefix(MapUIManager __instance)
    { if(NativeWorld.Host==null && !NativeGuest.Active) return true; TransitUI.Open(__instance.transitPrefab); return false; }
}
[HarmonyPatch(typeof(MapUIManager),nameof(MapUIManager.CloseUI))]
internal static class CloseLocalTransitView
{
    private static bool Prefix()
    { if(NativeWorld.NativeTravelExecution || NativeWorld.Host==null && !NativeGuest.Active) return true; TransitUI.Close(); return false; }
}
[HarmonyPatch(typeof(MapUIManager),"Update")]
internal static class ReplicaLocationTick
{
    private static bool Prefix()
    {
        if(!NativeGuest.Active) return true;
        if(MapUIManager.Instance.Current?.Id==AfterhourLocation.DUMPING_GROUND) MapUIManager.Instance.UpdateScavUI();
        return false;
    }
}
[HarmonyPatch(typeof(ScavHelper),nameof(ScavHelper.ScavengeDumpingGrounds))]
internal static class ReplicaScavengeRequest
{ private static bool Prefix() { if(!NativeGuest.Active) return true; NativeWorld.Guest?.RequestScavenge(); return false; } }
[HarmonyPatch(typeof(ScavHelper),nameof(ScavHelper.GetScavTimeLeft))]
internal static class ReplicaScavCount
{ private static bool Prefix(ref int __result) { if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true || NativeWorld.Guest.HasActorProjection) return true; __result=NativeWorld.Guest.GuestScavLeft; return false; } }
[HarmonyPatch(typeof(ScavHelper),nameof(ScavHelper.GetMinorWoundChance))]
internal static class ReplicaScavMinor
{ private static bool Prefix(ref float __result) { if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true || NativeWorld.Guest.HasActorProjection) return true; __result=NativeWorld.Guest.GuestScavMinor; return false; } }
[HarmonyPatch(typeof(ScavHelper),nameof(ScavHelper.GetMajorWoundChance))]
internal static class ReplicaScavMajor
{ private static bool Prefix(ref float __result) { if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true || NativeWorld.Guest.HasActorProjection) return true; __result=NativeWorld.Guest.GuestScavMajor; return false; } }
[HarmonyPatch]
internal static class ReplicaLocationDiscovery
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(DrJacksonLocation),nameof(AfterhourLocation.IsDiscovered));
        yield return AccessTools.Method(typeof(DumpingGroundLocation),nameof(AfterhourLocation.IsDiscovered));
        yield return AccessTools.Method(typeof(CommissaryLocation),nameof(AfterhourLocation.IsDiscovered));
    }
    private static bool Prefix(AfterhourLocation __instance,ref bool __result)
    { if(!NativeGuest.Active || NativeWorld.Guest?.IsDiscovered(__instance.Id,out bool value)!=true) return true; __result=value; return false; }
}
