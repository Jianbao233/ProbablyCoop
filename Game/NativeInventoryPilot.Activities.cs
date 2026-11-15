using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    [HarmonyPatch]
    private static class CampaignPhoneActions
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.StartCalling));
            yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.AutoCall));
            yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.HandleCall));
            yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.StartPhoneDialog));
            yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.StopCall));
            yield return AccessTools.Method(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.OnAdvancePressed));
            yield return AccessTools.Method(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.SelectChoice));
            yield return AccessTools.Method(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.ContinueConversation));
            yield return AccessTools.Method(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.OnCurrentTextDisplayed));
        }

        private static bool Prefix(MethodBase __originalMethod, object[] __args, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            var campaign = pilot?.campaign;
            if (campaign == null) return true;
            string action = __originalMethod.Name;
            if (!pilot.localHost)
            {
                if (action == nameof(PhoneUIManager.StopCall))
                {
                    if (pilot.Ready) campaign.RequestHangup();
                    return true; // reset only the local keypad and bubbles
                }
                if (!pilot.Ready) return false;
                switch (action)
                {
                    case nameof(PhoneUIManager.StartCalling):
                        campaign.RequestDial(false, NativeEffectCampaign.DialNumber(PhoneUIManager.Instance));
                        break;
                    case nameof(PhoneUIManager.AutoCall):
                        campaign.RequestDial(true, (long)__args[0]);
                        break;
                    case nameof(DialoguePhoneUIManager.OnAdvancePressed):
                        campaign.AdvanceReplicaPhone();
                        break;
                    case nameof(DialoguePhoneUIManager.SelectChoice):
                        if (campaign.HasPhoneView)
                            campaign.RequestActivity(ActivityAction.PhoneChoice, value: (int)__args[0]);
                        break;
                }
                return false;
            }
            if (campaign.CancellingPhone) return true;
            if (action == nameof(DialoguePhoneUIManager.OnCurrentTextDisplayed))
                campaign.TrackPhoneEnd(DialoguePhoneUIManager.Instance.currentDialog);
            if (pilot.RunningNativeBoundary) return true;
            bool input = action is nameof(PhoneUIManager.StartCalling) or nameof(PhoneUIManager.AutoCall) or
                nameof(PhoneUIManager.StopCall) or nameof(DialoguePhoneUIManager.OnAdvancePressed) or
                nameof(DialoguePhoneUIManager.SelectChoice);
            // A remote quick dial that did not connect can still go through the
            // original keypad delay and StartCalling from PhoneUIManager.Update.
            if (campaign.RemotePhoneExecution && input && action != nameof(PhoneUIManager.StartCalling)) return false;
            if (action == nameof(DialoguePhoneUIManager.OnAdvancePressed) &&
                (!campaign.HasPhoneView || !NativeAdvanceInput.SourceAllows(NativeAdvanceInput.Target.Phone))) return false;
            if (!BeginOriginalAuthorityAction(out __state)) return false;
            if (action is nameof(PhoneUIManager.StartCalling) or nameof(PhoneUIManager.AutoCall)) campaign.ObserveLocalDial();
            else campaign.MarkPhoneDirty();
            return true;
        }

        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch]
    private static class CampaignPhoneLine
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.DisplayClientText));
            yield return AccessTools.Method(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.DisplayerPlayerText));
        }
        private static void Postfix(Dialogue dialogue)
        {
            if (AuthoritativeCampaign) Current.campaign.ObservePhoneLine(dialogue);
        }
    }

    [HarmonyPatch(typeof(PhoneUIManager), nameof(PhoneUIManager.OpenUI))]
    private static class CampaignPhoneOpen
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix() => ActiveCampaign?.RemotePhoneExecution != true;
        private static void Postfix()
        {
            if (CampaignReplica) Current.campaign.ReopenReplicaPhone();
        }
    }

    [HarmonyPatch(typeof(PhoneUIManager), nameof(PhoneUIManager.CloseUI))]
    private static class CampaignPhoneClose
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix() => ActiveCampaign?.RemotePhoneExecution != true || Current.RunningNativeBoundary;
    }

    [HarmonyPatch(typeof(PhoneUIManager), nameof(PhoneUIManager.PressNumber))]
    private static class CampaignPhoneKeypad
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(int number) => ActiveCampaign == null ||
            (CampaignReplica ? number == -1 || !ActiveCampaign.ReplicaPhoneBusy : !ActiveCampaign.RemotePhoneExecution);
    }

    [HarmonyPatch(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.InterruptDialog))]
    private static class CampaignPhoneInterrupt
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static void Prefix()
        {
            if (AuthoritativeCampaign) ActiveCampaign.InterruptPhoneEnds();
        }
    }

    [HarmonyPatch(typeof(WildNetworkUI), nameof(WildNetworkUI.Open))]
    private static class CampaignWildOpen
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(out NativeInventoryPilot __state)
        {
            __state = null;
            if (ActiveCampaign == null) return true;
            if (CampaignReplica)
            {
                if (ActiveCampaign.PresentingActivity) return true;
                ActiveCampaign.RequestActivity(ActivityAction.BrowseUpgrades);
                return true;
            }
            if (ActiveCampaign.HandOffWild()) return false;
            return BeginOriginalAuthorityAction(out __state);
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch]
    private static class CampaignFacilityActions
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CommissaryElement), nameof(CommissaryElement.OnBuyClick));
            yield return AccessTools.Method(typeof(CommissaryUIManager), nameof(CommissaryUIManager.OnRedeemAllButtonClick));
            yield return AccessTools.Method(typeof(CommissaryUIManager), nameof(CommissaryUIManager.OnRedeemAllLottery));
            yield return AccessTools.Method(typeof(StoreRequest), nameof(StoreRequest.FillRequest));
            yield return AccessTools.Method(typeof(EndOfDayUIManager), "OnToggled");
        }

        private static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary) return true;
            string name = __originalMethod.Name;
            if (pilot.localHost)
                return (__instance is not StoreRequest order || pilot.campaign.CanDeliver(order)) &&
                    BeginOriginalAuthorityAction(out __state);
            if (!pilot.Ready) return false;
            switch (name)
            {
                case nameof(CommissaryElement.OnBuyClick):
                    pilot.campaign.RequestActivity(ActivityAction.CommissaryBuy, ((CommissaryElement)__instance).id);
                    break;
                case nameof(CommissaryUIManager.OnRedeemAllButtonClick):
                    pilot.campaign.RequestActivity(ActivityAction.RedeemStamps);
                    break;
                case nameof(CommissaryUIManager.OnRedeemAllLottery):
                    pilot.campaign.RequestActivity(ActivityAction.RedeemLottery);
                    break;
                case nameof(StoreRequest.FillRequest):
                    var request = (StoreRequest)__instance;
                    if (request?.CanFillRequest() == true)
                        pilot.campaign.RequestActivity(ActivityAction.RequestDelivery, request.id);
                    break;
                case "OnToggled":
                    pilot.campaign.RequestActivity(ActivityAction.ToggleService, ((StoreService)__args[0]).id,
                        value: (bool)__args[1] ? 1 : 0);
                    break;
            }
            return false;
        }

        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch]
    private static class CampaignFacilityStaticActions
    {
        private static readonly FieldInfo Selected = AccessTools.Field(typeof(WildNetworkUI), "selectedId");
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(StoreRequest), nameof(StoreRequest.ActivateRequest));
            yield return AccessTools.Method(typeof(WildNetworkUI), "OnUseClicked");
        }
        private static bool Prefix(MethodBase __originalMethod, out NativeInventoryPilot __state)
        {
            __state = null;
            if (Current?.world.Ready != true) return !CampaignReplica;
            if (!CampaignReplica) return BeginOriginalAuthorityAction(out __state);
            if (Current.Ready && __originalMethod.DeclaringType == typeof(WildNetworkUI))
            {
                string id = (string)Selected.GetValue(null);
                if (id != null && PlayerStore.Instance.networkUpgrade.TryGetValue(id, out var upgrade))
                    ActiveCampaign.RequestActivity(ActivityAction.BuyUpgrade, id, value: upgrade.GetCost());
            }
            return false;
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch(typeof(WildNetworkUI), "OnWindowClosed")]
    private static class CampaignWildClose
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(out NativeInventoryPilot __state)
        {
            __state = null;
            if (!CampaignReplica) return BeginOriginalAuthorityAction(out __state);
            if (ActiveCampaign.RemoteWild && !ActiveCampaign.PresentingActivity)
                ActiveCampaign.RequestActivity(ActivityAction.CloseUpgrades);
            return true;
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch(typeof(CommissaryUIManager), nameof(CommissaryUIManager.OpenUI))]
    private static class CampaignCommissaryOpen
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix() => !AuthoritativeCampaign || !Current.campaign.HandOffCommissary();
    }

    [HarmonyPatch]
    private static class CampaignSceneEvents
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AfterhourEvents), nameof(AfterhourEvents.Start));
            yield return AccessTools.Method(typeof(AfterhourEvents), nameof(AfterhourEvents.End));
            yield return AccessTools.Method(typeof(StoreEventManager), nameof(StoreEventManager.ActivateEvent));
            yield return AccessTools.Method(typeof(StoreEventManager), nameof(StoreEventManager.DeactivateEvent));
        }
        private static bool Prefix(out NativeInventoryPilot __state)
        {
            __state = null;
            if (CampaignReplica) return false;
            return Current?.world.Ready != true || BeginOriginalAuthorityAction(out __state);
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch(typeof(NewsUIManager), nameof(NewsUIManager.PopulateUI))]
    private static class CampaignNewspaper
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(NewsUIManager __instance)
        {
            if (!CampaignReplica || !ActiveCampaign.NewspaperIsEmpty()) return true;
            __instance.SelectLayout(new List<StoreEvent>());
            if (Current.Ready) ActiveCampaign.RequestActivity(ActivityAction.ReadNewspaper);
            return false;
        }
    }
}
