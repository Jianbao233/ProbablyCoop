#if PROBABLY_COOP_GRID_QA
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private static readonly bool MachineMatrixQa = QaArgs.Contains("--probably-coop-grid-machines-qa");
    private const string MachineQaTag = "QA_NATIVE_MACHINE";

    // Registered pawnshop machines, fixed installations, and the automation
    // and husbandry devices that share their inventory/production paths.
    private static readonly string[] MachineQaCatalog =
    {
        "furnace", "hydroponic", "plant_dna_manipulator", "printer", "deal_maker",
        "recharger", "powerblock", "makeshift_reactor", "security_alarm", "storage_coordinator",
        "mirage_projector", "moisture_farm", "water_purifier", "desequencer", "bottle_printer",
        "box_dispenser", "heating_plate", "blender", "evaporator", "chem_finisher", "cassette_player",
        "vending_machine", "vending_fountain", "wine_rack", "fridge",
        "animal_cage", "livestock_feed_dispenser", "auto_butcher", "ras", "ras_water_tank",
        "vacuum_robot", "mouse_trap", "intake_hopper", "outlet_hopper",
        "turbo_booster", "turbo_booster_adv", "turbo_booster_hypertech", "portable_water_purifier", "faucet",
    };

    private static readonly IReadOnlyDictionary<string, string> MachineQaStock = new Dictionary<string, string>
    {
        ["recharger"] = "energy_credit", ["animal_cage"] = "rat",
        ["livestock_feed_dispenser"] = "processed_meat", ["ras"] = "water_filter",
        ["ras_water_tank"] = "tilapia_fry", ["vacuum_robot"] = "feces_small",
        ["mouse_trap"] = "processed_cheese", ["auto_butcher"] = "postit",
    };

    private sealed class MachineQaStep
    {
        internal readonly string Name;
        internal readonly Func<bool> Run;

        internal MachineQaStep(string name, Func<bool> run)
        {
            Name = name;
            Run = run;
        }
    }

    private int machineQaIndex, machineQaFailed;
    private double machineQaDue;
    private IEnumerator<MachineQaStep> machineQaSteps;
    private MachineQaStep machineQaStep;
    private GameItem machineQaCurrent;
    private readonly Dictionary<string, Func<bool>> machineNightChecks = new Dictionary<string, Func<bool>>();
    private bool machineQaVoting, machineQaPrepared, machineQaOpened;
    private int machineQaStartDay;
    private double machineQaStartTime;

    private static string MachineStockTag(string id) => MachineQaTag + "_STOCK_" + id;

    private static GameItem FixedMachine(EmporiumEntry shop, string id) => id switch
    {
        "cassette_player" => shop.cassettePlayerElement.childItem,
        "faucet" => shop.faucet,
        "vending_machine" => shop.vendingMachineElement.childItem,
        "vending_fountain" => shop.vendingFountainElement.childItem,
        _ => null,
    };

    private static GameItem MachineQaCreate(string id, string marker, EmporiumEntry shop)
    {
        var item = DirectoryMaster.Item(id);
        CampaignCheck(item != null, "original factory missing for " + id);
        item.EnableTag(marker);
        var roots = new[] { shop.backInvinvElement, shop.invElement, shop.showcaseElement };
        foreach (var root in roots)
            if (root.TryFindOneValidInventorySlot(item)?.TryAcceptOnce() > 0) return item;
        item.Destroy();
        throw new InvalidOperationException("Campaign QA: no staging space for " + id);
    }

    private static void SeedMachineMatrixQa(EmporiumEntry shop)
    {
        PlayerStore.Instance.skipIntro = true;
        VendingMachineHelper.UnlockVendingMachine();
        VendingFountainHelper.UnlockVendingFountain();
        NetworkUpgrade.MarkUnlockedSilently("RUSTWATER_TAP");
        shop.ApplyFaucetState(true);
        var known = shop.GetAllItems().ToLookup(item => item.identifier);
        var vanillaFurniture = DirectoryMaster.GetIdentifierList<GameItem>(nameof(FurnitureItemDirectory));
        var unmatched = vanillaFurniture.Where(id => id != "test_gizmo" &&
            id != "vending_machine_core" && id != "vending_fountain_core" &&
            !MachineQaCatalog.Contains(id)).ToArray();
        CampaignCheck(unmatched.Length == 0, "unlisted furniture machine " + string.Join(",", unmatched));
        foreach (string id in MachineQaCatalog)
        {
            var item = FixedMachine(shop, id) ?? known[id]
                .Where(candidate => !candidate.IsTag("BROKEN_MACHINE_TAG"))
                .OrderBy(candidate => candidate.parentInventory?.GetParentItem()?.IsID("animal_cage") == true ? 0 : 1)
                .ThenBy(candidate => candidate.uniqueId).FirstOrDefault();
            bool spawned = item == null;
            item ??= MachineQaCreate(id, MachineQaTag, shop);
            item.EnableTag(MachineQaTag);
            var slot = PowerSlot(item);
            if (slot != null && slot.childItem == null && id != "vacuum_robot")
                MachineQaCreate(id == "mirage_projector" ? "energy_credit_ext" : "energy_credit",
                    MachineStockTag(id), shop);
            else if (MachineQaStock.TryGetValue(id, out string stock))
                MachineQaCreate(stock, MachineStockTag(id), shop);
            if (slot?.childItem?.IsTag("power_source_item") == true)
                PowerHelper.SetPowerSourceAt(slot.childItem, slot.childItem.GetTagReadonly("power_source_item_max_energy").GetInt());
            Debug.Log($"[probably_coop MATRIX] catalog={id} item={item.uniqueId} added={spawned}");
        }
        SeedMachineEffectsQa(shop);
    }

    private static GameSlotInventory PowerSlot(GameItem item)
    {
        if (item.contentWindow?.childElement is not MachineryPanelRow row) return null;
        var panel = row.GetPanel("power");
        return (panel?.content is MachineryIntake intake ? intake.content : panel?.content) as GameSlotInventory;
    }

    private static GameItem MatrixMachine(string id)
    {
        var shop = EmporiumEntry.Instance;
        return FixedMachine(shop, id) ?? shop.GetAllItems().SingleOrDefault(item => item.IsID(id) && item.IsTag(MachineQaTag));
    }

    private static GameItem MatrixStock(string marker) =>
        EmporiumEntry.Instance.GetAllItems().SingleOrDefault(item => item.IsTag(MachineStockTag(marker)));

    private static void DoOriginalDrop(GameInventory destination, GameItem item)
    {
        CampaignCheck(destination != null && item != null, "missing original slot or item");
        int count = item.unitCount;
        var marker = destination.TryFindOneValidInventorySlot(item);
        CampaignCheck(marker != null && marker.targetItem == null && marker.TryAcceptOnce() == count &&
            item.parentInventory == destination, "original slot refused " + item.identifier);
    }

    private static GameInventory StagingSlot(GameItem item)
    {
        var shop = EmporiumEntry.Instance;
        return new[] { shop.backInvinvElement, shop.invElement, shop.showcaseElement }
            .FirstOrDefault(root => root != item.parentInventory &&
                root.TryFindOneValidInventorySlot(item) is SlotMarker slot && slot.targetItem == null &&
                slot.numTransfer >= item.unitCount);
    }

    private void TickMachineMatrixQa()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        int day = StoreStation.GetDayCounter();
        if (machineQaStartDay == 0)
        {
            machineQaStartDay = day;
            machineQaStartTime = now;
            if (!localHost)
                session.Guest.Bounced += (tx, verdict) => Debug.Log(
                    $"[probably_coop MATRIX] bounce machine={machineQaCurrent?.identifier ?? "none"} serial={tx.Serial} verdict={verdict}");
        }
        if (localHost && !machineQaOpened && now >= machineQaStartTime + 2)
        {
            StoreShutterButton.Instance.OpenShutter();
            PlayerStore.Instance.storeClientManager.clientStack.Clear();
            SeedBusinessQa(day);
            machineQaOpened = true;
            Debug.Log($"[probably_coop MATRIX] original shop state={PlayerStore.Instance.storeState}");
        }
        if (!localHost && machineQaIndex < MachineQaCatalog.Length)
        {
            TickMachineActionsQa(now);
            return;
        }
        if (day == machineQaStartDay)
        {
            // This file coordinates the two QA drivers only. Machine actions,
            // inventory results and day settlement still use the normal room.
            string prepared = Path.Combine(Path.GetDirectoryName(QaArgs[FullStockArg + 1]), "machine-guest-prepared.txt");
            if (!localHost && !machineQaPrepared)
            {
                File.WriteAllText(prepared, "native machine inputs exercised");
                machineQaPrepared = true;
            }
            if (localHost && machineQaOpened && !machineQaPrepared && File.Exists(prepared))
            {
                StoreShutterButton.Instance.EndAndClose();
                machineQaPrepared = true;
                Debug.Log($"[probably_coop MATRIX] closed original shop state={PlayerStore.Instance.storeState}");
                return;
            }
            if (machineQaVoting || campaign.Busy || !localHost && session.Guest.PendingCount != 0 ||
                PlayerStore.Instance.storeState != PlayerStore.StoreState.CLOSED ||
                localHost && session.ReadyMask != 2) return;
            foreach (string id in MachineQaCatalog)
                if (ObserveMachineNight(id, MatrixMachine(id)) is Func<bool> expected)
                    machineNightChecks.Add(id, expected);
            machineQaVoting = true;
            var ui = EndOfDayUIManager.Instance;
            ui.OpenUI();
            ui.OnEndDayButtonClick();
            return;
        }
        if (campaign.Busy) return;
        CampaignCheck(day == machineQaStartDay + 1, "original machine night did not settle once");
        foreach (var entry in machineNightChecks)
        {
            bool passed;
            try { passed = entry.Value(); }
            catch (Exception e) { Debug.Log($"[probably_coop MATRIX] night assertion {entry.Key}: {e.Message}"); passed = false; }
            if (!passed) machineQaFailed++;
            Debug.Log($"[probably_coop MATRIX] night role={RoleName()} id={entry.Key} outcome={(passed ? "PASS" : "FAIL")}");
        }
        campaignDone = true;
        Debug.Log($"[probably_coop MATRIX] done role={RoleName()} count={MachineQaCatalog.Length} night={machineNightChecks.Count} failed={machineQaFailed}");
    }

    private void TickMachineActionsQa(double now)
    {
        string id = MachineQaCatalog[machineQaIndex];
        try
        {
            if (machineQaSteps == null)
            {
                machineQaCurrent = MatrixMachine(id);
                CampaignCheck(machineQaCurrent != null, "missing native machine on guest");
                machineQaSteps = MachineActionsQa(id, machineQaCurrent).GetEnumerator();
            }
            if (machineQaStep == null)
            {
                if (!machineQaSteps.MoveNext())
                {
                    FinishMachineActions(id, null);
                    return;
                }
                machineQaStep = machineQaSteps.Current;
                machineQaDue = now + 7;
            }
            CampaignCheck(now < machineQaDue, "wait for " + machineQaStep.Name + " timed out");
            if (session.Guest.PendingCount == 0 && machineQaStep.Run())
            {
                Debug.Log($"[probably_coop MATRIX] action id={id} step={machineQaStep.Name} outcome=PASS");
                machineQaStep = null;
            }
        }
        catch (Exception e)
        {
            FinishMachineActions(id, e.GetBaseException().Message.Replace("Campaign QA: ", ""));
        }
    }

    private void FinishMachineActions(string id, string failure)
    {
        Release();
        if (failure != null) machineQaFailed++;
        machineQaCurrent?.CloseContentWindow();
        machineQaSteps?.Dispose();
        machineQaSteps = null;
        machineQaStep = null;
        machineQaCurrent = null;
        machineQaIndex++;
        Debug.Log($"[probably_coop MATRIX] guest id={id} outcome={(failure == null ? "PASS" : "FAIL")} detail={failure ?? "confirmed native controls and inventory"}");
    }

    private IEnumerable<MachineQaStep> MachineActionsQa(string id, GameItem machine)
    {
        if (machine.contentWindow != null)
        {
            yield return new MachineQaStep("open native panel", () =>
            {
                if (id == "vending_machine") StoreVendingMachine.Instance.OnClick();
                else if (id == "vending_fountain") StoreVendingFountain.Instance.OnClick();
                else ItemMouseDoubleClickHandler.current.OpenContentAction(machine, new Vector2(320, 250));
                return machine.contentWindow.IsVisible();
            });
        }

        var power = PowerSlot(machine);
        var existing = power?.childItem;
        var stock = MatrixStock(id);
        if (power != null && (existing != null || stock?.IsTag("power_source_item") == true) && id != "vacuum_robot")
        {
            foreach (var step in existing == null ? DropAndConfirm(power, stock) : MoveOutAndBack(existing)) yield return step;
        }
        else if (stock != null && machine.TryFindOneValidInventorySlot(stock) is SlotMarker marker && marker.targetItem == null)
        {
            foreach (var step in DropAndConfirm(marker.inventory, stock)) yield return step;
        }
        else if (id != "cassette_player" && id != "faucet")
        {
            foreach (var step in MoveOutAndBack(machine)) yield return step;
        }

        foreach (var step in MachineEffectActionsQa(id, machine)) yield return step;
    }

    private static IEnumerable<MachineQaStep> DropAndConfirm(GameInventory destination, GameItem item)
    {
        yield return new MachineQaStep("place " + item.identifier, () => { DoOriginalDrop(destination, item); return true; });
        yield return new MachineQaStep("confirm " + item.identifier, () => item.parentInventory == destination);
    }

    private static IEnumerable<MachineQaStep> MoveOutAndBack(GameItem item)
    {
        var from = item.parentInventory;
        var pose = item.modifiedShape.Clone();
        var to = StagingSlot(item);
        foreach (var step in DropAndConfirm(to, item)) yield return step;
        yield return new MachineQaStep("restore original slot " + item.identifier, () =>
        {
            int count = item.unitCount;
            var original = from.TryInventorySlot(item, count, new Vector2(float.NaN, float.NaN), pose, item.modifiedState);
            CampaignCheck(original?.targetItem == null && original?.numTransfer == count &&
                original.TryAcceptOnce() == count && item.parentInventory == from,
                "original slot would not accept return of " + item.identifier);
            return true;
        });
        yield return new MachineQaStep("confirm original slot " + item.identifier, () => item.parentInventory == from);
    }

}
#endif
