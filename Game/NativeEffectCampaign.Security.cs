using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Sync;
using Motion = ProbablyCoop.NativeWorld.CustomerMotion;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    private readonly Motion[] officerMotion = new Motion[2];
    private readonly ulong[] officerRevision = new ulong[2];
    private readonly string[] officerSprite = { "", "" };
    private bool securityGlass, arresting, shootout;
    private NativeActorRun.Slot? reportingActor;

    internal void RecordReportingActor() => reportingActor = NativeActorSession.Current?.Run.CurrentActor;
    internal IDisposable EnterReportingActor() => host && reportingActor.HasValue ?
        NativeActorSession.Current?.Enter(reportingActor.Value) : null;
    internal void FinishReport() => reportingActor = null;

    internal bool CanReport()
    {
        var store = PlayerStore.Instance;
        return !ShotActive && !MapUIManager.Instance.IsOutside && !store.isClientBeingArrested &&
            !StoreReputation.IsPerkUnlocked("SEC_ALARM_IGNORED") && store.IsClientPresent() &&
            store.currentClientInstance?.GetClientBlueprint().CanReport() == true;
    }

    private void ObserveSecurity()
    {
        var store = PlayerStore.Instance;
        var ui = StoreUIManager.Instance;
        bool glass = ui.StoreGlass.IsClosed;
        if (glass != securityGlass || store.isClientBeingArrested != arresting || store.isHandlingShootout != shootout)
            CommerceChanged();
        securityGlass = glass;
        arresting = store.isClientBeingArrested;
        shootout = store.isHandlingShootout;
        if (!glass && !arresting && store.onSecAlarmLeftAction == null) FinishReport();
        if (officerMotion[0] != Motion.Hidden && !ui.secGameObject.gameObject.activeSelf)
            RecordOfficer(ui.secGameObject, Motion.Hidden);
        if (officerMotion[1] != Motion.Hidden && !ui.secGameObject2.gameObject.activeSelf)
            RecordOfficer(ui.secGameObject2, Motion.Hidden);
    }

    internal void RecordOfficer(GenericClientMono actor, Motion phase)
    {
        var ui = StoreUIManager.Instance;
        int index = actor == ui.secGameObject ? 0 : actor == ui.secGameObject2 ? 1 : -1;
        if (index < 0 || phase == Motion.Hidden && actor.gameObject.activeSelf ||
            phase == Motion.AtCounter && officerMotion[index] != Motion.Approaching) return;
        officerMotion[index] = phase;
        officerRevision[index]++;
        if (phase == Motion.Approaching)
            officerSprite[index] = SpriteDict.Instance.spriteDictionary.FirstOrDefault(pair => pair.Value == actor.image.sprite).Key ?? "";
        CommerceChanged();
    }

    private JObject CaptureSecurity() => new JObject
    {
        ["glass"] = securityGlass, ["arrest"] = arresting, ["shootout"] = shootout,
        ["officers"] = new JArray(Enumerable.Range(0, 2).Select(index =>
            new JArray(officerRevision[index], (int)officerMotion[index], officerSprite[index]))),
    };

    private static bool ValidSecurity(JObject facts) => facts != null &&
        facts["glass"]?.Type == JTokenType.Boolean && facts["arrest"]?.Type == JTokenType.Boolean &&
        facts["shootout"]?.Type == JTokenType.Boolean && facts["officers"] is JArray actors && actors.Count == 2 &&
        actors.All(node => node is JArray row && row.Count == 3 && row[0].Type == JTokenType.Integer &&
            ulong.TryParse(row[0].ToString(), out _) && Integer(row[1], 0, (int)Motion.LeavingRight) &&
            row[2].Type == JTokenType.String && ((string)row[2]).Length <= 160);

    private void ApplySecurity(JObject facts, bool restoring)
    {
        var ui = StoreUIManager.Instance;
        var store = PlayerStore.Instance;
        store.isClientBeingArrested = (bool)facts["arrest"];
        store.isHandlingShootout = (bool)facts["shootout"];
        if (ui.StoreGlass.IsClosed != (bool)facts["glass"]) ui.StoreGlass.ToggleShutterDoor();
        var rows = (JArray)facts["officers"];
        for (int i = 0; i < rows.Count; i++)
        {
            var row = (JArray)rows[i];
            ulong revision = (ulong)row[0];
            if (!restoring && revision == officerRevision[i]) continue;
            var actor = i == 0 ? ui.secGameObject : ui.secGameObject2;
            var phase = (Motion)(int)row[1];
            switch (phase)
            {
                case Motion.Approaching: actor.StartArrive(); break;
                case Motion.AtCounter:
                    if (restoring || officerMotion[i] != Motion.Approaching) actor.SetImmediatlyArrive();
                    break;
                case Motion.Leaving:
                    if (actor.gameObject.activeSelf) actor.StartLeave();
                    break;
                case Motion.Hidden:
                    if (restoring || officerMotion[i] != Motion.Leaving) actor.gameObject.SetActive(false);
                    break;
            }
            if (SpriteDict.Instance.spriteDictionary.TryGetValue((string)row[2], out var sprite))
                actor.image.sprite = sprite;
            officerMotion[i] = phase;
            officerRevision[i] = revision;
        }
    }
}

internal sealed partial class NativeInventoryPilot
{
    [HarmonyPatch(typeof(StoreSecButton), nameof(StoreSecButton.OnClick))]
    private static class CampaignReport
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(StoreSecButton __instance, out NativeInventoryPilot __state)
        {
            __state = null;
            if (ActiveCampaign == null || Current.RunningNativeBoundary) return true;
            if (CampaignReplica)
            {
                if (Current.Ready)
                {
                    __instance.audioSource.Play();
                    ActiveCampaign.RequestBusiness(BusinessAction.Report);
                }
                return false;
            }
            if (ActiveCampaign.CanReport()) ActiveCampaign.RecordReportingActor();
            return BeginOriginalAuthorityAction(out __state);
        }
        private static System.Exception Finalizer(System.Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch]
    private static class CampaignOfficer
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.StartArrive));
            yield return AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.StartLeave));
            yield return AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.OnArrived));
            yield return AccessTools.Method(typeof(GenericClientMono), nameof(GenericClientMono.OnLeft));
        }
        private static void Prefix(GenericClientMono __instance, out NativeInventoryPilot __state)
        {
            __state = null;
            if (AuthoritativeCampaign && __instance.id == "SEC") BeginOriginalAuthorityAction(out __state);
        }
        private static void Postfix(GenericClientMono __instance, MethodBase __originalMethod)
        {
            if (!AuthoritativeCampaign) return;
            var phase = __originalMethod.Name switch
            {
                nameof(GenericClientMono.StartArrive) => Motion.Approaching,
                nameof(GenericClientMono.StartLeave) => Motion.Leaving,
                nameof(GenericClientMono.OnArrived) => Motion.AtCounter,
                _ => Motion.Hidden,
            };
            ActiveCampaign.RecordOfficer(__instance, phase);
        }
        private static System.Exception Finalizer(System.Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }
}
