using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private string CounterProof() => HashData(new JObject { ["scene"]=MapUIManager.Instance.Current?.Id ?? "",
        ["items"]=new JArray(EmporiumEntry.Instance.frontInvinvElement.childItems.Concat(EmporiumEntry.Instance.backInvinvElement.childItems)
            .Select(i=>host ? MoveToken(i) : (string)model.Data[model.ReplicaId(i)]["moveToken"])) });
    internal void RequestCounterTransfer() => Submit(new JObject { ["kind"]="bulk",["action"]="counter",["front"]=EmporiumEntry.Instance.frontInvWindow.IsVisible(),["proof"]=CounterProof() });
    private static GameItem[] CounterItems(bool front,out GameInventory destination)
    {
        var entry=EmporiumEntry.Instance;
        bool toCounter=front && entry.frontInvinvElement.childItems.Count>0;
        destination=toCounter ? entry.backInvinvElement : entry.invElement;
        return (toCounter ? entry.frontInvinvElement.childItems : entry.backInvinvElement.childItems
            .Concat(front ? entry.frontInvinvElement.childItems : Enumerable.Empty<GameItem>()).Where(GeneralHelper.IsItemOwned)).ToArray();
    }
    internal bool CanTransferLocalCounter()
    {
        if(!PartnerHasHand) return true;
        var items=CounterItems(EmporiumEntry.Instance.frontInvWindow?.IsVisible()==true,out var destination);
        return !items.Any(RejectHeldInput) && !PartnerBlocksInventory(destination,true);
    }
    private ushort ExecuteBulk(JObject command)
    {
        if((string)command["action"]!="counter") return (ushort)ShopResult.Unavailable;
        if(shop.Settling) return (ushort)ShopResult.Busy;
        if((string)command["proof"]!=CounterProof()) return (ushort)ShopResult.Stale;
        var entry=EmporiumEntry.Instance; bool front=(bool)command["front"];
        if(!Accessible(entry.backInvinvElement) || front && !Accessible(entry.frontInvinvElement)) return (ushort)ShopResult.Unavailable;
        var items=CounterItems(front,out var destination);
        if(items.Any(HandTouches) || HandInside(destination)) return (ushort)ShopResult.Busy;
        if(!Accessible(destination)) return (ushort)ShopResult.Unavailable;
        int moved=0;
        foreach(var item in items)
        {
            if(!item.MayRemove()) continue;
            int amount=System.Math.Min(item.unitCount,item.MaxNumRemove());
            if(amount>0) moved+=amount-GraphUtils.TryAcceptAll(destination,item,amount);
        }
        TutorialUIManager.Instance.OnTransferButtonClicked();
        nextCapture=0;
        return (ushort)(moved>0 || items.Length==0 ? ShopResult.Applied : ShopResult.NoSpace);
    }
}
[HarmonyPatch(typeof(StoreTransferButton),nameof(StoreTransferButton.OnClick))]
internal static class ReplicaTransferCounter
{ private static bool Prefix(StoreTransferButton __instance) { if(!NativeGuest.Active) return NativeWorld.Host?.CanTransferLocalCounter()!=false; __instance.audioSource.Play(); NativeWorld.Guest?.RequestCounterTransfer(); return false; } }
