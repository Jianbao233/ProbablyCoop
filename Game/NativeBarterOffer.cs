using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    // Changing offer sets replaces the shared customer's goods. Their identity
    // is part of the proof: returning to the same set must not revive an old click.
    private string BarterGoodsProof()
    {
        var e=EmporiumEntry.Instance;
        return string.Join(";",new[]{e.frontInvinvElement,e.backInvinvElement}
            .SelectMany((inventory,place)=>inventory.childItems.Where(i=>!GeneralHelper.IsItemOwned(i))
                .Select(i=>new { Id=host ? model.Id(i) : model.ReplicaId(i), Count=i.unitCount, Place=place }))
            .OrderBy(row=>row.Id).Select(row=>row.Id+":"+row.Count+":"+row.Place));
    }
    private JObject BarterOfferCommand(int offset)
    {
        var client=PlayerStore.Instance.currentClientInstance?.GetClientBlueprint();
        var offer=client?.barterOffer;
        if(host || !Ready || client==null || offer==null || offset is not (-1 or 1)) return null;
        var command=new JObject { ["kind"]="barterOffer",["offset"]=offset,
            ["client"]=replicaCustomerId,["index"]=offer.currentSetIndex,["count"]=offer.OfferSetCount(),
            ["token"]=tradeToken,["goods"]=BarterGoodsProof() };
        SealOperation(command);
        return command;
    }
    internal bool RequestBarterOffer(int offset)
    {
        var command=BarterOfferCommand(offset);
        return command!=null && Submit(command);
    }
    private ushort BarterOfferAvailability(int offset)
    {
        if(offset is not (-1 or 1)) return (ushort)ShopResult.Unavailable;
        if(NativeActorSession.Current is NativeActorSession actors && (actors.Faulted || actors.Terminal))
            return (ushort)ShopResult.Busy;
        var store=PlayerStore.Instance; var client=store.currentClientInstance?.GetClientBlueprint();
        if(client?.clientIntent!=StoreClient.ClientIntent.BARTER || client.isBusinessComplete || !client.isIntroduced ||
            client.barterOffer?.UseOfferSets()!=true || client.barterOffer.OfferSetCount()<2 || !store.isClientArrived ||
            MapUIManager.Instance.IsOutside || store.storeState!=PlayerStore.StoreState.OPEN)
            return (ushort)ShopResult.Unavailable;
        if(shop?.Settling==true || NegociationUIManager.IsTradeLocked() ||
            DialogUIManager.Instance.GetCurrentDialog()?.isMainDialog==true) return (ushort)ShopResult.Busy;
        // Refresh even on local host clicks. Remote UI execution already has a
        // footprint but local clicks can occur without a network execution scope.
        var held=CaptureHostHand(true);
        if(EmporiumEntry.Instance.GetAllNonOwnedItem().Any(held.TouchesItem)) return (ushort)ShopResult.Busy;
        return (ushort)ShopResult.Applied;
    }
    internal bool AllowNativeBarterOffer(int offset)
    {
        ushort result=BarterOfferAvailability(offset);
        if(result==(ushort)ShopResult.Busy && !ReferenceEquals(RemoteUiExecution,this))
            CommandNotice("请先放下当前报价物品，再切换交换方案。");
        return result==(ushort)ShopResult.Applied;
    }
    internal bool AllowNativeBarterAccept()
    {
        if(PlayerStore.Instance.currentClientInstance?.GetClientBlueprint()?.clientIntent!=StoreClient.ClientIntent.BARTER) return true;
        if(NativeActorSession.Current is NativeActorSession actors && (actors.Faulted || actors.Terminal)) return false;
        var held=CaptureHostHand(true); var e=EmporiumEntry.Instance;
        bool blocked=held.TouchesContents(e.frontInvinvElement) || held.TouchesContents(e.backInvinvElement);
        if(blocked && !ReferenceEquals(RemoteUiExecution,this)) CommandNotice("请先放下柜台物品，再确认交换。");
        return !blocked;
    }
    private ushort ExecuteBarterOffer(JObject command)
    {
        if(command["offset"]?.Type!=JTokenType.Integer || !int.TryParse(command["offset"].ToString(),out int offset) ||
            command["client"]?.Type!=JTokenType.Integer || !uint.TryParse(command["client"].ToString(),out uint customer) ||
            command["index"]?.Type!=JTokenType.Integer || !int.TryParse(command["index"].ToString(),out int index) ||
            command["count"]?.Type!=JTokenType.Integer || !int.TryParse(command["count"].ToString(),out int count) ||
            command["token"]?.Type!=JTokenType.String || command["goods"]?.Type!=JTokenType.String)
            return (ushort)ShopResult.Unavailable;
        var client=PlayerStore.Instance.currentClientInstance?.GetClientBlueprint();
        var offer=client?.barterOffer;
        if(client==null || offer==null || customer==0 || customer!=model.Id(PlayerStore.Instance.currentClientInstance) || index!=offer.currentSetIndex ||
            count!=offer.OfferSetCount() || (string)command["token"]!=TradeToken() || (string)command["goods"]!=BarterGoodsProof())
            return (ushort)ShopResult.Stale;
        ushort available=BarterOfferAvailability(offset);
        if(available!=(ushort)ShopResult.Applied) return available;
        var ui=NegociationUIManager.Instance;
        if(offset<0) ui.OnLeftArrowButtonClick(); else ui.OnRightArrowButtonClick();
        if(offer.currentSetIndex!=(index+offset+count)%count)
            throw new InvalidOperationException("Native barter offer did not advance after validation");
        nextCapture=0;
        return (ushort)ShopResult.Applied;
    }
}

[HarmonyPatch(typeof(NegociationUIManager),nameof(NegociationUIManager.OnOfferAcceptClick))]
internal static class NativeBarterAcceptUiGuard
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix()=>NativeGuest.Active || NativeWorld.Host?.AllowNativeBarterAccept()!=false;
}

[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.AcceptBarter))]
internal static class NativeBarterAcceptGuard
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(out NativeActorSession __state)
    {
        __state=null;
        if(NativeGuest.Active) return false;
        if(NativeWorld.Host==null) return true;
        bool allowed=NativeWorld.Host.AllowNativeBarterAccept();
        if(allowed) __state=NativeActorSession.Current;
        return allowed;
    }
    private static void Finalizer(Exception __exception,bool __runOriginal,NativeActorSession __state)
    { if(__exception!=null && __runOriginal && __state!=null) __state.MarkFault(__exception); }
}

[HarmonyPatch(typeof(NegociationUIManager),"CycleBarterOfferSet")]
internal static class NativeBarterOfferRequests
{
    private static bool Prefix(int offset,out NativeActorSession __state)
    {
        __state=null;
        if(NativeGuest.Active) { NativeWorld.Guest?.RequestBarterOffer(offset); return false; }
        if(NativeWorld.Host==null) return true;
        bool allowed=NativeWorld.Host.AllowNativeBarterOffer(offset);
        if(allowed) __state=NativeActorSession.Current;
        return allowed;
    }
    private static void Finalizer(Exception __exception,bool __runOriginal,NativeActorSession __state)
    {
        if(__exception!=null && __runOriginal && __state!=null) __state.MarkFault(__exception);
    }
}
