using System;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    internal bool CanOpen,CanClose;
    internal int QueuedClients;
    private string shutterToken="";
    private static JObject CaptureShutter() => new JObject {
        ["canOpen"]=PlayerStore.Instance.CanStartDay(),["canClose"]=PlayerStore.Instance.CanEndDay(),
        ["queue"]=PlayerStore.Instance.storeClientManager.clientStack.Count,
        ["day"]=StoreStation.Instance.dayCounter,["phase"]=(int)PlayerStore.Instance.storeState
    };
    private void ApplyShutter(JObject data)
    { CanOpen=(bool)data["canOpen"]; CanClose=(bool)data["canClose"]; QueuedClients=(int)data["queue"]; shutterToken=HashData(data); }
    internal void RequestShutter(bool close) => Submit(new JObject { ["kind"]="shutter",["close"]=close,["token"]=shutterToken });
    private ushort ExecuteShutter(JObject command)
    {
        if(shop.Settling || NegociationUIManager.IsTradeLocked()) return (ushort)ShopResult.Busy;
        if((string)command["token"]!=HashData(CaptureShutter())) return (ushort)ShopResult.Stale;
        var store=PlayerStore.Instance;
        if(store.lockShutterButtonFlag>0 || store.isClientBeingArrested || store.isHandlingShootout || MapUIManager.Instance.IsOutside) return (ushort)ShopResult.Unavailable;
        if((bool)command["close"])
        {
            if(store.storeState!=PlayerStore.StoreState.OPEN || !store.CanEndDay()) return (ushort)ShopResult.Unavailable;
            StoreShutterButton.Instance.EndAndClose();
        }
        else
        {
            if(store.storeState!=PlayerStore.StoreState.MORNING || !store.CanStartDay()) return (ushort)ShopResult.Unavailable;
            StoreShutterButton.Instance.OpenShutter();
        }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
}
[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.CanStartDay))]
internal static class ReplicaCanOpen
{ private static bool Prefix(ref bool __result) { if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true) return true; __result=NativeWorld.Guest.CanOpen; return false; } }
[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.CanEndDay))]
internal static class ReplicaCanClose
{ private static bool Prefix(ref bool __result) { if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true) return true; __result=NativeWorld.Guest.CanClose; return false; } }
[HarmonyPatch(typeof(StoreShutterButton),nameof(StoreShutterButton.CloseShutter))]
internal static class ReplicaCloseConfirmation
{
    private static bool Prefix(StoreShutterButton __instance)
    {
        if(!NativeGuest.Active) return true;
        if(NativeWorld.Guest?.QueuedClients>0) __instance.ShowConfirmCloseUI(); else __instance.EndAndClose(); return false;
    }
}
[HarmonyPatch(typeof(StoreShutterButton),nameof(StoreShutterButton.OpenShutter))]
internal static class ReplicaOpenShutter
{ private static bool Prefix() { if(!NativeGuest.Active) return true; NativeWorld.Guest?.RequestShutter(false); return false; } }
[HarmonyPatch(typeof(StoreShutterButton),nameof(StoreShutterButton.EndAndClose))]
internal static class ReplicaConfirmShutter
{ private static bool Prefix() { if(!NativeGuest.Active) return true; NativeWorld.Guest?.RequestShutter(true); StoreUIManager.Instance.closeShutterConfirmationPanel.SetActive(false); return false; } }
