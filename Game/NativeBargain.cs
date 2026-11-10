using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private static readonly FieldInfo[] BargainFields = new[]{"baseTradeRepMultiplier","discountOffered","discountPercent",
        "markupOffered","markupPercent","markupApplied","blackmailOffered","blackmailApplied","buyingMarkupOffered",
        "buyingMarkupPercent","buyingDiscountOffered","buyingDiscountPercent","buyingDiscountApplied","threatenOffered","threatenApplied"}
        .Select(n=>AccessTools.Field(typeof(BargainUIManager),n)).ToArray();
    private static JObject CaptureBargain()
    {
        var ui=BargainUIManager.Instance;
        // Same refresh the original CanOffer queries use; never perform a bargain here.
        AccessTools.Method(typeof(BargainUIManager),"Refresh").Invoke(ui,null);
        var data=new JObject(); foreach(var f in BargainFields) data[f.Name]=JToken.FromObject(f.GetValue(ui));
        return data;
    }
    private static void ApplyBargain(JObject data)
    {
        var ui=BargainUIManager.Instance;
        AccessTools.Field(typeof(BargainUIManager),"trackedClient").SetValue(ui,PlayerStore.Instance.currentClientInstance?.GetClientBlueprint());
        foreach(var f in BargainFields) f.SetValue(ui,data[f.Name].ToObject(f.FieldType));
        if(ui.IsOpen) ui.RefreshUI();
    }
    internal void RequestBargain(string action,int percent) => Submit(new JObject {
        ["kind"]="bargain",["action"]=action,["percent"]=percent,["token"]=tradeToken });
    private ushort ExecuteBargain(JObject command)
    {
        if(shop.Settling || NegociationUIManager.IsTradeLocked()) return (ushort)ShopResult.Busy;
        if((string)command["token"]!=TradeToken()) return (ushort)ShopResult.Stale;
        var store=PlayerStore.Instance; var client=store.currentClientInstance?.GetClientBlueprint();
        if(client==null || client.isBusinessComplete || !store.isClientArrived || !client.isBargainAvailable ||
            !store.isBargainIntroduced || MapUIManager.Instance.IsOutside || StartingPerk.IsAwfulNegotiator()) return (ushort)ShopResult.Unavailable;
        int percent=(int)command["percent"]; string action=(string)command["action"];
        if(action=="Blackmail" || action=="ThreatenAug") { if(percent!=0) return (ushort)ShopResult.Unavailable; }
        else if(!new[]{10,25,50,75}.Contains(percent)) return (ushort)ShopResult.Unavailable;
        var ui=BargainUIManager.Instance;
        switch(action)
        {
            case "OfferDiscount": if(!ui.CanOfferDiscount()) return (ushort)ShopResult.Unavailable; ui.OfferDiscount(percent); break;
            case "OfferMarkup": if(!ui.CanOfferMarkup()) return (ushort)ShopResult.Unavailable; ui.OfferMarkup(percent); break;
            case "Blackmail": if(!ui.CanBlackmail()) return (ushort)ShopResult.Unavailable; ui.Blackmail(); break;
            case "OfferBuyingMarkup": if(!ui.CanOfferBuyingMarkup()) return (ushort)ShopResult.Unavailable; ui.OfferBuyingMarkup(percent); break;
            case "DemandBuyingDiscount": if(!ui.CanDemandBuyingDiscount()) return (ushort)ShopResult.Unavailable; ui.DemandBuyingDiscount(percent); break;
            case "ThreatenAug": if(!ui.CanThreatenAug()) return (ushort)ShopResult.Unavailable; ui.ThreatenAug(); break;
            default: return (ushort)ShopResult.Unavailable;
        }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
}

[HarmonyPatch]
internal static class NativeBargainRequests
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach(string n in new[]{"OfferDiscount","OfferMarkup","Blackmail","OfferBuyingMarkup","DemandBuyingDiscount","ThreatenAug"})
            yield return AccessTools.Method(typeof(BargainUIManager),n);
    }
    private static bool Prefix(MethodBase __originalMethod,object[] __args)
    {
        if(!NativeGuest.Active) return true;
        NativeWorld.Guest?.RequestBargain(__originalMethod.Name,__args.Length==0 ? 0 : (int)__args[0]); return false;
    }
}
