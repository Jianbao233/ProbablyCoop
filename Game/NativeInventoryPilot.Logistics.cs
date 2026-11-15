using System;
using HarmonyLib;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.ModifyTag), new[] { typeof(string), typeof(Action<TagState>), typeof(bool) })]
    private static class CampaignLogisticsSetting
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(GameItem __instance, string tagName, Action<TagState> action, ref GameItem __result,
            out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary || pilot.world.Applying ||
                !pilot.world.Contains(__instance) || !NativeEffectCampaign.IsLogisticsSetting(__instance, tagName))
                return true;
            if (pilot.capture != null)
            {
                pilot.Touch(__instance);
                return true;
            }
            if (pilot.localHost) return pilot.CanMove(__instance) && BeginOriginalAuthorityAction(out __state);
            if (pilot.Ready) pilot.campaign.EditLogistics(__instance, tagName, action);
            __result = __instance;
            return false;
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.GetTagReadonly), new[] { typeof(string) })]
    private static class CampaignLogisticsDraft
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(GameItem __instance, string tagName, ref TagState __result)
        {
            if (!CampaignReplica || !Current.campaign.ReadLogisticsDraft(__instance, tagName, out var draft)) return true;
            __result = draft;
            return false;
        }
    }

    [HarmonyPatch(typeof(HopperHelper), "Watch")]
    private static class CampaignHopperWatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix() => !CampaignReplica;
    }

    [HarmonyPatch(typeof(HopperHelper), nameof(HopperHelper.RunAndRecord))]
    private static class CampaignHopperTransfer
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(GameItem intake, ref int __result, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary || !pilot.world.Ready) return true;
            if (pilot.localHost)
            {
                var actors = NativeActorSession.Current;
                byte actor = actors?.Run.CurrentActor == NativeActorRun.Slot.Partner ? NetSession.GuestPeer : SyncHost.HostPeer;
                return pilot.campaign.MayRunHopper(intake, actor) && BeginOriginalAuthorityAction(out __state);
            }
            if (pilot.CanMove(intake)) pilot.campaign.RequestActivity(ActivityAction.HopperRun, owner: intake);
            __result = 0;
            return false;
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }
}
