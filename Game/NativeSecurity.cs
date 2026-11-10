using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;
internal sealed partial class NativeWorld
{
    private JObject securityScene;
    private uint reportRevision;
    internal void ReportChanged() { reportRevision++; nextCapture=0; }
    private string ReportProof() => HashData(new JObject { ["client"]=model.Id(PlayerStore.Instance.currentClientInstance?.GetClientBlueprint()),
        ["reported"]=PlayerStore.Instance.currentClientInstance?.GetClientBlueprint().reported ?? false,["revision"]=reportRevision });
    private JObject CaptureSecurityScene() => new JObject { ["proof"]=ReportProof(),["glass"]=StoreUIManager.Instance.StoreGlass.IsClosed,
        ["officers"]=new JArray(CaptureOfficer(StoreUIManager.Instance.secGameObject),CaptureOfficer(StoreUIManager.Instance.secGameObject2)) };
    private static JObject CaptureOfficer(GenericClientMono actor)
    {
        var p=actor.transform.localPosition; var c=actor.image.color;
        string key=null;
        using(NativePerformance.Measure("host.officer_sprite"))
        {
            // Read the native renderer exactly once. Preserve the dictionary's
            // first-match order even if mods install aliases for the same sprite.
            var sprite=actor.image.sprite;
            foreach(var pair in SpriteDict.Instance.spriteDictionary)
                if(pair.Value==sprite) { key=pair.Key; break; }
        }
        return new JObject { ["visible"]=actor.gameObject.activeSelf,["sprite"]=key,["position"]=new JArray(p.x,p.y,p.z),["color"]=new JArray(c.r,c.g,c.b,c.a) };
    }
    private void ApplySecurityScene(JObject data)
    {
        securityScene=data;
        var ui=StoreUIManager.Instance;
        if(ui.StoreGlass.IsClosed!=(bool)data["glass"]) ui.StoreGlass.ToggleShutterDoor();
        ApplyOfficer(ui.secGameObject,(JObject)data["officers"][0]); ApplyOfficer(ui.secGameObject2,(JObject)data["officers"][1]);
    }
    private static void ApplyOfficer(GenericClientMono actor,JObject data)
    {
        bool active=(bool)data["visible"];
        if(active && !actor.gameObject.activeSelf) actor.SetImmediatlyArrive();
        actor.gameObject.SetActive(active);
        string key=(string)data["sprite"];
        if(key!=null && SpriteDict.Instance.spriteDictionary.TryGetValue(key,out var sprite)) actor.image.sprite=sprite;
        var p=(JArray)data["position"]; actor.transform.localPosition=new Vector3((float)p[0],(float)p[1],(float)p[2]);
        var c=(JArray)data["color"]; actor.image.color=new Color((float)c[0],(float)c[1],(float)c[2],(float)c[3]);
    }
    internal void RequestReport() { if(securityScene!=null) Submit(new JObject { ["kind"]="security",["proof"]=securityScene["proof"] }); }
    private ushort ExecuteReport(JObject command)
    {
        if(shop.Settling || MapUIManager.Instance.IsOutside) return (ushort)ShopResult.Busy;
        if((string)command["proof"]!=ReportProof()) return (ushort)ShopResult.Stale;
        var store=PlayerStore.Instance; var client=store.currentClientInstance?.GetClientBlueprint();
        if(client==null || !store.isClientArrived || !client.CanReport() || StoreUIManager.Instance.isShot || StoreReputation.IsPerkUnlocked("SEC_ALARM_IGNORED")) return (ushort)ShopResult.Unavailable;
        store.OnCustomerReported(); nextCapture=0; return (ushort)ShopResult.Applied;
    }
}
[HarmonyPatch(typeof(StoreSecButton),nameof(StoreSecButton.OnClick))]
internal static class ReplicaSecurityButton
{
    private static bool Prefix(StoreSecButton __instance)
    { if(!NativeGuest.Active) return true; __instance.audioSource.Play(); NativeWorld.Guest?.RequestReport(); return false; }
}
[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.OnCustomerReported))]
internal static class ReplicaSecurityReport
{
    private static bool Prefix() { if(!NativeGuest.Active) return true; NativeWorld.Guest?.RequestReport(); return false; }
    private static void Postfix() { if(!NativeGuest.Active) NativeWorld.Host?.ReportChanged(); }
}
[HarmonyPatch]
internal static class ReplicaSecurityNoArrivalBusiness
{
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(StoreUIManager),nameof(StoreUIManager.OnGenericArrived));
        yield return AccessTools.Method(typeof(StoreUIManager),nameof(StoreUIManager.OnGenericLeft));
    }
    private static bool Prefix() => !NativeGuest.Active;
}
