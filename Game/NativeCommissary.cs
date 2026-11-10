using System;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    internal void RequestCommissary(string action,string id="")
    {
        var data=PlayerStore.Instance.commissaryData; var stock=data.GetItemData(id);
        Submit(new JObject { ["kind"]="commissary",["action"]=action,["id"]=id,["day"]=StoreStation.Instance.dayCounter,
            ["tokens"]=data.redeemedCredit,["stock"]=stock?.StockCount ?? -1,["price"]=stock?.price ?? 0 });
    }
    private ushort ExecuteCommissary(JObject command)
    {
        if(shop.Settling || MapUIManager.Instance.Current?.Id!=AfterhourLocation.COMMISSARY) return (ushort)ShopResult.Unavailable;
        var data=PlayerStore.Instance.commissaryData;
        if((int)command["day"]!=StoreStation.Instance.dayCounter || (int)command["tokens"]!=data.redeemedCredit) return (ushort)ShopResult.Stale;
        switch((string)command["action"])
        {
            case "buy":
                string id=(string)command["id"]; var stock=data.GetItemData(id);
                if(stock==null || !CommissaryUIManager.CanBuy(id)) return (ushort)ShopResult.Unavailable;
                if((int)command["stock"]!=stock.StockCount || (int)command["price"]!=stock.price) return (ushort)ShopResult.Stale;
                if(!BuyCommissaryItem(id)) return (ushort)ShopResult.NoSpace;
                break;
            case "stamps": CommissaryData.RedeemAllStampInInventory(); break;
            case "lottery": CommissaryUIManager.Instance.OnRedeemAllLottery(); break;
            default: return (ushort)ShopResult.Unavailable;
        }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    // The native purchase subtracts tokens before attempting an unchecked insertion.
    // In a shared shop another player may fill the ground between viewing and buying.
    // Reserve a valid native slot and insert in the same Unity tick before charging.
    internal static bool BuyCommissaryItem(string id)
    {
        if(MapUIManager.Instance.Current?.Id!=AfterhourLocation.COMMISSARY) return false;
        var data=PlayerStore.Instance.commissaryData; var stock=data.GetItemData(id);
        if(stock==null || stock.StockCount<1 || data.redeemedCredit<stock.price) return false;
        var item=DirectoryMaster.Item(id);
        int quantity=item.unitCount;
        var slot=EmporiumEntry.Instance.afterhourInventory.TryFindOneValidInventorySlot(item);
        if(slot?.IsValid()!=true || slot.numTransfer!=quantity) { item.Destroy(); return false; }
        if(slot.TryAcceptOnce()!=quantity) { if(item.parentInventory==null) item.Destroy(); return false; }
        data.redeemedCredit-=stock.price; stock.StockCount--;
        NegociationUIManager.Instance.audioSourceCashRegister.Play();
        return true;
    }
}
[HarmonyPatch(typeof(CommissaryData),nameof(CommissaryData.BuyItem))]
internal static class ReplicaCommissaryBuy
{ private static bool Prefix(string id) { if(NativeGuest.Active) NativeWorld.Guest?.RequestCommissary("buy",id); else if(NativeWorld.Host!=null) NativeWorld.BuyCommissaryItem(id); else return true; return false; } }
[HarmonyPatch(typeof(CommissaryData),nameof(CommissaryData.RedeemAllStampInInventory))]
internal static class ReplicaCommissaryStamps
{ private static bool Prefix() { if(!NativeGuest.Active) return true; NativeWorld.Guest?.RequestCommissary("stamps"); return false; } }
[HarmonyPatch(typeof(CommissaryUIManager),nameof(CommissaryUIManager.OnRedeemAllLottery))]
internal static class ReplicaCommissaryLottery
{ private static bool Prefix() { if(!NativeGuest.Active) return true; NativeWorld.Guest?.RequestCommissary("lottery"); return false; } }
