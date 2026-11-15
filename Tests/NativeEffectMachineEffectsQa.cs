#if PROBABLY_COOP_GRID_QA
using System;
using System.Collections.Generic;
using System.Linq;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private static int MachineUnits(GameInventory inventory, string id) =>
        inventory?.childItems.Where(item => item.IsID(id)).Sum(item => item.unitCount) ?? -1;

    private static int Energy(GameItem cell) => cell?.GetTagReadonly("power_source_item_energy").GetInt() ?? -1;
    private static int Number(GameItem item, string tag) => item?.GetTagReadonly(tag).GetInt() ?? -1;
    private static double Temperature(GameItem item) =>
        item.GetTagReadonly(ChemicalHelper.CONTAINER_TEMP_TAG).GetDouble();

    private static GameInventory MachineSlot(GameItem item, string role)
    {
        if (item?.contentWindow?.childElement is not MachineryPanelRow row) return null;
        var slots = new List<GraphNodeStorage>();
        row.GetPanel(role)?.CollectStorage(slots);
        return slots.OfType<GameInventory>().FirstOrDefault();
    }

    private static GameItem SlotItem(GameItem item, string role) => MachineSlot(item, role)?.childItems.FirstOrDefault();

    private static void SeedMachineEffectsQa(EmporiumEntry shop)
    {
        GameItem Supply(string marker, string id, Action<GameItem> configure = null)
        {
            var item = MachineQaCreate(id, MachineStockTag(marker), shop);
            configure?.Invoke(item);
            return item;
        }
        void Empty(GameItem item) => ChemicalHelper.RemoveAllLiquid(item);

        Supply("furnace-ore", "scrap_metal");
        Supply("hydroponic-seed", "nutrifruit_seed");
        WaterHelper.FillWithNutrientSolution(SlotItem(MatrixMachine("hydroponic"), "water"));
        Supply("dna-seed", "fillerweed_seed", item => PlantGeneticsHelper.SetTraitRank(item, "fast_grower", 1));
        Supply("dna-disk", "plant_dna_disk");
        Supply("printer-chip", "printer_chip_small_bottle");
        Supply("printer-input", "printer_plastic");
        PowerHelper.SetPowerSourceAt(MatrixStock("recharger"), 2);
        var blockCell = Supply("powerblock-cell", "energy_credit", item => PowerHelper.SetPowerSourceAt(item, 3));
        var block = MatrixMachine("powerblock");
        var cabinet = MachineQaCreate("hypertech_bay", MachineStockTag("power-cabinet"), shop);
        DoOriginalDrop(GraphUtils.FindChildrenType<GameInventory>(cabinet).First(), block);
        PlaceNearPowerBlock(block, blockCell);
        var reactor = MatrixMachine("makeshift_reactor");
        reactor.ModifyTag(ReactorHelper.INTEGRITY_INT, tag => tag.Enable().SetInt(ReactorHelper.GetMaxIntegrity(reactor) - 60));
        Supply("reactor-scrap", "scrap_metal");
        var farm = MatrixMachine("moisture_farm");
        var farmPower = PowerSlot(farm);
        if (farmPower.childItem != null) DoOriginalDrop(StagingSlot(farmPower.childItem), farmPower.childItem);
        DoOriginalDrop(farmPower, Supply("farm-power", "energy_credit_ext", item => PowerHelper.SetPowerSourceAt(item, 100)));
        var farmWater = SlotItem(farm, "container");
        if (farmWater != null) Empty(farmWater);
        else Supply("farm-water", "water_jug", Empty);
        foreach (string id in new[] { "turbo_booster_adv", "turbo_booster_hypertech" })
        {
            var booster = MatrixMachine(id);
            booster.ModifyTag("CURRENT_CHARGE_TAG", tag => tag.Enable().SetInt(15));
            booster.EnableTag("TURBO_READY_TAG");
        }
        Supply("purifier-water", "bottled_water", WaterHelper.FillWithRustWater);
        Supply("crypto-chip", "crypto_module_ser");
        Supply("crypto-card", "blank_keycard");
        Supply("heater-water", "small_bottled_water", WaterHelper.FillWithBaseWater);
        Supply("blender-water", "bottled_water", Empty);
        Supply("blender-food", "dream_cap");
        Supply("evaporator-water", "bottled_water", WaterHelper.FillWithRustWater);
        Supply("evaporator-output", "bottled_water", Empty);
        Supply("finisher-reagent", "small_bottled_water", item =>
        {
            var recipe = ChemFinisherRecipe.GetRecipeById(QaRecipe);
            Empty(item);
            ChemicalHelper.SetChemContentUL(item, recipe.activeIngredientId, recipe.requiredVolume);
        });
        Supply("cassette-tape", "cassette1");
        Supply("vending-food", "processed_meat");
        Supply("vending-water", "large_bottled_water", WaterHelper.FillWithBaseWater);
        var wine = WineHelper.CreateFinishedWineForDebug(48);
        wine.EnableTag(MachineStockTag("wine"));
        DoOriginalDrop(StagingSlot(wine), wine);
        Supply("fridge-food", "beis_icecream");
        Supply("faucet-water", "small_bottled_water", Empty);
        Supply("portable-dirty", "small_bottled_water", WaterHelper.FillWithRustWater);
        var portable = MatrixMachine("portable_water_purifier");
        DurabilityHelper.SetCurrentDurability(portable, DurabilityHelper.GetMaxDurability(portable));
        Supply("fish-feed", "industrial_fish_feed");
        Supply("butcher-rat", "rat");
    }

    private static void PlaceNearPowerBlock(GameItem block, GameItem cell)
    {
        if (block.parentInventory is not GameGridInventory grid) return;
        var blockPose = block.modifiedShape;
        for (int y = 0; y < grid.inventoryShape.height; y++)
            for (int x = 0; x < grid.inventoryShape.width; x++)
            {
                var shape = new GridShapeBuilder(cell.modifiedShape).SetTransform(x, y, false, 0);
                var slot = grid.TryInventorySlot(cell, 1, shape, cell.modifiedState);
                bool adjacent = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(offset =>
                    ((GridShape)new GridShapeBuilder(blockPose).SetTransform(blockPose.minX + offset.Item1,
                        blockPose.minY + offset.Item2, blockPose.flipped, blockPose.orientation)).Intersects(shape));
                if (slot?.targetItem == null && slot?.numTransfer == 1 && adjacent &&
                    slot.TryAcceptOnce() == 1) return;
            }
        throw new InvalidOperationException("Campaign QA: no adjacent battery slot for the power block");
    }

    private static MachineQaStep Click(string description, Func<SelectableElement> control) =>
        new MachineQaStep(description, () =>
        {
            var button = control();
            CampaignCheck(button != null && !button.isDisabled && !button.isHidden, "original button unavailable");
            button.Select();
            return true;
        });

    private static MachineryButton MachineButton(GameItem machine, int index = 0) =>
        NativeMachineControls.Read(machine).OfType<MachineryButton>().ElementAtOrDefault(index);

    private static IEnumerable<MachineQaStep> Into(GameItem machine, string role, string marker)
    {
        var item = MatrixStock(marker);
        CampaignCheck(item != null, "missing fixture " + marker);
        return DropAndConfirm(MachineSlot(machine, role), item);
    }

    private static IEnumerable<MachineQaStep> UseOn(GameItem tool, GameItem target, Func<bool> result)
    {
        yield return new MachineQaStep("original tool use", () =>
        {
            CampaignCheck(tool?.CanTarget(target) == true, "original tool cannot target " + target?.identifier);
            tool.Target(target);
            return true;
        });
        yield return new MachineQaStep("confirmed tool result", result);
    }

    private IEnumerable<MachineQaStep> UseWhileHeld(GameItem tool, GameItem target, Func<bool> result)
    {
        var key = EntityKey.Item(tool.uniqueId);
        yield return new MachineQaStep("reserve dragged item", () => BeginDrag(tool));
        yield return new MachineQaStep("original drop callbacks with acknowledged reservation", () =>
        {
            if (!session.Guest.Holder(key, out byte owner) || owner != NetSession.GuestPeer)
                return false;
            CampaignCheck(tool.CanTarget(target), "original tool cannot target " + target?.identifier);
            var gesture = EnterTargetGesture();
            bool completed = false;
            try
            {
                ItemBehaviourManager.current.Target(tool, target);
                tool.Target(target);
                completed = true;
            }
            finally { gesture.ExitTargetGesture(completed); }
            return true;
        });
        yield return new MachineQaStep("confirmed use before releasing the reservation", () =>
        {
            if (!result()) return false;
            Release();
            return true;
        });
    }

    private IEnumerable<MachineQaStep> MachineEffectActionsQa(string id, GameItem machine)
    {
        switch (id)
        {
            case "deal_maker":
                yield return new MachineQaStep("call original customer", () =>
                {
                    if (PlayerStore.Instance.storeState != PlayerStore.StoreState.OPEN ||
                        PlayerStore.Instance.lockNextBellFlag > 0) return false;
                    UnityEngine.Object.FindFirstObjectByType<StoreBell>().OnClick();
                    return true;
                });
                yield return new MachineQaStep("wait for customer", () => PlayerStore.Instance.IsClientPresent());
                var dealPower = PowerSlot(machine).childItem;
                int dealStart = Energy(dealPower);
                yield return Click("apply bargaining bonus", () => MachineButton(machine));
                yield return new MachineQaStep("confirmed power and bargaining bonus", () => Energy(dealPower) < dealStart &&
                    PlayerStore.Instance.dealMakerActivatedThisCustomer && PlayerStore.Instance.dealMakerBargainBonus > 0);
                break;
            case "furnace":
                foreach (var step in Into(machine, "input", "furnace-ore")) yield return step;
                break;
            case "hydroponic":
                foreach (var step in Into(machine, "seed", "hydroponic-seed")) yield return step;
                break;
            case "plant_dna_manipulator":
                foreach (var step in Into(machine, "seed", "dna-seed")) yield return step;
                foreach (var step in Into(machine, "output_disk", "dna-disk")) yield return step;
                yield return new MachineQaStep("toggle keep disk", () => { machine.ToggleSlotItem(); return true; });
                yield return new MachineQaStep("confirmed keep disk", () =>
                    machine.GetTagReadonly("DNA_MANIPULATOR_KEEP_DISK_BOOL").GetBool());
                break;
            case "printer":
                var oldChip = SlotItem(machine, "chip");
                if (oldChip != null)
                    foreach (var step in DropAndConfirm(StagingSlot(oldChip), oldChip)) yield return step;
                foreach (var step in Into(machine, "chip", "printer-chip")) yield return step;
                foreach (var step in Into(machine, "input", "printer-input")) yield return step;
                break;
            case "makeshift_reactor":
                int integrity = ReactorHelper.GetIntegrity(machine);
                foreach (var step in UseOn(MatrixStock("reactor-scrap"), machine,
                    () => ReactorHelper.GetIntegrity(machine) > integrity)) yield return step;
                break;
            case "moisture_farm":
                if (SlotItem(machine, "container") == null)
                    foreach (var step in Into(machine, "container", "farm-water")) yield return step;
                break;
            case "water_purifier":
                foreach (var step in Into(machine, "container", "purifier-water")) yield return step;
                break;
            case "desequencer":
                foreach (var step in Into(machine, "chip", "crypto-chip")) yield return step;
                foreach (var step in Into(machine, "input", "crypto-card")) yield return step;
                break;
            case "bottle_printer":
            case "box_dispenser":
                string product = id == "bottle_printer" ? "small_bottled_water" : "ammo_box";
                var output = MachineSlot(machine, "output");
                var cell = PowerSlot(machine)?.childItem;
                int prior = MachineUnits(output, product), charge = Energy(cell);
                yield return Click("produce " + product, () => MachineButton(machine));
                if (!DirectoryMaster.Has<GameItem>(product))
                {
                    yield return new MachineQaStep("registered output", () =>
                        throw new InvalidOperationException("vanilla product factory is unavailable for " + product));
                    break;
                }
                yield return new MachineQaStep("confirmed product and one power charge", () =>
                    MachineUnits(output, product) == prior + 1 && Energy(cell) == charge - (id == "bottle_printer" ? 4 : 1));
                var produced = output.childItems.First(item => item.IsID(product));
                foreach (var step in DropAndConfirm(StagingSlot(produced), produced)) yield return step;
                break;
            case "heating_plate":
                var water = MatrixStock("heater-water");
                var heatSlot = machine.TryFindOneValidInventorySlot(water)?.inventory;
                foreach (var step in DropAndConfirm(heatSlot, water)) yield return step;
                double temp = Temperature(water);
                yield return Click("switch on heater", () => NativeMachineControls.Read(machine)
                    .OfType<MachineryRadioButton>().FirstOrDefault(button => !button.isOn));
                yield return new MachineQaStep("confirmed rise in temperature", () => Temperature(water) > temp);
                foreach (var step in DropAndConfirm(StagingSlot(water), water)) yield return step;
                break;
            case "blender":
                foreach (var step in Into(machine, "container", "blender-water")) yield return step;
                var blendFood = MatrixStock("blender-food");
                foreach (var step in Into(machine, "input", "blender-food")) yield return step;
                yield return Click("blend ingredients", () => MachineButton(machine));
                yield return new MachineQaStep("confirmed blended liquid and consumed food", () =>
                    blendFood.parentInventory == null && WaterHelper.GetTotalVolume(SlotItem(machine, "container")) > 0);
                break;
            case "evaporator":
                foreach (var step in Into(machine, "container", "evaporator-water")) yield return step;
                foreach (var step in Into(machine, "distillate", "evaporator-output")) yield return step;
                break;
            case "chem_finisher":
                var reagent = MatrixStock("finisher-reagent");
                foreach (var step in DropAndConfirm(machine.TryFindOneValidInventorySlot(reagent)?.inventory, reagent)) yield return step;
                var recipes = ChemFinisherUIManager.Instance;
                yield return Click("open original recipes", () => NativeMachineControls.Read(machine)
                    .OfType<SelectableElement>().Single(NativeMachineControls.OpensRecipePanel));
                yield return new MachineQaStep("select original recipe", () =>
                {
                    if (!recipes.UIPanel.activeSelf) return false;
                    var row = recipes.LayoutGroup.GetComponentsInChildren<ChemFinishElement>()
                        .FirstOrDefault(entry => entry.identifier == QaRecipe);
                    if (row == null) return false;
                    row.OnButtonClick();
                    return true;
                });
                yield return new MachineQaStep("confirmed recipe", () =>
                    machine.GetTagReadonly(MachineChemFinisher.SELECTED_CHEM_ID_TAG).GetString() == QaRecipe);
                yield return Click("package chemical", () =>
                {
                    recipes.HideUI();
                    return NativeMachineControls.Read(machine).OfType<MachineryButton>()
                        .Single(button => !NativeMachineControls.OpensRecipePanel(button));
                });
                yield return new MachineQaStep("confirmed packaged product and depleted reagent", () =>
                    WaterHelper.GetTotalVolume(reagent) == 0 &&
                    GraphUtils.FindAllChildrenType<GameItem>(machine).Any(item => item.IsID(QaRecipe)));
                break;
            case "cassette_player":
                float volume = EmporiumOption.Instance.musicVolume;
                yield return Click("native local music volume", () => MachineButton(machine, volume > 0.1f ? 4 : 5));
                yield return new MachineQaStep("local volume changed", () => !Mathf.Approximately(volume, EmporiumOption.Instance.musicVolume));
                foreach (var step in DropAndConfirm(MachineCassettePlayer.GetCassetteInventory(), MatrixStock("cassette-tape"))) yield return step;
                yield return Click("start local playback", () => MachineButton(machine, 1));
                yield return new MachineQaStep("local tape playing", () => Number(machine, "CASETTE_PLAYER_STATE_INT") == 1);
                yield return Click("stop local playback", () => MachineButton(machine, 2));
                yield return new MachineQaStep("local tape stopped", () => Number(machine, "CASETTE_PLAYER_STATE_INT") == 0);
                break;
            case "vending_machine":
                foreach (var step in Into(machine, "inventory", "vending-food")) yield return step;
                break;
            case "vending_fountain":
                foreach (var step in Into(machine, "base_water", "vending-water")) yield return step;
                break;
            case "wine_rack":
                foreach (var step in Into(machine, "wine", "wine")) yield return step;
                break;
            case "fridge":
                foreach (var step in Into(machine, "freeze", "fridge-food")) yield return step;
                break;
            case "livestock_feed_dispenser":
                int kcal = Number(machine, MachineFeedDispenser.STORED_KCAL_TAG);
                yield return Click("blend animal feed", () => MachineButton(machine));
                yield return new MachineQaStep("confirmed feed stored", () => Number(machine, MachineFeedDispenser.STORED_KCAL_TAG) > kcal);
                break;
            case "auto_butcher":
                var cage = MachineAutoButcher.GetHost(machine);
                CampaignCheck(cage != null, "the original butcher is attached to a cage or RAS");
                var rat = MatrixStock("butcher-rat");
                foreach (var step in DropAndConfirm(MachineSlot(cage, "cage"), rat)) yield return step;
                foreach (var step in UseWhileHeld(rat, machine, () =>
                    rat.parentInventory == null && MachineAutoButcher.GetOutputGrid(machine).childItems.Count > 0)) yield return step;
                break;
            case "ras_water_tank":
                var ras = MatrixMachine("ras");
                var fish = MatrixStock("ras_water_tank");
                foreach (var step in DropAndConfirm(MachineRAS.GetTankGrid(ras), fish)) yield return step;
                var dispenser = MachineRAS.GetDispenserSlot(ras).childItem;
                if (dispenser != null)
                    foreach (var step in DropAndConfirm(MachineFeedDispenser.GetStorageGrid(dispenser), MatrixStock("fish-feed"))) yield return step;
                break;
            case "vacuum_robot":
                foreach (var step in DropAndConfirm(MachineSlot(MatrixMachine("animal_cage"), "cage"), machine)) yield return step;
                break;
            case "mouse_trap":
                yield return new MachineQaStep("confirmed native cheese bait", () =>
                    RatTrap.GetTrapSlot(machine)?.childItem?.IsID("processed_cheese") == true);
                break;
            case "turbo_booster":
            case "turbo_booster_adv":
            case "turbo_booster_hypertech":
                var farm = MatrixMachine("moisture_farm");
                var reservoir = SlotItem(farm, "container");
                var farmBattery = PowerSlot(farm).childItem;
                int waterBefore = WaterHelper.GetTotalVolume(reservoir);
                int powerBefore = Energy(farmBattery);
                foreach (var step in UseOn(machine, farm, () => WaterHelper.GetTotalVolume(reservoir) > waterBefore &&
                    Energy(farmBattery) < powerBefore && (id == "turbo_booster" ? machine.parentInventory == null :
                        Number(machine, "CURRENT_CHARGE_TAG") == 0 && !MachineTurboBoosterAdv.IsTurboReady(machine))))
                    yield return step;
                break;
            case "portable_water_purifier":
                var dirty = MatrixStock("portable-dirty");
                int dirtyBefore = WaterHelper.GetWaterPurity(dirty);
                int durability = DurabilityHelper.GetRealDurability(machine);
                foreach (var step in UseOn(machine, dirty, () => WaterHelper.GetWaterPurity(dirty) > dirtyBefore &&
                    DurabilityHelper.GetRealDurability(machine) < durability)) yield return step;
                break;
            case "faucet":
                var empty = MatrixStock("faucet-water");
                foreach (var step in UseWhileHeld(empty, machine, () => WaterHelper.GetTotalVolume(empty) > 0)) yield return step;
                foreach (var step in UseWhileHeld(empty, EmporiumEntry.Instance.drain, () => WaterHelper.GetTotalVolume(empty) == 0))
                    yield return step;
                break;
        }
    }

    private static Func<bool> Increase(Func<int> measure)
    {
        int before = measure();
        return () =>
        {
            int after = measure();
            Debug.Log($"[probably_coop MATRIX] measured {before}->{after}");
            return after > before;
        };
    }

    private static Func<bool> ObserveMachineNight(string id, GameItem machine)
    {
        switch (id)
        {
            case "furnace":
                return Increase(() => MachineUnits(MachineSlot(machine, "output"), "metal_ingot"));
            case "hydroponic":
                return Increase(() => MachineProgressHelper.GetProgress(machine).Item1);
            case "plant_dna_manipulator":
                return Increase(() => MachineProgressHelper.GetProgress(machine).Item1);
            case "printer":
                return Increase(() => MachineUnits(MachineSlot(machine, "output"), "small_bottled_water"));
            case "recharger":
                return Increase(() => Energy(MatrixStock("recharger")));
            case "powerblock":
                return Increase(() => Energy(MatrixStock("powerblock-cell")));
            case "makeshift_reactor":
                return Increase(() => -ReactorHelper.GetIntegrity(machine));
            case "security_alarm":
                return () => machine.IsTag("POWERED");
            case "mirage_projector":
                return () => machine.IsTag("IS_PROJECTOR_ACTIVE");
            case "moisture_farm":
                return Increase(() => WaterHelper.GetTotalVolume(SlotItem(machine, "container")));
            case "water_purifier":
                return Increase(() => WaterHelper.GetWaterPurity(SlotItem(machine, "container")));
            case "desequencer":
                return Increase(() => MachineProgressHelper.GetProgress(machine).Item1 +
                    Math.Max(0, MachineSlot(machine, "output")?.childItems.Count ?? 0));
            case "evaporator":
                return Increase(() => -WaterHelper.GetTotalVolume(SlotItem(machine, "container")));
            case "vending_machine":
            case "vending_fountain":
                return Increase(() => Number(machine, VendingMachineHelper.VENDING_CASH));
            case "wine_rack":
                return Increase(() => Number(MatrixStock("wine"), "AGE_INT"));
            case "fridge":
                return () => MatrixStock("fridge-food")?.FindItemFeatureByID("frozen") != null;
            case "animal_cage":
                var rat = MatrixStock("animal_cage");
                return Increase(() => Number(rat, HusbandryHelper.ANIMAL_AGE_TAG));
            case "ras":
                return Increase(() => -Energy(PowerSlot(machine)?.childItem));
            case "ras_water_tank":
                return Increase(() => Number(MatrixStock("ras_water_tank"), AquacultureHelper.FISH_AGE_TAG));
            case "vacuum_robot":
                return Increase(() => MachineSlot(machine, "storage")?.childItems.Sum(item => item.unitCount) ?? -1);
            case "mouse_trap":
                return () =>
                {
                    var catchOrBait = RatTrap.GetTrapSlot(machine).childItem;
                    Debug.Log($"[probably_coop MATRIX] trap result {catchOrBait?.identifier ?? "empty"}");
                    return catchOrBait == null || catchOrBait.IsID("processed_cheese") || catchOrBait.IsID("rat");
                };
            case "turbo_booster_adv":
            case "turbo_booster_hypertech":
                return Increase(() => Number(machine, "CURRENT_CHARGE_TAG"));
            default:
                return null;
        }
    }
}
#endif
