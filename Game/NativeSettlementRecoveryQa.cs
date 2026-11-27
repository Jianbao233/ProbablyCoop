#if PROBABLY_COOP_GRID_QA
using System;
using System.IO;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int settlementRecoverySlot = -1;
    private readonly bool settlementAfterRotation = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-settlement-after-rotation") >= 0;
    private readonly bool travelRecovery = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-travel-recovery") >= 0;
    private bool travelRecoveryConsent;
    private int travelRecoveryUnexpectedSaves;
    private const string TravelRecoverySite = AfterhourLocation.DUMPING_GROUND;
    private const string InjectedSaveError = "Isolated QA stopped the first native next-day write.";
    private static NativeBackgroundQa settlementSaveProbe;
    private NativeActorSession settlementOriginal;
    private NativeActorCheckpoint.Envelope settlementSaved;
    private NativeWorld settlementGuestWorld;
    private byte[] settlementImage;
    private int settlementStage, settlementCash, settlementGuestDay, settlementWrites, settlementFaults;
    private string settlementOldCode;
    private double settlementUntil;

    private bool TickSettlementRecovery(RoomSelector room, CoopPanel panel, NativeShop shop, bool inMenu, double now)
    {
        if (settlementRecoverySlot < 0) return false;
        Require(Enabled && saveRoundTrip && (host ? IsHostProduct : IsGuestProduct), "settlement recovery requires disposable native QA profiles");
        if (settlementUntil == 0) settlementUntil = now + 210;
        Require(now < settlementUntil, "settlement recovery timed out at step=" + settlementStage);
        if (!host) { TickSettlementRecoveryGuest(room, inMenu); return true; }
        if (!loaded) return false; // Reuse the ordinary exact single-checkpoint native loader.

        if (settlementStage == 0 && shop != null && NativeActorSession.Current is { VerifiedCheckpoint: { } checkpoint } actors)
        {
            Require(checkpoint.Slot == settlementRecoverySlot, "native QA loaded the wrong source slot");
            settlementOriginal = actors;
            settlementSaved = checkpoint;
            settlementCash = actors.Store.GetCash();
            settlementImage = ReadSettlementImage();
            settlementOldCode = room.Code;
            ModHelper.WriteModDataFileText("probably_coop", "qa-room.txt", settlementOldCode);
            settlementStage = 1;
        }
        if (settlementStage == 1 && SettlementPartnerReady(room, "initial"))
        {
            settlementSaveProbe = this;
            if (settlementAfterRotation) EmporiumOption.Instance.saveHistoryDays = 1;
            PrepareSettlementEvening();
            settlementStage = 2;
            if (travelRecovery) StartTravelRecoveryOuting();
            else ClickSettlementBedroll();
        }
        if (settlementStage == 2 && inMenu && MainMenuUIController.Instance != null)
        {
            Require(settlementFaults == 1 && settlementWrites == 0 && travelRecoveryUnexpectedSaves == 0 &&
                ReadSettlementImage().AsSpan().SequenceEqual(settlementImage),
                "the injected fault retired or changed the previous confirmed save");
            string code = travelRecovery ? NativeActorSession.TravelFaultCode : "DAY-206";
            Require(panel.QaRecoverVerifiedDay(settlementSaved, code),
                "main-menu Connection Details did not expose the exact verified checkpoint and " + code);
            settlementStage = 3;
            string boundary = travelRecovery ? "inside native travel while still in transit" :
                settlementAfterRotation ? "after native rotation" : "before native rotation";
            Record("PASS settlement-recovery-menu", $"fault injected {boundary}; original slot image and {code} recovery button verified");
            return true;
        }
        if (settlementStage == 3 && shop != null && NativeActorSession.Current is { Hydrating: false, Faulted: false } restored)
        {
            Require(!ReferenceEquals(settlementOriginal, restored) && restored.VerifiedCheckpoint?.Checkpoint == settlementSaved.Checkpoint &&
                restored.Run.Run == settlementSaved.Run && restored.Store.saveSlotId == settlementSaved.Slot &&
                StoreStation.GetDayCounter() == settlementSaved.Day && restored.Store.GetCash() == settlementCash &&
                ReadSettlementImage().AsSpan().SequenceEqual(settlementImage),
                "recovery failed to reload the verified day, cash and exact slot image");
            Require(room.IsHost && room.Code != settlementOldCode, "restore did not retire the original room");
            ModHelper.WriteModDataFileText("probably_coop", "qa-room.txt", room.Code);
            Record("PASS settlement-recovery-load", "fresh native actor session restored the same slot, nonce, day and cash; new room opened");
            settlementStage = 4;
        }
        if (settlementStage == 4 && SettlementPartnerReady(room, "rejoined"))
        {
            PrepareSettlementEvening();
            settlementStage = 5;
            ClickSettlementBedroll();
        }
        if (settlementStage == 5 && shop != null && room.Session?.Shop.View.Phase == ShopDay.Saved)
        {
            Require(settlementWrites == 1 && settlementFaults == 1 && StoreStation.GetDayCounter() == settlementSaved.Day + 1 &&
                CoopRun.VerifyCheckpoint(PlayerStore.Instance, settlementSaved.Day + 1) &&
                !ReadSettlementImage().AsSpan().SequenceEqual(settlementImage),
                "retry did not produce exactly one verified native next-day write");
            settlementStage = 6;
            Record("PASS settlement-recovery-once", $"retried through the native bedroll; day={settlementSaved.Day + 1} cash={PlayerStore.Instance.GetCash()} writes={settlementWrites}");
        }
        if (settlementStage == 6 && room.Session?.State == LinkState.Waiting)
            Finish("PASS host runtime");
        return true;
    }

    private static bool SettlementPartnerReady(RoomSelector room, string expected) => room.Session?.State == LinkState.Connected &&
        room.Session.Native.FullRevision != 0 && NativeWorld.Host?.PartnerSetupRequiredForQa == false &&
        ModHelper.ReadModDataFileText("probably_coop", $"qa-settlement-{expected}.txt") == expected;

    private void TickSettlementRecoveryGuest(RoomSelector room, bool inMenu)
    {
        var world = NativeWorld.Guest;
        if (settlementStage == 0 && world?.Ready == true)
        {
            settlementGuestDay = StoreStation.GetDayCounter();
            settlementGuestWorld = world;
            settlementOldCode = room.Code;
            Require(PlayerStore.Instance.saveSlotId == -1, "original QA guest has a native slot");
            settlementStage = 1;
            Record("PASS settlement-recovery-first-join", $"original guest projected day={settlementGuestDay} with no save slot");
        }
        if (travelRecovery && !travelRecoveryConsent && settlementStage == 1 && world?.Ready == true &&
            !world.HasPendingWork && world.TripReady == 1 && world.ProposedTrip == TravelRecoverySite)
        {
            Require(!world.IndependentTravel, "native travel recovery requires both players on the shared outing");
            travelRecoveryConsent = true;
            MapUIManager.Instance.Travel(AfterhourLocation.Get(TravelRecoverySite));
            Record("PASS travel-recovery-guest-consent", "guest accepted the original joint-travel request");
        }
        if (settlementStage == 1 && inMenu && !room.HasLobby && !room.Busy)
        {
            string code = ModHelper.ReadModDataFileText("probably_coop", "qa-settlement-rejoin.txt")?.Trim();
            if (string.IsNullOrEmpty(code) || code == settlementOldCode) return;
            room.Join(code);
            settlementStage = 2;
        }
        if (settlementStage == 2 && world?.Ready == true && !world.HasPendingWork)
        {
            Require(!ReferenceEquals(settlementGuestWorld, world) && StoreStation.GetDayCounter() == settlementGuestDay &&
                PlayerStore.Instance.saveSlotId == -1, "guest rejoined the wrong day or still had its old projection");
            settlementStage = 3;
            Record("PASS settlement-recovery-rejoin", "original guest returned to menu and rejoined the recovered checkpoint with no save slot");
        }
        if (settlementStage == 3 && world?.Ready == true && !world.HasPendingWork &&
            room.Session?.Shop.View.Phase == ShopDay.Saved && world.MorningReportDay == settlementGuestDay + 1)
        {
            Require(StoreStation.GetDayCounter() == settlementGuestDay + 1 && PlayerStore.Instance.saveSlotId == -1,
                "guest did not receive the single resumed next-day settlement");
            Record("PASS settlement-recovery-guest-day", $"day={StoreStation.GetDayCounter()} cash={PlayerStore.Instance.GetCash()}");
            Finish("PASS guest runtime");
        }
    }

    private byte[] ReadSettlementImage() => NativeActorPersistence.ReadFileImage(
        new ES3Settings(SaveFiles.Slot(settlementSaved.Slot)).FullPath);

    private static void PrepareSettlementEvening()
    {
        var store = PlayerStore.Instance;
        StoreUIManager.Instance.CloseAllUI();
        Require(!MapUIManager.Instance.IsOutside && store.CanEndDay(), "isolated source checkpoint has mandatory business or is away from the shop");
        // Establish the same closed-shop precondition on both attempts; neither
        // the native bedroll, night effects nor the wake sequence are stubbed.
        store.storeState = PlayerStore.StoreState.CLOSED;
    }

    private static void StartTravelRecoveryOuting()
    {
        var store = PlayerStore.Instance;
        var map = MapUIManager.Instance;
        var destination = AfterhourLocation.Get(TravelRecoverySite);
        var world = NativeWorld.Host;
        store.isAfterhoursIntroduced = true;
        store.visitLeftTonight = 1;
        Require(world?.IndependentTravel == false && map.IsInTransit == false && destination?.IsDiscovered() == true &&
            destination.IsAvailable() && !HealthData.IsSeriouslyWounded() && !StartingPerkContent.IsAgoraphobiaResting() &&
            !StoreClientData.IsButcherHunting(), "isolated checkpoint cannot start a shared outing");
        map.Travel(destination);
        Require(world.TripReady == 1 && world.ProposedTrip == TravelRecoverySite,
            "original map action did not register the host's joint-outing consent");
    }

    [HarmonyPatch(typeof(MapUIManager), "OnGoingOutside")]
    private static class TravelRecoveryOracle
    {
        private static void Prefix(MapUIManager __instance)
        {
            var probe = settlementSaveProbe;
            if (!Enabled || !IsHostProduct || probe is not { travelRecovery: true, settlementStage: 2 } ||
                !ReferenceEquals(NativeActorSession.Current, probe.settlementOriginal)) return;
            Require(probe.settlementFaults == 0 && __instance.IsInTransit && !__instance.IsOutside,
                "fault did not reach the deferred original map call while the transit lock was still held");
            probe.settlementFaults++;
            probe.Record("PASS travel-recovery-original-callback", "original map callback reached while in transit");
            throw new InvalidOperationException("Isolated QA stopped the original map transition.");
        }
    }

    private static void ClickSettlementBedroll()
    {
        UnityEngine.Object.FindObjectOfType<StoreBedroll>().GetComponent<Interactable>().OnClick();
        var button = (TabletButton)AccessTools.Field(typeof(EndOfDayUIManager), "endDayButton").GetValue(EndOfDayUIManager.Instance);
        Require(EndOfDayUIManager.Instance.IsOpen && button?.Interactable == true, "native day confirmation unavailable");
        button.button.onClick.Invoke();
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SaveGame))]
    private static class SettlementWriteOracle
    {
        internal static bool IsProbeWrite(PlayerStore store, NativeBackgroundQa probe) => Enabled && IsHostProduct &&
            probe?.settlementSaved is { } saved && store.saveSlotId == saved.Slot &&
            Guid.TryParse(store.runID, out var run) && run == saved.Run &&
            StoreStation.GetDayCounter() == saved.Day + 1;

        [HarmonyPriority(Priority.Last)]
        private static void Prefix(PlayerStore __instance)
        {
            var probe = settlementSaveProbe;
            if (probe is { travelRecovery: true })
            {
                if (probe.settlementStage == 2 && ReferenceEquals(probe.settlementOriginal?.Store, __instance))
                    probe.travelRecoveryUnexpectedSaves++;
                return;
            }
            if (!IsProbeWrite(__instance, probe) || probe.settlementAfterRotation ||
                probe.settlementStage != 2 || probe.settlementFaults != 0) return;
            probe.settlementFaults++;
            throw new IOException(InjectedSaveError);
        }

        private static void Postfix(PlayerStore __instance, bool __runOriginal)
        {
            var probe = settlementSaveProbe;
            if (__runOriginal && IsProbeWrite(__instance, probe) && !probe.settlementAfterRotation) probe.settlementWrites++;
        }

        [HarmonyPriority(Priority.Last)]
        private static Exception Finalizer(Exception __exception)
        {
            // The original recovery finalizers handle the fault first. Keep
            // unrelated exceptions visible to the isolated launcher.
            return settlementSaveProbe != null && __exception is IOException && __exception.Message == InjectedSaveError ? null : __exception;
        }
    }

    [HarmonyPatch(typeof(PlayerStore), "WriteSlotFile")]
    private static class SettlementAfterRotationOracle
    {
        private static void Prefix(PlayerStore __instance)
        {
            var probe = settlementSaveProbe;
            if (!SettlementWriteOracle.IsProbeWrite(__instance, probe) || !probe.settlementAfterRotation || probe.settlementStage != 2) return;
            if (probe.settlementFaults == 0)
            {
                var saved = probe.settlementSaved;
                var retained = CoopRun.FindVerifiedCheckpoint(saved);
                Require(EmporiumOption.SaveHistoryDays == 1 && !File.Exists(SaveFiles.AbsolutePath(SaveFiles.Slot(saved.Slot))) &&
                    retained is { Historical: true } && retained.MatchesVerified(saved) &&
                    NativeActorPersistence.ReadFileImage(SaveFiles.AbsolutePath(retained.File)).AsSpan().SequenceEqual(probe.settlementImage),
                    "native rotation discarded the verified predecessor before trying to write the new day");
                probe.settlementFaults++;
                probe.Record("PASS settlement-recovery-rotated", "one-day history setting retained the exact original save after native rotation");
            }
            // Deny each original IO retry. The game's own IO recovery should
            // move the retained bytes back to the original slot before exit.
            throw new IOException(InjectedSaveError);
        }

        private static void Postfix(PlayerStore __instance, bool __runOriginal)
        {
            var probe = settlementSaveProbe;
            if (__runOriginal && SettlementWriteOracle.IsProbeWrite(__instance, probe) && probe.settlementAfterRotation)
                probe.settlementWrites++;
        }
    }
}
#endif
