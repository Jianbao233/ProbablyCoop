#if PROBABLY_COOP_GRID_QA
using System.Linq;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private const string QaRecipe = "dream_dust";
    private int workshopStage, rawPurity, rawDurability, rawReliability;
    private bool sawInstalledPart, checkedWorkshop;
    private double workshopDeadline;
    private GameItem qaFinisher, qaReagent, qaPurifier, qaDirtyWater, qaGun, qaGunPart;
    private GameInventory partOrigin;
    private GridShape partPose;

    private void SeedWorkshopQa()
    {
        GameItem Place(string catalog, string role)
        {
            var item = DirectoryMaster.Item(catalog);
            CampaignCheck(item != null, "original workshop item " + catalog);
            item.EnableTag("QA_EFFECT_" + role);
            GeneralHelper.SetItemOwned(item, true);
            GraphUtils.EmporiumTryAdd(EmporiumEntry.Instance.invElement, item);
            CampaignCheck(item.parentInventory != null, "workshop fixture fits in native stock");
            return item;
        }
        qaFinisher = Place("chem_finisher", "FINISHER");
        qaReagent = Place("small_bottled_water", "REAGENT");
        qaPurifier = Place("portable_water_purifier", "PURIFIER");
        qaDirtyWater = Place("small_bottled_water", "DIRTY_WATER");
        qaGun = Place("handmade_pistol", "GUN");
        qaGunPart = Place("internal_springs", "SPRINGS");
        var recipe = ChemFinisherRecipe.GetRecipeById(QaRecipe);
        ChemicalHelper.RemoveAllLiquid(qaReagent);
        ChemicalHelper.SetChemContentUL(qaReagent, recipe.activeIngredientId, recipe.requiredVolume);
        WaterHelper.FillWithRustWater(qaDirtyWater);
        CampaignCheck(recipe.CanOutput(qaReagent), "native chemistry reagent meets the recipe");
        RecordWorkshopBaseline();
    }

    private void RecordWorkshopBaseline()
    {
        rawPurity = WaterHelper.GetWaterPurity(qaDirtyWater);
        rawDurability = DurabilityHelper.GetRealDurability(qaPurifier);
        rawReliability = GunReliability();
        partOrigin = qaGunPart.parentInventory;
        partPose = qaGunPart.modifiedShape.Clone();
    }

    private int GunReliability() => qaGun.GetTagReadonly(GunHelper.GUN_CURRENT_RELIABILITY_TAG).GetInt();
    private GameItem QaProduct() => GraphUtils.FindAllChildrenType<GameItem>(qaFinisher).SingleOrDefault(item => item.IsID(QaRecipe));
    private bool PartInstalled() => GraphUtils.FindAllChildrenType<GameItem>(qaGun).Contains(qaGunPart);

    private bool CheckHostWorkshopQa()
    {
        if (checkedWorkshop) return true;
        if (PartInstalled())
        {
            sawInstalledPart = true;
            CampaignCheck(GunReliability() > rawReliability, "original host weapon updates native reliability");
        }
        if (!sawInstalledPart || qaGunPart.parentInventory != partOrigin || QaProduct() == null) return false;
        CampaignCheck(WaterHelper.GetTotalVolume(qaReagent) == 0 &&
            WaterHelper.GetWaterPurity(qaDirtyWater) > rawPurity &&
            DurabilityHelper.GetRealDurability(qaPurifier) < rawDurability &&
            GunReliability() == rawReliability &&
            !qaFinisher.contentWindow.IsVisible() && !ChemFinisherUIManager.Instance.UIPanel.activeSelf,
            "chemistry, water and weapon facts settle on the host without opening its machine UI");
        checkedWorkshop = true;
        Debug.Log("[probably_coop MECHANISMS] PASS host chemistry, water purification and gun parts");
        return true;
    }

    private bool RunWorkshopQa()
    {
        if (workshopStage == 7) return true;
        if (workshopDeadline == 0) workshopDeadline = Time.realtimeSinceStartupAsDouble + 15;
        CampaignCheck(Time.realtimeSinceStartupAsDouble < workshopDeadline,
            $"native workshop stalled at step {workshopStage}, pending={session.Guest.PendingCount}");
        if (session.Guest.PendingCount != 0) return false;
        var recipes = ChemFinisherUIManager.Instance;
        switch (workshopStage)
        {
            case 0:
                GameItem Item(string role) => SharedStock().Single(item => item.IsTag("QA_EFFECT_" + role));
                qaFinisher = Item("FINISHER");
                qaReagent = Item("REAGENT");
                qaPurifier = Item("PURIFIER");
                qaDirtyWater = Item("DIRTY_WATER");
                qaGun = Item("GUN");
                qaGunPart = Item("SPRINGS");
                RecordWorkshopBaseline();
                CampaignCheck(qaFinisher.TryFindOneValidInventorySlot(qaReagent)?.TryAcceptOnce() == 1,
                    "original finisher accepts the prepared reagent");
                ItemMouseDoubleClickHandler.current.OpenContentAction(qaFinisher, new Vector2(200, 250));
                ((SelectableElement)NativeMachineControls.Read(qaFinisher).Single(NativeMachineControls.OpensRecipePanel)).Select();
                break;
            case 1:
                if (!recipes.UIPanel.activeSelf) return false;
                var row = recipes.LayoutGroup.GetComponentsInChildren<ChemFinishElement>()
                    .FirstOrDefault(entry => entry.identifier == QaRecipe);
                if (row == null) return false;
                row.OnButtonClick();
                break;
            case 2:
                if (qaFinisher.GetTagReadonly(MachineChemFinisher.SELECTED_CHEM_ID_TAG).GetString() != QaRecipe) return false;
                recipes.HideUI();
                var output = NativeMachineControls.Read(qaFinisher).OfType<MachineryButton>()
                    .Single(button => !NativeMachineControls.OpensRecipePanel(button));
                output.Select();
                break;
            case 3:
                if (QaProduct() is not GameItem product) return false;
                CampaignCheck(WaterHelper.GetTotalVolume(qaReagent) == 0 && ChemicalHelper.GetTotalVolume(product) > 0,
                    "original chemistry output atomically consumes reagent and produces one product");
                CampaignCheck(qaPurifier.MayTarget(qaDirtyWater) && qaPurifier.CanTarget(qaDirtyWater),
                    "original purifier accepts the dirty bottle");
                qaPurifier.Target(qaDirtyWater);
                break;
            case 4:
                if (WaterHelper.GetWaterPurity(qaDirtyWater) <= rawPurity) return false;
                CampaignCheck(DurabilityHelper.GetRealDurability(qaPurifier) < rawDurability,
                    "original water purification consumes tool durability once");
                CampaignCheck(qaGun.TryFindOneValidInventorySlot(qaGunPart)?.TryAcceptOnce() == 1,
                    "original gun slot accepts the spring");
                break;
            case 5:
                if (!PartInstalled() || GunReliability() <= rawReliability) return false;
                var takeout = partOrigin.TryInventorySlot(qaGunPart, 1, partPose, qaGunPart.modifiedState);
                CampaignCheck(takeout?.TryAcceptOnce() == 1, "original weapon spring returns to its own cell");
                break;
            case 6:
                if (qaGunPart.parentInventory != partOrigin || GunReliability() != rawReliability) return false;
                qaFinisher.CloseContentWindow();
                Debug.Log("[probably_coop MECHANISMS] PASS guest original chemistry, water purification and gun parts");
                break;
        }
        Debug.Log($"[probably_coop MECHANISMS] guest workshop completed step={workshopStage++}");
        workshopDeadline = Time.realtimeSinceStartupAsDouble + 15;
        return workshopStage == 7;
    }
}
#endif
