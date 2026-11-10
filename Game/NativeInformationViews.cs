using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    // Vanilla's empty newspaper creates an event. Run that fallback only on the
    // host, then let the original guest renderer consume the resulting projection.
    private bool newsPending;
    internal void RequestNews() { newsPending=true; FlushNews(); }
    private void FlushNews()
    {
        if(!newsPending || link==null || UiPending("newspaper")) return;
        if(NewsUIManager.Instance.uiGameObject.activeSelf && !StoreStation.Instance.storeEventManager.GetActiveEvents().Any(e=>!e.isHiddenFromNewsPaper))
        { if(!Submit(new JObject { ["kind"]="newspaper" })) return; }
        newsPending=false;
    }
    private ushort ExecuteNews()
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        var manager=StoreStation.Instance.storeEventManager;
        if(!manager.GetActiveEvents().Any(e=>!e.isHiddenFromNewsPaper))
            manager.ActivateEvent(EventListCosmetic.AllQuiet(),debugLog:false,force:true);
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
}

[HarmonyPatch(typeof(NewsUIManager),nameof(NewsUIManager.PopulateUI))]
internal static class ReplicaNewsView
{
    private static bool Prefix(NewsUIManager __instance)
    {
        if(!NativeGuest.Active || StoreStation.Instance.storeEventManager.GetActiveEvents().Any(e=>!e.isHiddenFromNewsPaper)) return true;
        __instance.SelectLayout(new System.Collections.Generic.List<StoreEvent>()); NativeWorld.Guest?.RequestNews(); return false;
    }
}

[HarmonyPatch(typeof(StoreReputation),nameof(StoreReputation.UpdateReputation))]
internal static class ReplicaReputationRules
{ private static bool Prefix() => !NativeGuest.Active; }

internal static class ReplicaReputationView
{
    private static readonly ConditionalWeakTable<ReputationElement,StoreReputation> rows=new ConditionalWeakTable<ReputationElement,StoreReputation>();
    private static StoreReputationPerk hovered;
    internal static void Refresh()
    {
        var ui=ReputationUIManager.Instance;
        if(ui?.uiPanel.activeSelf!=true) return;
        foreach(var row in ui.layout.GetComponentsInChildren<ReputationElement>())
        {
            if(!rows.TryGetValue(row,out var reputation)) continue;
            row.reputationStatus.text=StoreReputation.GetReputationStatus(reputation.GetReputation());
            row.reputationNumber.text=reputation.GetReputationExact().ToString("F2")+"/200";
            row.UpdateBar(reputation.GetReputation());
        }
        if(hovered!=null) ui.UpdateToolTipContent(hovered);
    }
    [HarmonyPatch(typeof(ReputationElement),nameof(ReputationElement.Init))]
    private static class Bind
    { private static void Postfix(ReputationElement __instance,StoreReputation storeReputation) { if(!NativeGuest.Active) return; rows.Remove(__instance); rows.Add(__instance,storeReputation); } }
    [HarmonyPatch(typeof(ReputationUIManager),nameof(ReputationUIManager.UpdateToolTipContent))]
    private static class Hover
    { private static void Postfix(StoreReputationPerk perk) { if(NativeGuest.Active) hovered=perk; } }
    [HarmonyPatch(typeof(ReputationUIManager),nameof(ReputationUIManager.OpenUI))]
    private static class Open
    { private static void Prefix() { if(NativeGuest.Active) hovered=null; } }
}
