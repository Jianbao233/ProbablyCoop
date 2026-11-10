using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private uint wildOpenSerial,wildAppliedSerial;
    private JObject wildState;
    private bool wildAwaitingClose;
    private JObject pendingWildClose;
    private string WildProof() => HashData(new JObject { ["cash"]=PlayerStore.Instance.playerCash,["favor"]=PlayerStore.Instance.wildFavor,
        ["upgrades"]=new JObject(PlayerStore.Instance.networkUpgrade.Select(p=>new JProperty(p.Key,BusinessFields(p.Value,UpgradeFields)))) });
    private string WildCloseProof() => HashData(new JObject { ["open"]=wildOpenSerial,
        ["client"]=model.Id(PlayerStore.Instance.currentClientInstance?.GetClientBlueprint()),["intro"]=TutorialUIManager.Instance.isIntroBrowsingUpgrade });
    private JObject CaptureWild() => new JObject { ["open"]=wildOpenSerial,["active"]=wildAwaitingClose,["proof"]=null,["closeProof"]=WildCloseProof() };
    private int verifiedUpgradeInputs;
    private readonly NativeDeferredFields.UpgradeProofCache upgradeProofCache=new NativeDeferredFields.UpgradeProofCache();
    internal void HandoffWild()
    {
        NetworkUpgrade.AddMissingAfterLoad(); PlayerStore.Instance.daySinceLastBrowseUpgrade=0;
        wildOpenSerial++; wildAwaitingClose=true; nextCapture=0;
    }
    private void ApplyWild(JObject data)
    {
        bool changed=wildState==null || !JToken.DeepEquals(wildState["proof"],data["proof"]);
        wildState=data;
        uint serial=(uint)data["open"];
        if(serial!=wildAppliedSerial) { wildAppliedSerial=serial; pendingWildClose=null; if((bool)data["active"]) { PhoneUIManager.Instance.CloseUI(); WildNetworkUI.Open(); } }
        else if(changed && WildNetworkUI.IsOpen) WildNetworkUI.RefreshAll();
    }
    internal bool RequestWild(string action,string id="")
    {
        if(wildState==null) return false;
        var command=new JObject { ["kind"]="upgrade",["action"]=action,["id"]=id,["proof"]=wildState[action=="close" ? "closeProof" : "proof"] };
        if(action!="close") return Submit(command);
        if(!(bool)wildState["active"]) return false;
        // A native close is not repeatable by the player after the window disappears.
        // Preserve its original context while a purchase or another request is in flight.
        pendingWildClose=command; FlushWildClose(); return true;
    }
    private void FlushWildClose()
    {
        if(pendingWildClose==null) return;
        if(wildState==null || !(bool)wildState["active"] || (string)pendingWildClose["proof"]!=(string)wildState["closeProof"])
        { pendingWildClose=null; return; }
        if(Submit(pendingWildClose)) pendingWildClose=null;
    }
    private ushort ExecuteWild(JObject command)
    {
        if(shop.Settling || MapUIManager.Instance.IsOutside) return (ushort)ShopResult.Busy;
        var store=PlayerStore.Instance;
        if((string)command["action"]=="close")
        {
            if(!wildAwaitingClose || (string)command["proof"]!=WildCloseProof()) return (ushort)ShopResult.Stale;
            wildAwaitingClose=false;
            if(TutorialUIManager.Instance.isIntroBrowsingUpgrade)
            { TutorialUIManager.Instance.isIntroBrowsingUpgrade=false; DialogUIManager.Instance.StartDialogue(StoreClientDialogList.WildNetworkContinue()); }
            else if(store.currentClientInstance?.GetClientBlueprint().identifier=="wild_upgrade_reminder") store.DismissCurrentClient(false);
            nextCapture=0; return (ushort)ShopResult.Applied;
        }
        if((string)command["proof"]!=WildProof()) return (ushort)ShopResult.Stale;
        if((string)command["action"]!="buy" || !store.networkUpgrade.TryGetValue((string)command["id"],out var upgrade) ||
            !upgrade.IsReady() || upgrade.IsUnlocked() || upgrade.GetCost()>store.playerCash+store.wildFavor) return (ushort)ShopResult.Unavailable;
        // Native callback handles favor, cash, statistics and the complete upgrade action.
        var field=AccessTools.Field(typeof(WildNetworkUI),"selectedId"); var previous=field.GetValue(null);
        try { field.SetValue(null,upgrade.id); AccessTools.Method(typeof(WildNetworkUI),"OnUseClicked").Invoke(null,null); }
        finally { field.SetValue(null,previous); WildNetworkUI.RefreshAll(); }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
}
[HarmonyPatch(typeof(WildNetworkUI),nameof(WildNetworkUI.Open))]
internal static class ReplicaWildOpen
{
    private static bool Prefix()
    {
        if(NativeGuest.Active || NativeWorld.Host==null) return true;
        string client=PlayerStore.Instance.currentClientInstance?.GetClientBlueprint().identifier;
        bool remoteStory=NativeWorld.Host.RemoteDialogueOwned && (client=="wild2" || client=="wild_upgrade_reminder");
        if(!NativeWorld.Host.RemotePhoneOwned && !remoteStory) return true;
        NativeWorld.Host.HandoffWild(); return false;
    }
}
[HarmonyPatch(typeof(WildNetworkUI),"OnUseClicked")]
internal static class ReplicaWildBuy
{ private static bool Prefix() { if(!NativeGuest.Active) return true; NativeWorld.Guest?.RequestWild("buy",(string)AccessTools.Field(typeof(WildNetworkUI),"selectedId").GetValue(null)); return false; } }
[HarmonyPatch(typeof(NetworkUpgrade),nameof(NetworkUpgrade.AddMissingAfterLoad))]
internal static class ReplicaWildNoFactory
{ private static bool Prefix() => !NativeGuest.Active; }
[HarmonyPatch(typeof(WildNetworkUI),"OnWindowClosed")]
internal static class ReplicaWildClose
{
    private static void Prefix(ref bool __state)
    {
        if(!NativeGuest.Active) return;
        __state=TutorialUIManager.Instance.isIntroBrowsingUpgrade; TutorialUIManager.Instance.isIntroBrowsingUpgrade=false;
        NativeWorld.Guest?.RequestWild("close");
    }
    private static void Postfix(bool __state) { if(NativeGuest.Active) TutorialUIManager.Instance.isIntroBrowsingUpgrade=__state; }
}
[HarmonyPatch(typeof(DialogUIManager),nameof(DialogUIManager.StartDialogue))]
internal static class ReplicaNoLocalDialogueBusiness
{ private static bool Prefix() => !NativeGuest.Active; }
[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.DismissCurrentClient))]
internal static class ReplicaNoLocalDismissBusiness
{ private static bool Prefix() => !NativeGuest.Active; }
