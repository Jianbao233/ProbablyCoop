using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;
internal sealed partial class NativeWorld
{
    private JObject securityScene;
    private uint reportRevision;
    private readonly OfficerState[] officers = { new OfficerState(), new OfficerState() };

    internal enum OfficerMotion { Hidden, Entering, AtCounter, Leaving, Cleared }

    private sealed class OfficerState
    {
        internal GenericClientMono Actor;
        internal OfficerMotion Motion;
        internal ulong Revision;
        internal string Sprite = "";
    }

    internal void ReportChanged() { reportRevision++; nextCapture=0; }
    private string ReportProof() => HashData(new JObject { ["client"]=model.Id(PlayerStore.Instance.currentClientInstance?.GetClientBlueprint()),
        ["reported"]=PlayerStore.Instance.currentClientInstance?.GetClientBlueprint().reported ?? false,["revision"]=reportRevision });
    private JObject CaptureSecurityScene()
    {
        var ui = StoreUIManager.Instance;
        return new JObject { ["proof"] = ReportProof(), ["glass"] = ui.StoreGlass.IsClosed,
            ["officers"] = new JArray(CaptureOfficer(0, ui.secGameObject), CaptureOfficer(1, ui.secGameObject2)) };
    }

    private JArray CaptureOfficer(int index, GenericClientMono actor)
    {
        var state = officers[index];
        if (!actor.gameObject.activeSelf && state.Motion is not (OfficerMotion.Hidden or OfficerMotion.Cleared))
            ChangeOfficer(state, actor, OfficerMotion.Cleared);
        if (actor.gameObject.activeSelf && (state.Actor != actor || state.Motion is OfficerMotion.Hidden or OfficerMotion.Cleared))
            ChangeOfficer(state, actor, OfficerMotion.AtCounter);
        return new JArray(state.Revision, (int)state.Motion, state.Sprite);
    }

    internal void RecordOfficer(GenericClientMono actor, OfficerMotion motion)
    {
        var ui = StoreUIManager.Instance;
        if (ui == null) return;
        int index = actor == ui.secGameObject ? 0 : actor == ui.secGameObject2 ? 1 : -1;
        if (index < 0) return;
        var state = officers[index];
        if (motion == OfficerMotion.Hidden && actor.gameObject.activeSelf ||
            motion == OfficerMotion.AtCounter && state.Motion != OfficerMotion.Entering ||
            motion == OfficerMotion.Leaving && !actor.gameObject.activeSelf) return;
        ChangeOfficer(state, actor, motion);
        nextCapture = 0;
    }

    private static void ChangeOfficer(OfficerState state, GenericClientMono actor, OfficerMotion motion)
    {
        if (motion == OfficerMotion.Entering ||
            motion == OfficerMotion.AtCounter && (state.Actor != actor || state.Motion is OfficerMotion.Hidden or OfficerMotion.Cleared))
        {
            state.Sprite = "";
            var sprite = actor.image.sprite;
            foreach (var pair in SpriteDict.Instance.spriteDictionary)
                if (pair.Value == sprite) { state.Sprite = pair.Key; break; }
        }
        state.Actor = actor;
        state.Motion = motion;
        state.Revision++;
    }
    private void ApplySecurityScene(JObject data)
    {
        securityScene=data;
        var ui=StoreUIManager.Instance;
        if(ui.StoreGlass.IsClosed!=(bool)data["glass"]) ui.StoreGlass.ToggleShutterDoor();
        var rows = (JArray)data["officers"];
        ApplyOfficer(officers[0], ui.secGameObject, (JArray)rows[0]);
        ApplyOfficer(officers[1], ui.secGameObject2, (JArray)rows[1]);
    }

    private static void ApplyOfficer(OfficerState state, GenericClientMono actor, JArray data)
    {
        bool restoring = state.Actor != actor;
        ulong revision = (ulong)data[0];
        if (!restoring && revision <= state.Revision) return;
        var motion = (OfficerMotion)(int)data[1];
        switch (motion)
        {
            case OfficerMotion.Entering:
                actor.StartArrive();
                break;
            case OfficerMotion.AtCounter:
                if (restoring || state.Motion != OfficerMotion.Entering || revision - state.Revision != 1)
                    actor.SetImmediatlyArrive();
                break;
            case OfficerMotion.Leaving:
                if (!actor.gameObject.activeSelf) actor.SetImmediatlyArrive();
                actor.StartLeave();
                break;
            case OfficerMotion.Hidden:
                // Let the original rewind finish if the departure already started
                // here. Its OnLeft hides the actor, including on a slower peer.
                if (!restoring && state.Motion == OfficerMotion.Leaving && revision - state.Revision == 1) break;
                goto case OfficerMotion.Cleared;
            case OfficerMotion.Cleared:
                if (actor.GetComponent<DOTweenPath>() is DOTweenPath path) path.DOPause();
                actor.GetComponent<IdleAnimator>()?.Stop();
                actor.gameObject.SetActive(false);
                break;
        }
        if (motion is not (OfficerMotion.Hidden or OfficerMotion.Cleared) &&
            SpriteDict.Instance.spriteDictionary.TryGetValue((string)data[2], out var sprite))
            actor.image.sprite = sprite;
        state.Actor = actor;
        state.Motion = motion;
        state.Revision = revision;
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
[HarmonyPatch]
internal static class ReplicaSecurityMotionEvents
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.StartArrive));
        yield return AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.OnArrived));
        yield return AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.StartLeave));
        yield return AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.OnLeft));
    }

    private static void Postfix(GenericClientMono __instance, MethodBase __originalMethod)
    {
        if (NativeGuest.Active || NativeWorld.Host is not NativeWorld world) return;
        var motion = __originalMethod.Name switch
        {
            nameof(GenericClientMono.StartArrive) => NativeWorld.OfficerMotion.Entering,
            nameof(GenericClientMono.OnArrived) => NativeWorld.OfficerMotion.AtCounter,
            nameof(GenericClientMono.StartLeave) => NativeWorld.OfficerMotion.Leaving,
            _ => NativeWorld.OfficerMotion.Hidden,
        };
        world.RecordOfficer(__instance, motion);
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
    private static bool Prefix() => !NativeGuest.Active && !NativeInventoryPilot.CampaignReplica;
}
