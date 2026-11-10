using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private int calendarRevision;
    private bool pendingCalendar;
    private string statusProof;
    private readonly RunClockSync runClock=new RunClockSync();
    internal float SharedRunSeconds=>runClock.At(UnityEngine.Time.realtimeSinceStartupAsDouble);
    private JArray clientelePredictions=new JArray();
    private JObject itemNames=new JObject(),clientNames=new JObject();
    internal static bool RefreshingCalendar;
    private JObject CaptureAuxiliary()
    {
        foreach(var item in model.Objects.Values.OfType<GameItem>()) if(!string.IsNullOrEmpty(item.identifier)) itemNames[item.identifier]=item.name;
        CaptureAppraisalNames();
        runClock.Sample(PlayerStore.Instance.GetCurrentRunPlayTime(),UnityEngine.Time.realtimeSinceStartupAsDouble);
        return new JObject { ["clockSerial"]=runClock.Serial,["runSeconds"]=runClock.Seconds,["calendarRevision"]=calendarRevision,["predictions"]=clientelePredictions.DeepClone(),["itemNames"]=itemNames.DeepClone(),["clientNames"]=clientNames.DeepClone() };
    }
    internal List<(string,int,string)> ClientelePredictions() => clientelePredictions.Cast<JArray>().Select(r=>((string)r[0],(int)r[1],(string)r[2])).ToList();
    internal string ItemDisplayName(string id) => string.IsNullOrEmpty(id) ? "[Invalid ID]" : (string)itemNames[id] ?? id;
    internal string ClientDisplayName(string id) => string.IsNullOrEmpty(id) ? "" : (string)clientNames[id] ?? id;
    internal void PrepareClientele() => Submit(new JObject { ["kind"]="auxiliary",["action"]="clientele" });
    internal void PrepareCalendar()
    { if(!RefreshingCalendar) pendingCalendar=Submit(new JObject { ["kind"]="auxiliary",["action"]="calendar" }); }
    internal void RequestScale() => Submit(new JObject { ["kind"]="auxiliary",["action"]="scale",["trade"]=tradeToken });
    private ushort ExecuteAuxiliary(JObject command)
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        switch((string)command["action"])
        {
            case "calendar":
                AdvCalendarUIManager.Instance.HandleInnateEvent(); calendarRevision++; break;
            case "clientele":
                clientelePredictions=new JArray();
                foreach(var row in ShowcaseHelper.GetDisplayCasePrediction())
                {
                    clientelePredictions.Add(new JArray(row.Item1,row.Item2,row.Item3));
                    itemNames[row.Item3]=GeneralHelper.GetDisplayNameFromID(row.Item3);
                    clientNames[row.Item1]=StoreClientListDict.GetClientDisplayName(row.Item1);
                }
                clientNames["ulDiscerningShopper"]=StoreClientListDict.GetClientDisplayName("ulDiscerningShopper");
                break;
            case "scale":
                if(MapUIManager.Instance.IsOutside || (string)command["trade"]!=TradeToken()) return (ushort)ShopResult.Stale;
                var items=EmporiumEntry.Instance.backInvinvElement.childItems;
                if(items.Count!=1 || items[0].FindItemFeatureByCategory("weight")==null) return (ushort)ShopResult.Unavailable;
                var store=PlayerStore.Instance; var previous=store.currentNegociatedItem;
                try { store.currentNegociatedItem=items[0]; StoreScale.Instance.OnClick(); } finally { store.currentNegociatedItem=previous; }
                break;
            default:return (ushort)ShopResult.Unavailable;
        }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    private void ApplyAuxiliary(JObject data)
    {
        runClock.Receive((ulong)data["clockSerial"],(float)data["runSeconds"],UnityEngine.Time.realtimeSinceStartupAsDouble);
        int revision=(int)data["calendarRevision"];
        clientelePredictions=(JArray)data["predictions"]; itemNames=(JObject)data["itemNames"]; clientNames=(JObject)data["clientNames"];
        bool redraw=pendingCalendar && revision!=calendarRevision; calendarRevision=revision;
        var ui=AdvCalendarUIManager.Instance;
        if(redraw)
        {
            pendingCalendar=false;
            if(ui?.UIPanel?.activeSelf==true && ui.calendarSection.activeSelf)
            { RefreshingCalendar=true; try { ui.OpenUI(); } finally { RefreshingCalendar=false; } }
        }
        NativeActorPersonalUi.RefreshOpenStatus(PlayerStore.Instance,ref statusProof);
        if(ClienteleUIManager.Instance?.panel?.activeSelf==true) ClienteleUIManager.Instance.RefreshUI();
        StoreScale.Instance?.UpdateScaleDisplay();
    }
}
[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.GetCurrentRunPlayTime))]
internal static class ReplicaRunClock
{ private static bool Prefix(ref float __result) { if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true) return true; __result=NativeWorld.Guest.SharedRunSeconds; return false; } }
[HarmonyPatch(typeof(AdvCalendarUIManager),nameof(AdvCalendarUIManager.HandleInnateEvent))]
internal static class ReplicaCalendarScheduling
{ private static bool Prefix() { if(!NativeGuest.Active) return true; NativeWorld.Guest?.PrepareCalendar(); return false; } }
[HarmonyPatch(typeof(StoreScale),nameof(StoreScale.OnClick))]
internal static class ReplicaWeighItem
{ private static bool Prefix() { if(!NativeGuest.Active) return true; NativeWorld.Guest?.RequestScale(); return false; } }
[HarmonyPatch(typeof(ClienteleUIManager),nameof(ClienteleUIManager.OpenUI))]
internal static class ReplicaClienteleOpen
{ private static void Prefix() { if(NativeGuest.Active) NativeWorld.Guest?.PrepareClientele(); } }
[HarmonyPatch(typeof(ShowcaseHelper),nameof(ShowcaseHelper.GetDisplayCasePrediction))]
internal static class ReplicaClientelePrediction
{ private static bool Prefix(ref List<(string,int,string)> __result) { if(!NativeGuest.Active) return true; __result=NativeWorld.Guest?.ClientelePredictions() ?? new List<(string,int,string)>(); return false; } }
[HarmonyPatch(typeof(GeneralHelper),nameof(GeneralHelper.GetDisplayNameFromID))]
internal static class ReplicaItemNameLookup
{ private static bool Prefix(string itemID,ref string __result) { if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true) return true; __result=NativeWorld.Guest.ItemDisplayName(itemID); return false; } }
[HarmonyPatch(typeof(StoreClientListDict),nameof(StoreClientListDict.GetClientDisplayName))]
internal static class ReplicaClientNameLookup
{ private static bool Prefix(string identifier,ref string __result) { if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true) return true; __result=NativeWorld.Guest.ClientDisplayName(identifier); return false; } }
