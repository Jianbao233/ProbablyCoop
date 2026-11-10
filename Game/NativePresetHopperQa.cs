using System.Linq;
using System.Collections.Generic;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string PresetQaName = "QA shared workshop";
    private int extendedLogisticsStep;
    private double extendedLogisticsUntil;
    private bool extendedLogisticsSeeded;
    private bool sourcePresetConfirmed;
    private bool ExtendedLogisticsQaHostDone { get; set; }

    private void CheckHostPresetHopperQa()
    {
        if (!StorageTabsQaHostDone || ExtendedLogisticsQaHostDone) return;
        if (!extendedLogisticsSeeded)
        {
            var middle = (GameInventory)logisticsQaItems["DESTINATION"].contentWindow.childElement;
            var final = LogisticsQaCreate("CHAIN_END", "backpack_medium");
            var intake = LogisticsQaCreate("CHAIN_INTAKE", "intake_hopper", middle);
            var outlet = LogisticsQaCreate("CHAIN_OUTLET", "outlet_hopper", (GameInventory)final.contentWindow.childElement);
            HopperHelper.Link(intake, outlet);
            extendedLogisticsSeeded = true;
            return;
        }

        var coordinator = logisticsQaItems["COORDINATOR"];
        if (!sourcePresetConfirmed && logisticsQaPresetWrites == 2)
        {
            var configured = MachineStorageCoordinator.LoadPresets(coordinator).Single();
            Require(configured.name == PresetQaName && configured.keyCode == (int)KeyCode.F9 &&
                configured.itemIds.SequenceEqual(new[] { storageTabsQaBay.uniqueId, logisticsQaItems["CHAIN_END"].uniqueId }) &&
                configured.GetTabs(storageTabsQaBay.uniqueId) == "2", "original source preset lost a confirmed field");
            sourcePresetConfirmed = true;
        }
        if (logisticsQaChainRuns == 0 || logisticsQaPresetWrites < 3) return;
        var end = logisticsQaItems["CHAIN_END"].contentWindow.childElement;
        Require(logisticsQaChainRuns == 1 && new[] { "LOOT", "AUTO_LOOT" }.All(role =>
            ReferenceEquals(logisticsQaItems[role].parentInventory, end) && logisticsQaItems[role].unitCount == 1),
            "source second hop did not conserve both original units");
        Require(sourcePresetConfirmed && MachineStorageCoordinator.LoadPresets(coordinator).Count == 0 &&
            !storageTabsQaBay.contentWindow.IsVisible() && !logisticsQaItems["CHAIN_END"].contentWindow.IsVisible(),
            "source preset delete or guest window independence failed");
        Record("PASS host-native-hopper-chain", "same two original units reached the third container; each native hop ran once");
        Record("PASS host-native-machine-preset", "original create, edit and delete each ran once; replica toggle left source windows closed");
        ExtendedLogisticsQaHostDone = true;
    }

    private bool RunGuestPresetHopperQa(ReplicaLink link, double now)
    {
        if (extendedLogisticsStep == 6) return true;
        if (extendedLogisticsUntil == 0) extendedLogisticsUntil = now + 40;
        Require(now < extendedLogisticsUntil, "preset/hopper chain timed out at step " + extendedLogisticsStep);
        if (QaWaiting(link)) return false;
        var intake = NativeWorld.Guest.Objects.Values.OfType<GameItem>().SingleOrDefault(item => item.IsTag(LogisticsQaTag + "CHAIN_INTAKE"));
        if (intake == null) return false;
        var coordinator = LogisticsQaItem("COORDINATOR");
        var final = LogisticsQaItem("CHAIN_END");
        var bay = NativeWorld.Guest.Objects.Values.OfType<GameItem>().Single(item => item.IsTag(StorageTabsFixture));

        switch (extendedLogisticsStep)
        {
            case 0:
                Require(new[] { "LOOT", "AUTO_LOOT" }.All(role =>
                    ReferenceEquals(LogisticsQaItem(role).parentInventory, intake.parentInventory)), "first-hop stock missing from intermediate container");
                intake.ActivateSlotItem();
                break;
            case 1:
                if (!NativeLogisticsUi.Visible(NativeWorld.FrontendKind.Hopper, intake)) return false;
                LogisticsQaClick(CustomUIManager.Instance.GetWindow("intake_hopper"), LocHelper.GetLocalizedMechanic("mech_hopper_btn_transfer_now"));
                break;
            case 2:
                Require(new[] { "LOOT", "AUTO_LOOT" }.All(role =>
                    ReferenceEquals(LogisticsQaItem(role).parentInventory, final.contentWindow.childElement) && LogisticsQaItem(role).unitCount == 1),
                    "second-hop output not projected to the final container");
                Record("PASS native-hopper-chain", "first native hops fed a middle container; second native hopper moved the same identities to the final container");
                CustomUIManager.Instance.CloseWindow("intake_hopper");
                coordinator.ActivateSlotItem();
                break;
            case 3:
                if (!NativeLogisticsUi.Visible(NativeWorld.FrontendKind.StorageCoordinator, coordinator)) return false;
                var presets = OpenQaPresets();
                var preset = presets.Single();
                preset.name = PresetQaName;
                preset.keyCode = (int)KeyCode.F9;
                preset.itemIds.Add(bay.uniqueId);
                preset.itemIds.Add(final.uniqueId);
                preset.SetTabs(bay.uniqueId, "2");
                // The native picker writes this same schema. Feed its save
                // callback directly since this desktop has no physical input.
                MachineStorageCoordinator.SavePresets(coordinator, presets);
                break;
            case 4:
                if (NativeWorld.Guest.ReadLogisticsTag(coordinator, MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING, out _)) return false;
                var confirmed = MachineStorageCoordinator.LoadPresets(coordinator);
                if (confirmed.Count != 1 || confirmed[0].name != PresetQaName) return false;
                Require(confirmed[0].keyCode == (int)KeyCode.F9 && confirmed[0].itemIds.SequenceEqual(new[] { bay.uniqueId, final.uniqueId }) &&
                    confirmed[0].GetTabs(bay.uniqueId) == "2", "confirmed preset lost key, items or tab");
                bay.CloseContentWindow();
                final.CloseContentWindow();
                MachineStorageCoordinator.TogglePresets(confirmed);
                Require(bay.contentWindow.IsVisible() && final.contentWindow.IsVisible() && bay.GetContentTabsSelection() == "2",
                    "original preset toggle did not open both target windows at the selected tab");
                MachineStorageCoordinator.TogglePresets(confirmed);
                Require(!bay.contentWindow.IsVisible() && !final.contentWindow.IsVisible(), "original preset toggle did not close both target windows");
                AccessTools.Method(typeof(MachineStorageCoordinator), "RemovePreset").Invoke(null, new object[] { OpenQaPresets().Single() });
                break;
            case 5:
                if (NativeWorld.Guest.ReadLogisticsTag(coordinator, MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING, out _)) return false;
                if (MachineStorageCoordinator.LoadPresets(coordinator).Count != 0) return false;
                CustomUIManager.Instance.CloseWindow("storage_coordinator");
                Record("PASS native-machine-preset", "native configuration round-tripped key, two targets and a tab; native toggle opened/closed locally; native removal confirmed");
                break;
        }
        extendedLogisticsStep++;
        return extendedLogisticsStep == 6;
    }

    private static List<MachineStorageCoordinator.Preset> OpenQaPresets() =>
        (List<MachineStorageCoordinator.Preset>)AccessTools.Field(typeof(MachineStorageCoordinator), "presets").GetValue(null);
}
