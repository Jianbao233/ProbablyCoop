#if PROBABLY_COOP_GRID_QA
using System;
using DG.Tweening;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private static readonly bool CustomerArrivalQa =
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-customer-arrival") >= 0;
    private static readonly bool MissCustomerApproachCue =
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-miss-customer-approach") >= 0;
    private const string ArrivalLine = "QA customer speaks after reaching the counter.";
    private static int arrivalFootsteps;
    private static bool arrivalCueSuppressed, arrivedAfterSuppression;
    private bool arrivalCustomerQueued, arrivalSawEarlyDialogue, arrivalCapturedWalking;
    private bool arrivalHostConfirmed;

    private void TickCustomerArrivalHost(RoomSelector room, double now)
    {
        if (room.Session?.State != LinkState.Connected || room.Session.Native.FullRevision == 0 ||
            NativeWorld.Host?.PartnerSetupRequiredForQa != false) return;
        var store = PlayerStore.Instance;
        if (stage == 1)
        {
            // Hold the source line open so the result is independent of the
            // QA profile's original auto-advance setting.
            EmporiumOption.Instance.autoNextDialog = false;
            AdvCalendarUIManager.Instance?.CloseUI();
            Require(store.storeState == PlayerStore.StoreState.MORNING && store.CanStartDay(),
                "customer arrival QA expected the saved day to be ready to open");
            StoreShutterButton.Instance.OnClick();
            stage = 2;
            next = now + 4;
        }
        if (stage == 2 && now >= next && !arrivalCustomerQueued && store.lockNextBellFlag == 0)
        {
            var client = StoreClientListWanted.CreateWanted2Return();
            client.mainDialogue = new Dialogue { isMainDialog = true };
            client.mainDialogue.SetText("QA customer", ArrivalLine);
            client.mainDialogue.nextDialogue = new Dialogue().SetText("QA customer", "QA follow-up.");
            store.storeClientManager.clientStack.Clear();
            store.storeClientManager.AddNextClient(client);
            store.TryCallNextClient();
            arrivalCustomerQueued = true;
        }
        if (!arrivalHostConfirmed && arrivalCustomerQueued && store.isClientArrived &&
            DialogUIManager.Instance.clientBubble.gameObject.activeSelf)
        {
            arrivalHostConfirmed = true;
            Record("PASS host customer arrival", "ordinary customer arrived and opened its original dialogue");
        }
    }

    private void TickCustomerArrivalGuest(double now)
    {
        if (NativeWorld.Guest?.Ready != true || StoreClientMono.Instance is not { } client ||
            client == null || DialogUIManager.Instance is not { } ui || ui == null) return;
        if (MissCustomerApproachCue && arrivedAfterSuppression && arrivalFootsteps == 0)
        {
            NativeOffscreenCapture.Capture("qa-customer-arrival-skipped");
            Require(false, "guest skipped native approach when only the at-counter cue survived");
        }
        var path = client.GetComponent<DOTweenPath>()?.tween;
        bool approaching = arrivalFootsteps > 0 && path != null && path.IsActive() &&
            !path.IsBackwards() && !path.IsComplete();
        if (MissCustomerApproachCue && approaching && !arrivalCapturedWalking &&
            path.ElapsedPercentage() >= 0.25f)
        {
            arrivalCapturedWalking = true;
            NativeOffscreenCapture.Capture("qa-ordinary-customer-recovered-walk");
        }
        if (approaching && PlayerStore.Instance.isClientArrived && ui.clientBubble.gameObject.activeInHierarchy)
        {
            if (!arrivalSawEarlyDialogue)
                NativeOffscreenCapture.Capture("qa-ordinary-customer-dialogue-early");
            arrivalSawEarlyDialogue = true;
        }
        if (arrivalFootsteps == 0 || approaching || !ui.clientBubble.gameObject.activeInHierarchy ||
            !PlayerStore.Instance.isClientArrived) return;
        NativeOffscreenCapture.Capture("qa-ordinary-customer-at-counter");
        Require(!arrivalSawEarlyDialogue, "customer bubble was displayed during the guest's original approach");
        Require(arrivalFootsteps == 1, "guest did not start exactly one original walking audio sequence");
        Require(ui.clientNameBubble.activeInHierarchy, "customer name never displayed at the counter");
        if (MissCustomerApproachCue)
        {
            Require(arrivedAfterSuppression && arrivalCapturedWalking, "QA did not observe a walk with the first cue suppressed");
            Record("PASS guest coalesced customer cues", "only the at-counter cue was applied; guest ran the original walk before dialogue");
        }
        Finish("PASS guest customer arrival");
    }

    [HarmonyPatch(typeof(NativeWorld), "ApplyCustomerMotion")]
    private static class QaSuppressCustomerApproachCue
    {
        private static bool Prepare() => CustomerArrivalQa && MissCustomerApproachCue;
        private static bool Prefix(JObject data)
        {
            if (!NativeGuest.Active || data == null) return true;
            var phase = (NativeWorld.CustomerMotion)(int)data["phase"];
            if (phase == NativeWorld.CustomerMotion.AtCounter && arrivalCueSuppressed)
                arrivedAfterSuppression = true;
            if (phase != NativeWorld.CustomerMotion.Approaching) return true;
            if (!arrivalCueSuppressed)
                Debug.Log("[probably_coop QA] withheld the approaching cue from the guest presenter");
            arrivalCueSuppressed = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(StoreClientMono), nameof(StoreClientMono.StartApprochingStore))]
    private static class QaSlowGuestCustomer
    {
        private static bool Prepare() => CustomerArrivalQa;
        private static void Postfix(StoreClientMono __instance)
        {
            if (!NativeGuest.Active) return;
            var tween = __instance.GetComponent<DOTweenPath>()?.tween;
            if (tween != null) tween.timeScale = 0.42f;
        }
    }

    [HarmonyPatch(typeof(EmporiumAudioPlayer), nameof(EmporiumAudioPlayer.PlayWalking))]
    private static class QaCustomerFootsteps
    {
        private static bool Prepare() => CustomerArrivalQa;
        private static void Postfix()
        {
            if (NativeGuest.Active) arrivalFootsteps++;
        }
    }
}
#endif
