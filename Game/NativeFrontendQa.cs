using System;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using TMPro;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string FrontendFixture="QA_NATIVE_FRONTEND_FIXTURE";
    private const string FrontendRole="qaNativeFrontends";
    private const string FrontendPrime="QA_NATIVE_FRONTEND_PRIME", FrontendArmed="QA_NATIVE_FRONTEND_ARMED";
    private const string FrontendDone="QA_NATIVE_FRONTEND_DONE", FrontendTutorial="QA_NATIVE_FRONTEND_TUTORIAL";
    private GameItem[] frontendItems;
    private GameItem frontendChem,frontendHostChem,frontendSeed,frontendHostSeed,frontendTool,frontendLog,frontendHostLog,frontendExchange;
    private System.Collections.Generic.List<string> frontendPromotionIds;
    private bool frontendHostOpened,frontendHostDone;
    private bool frontendTutorial2,frontendTutorialRecorder;
    private int frontendTutorialStage;
    private static bool frontendTutorialArmed,frontendTutorialAdvanced;
    private static int frontendTutorialCalls;
    private static byte[] firstFrontendCommand;
    private static readonly FieldInfo FrontendTutorialStage=AccessTools.Field(typeof(TutorialUIManager),"tutorialStage");
    private int frontendStep;
    private double frontendUntil,frontendNext,frontendListWaiting;
    private ChemFinishElement frontendFirstRow;
    private string frontendFirstRecipe,frontendSecondRecipe;
    private ulong frontendCloseSerial;
    private int frontendGuestTutorialStage;

    internal static void RememberFrontendCommand(JObject command)
    { firstFrontendCommand??=Encoding.UTF8.GetBytes(command.ToString(Formatting.None)); }

    private GameItem SeedFrontendItem(string id,string role)
    {
        var item=DirectoryMaster.Item(id); GeneralHelper.SetItemOwned(item,true);
        item.EnableTag(FrontendFixture); item.EnableTag(role);
        Require(TryPlaceInEmptyCell(item,EmporiumEntry.Instance.invElement),"native frontend fixture does not fit stock: "+id);
        return item;
    }
    private void SeedNativeFrontends()
    {
        frontendChem=SeedFrontendItem("chem_finisher","QA_FRONTEND_CLIENT_CHEM");
        frontendHostChem=SeedFrontendItem("chem_finisher","QA_FRONTEND_HOST_CHEM");
        frontendSeed=SeedFrontendItem("nutrifruit_seed","QA_FRONTEND_CLIENT_SEED");
        frontendHostSeed=SeedFrontendItem("kotton_seed","QA_FRONTEND_HOST_SEED");
        frontendTool=SeedFrontendItem("plant_analyser","QA_FRONTEND_TOOL");
        frontendLog=SeedFrontendItem("expedition_log","QA_FRONTEND_CLIENT_LOG");
        frontendHostLog=SeedFrontendItem("expedition_log","QA_FRONTEND_HOST_LOG");
        frontendExchange=SeedFrontendItem("exchange_directive","QA_FRONTEND_EXCHANGE");
        frontendLog.ModifyTag("LOG_CONTENT_TAG",tag=>tag.Enable().SetString("QA guest journal/Local original frontend"));
        frontendHostLog.ModifyTag("LOG_CONTENT_TAG",tag=>tag.Enable().SetString("QA host journal/Independent original frontend"));
        frontendItems=new[]{frontendChem,frontendHostChem,frontendSeed,frontendHostSeed,frontendTool,frontendLog,frontendHostLog,frontendExchange};
        var button=new MachineryButton("QA native frontend",()=>
        {
            if(qaMachine.IsTag(FrontendDone))
            {
                qaMachine.ModifyTag("LOG_CONTENT_TAG",tag=>tag.Enable().SetString("QA reconnect frontend"));
                LogUIManager.Instance.OpenUI(qaMachine);
            }
            else qaMachine.EnableTag(qaMachine.IsTag(FrontendPrime) ? FrontendDone : FrontendPrime);
        });
        ((MachineryPanelRow)qaMachine.contentWindow.childElement).Add(new MachineryPanel("API frontends",button,FrontendRole));
        ((MachineryPanelRow)qaMachine.contentWindow.childElement).Add(new MachineryPanel("Scanner QA",
            new MachineryButton("QA scanner complete",()=>qaMachine.EnableTag(ScannerDone)),ScannerRole));
        ((MachineryPanelRow)qaMachine.contentWindow.childElement).Add(new MachineryPanel("Scanner confront QA",
            new MachineryButton("QA scanner confront complete",()=>qaMachine.EnableTag(ScannerConfrontDone)),ScannerConfrontRole));
        ((MachineryPanelRow)qaMachine.contentWindow.childElement).Add(new MachineryPanel("Scanner liquid QA",
            new MachineryButton("QA change liquid",MutateScannerWater),ScannerWaterChangeRole));
        SeedStripControl();
        SeedScratchControl();
    }
    private static string FirstLogLine()=>((System.Collections.Generic.List<TextMeshProUGUI>)
        AccessTools.Field(typeof(LogUIManager),"elementList").GetValue(LogUIManager.Instance))[0].text.Trim();
    private void CheckHostNativeFrontends()
    {
        if(frontendHostDone) { CheckHostScannerLifecycle(); CheckHostStripFrontends(); CheckHostScratchFrontends(); return; }
        if(!qaMachine.IsTag(FrontendPrime)) return;
        var chem=ChemFinisherUIManager.Instance; var plant=PlantAnalyserUIManager.Instance; var log=LogUIManager.Instance;
        if(!frontendHostOpened)
        {
            frontendPromotionIds=PlayerStore.Instance.exchangeManager.activePromotionalBarterIds.ToList();
            var promotionIds=ExchangeUIManager.Instance.promotionalSection.GetComponentsInChildren<ExchangeElement>(true)
                .Select(row=>row.identifier).Where(id=>!string.IsNullOrEmpty(id)).Distinct().Take(2).ToList();
            Require(promotionIds.Count==2,"exchange original rows unavailable");
            PlayerStore.Instance.exchangeManager.activePromotionalBarterIds=promotionIds;
            ExchangeUIManager.Instance.OpenPanel(frontendExchange);
            qaMachine.ModifyTag("QA_NATIVE_EXCHANGE_ROWS",tag=>tag.Enable().SetString(FrontendExchangeRows()));
            plant.OpenUI(frontendSeed);
            qaMachine.ModifyTag("QA_NATIVE_PLANT_RANGE",tag=>tag.Enable().SetString(plant.nutrimentRangeTMP.text));
            qaMachine.ModifyTag("QA_NATIVE_PLANT_USAGE",tag=>tag.Enable().SetString(plant.nutrientUseTMP.text));
            plant.HideUI(); plant.OpenUI(frontendHostSeed);
            chem.OpenUI(frontendHostChem); log.OpenUI(frontendHostLog);
            frontendTutorialStage=(int)FrontendTutorialStage.GetValue(TutorialUIManager.Instance);
            frontendTutorial2=TutorialUIManager.Instance.tutorial2.activeSelf;
            frontendTutorialRecorder=TutorialUIManager.Instance.tutorialRecorder.activeSelf;
            FrontendTutorialStage.SetValue(TutorialUIManager.Instance,8);
            frontendTutorialCalls=0; frontendTutorialAdvanced=false; frontendTutorialArmed=true;
            frontendHostOpened=true; qaMachine.EnableTag(FrontendArmed);
            return;
        }
        Require(chem.UIPanel.activeSelf && ReferenceEquals(chem.currentItem,frontendHostChem) &&
            plant.UIPanel.activeSelf && ReferenceEquals(plant.currentItem,frontendHostSeed) &&
            log.UIPanel.activeSelf && FirstLogLine()=="QA host journal" && !frontendHostChem.IsTag("SELECTED_CHEM_ID_TAG"),
            "remote native frontend changed the host's independent window, bound target or machine");
        Require(ExchangeUIManager.Instance.panel.activeSelf && FrontendExchangeRows()==qaMachine.GetTagReadonly("QA_NATIVE_EXCHANGE_ROWS").GetString(),
            "remote exchange frontend changed the host's original panel");
        if(frontendTutorialCalls>0)
        {
            Require(frontendTutorialCalls==1 && frontendTutorialAdvanced,"native remote journal did not advance original tutorial 8->9 exactly once");
            qaMachine.EnableTag(FrontendTutorial);
        }
        if(!qaMachine.IsTag(FrontendDone)) return;
        Require(frontendTutorialCalls==1 && frontendTutorialAdvanced,"native journal tutorial callback was not exercised");
        frontendTutorialArmed=false;
        chem.HideUI(); plant.HideUI(); log.CloseUI(); ExchangeUIManager.Instance.ClosePanel();
        PlayerStore.Instance.exchangeManager.activePromotionalBarterIds=frontendPromotionIds;
        FrontendTutorialStage.SetValue(TutorialUIManager.Instance,frontendTutorialStage);
        TutorialUIManager.Instance.tutorial2.SetActive(frontendTutorial2);
        TutorialUIManager.Instance.tutorialRecorder.SetActive(frontendTutorialRecorder);
        foreach(var item in frontendItems) item.Destroy();
        frontendHostDone=true;
        SeedScannerFrontends(); OpenHostScannerFrontends();
        Record("PASS host-native-frontends","original host recipe/plant/journal panels and targets survived all guest views and commits; original tutorial 8->9 once; fixtures retired");
    }
    private GameItem FrontendItem(string tag)=>NativeWorld.Guest.Objects.Values.OfType<GameItem>().Single(item=>item.IsTag(tag));
    private static MachineryButton FrontendConfigure(GameItem item)=>(MachineryButton)
        ((MachineryStack)((MachineryPanelRow)item.contentWindow.childElement).Panels.Single(panel=>panel.role=="control").content).elements.First();
    private void FrontendPrimeClick()
    {
        var button=(MachineryButton)((MachineryPanelRow)MachineReplica().contentWindow.childElement).Panels.Single(panel=>panel.role==FrontendRole).content;
        AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(button,null);
    }
    private static ChemFinishElement FrontendRecipeRow(string id)=>ChemFinisherUIManager.Instance.LayoutGroup
        .GetComponentsInChildren<ChemFinishElement>(true).Single(row=>row.identifier==id);
    private bool FrontendListReady(double now)
    {
        var rows=ChemFinisherUIManager.Instance.LayoutGroup.GetComponentsInChildren<ChemFinishElement>(true);
        var recipes=ChemFinisherRecipe.chemFinisherRecipes;
        if(rows.Length==recipes.Count && rows.Select(row=>row.identifier).ToHashSet().SetEquals(recipes.Select(recipe=>recipe.identifier)))
        { frontendListWaiting=0; return true; }
        // Original OpenUI destroys old rows at end of frame. Do not count
        // still-pending old children as an incomplete newly opened list.
        if(frontendListWaiting==0) frontendListWaiting=now;
        Require(now-frontendListWaiting<2,"original recipe list did not settle: "+rows.Length+" vs "+recipes.Count);
        return false;
    }
    private bool RunNativeFrontends(ReplicaLink link,double now)
    {
        if(frontendStep==99) return true;
        if(frontendUntil==0) frontendUntil=now+95;
        Require(now<frontendUntil,"native Unity frontend timed out at "+frontendStep);
        if(QaWaiting(link) || now<frontendNext) return false;
        var world=NativeWorld.Guest; var chem=ChemFinisherUIManager.Instance;
        var plant=PlantAnalyserUIManager.Instance; var log=LogUIManager.Instance;
        switch(frontendStep)
        {
            case 0: FrontendPrimeClick(); frontendStep=1; return false;
            case 1:
                if(!MachineReplica().IsTag(FrontendArmed)) return false;
                frontendChem=FrontendItem("QA_FRONTEND_CLIENT_CHEM");
                frontendSeed=FrontendItem("QA_FRONTEND_CLIENT_SEED"); frontendTool=FrontendItem("QA_FRONTEND_TOOL");
                frontendLog=FrontendItem("QA_FRONTEND_CLIENT_LOG");
                frontendFirstRecipe=ChemFinisherRecipe.chemFinisherRecipes[0].identifier;
                frontendSecondRecipe=ChemFinisherRecipe.chemFinisherRecipes[1].identifier;
                ItemMouseDoubleClickHandler.current.OpenContentAction(frontendChem,new Vector2(400,240));
                AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(FrontendConfigure(frontendChem),null);
                Require(chem.UIPanel.activeSelf && ReferenceEquals(chem.currentItem,frontendChem) && QaWaiting(link),
                    "original recipe UI waited for authority instead of opening in the input frame");
                frontendFirstRow=chem.LayoutGroup.GetComponentsInChildren<ChemFinishElement>(true)
                    .Last(row=>row.identifier==frontendFirstRecipe);
                frontendStep=2; return false;
            case 2:
                if(!chem.UIPanel.activeSelf || !ReferenceEquals(chem.currentItem,frontendChem)) return false;
                if(!FrontendListReady(now)) return false;
                Require(ReferenceEquals(frontendFirstRow,FrontendRecipeRow(frontendFirstRecipe)),
                    "authority confirmation reconstructed the already opened native recipe UI");
                Require(chem.LayoutGroup.GetComponentsInChildren<ChemFinishElement>(true).Length==ChemFinisherRecipe.chemFinisherRecipes.Count,
                    "native recipe list incomplete");
                frontendFirstRow.OnButtonClick();
                Require(QaWaiting(link) && !frontendChem.IsTag("SELECTED_CHEM_ID_TAG"),"native recipe selection mutated guest authority state or sent no request");
                frontendStep=3; return false;
            case 3:
                if(frontendChem.GetTagReadonly("SELECTED_CHEM_ID_TAG").GetString()!=frontendFirstRecipe) return false;
                Require(ReferenceEquals(frontendFirstRow,FrontendRecipeRow(frontendFirstRecipe)),"recipe projection rebuilt the original local list");
                NativeOffscreenCapture.Capture("qa-native-chem-finisher");
                FrontendRecipeRow(frontendSecondRecipe).OnButtonClick(); frontendStep=4; return false;
            case 4:
                if(frontendChem.GetTagReadonly("SELECTED_CHEM_ID_TAG").GetString()!=frontendSecondRecipe) return false;
                Require(ReferenceEquals(frontendFirstRow,FrontendRecipeRow(frontendFirstRecipe)),"second recipe reset independent local list");
                chem.HideUI(); frontendCloseSerial=world.LastSubmittedUiRequest; frontendNext=now+0.8; frontendStep=5; return false;
            case 5:
                Require(!chem.UIPanel.activeSelf && world.LastSubmittedUiRequest==frontendCloseSerial,"local close sent a command or snapshot reopened the recipe UI");
                AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(FrontendConfigure(frontendChem),null);
                frontendStep=6; return false;
            case 6:
                if(!chem.UIPanel.activeSelf) return false;
                if(!FrontendListReady(now)) return false;
                Require(!ReferenceEquals(frontendFirstRow,FrontendRecipeRow(frontendFirstRecipe)),"explicit original reopen did not create a fresh local list");
                Require(firstFrontendCommand!=null,"native recipe command witness missing");
                var obsolete=JObject.Parse(Encoding.UTF8.GetString(firstFrontendCommand));
                world.TryData(frontendChem,out var current);
                obsolete["token"]=current["moveToken"]; // Isolate old view generation, not an old object proof.
                Require(link.Submit(Encoding.UTF8.GetBytes(obsolete.ToString(Formatting.None)),now),"cannot replay obsolete frontend view");
                frontendStep=7; return false;
            case 7:
                Require(QaResult(link)==(ushort)ShopResult.Stale && frontendChem.GetTagReadonly("SELECTED_CHEM_ID_TAG").GetString()==frontendSecondRecipe,
                    "old frontend generation changed the current machine");
                Record("PASS native-frontend-recipe","original configure and recipe buttons, two host commits, stable independent list, local close/reopen and obsolete generation rejected");
                ItemSelectHandler.current.TrySelectItem(frontendTool); frontendStep=8; return false;
            case 8:
                if(!frontendTool.MayTarget(frontendSeed) || !frontendTool.CanTarget(frontendSeed)) return false;
                frontendTool.Target(frontendSeed);
                Require(plant.UIPanel.activeSelf && ReferenceEquals(plant.currentItem,frontendSeed) && QaWaiting(link),
                    "original plant UI waited for authority instead of computing locally in the input frame");
                ItemSelectHandler.current.EndSelect(); frontendStep=9; return false;
            case 9:
                if(!plant.UIPanel.activeSelf || !ReferenceEquals(plant.currentItem,frontendSeed)) return false;
                Require(plant.titleTMP.text==frontendSeed.name &&
                    plant.nutrimentRangeTMP.text==MachineReplica().GetTagReadonly("QA_NATIVE_PLANT_RANGE").GetString() &&
                    plant.nutrientUseTMP.text==MachineReplica().GetTagReadonly("QA_NATIVE_PLANT_USAGE").GetString(),
                    "original plant UI differs from original host calculations on the same seed");
                var values=NativeMirrorProbe.CaptureValues(frontendSeed); plant.Refresh();
                Require(JToken.DeepEquals(values,NativeMirrorProbe.CaptureValues(frontendSeed)),"local original plant refresh wrote shared item scalars");
                NativeOffscreenCapture.Capture("qa-native-plant-analyser");
                frontendGuestTutorialStage=(int)FrontendTutorialStage.GetValue(TutorialUIManager.Instance);
                frontendLog.ActivateSlotItem();
                Require(log.UIPanel.activeSelf && FirstLogLine()=="QA guest journal" && QaWaiting(link),
                    "original journal waited for authority instead of drawing local item data immediately");
                frontendStep=10; return false;
            case 10:
                if(!log.UIPanel.activeSelf || FirstLogLine()!="QA guest journal" || !MachineReplica().IsTag(FrontendTutorial)) return false;
                Require((int)FrontendTutorialStage.GetValue(TutorialUIManager.Instance)==frontendGuestTutorialStage,
                    "guest journal replay advanced tutorial locally");
                NativeOffscreenCapture.Capture("qa-native-journal");
                frontendCloseSerial=world.LastSubmittedUiRequest;
                frontendLog.ActivateSlotItem();
                Require(!log.UIPanel.activeSelf && world.LastSubmittedUiRequest==frontendCloseSerial,"second activation did not close guest journal locally");
                frontendNext=now+0.8; frontendStep=11; return false;
            case 11:
                if(frontendEdgeStep==0) Require(!log.UIPanel.activeSelf && world.LastSubmittedUiRequest==frontendCloseSerial,"snapshot reopened a locally closed journal");
                if(!RunFrontendEdgeCases(link,now)) return false;
                Record("PASS native-frontend-plant-journal","original tool target/plant computation and journal text; host tutorial callback once, guest has no tutorial mutation; independent local toggle");
                FrontendPrimeClick(); frontendStep=12; return false;
            case 12:
                if(world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag(FrontendFixture))) return false;
                Require(!chem.UIPanel.activeSelf && !plant.UIPanel.activeSelf && !log.UIPanel.activeSelf && !ExchangeUIManager.Instance.panel.activeSelf && chem.currentItem==null && plant.currentItem==null,
                    "retired frontend target left an active panel or bound ghost item");
                if(!RunScannerFrontends(link,now)) return false;
                if(!RunStripFrontends(link,now)) return false;
                if(!RunScratchFrontends(link,now)) return false;
                Record("PASS native-frontend-lifetime","authority retired real target items; guest original modals closed, target bindings released; no local save");
                frontendStep=99; return true;
        }
        return false;
    }
    [HarmonyPatch(typeof(TutorialUIManager),nameof(TutorialUIManager.OnRecorderOpen))]
    private static class FrontendTutorialOracle
    {
        private static void Prefix(TutorialUIManager __instance,out int __state)=>__state=(int)FrontendTutorialStage.GetValue(__instance);
        private static void Postfix(TutorialUIManager __instance,int __state)
        {
            if(!Enabled || NativeGuest.Active || !frontendTutorialArmed) return;
            frontendTutorialCalls++;
            frontendTutorialAdvanced|=__state==8 && (int)FrontendTutorialStage.GetValue(__instance)==9;
        }
    }
}
