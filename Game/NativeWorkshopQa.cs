using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string WorkshopRecipe = "dream_dust";
    private GameItem workshopChem, workshopReagent, workshopWater, workshopPurifier, workshopGun, workshopPart;
    private GameGridInventory workshopPartOrigin;
    private GridShape workshopPartShape;
    private int workshopPurity, workshopDurability, workshopReliability;
    private int workshopStep;
    private double workshopUntil;
    private bool workshopMade, workshopPurified, workshopInstalled, workshopHostDone;

    private void SeedWorkshopQa()
    {
        workshopChem=SeedFrontendItem("chem_finisher","QA_WORKSHOP_CHEM");
        workshopReagent=SeedFrontendItem("small_bottled_water","QA_WORKSHOP_REAGENT");
        workshopWater=SeedFrontendItem("small_bottled_water","QA_WORKSHOP_WATER");
        workshopPurifier=SeedFrontendItem("portable_water_purifier","QA_WORKSHOP_PURIFIER");
        workshopGun=SeedFrontendItem("handmade_pistol","QA_WORKSHOP_GUN");
        workshopPart=SeedFrontendItem("internal_springs","QA_WORKSHOP_PART");
        WaterHelper.FillWithRustWater(workshopWater);
        ChemicalHelper.RemoveAllLiquid(workshopReagent);
        var recipe=ChemFinisherRecipe.GetRecipeById(WorkshopRecipe);
        ChemicalHelper.SetChemContentUL(workshopReagent,recipe.activeIngredientId,recipe.requiredVolume);
        Require(recipe.CanOutput(workshopReagent) && WaterHelper.GetWaterPurity(workshopWater)<9800,
            "original chemistry and water treatment fixtures must start usable");
        ReadWorkshopBaseline();
    }

    private void ReadWorkshopBaseline()
    {
        workshopPurity=WaterHelper.GetWaterPurity(workshopWater);
        workshopDurability=DurabilityHelper.GetRealDurability(workshopPurifier);
        workshopReliability=WorkshopReliability();
        workshopPartOrigin=(GameGridInventory)workshopPart.parentInventory;
        workshopPartShape=workshopPart.modifiedShape.Clone();
    }

    private int WorkshopReliability()=>workshopGun.GetTagReadonly(GunHelper.GUN_CURRENT_RELIABILITY_TAG).GetInt();
    private GameItem WorkshopProduct()=>GraphUtils.FindAllChildrenType<GameItem>(workshopChem).SingleOrDefault(item=>item.IsID(WorkshopRecipe));

    private void CheckHostWorkshopQa()
    {
        if(workshopHostDone) return;
        if(!workshopMade && WorkshopProduct() is GameItem product)
        {
            Require(WaterHelper.GetTotalVolume(workshopReagent)==0 && ChemicalHelper.GetTotalVolume(product)>0,
                "chemical output did not consume the original feed and produce one original item");
            workshopMade=true;
            Record("PASS host-native-chemical-output","original finish callback consumed the dry feed and produced one dream_dust");
        }
        if(!workshopPurified && WaterHelper.GetWaterPurity(workshopWater)>workshopPurity)
        {
            Require(DurabilityHelper.GetRealDurability(workshopPurifier)<workshopDurability,"purification did not debit the original purifier");
            workshopPurified=true;
            Record("PASS host-native-water-purification","original purifier changed water contents and consumed durability");
        }
        if(!workshopInstalled && GraphUtils.FindAllChildrenType<GameItem>(workshopGun).Contains(workshopPart))
        {
            Require(WorkshopReliability()>workshopReliability,"gun installation did not recalculate native reliability");
            workshopInstalled=true;
        }
        if(workshopMade && workshopPurified && workshopInstalled && ReferenceEquals(workshopPart.parentInventory,workshopPartOrigin))
        {
            Require(WorkshopReliability()==workshopReliability,"gun removal did not restore native reliability");
            workshopHostDone=true;
            Record("PASS host-native-gun-modification","original slot callbacks added and removed the same attachment and restored the initial reliability");
        }
    }

    private bool RunGuestWorkshopQa(ReplicaLink link,double now)
    {
        if(workshopStep==9) return true;
        if(workshopUntil==0) workshopUntil=now+80;
        Require(now<workshopUntil,"original workshop regression timed out at step "+workshopStep+" result="+QaResult(link));
        var world=NativeWorld.Guest;
        if(world?.Ready!=true || world.HasPendingWork || QaWaiting(link)) return false;
        var chem=ChemFinisherUIManager.Instance;
        switch(workshopStep)
        {
            case 0:
                if(!world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag("QA_WORKSHOP_CHEM"))) return false;
                workshopChem=FrontendItem("QA_WORKSHOP_CHEM"); workshopReagent=FrontendItem("QA_WORKSHOP_REAGENT");
                workshopWater=FrontendItem("QA_WORKSHOP_WATER"); workshopPurifier=FrontendItem("QA_WORKSHOP_PURIFIER");
                workshopGun=FrontendItem("QA_WORKSHOP_GUN"); workshopPart=FrontendItem("QA_WORKSHOP_PART");
                ReadWorkshopBaseline();
                ReleaseWholeIntoContainer((GameItemElement)workshopReagent,workshopChem);
                break;
            case 1:
                if(!GraphUtils.FindAllChildrenType<GameItem>(workshopChem).Contains(workshopReagent)) return false;
                ItemMouseDoubleClickHandler.current.OpenContentAction(workshopChem,new Vector2(400,240));
                AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(FrontendConfigure(workshopChem),null);
                break;
            case 2:
                if(!chem.UIPanel.activeSelf || !FrontendListReady(now)) return false;
                FrontendRecipeRow(WorkshopRecipe).OnButtonClick();
                break;
            case 3:
                if(workshopChem.GetTagReadonly("SELECTED_CHEM_ID_TAG").GetString()!=WorkshopRecipe) return false;
                chem.HideUI();
                var controls=((MachineryPanelRow)workshopChem.contentWindow.childElement).Panels.Single(panel=>panel.role=="control");
                var output=((MachineryStack)controls.content).elements.OfType<MachineryButton>().Last();
                AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(output,null);
                break;
            case 4:
                if(WorkshopProduct() is not GameItem product) return false;
                Require(WaterHelper.GetTotalVolume(workshopReagent)==0 && ChemicalHelper.GetTotalVolume(product)>0,
                    "guest did not receive original chemical output and consumption together");
                Record("PASS guest-native-chemical-output","guest loaded the original slot, selected the recipe and used the original output button");
                ItemSelectHandler.current.TrySelectItem(workshopPurifier);
                break;
            case 5:
                if(!workshopPurifier.MayTarget(workshopWater) || !workshopPurifier.CanTarget(workshopWater)) return false;
                OriginalScannerTarget(workshopPurifier,workshopWater);
                break;
            case 6:
                if(WaterHelper.GetWaterPurity(workshopWater)<=workshopPurity) return false;
                Require(DurabilityHelper.GetRealDurability(workshopPurifier)<workshopDurability,"guest purifier durability differs after use");
                Record("PASS guest-native-water-purification","guest used the native tool on dirty water and received changed purity and tool durability");
                ReleaseWholeIntoContainer((GameItemElement)workshopPart,workshopGun);
                break;
            case 7:
                if(!GraphUtils.FindAllChildrenType<GameItem>(workshopGun).Contains(workshopPart)) return false;
                Require(WorkshopReliability()>workshopReliability,"guest gun reliability did not update when the original part was installed");
                var slot=workshopPartOrigin.TryInventorySlot(workshopPart,1,workshopPartShape,workshopPart.modifiedState);
                Require(slot?.IsValid()==true,"gun part's original inventory cell is unavailable");
                ReleaseAtNativeSlot((GameItemElement)workshopPart,slot);
                break;
            case 8:
                if(!ReferenceEquals(workshopPart.parentInventory,workshopPartOrigin)) return false;
                Require(WorkshopReliability()==workshopReliability,"guest gun reliability did not return to its initial value");
                Record("PASS guest-native-gun-modification","original drag installed and removed the same springs, and synced both reliability values");
                workshopStep=9;
                return true;
        }
        workshopStep++;
        return false;
    }
}
