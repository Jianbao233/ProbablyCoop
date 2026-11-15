using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProbablyCoop.Sync;
using UnityEngine.Localization;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    internal static bool CampaignReplica => Current?.campaign != null && !Current.localHost;
    internal static NativeEffectCampaign ActiveCampaign => Current?.campaign;

    internal static void RecordCustomerMotion(NativeWorld.CustomerMotion phase)
    {
        if (AuthoritativeCampaign) Current.campaign.CustomerMotion(phase);
    }

    internal static void RecordCampaignBell()
    {
        if (AuthoritativeCampaign) Current.campaign.BellRang();
    }

    [HarmonyPatch(typeof(StoreClientCounter), "Update")]
    private static class CampaignCustomerCounter
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(StoreClientCounter __instance)
        {
            if (!CampaignReplica || PlayerStore.Instance.storeState != PlayerStore.StoreState.OPEN) return true;
            var display = __instance.clientCounterTMP;
            if (display != null)
            {
                string remaining = Current.campaign.QueuedCustomers.ToString();
                if (display.text != remaining) display.text = remaining;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(StoreClientCounter), nameof(StoreClientCounter.GetTooltip))]
    private static class CampaignCustomerCounterTooltip
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(ref string __result)
        {
            if (!CampaignReplica) return true;
            __result = new LocalizedString("UI", "inte_client_counter").GetLocalizedString(
                new Dictionary<string, object> { ["count"] = Current.campaign.QueuedCustomers });
            return false;
        }
    }

    [HarmonyPatch]
    private static class CampaignBusinessControls
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(StoreBell), nameof(StoreBell.RingBell));
            yield return AccessTools.Method(typeof(StoreDismissButton), nameof(StoreDismissButton.OnClick));
            yield return AccessTools.Method(typeof(DialogUIManager), nameof(DialogUIManager.OnAdvancePressed));
            yield return AccessTools.Method(typeof(DialogUIManager), nameof(DialogUIManager.SelectChoice));
            yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.OnBuyAllClick));
            yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.OnSellAllClick));
            yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.OnOfferAcceptClick));
            yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.OnConfirmPanelYes));
            yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.OnWholesaleClick));
            yield return AccessTools.Method(typeof(NegociationUIManager), "CycleBarterOfferSet");
            yield return AccessTools.Method(typeof(BargainUIManager), nameof(BargainUIManager.OfferDiscount));
            yield return AccessTools.Method(typeof(BargainUIManager), nameof(BargainUIManager.OfferMarkup));
            yield return AccessTools.Method(typeof(BargainUIManager), nameof(BargainUIManager.Blackmail));
            yield return AccessTools.Method(typeof(BargainUIManager), nameof(BargainUIManager.OfferBuyingMarkup));
            yield return AccessTools.Method(typeof(BargainUIManager), nameof(BargainUIManager.DemandBuyingDiscount));
            yield return AccessTools.Method(typeof(BargainUIManager), nameof(BargainUIManager.ThreatenAug));
        }

        private static bool Prefix(MethodBase __originalMethod, object[] __args, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary) return true;
            if (!pilot.Ready) return false;
            if (pilot.localHost)
            {
                if (__originalMethod.Name == nameof(DialogUIManager.OnAdvancePressed) &&
                    !NativeAdvanceInput.SourceAllows(NativeAdvanceInput.Target.Customer)) return false;
                if (pilot.campaign.ShotActive && __originalMethod.DeclaringType is Type control &&
                    (control == typeof(StoreBell) || control == typeof(StoreDismissButton))) return false;
                if (__originalMethod.DeclaringType == typeof(NegociationUIManager) &&
                    !pilot.campaign.HostCanTrade(__originalMethod.Name)) return false;
                return BeginOriginalAuthorityAction(out __state);
            }
            var action = __originalMethod.Name switch
            {
                nameof(StoreBell.RingBell) => BusinessAction.Bell,
                nameof(StoreDismissButton.OnClick) => BusinessAction.Dismiss,
                nameof(DialogUIManager.OnAdvancePressed) => BusinessAction.Advance,
                nameof(DialogUIManager.SelectChoice) => BusinessAction.Choice,
                nameof(NegociationUIManager.OnBuyAllClick) => BusinessAction.Buy,
                nameof(NegociationUIManager.OnSellAllClick) => BusinessAction.Sell,
                nameof(NegociationUIManager.OnOfferAcceptClick) => BusinessAction.Accept,
                nameof(NegociationUIManager.OnConfirmPanelYes) => BusinessAction.ConfirmBudget,
                nameof(NegociationUIManager.OnWholesaleClick) => BusinessAction.Wholesale,
                "CycleBarterOfferSet" => BusinessAction.BarterCycle,
                nameof(BargainUIManager.OfferDiscount) => BusinessAction.Discount,
                nameof(BargainUIManager.OfferMarkup) => BusinessAction.Markup,
                nameof(BargainUIManager.Blackmail) => BusinessAction.Blackmail,
                nameof(BargainUIManager.OfferBuyingMarkup) => BusinessAction.BuyingMarkup,
                nameof(BargainUIManager.DemandBuyingDiscount) => BusinessAction.BuyingDiscount,
                nameof(BargainUIManager.ThreatenAug) => BusinessAction.Threaten,
                _ => throw new InvalidOperationException("Unregistered native business control."),
            };
            if (action == BusinessAction.Advance && pilot.campaign.ShouldSkipReplicaLine()) return true;
            pilot.campaign.RequestBusiness(action, __args.Length == 0 ? 0 : (int)__args[0]);
            return false;
        }

        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch]
    private static class CampaignCustomerCallbacks
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(StoreUIManager), nameof(StoreUIManager.OnNextClientArrived));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.DismissCurrentClient));
        }

        private static bool Prefix(out NativeInventoryPilot __state) => BeginOriginalAuthorityAction(out __state);

        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.IsTradeLocked))]
    private static class CampaignTradeLock
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static void Postfix(ref bool __result)
        {
            if (CampaignReplica) __result |= Current.campaign.ReplicaTradeLocked;
        }
    }

    [HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.EndConversation))]
    private static class CampaignDialogueDisplay
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(DialogUIManager __instance) =>
            !CampaignReplica || !Current.campaign.HoldsReplicaLine(__instance);
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.GetNegociatedValue))]
    private static class CampaignCounterPrice
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(GameItem __instance, ref long __result)
        {
            if (!CampaignReplica || __instance.parentInventory != EmporiumEntry.Instance.backInvinvElement ||
                !Current.campaign.TryCounterPrice(__instance, out int price)) return true;
            __result = price;
            return false;
        }
    }

    [HarmonyPatch]
    private static class CampaignCounterDisplay
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AddedItemToWeightedArea));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.RemovedItemToWeightedArea));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AddedItemToClientArea));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.RemovedItemToClientArea));
        }
        private static bool Prefix(GameItem gameItem, MethodBase __originalMethod)
        {
            if (!CampaignReplica) return true;
            if (__originalMethod.Name is nameof(PlayerStore.AddedItemToWeightedArea) or
                nameof(PlayerStore.RemovedItemToWeightedArea))
                Current.campaign.RefreshCounterForLocalGuest(gameItem,
                    __originalMethod.Name == nameof(PlayerStore.AddedItemToWeightedArea));
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.HandleAutoNegoUIDisplay))]
    private static class CampaignLocalTablet
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix() => Current?.campaign?.ExecutingGuestBusiness != true;
    }
}
