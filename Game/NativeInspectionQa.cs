using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string InspectionFixture="QA_INSPECTION_FIXTURE",InspectionDone="QA_INSPECTION_DONE",InspectionRole="qaInspectionDone";
    private GameItem[] inspectionItems;
    private GameItem inspectionTool,inspectionHostItem,inspectionRenameTool;
    private bool inspectionSeeded,inspectionHostDone;
    private int inspectionIndex,inspectionStep;
    private double inspectionUntil,inspectionNext;
    private static readonly Dictionary<GameItem,int> inspectionCommits=new Dictionary<GameItem,int>();
    private static readonly string[] InspectionCategories={"CATEGORY_GENUINE_CIGARETTE","CATEGORY_GENUINE_STAMP","CATEGORY_GENUINE_INJECTOR"};
    private static readonly string[][] InspectionReasons={
        new[]{"CIGARETTE_LETTERING_ISSUE","CIGARETTE_COLOR_ISSUE","CIGARETTE_SEAL_ISSUE","CIGARETTE_LOGO_ISSUE","CIGARETTE_BOX_ISSUE"},
        new[]{"STAMP_LETTERING_ISSUE","STAMP_LOGO_ISSUE","STAMP_SEAL_ISSUE","STAMP_AMOUNT_ISSUE","STAMP_CHIP_ISSUE"},
        new[]{"ISSUE_INDICATOR","ISSUE_LABEL","ISSUE_SERIAL","ISSUE_BODY","ISSUE_COLOR"}};
    private void SeedInspectionControl()=>((MachineryPanelRow)qaMachine.contentWindow.childElement).Add(new MachineryPanel("Inspection QA",
        new MachineryButton("QA inspection complete",()=>qaMachine.EnableTag(InspectionDone)),InspectionRole));
    private static string InspectionText()
    {
        var ui=InspectionUIManager.Instance;
        return string.Join("|",ui.confrontTitleTMP.text,
            string.Join("|",new[]{ui.toggle1,ui.toggle2,ui.toggle3,ui.toggle4,ui.toggle5}.Select(toggle=>
                toggle.gameObject.activeSelf+":"+toggle.GetComponentInChildren<TMPro.TextMeshProUGUI>().text)),
            ui.cigaretteUILarge.gameObject.activeSelf,ui.stampUILarge.gameObject.activeSelf,ui.injectorUILarge.gameObject.activeSelf);
    }
    private void CheckHostInspectionQa()
    {
        if(inspectionHostDone || !scannerConfrontHostDone || hostDayStarted) return;
        var ui=InspectionUIManager.Instance;
        if(!inspectionSeeded)
        {
            inspectionTool=SeedFrontendItem("magnifier","QA_INSPECTION_TOOL");
            inspectionHostItem=PreBuiltItemHelper.GenuineCigarette(); inspectionHostItem.EnableTag("QA_INSPECTION_HOST");
            Require(TryPlaceInEmptyCell(inspectionHostItem,EmporiumEntry.Instance.invElement),"source native inspection item cannot fit stock");
            inspectionItems=new[]{PreBuiltItemHelper.FakeCigarette(),PreBuiltItemHelper.CreateFakeStamp(),
                PreBuiltItemHelper.CreateInjector(ItemFeatureList.InjectorConditionType.Genuine,ItemFeatureList.InjectorConditionType.Expired)};
            for(int i=0;i<inspectionItems.Length;i++)
            {
                var item=inspectionItems[i]; GeneralHelper.SetItemOwned(item,false); item.EnableTag(InspectionFixture); item.EnableTag("QA_INSPECTION_"+i);
                var feature=item.FindItemFeatureByCategory(InspectionCategories[i]);
                Require(feature!=null,"native inspection factory lacks its authenticity feature: "+item.identifier);
                Require(!feature.IsExposed(),"native prebuilt inspection fixture unexpectedly exposed");
                Require(feature.GetCurrentItemCondition().identifier==new[]{"genuineCigarette","genuineStamp","genuineInjector"}[i],"native fixture does not claim genuine: "+item.identifier);
                var slot=EmporiumEntry.Instance.backInvinvElement.TryFindOneValidInventorySlot(item);
                Require(slot?.IsValid()==true && slot.TryAcceptOnce()==item.unitCount,"native inspection fixture cannot fit back counter");
                ui.OnInspectItem(item);
                Require(ui.confrontPanel.activeSelf,"source native inspection confrontation panel missing");
                qaMachine.ModifyTag("QA_INSPECTION_TEXT_"+i,tag=>tag.Enable().SetString(InspectionText())); ui.CloseUI();
            }
            foreach(var item in new[]{inspectionTool,inspectionHostItem}) item.DisableTag(FrontendFixture);
            ui.OnInspectItem(inspectionHostItem); ui.toggle1.SetIsOnWithoutNotify(true);
            inspectionSeeded=true;
            Record("FIXTURE native-inspection","three original category factories with native issue helpers, real arrived customer/back counter; source own magnifier panel stays open; disposable QA only");
            return;
        }
        Require(ui.panel.activeSelf && ReferenceEquals(ui.currentInspectingItem,inspectionHostItem) && ui.toggle1.isOn,
            "guest inspection overwrote source original magnifier panel/target/toggle");
        if(!qaMachine.IsTag(InspectionDone)) return;
        Require(DialogUIManager.Instance.currentDialog==null,"inspection completion while original shared dialogue remains active");
        foreach(var item in inspectionItems)
            Require(item.IsTag("ALREADY_CONFRONTED_TAG") && item.itemFeatures.Count(f=>f.identifier=="confrontSucessDiscount")==1 &&
                inspectionCommits.TryGetValue(item,out int calls) && calls==1,"original inspection result did not commit once: "+item.identifier);
        ui.CloseUI(); foreach(var item in inspectionItems) item.Destroy(); inspectionTool.Destroy(); inspectionHostItem.Destroy();
        inspectionHostDone=true;
        Record("PASS host-native-inspection","source independent original magnifier panel/target/toggle retained; each of cigarette/stamp/injector native success helper and discount executed once; fixtures removed before save");
    }
    private bool RunInspectionQa(ReplicaLink link,double now)
    {
        if(inspectionStep==99) return true;
        if(inspectionUntil==0) inspectionUntil=now+110;
        Require(now<inspectionUntil,"native inspection timed out at "+inspectionIndex+":"+inspectionStep);
        if(QaWaiting(link) || now<inspectionNext) return false;
        var world=NativeWorld.Guest; var ui=InspectionUIManager.Instance;
        if(inspectionStep==0)
        {
            if(!world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag("QA_INSPECTION_0"))) return false;
            inspectionTool=FrontendItem("QA_INSPECTION_TOOL");
            inspectionHostItem=FrontendItem("QA_INSPECTION_HOST"); inspectionRenameTool=ToolItem("labeler_blue");
            inspectionItems=Enumerable.Range(0,3).Select(i=>FrontendItem("QA_INSPECTION_"+i)).ToArray();
            ItemSelectHandler.current.TrySelectItem(inspectionRenameTool); inspectionStep=10; return false;
        }
        if(inspectionStep==10)
        {
            if(!inspectionRenameTool.MayTarget(inspectionHostItem) || !inspectionRenameTool.CanTarget(inspectionHostItem)) return false;
            OriginalScannerTarget(inspectionRenameTool,inspectionHostItem);
            Require(LabelerBlueUIManager.Instance.panel.activeSelf,"original independent rename panel did not open");
            inspectionStep=1;
        }
        if(inspectionStep==1)
        { ItemSelectHandler.current.TrySelectItem(inspectionTool); inspectionStep=2; return false; }
        if(inspectionStep==2)
        {
            var item=inspectionItems[inspectionIndex];
            if(!inspectionTool.MayTarget(item) || !inspectionTool.CanTarget(item)) return false;
            OriginalScannerTarget(inspectionTool,item);
            Require(ui.panel.activeSelf && ui.confrontPanel.activeSelf && ReferenceEquals(ui.currentInspectingItem,item),"original guest magnifier did not open in input frame");
            Require(InspectionText()==MachineReplica().GetTagReadonly("QA_INSPECTION_TEXT_"+inspectionIndex).GetString(),"source/guest original inspection labels/schema differ");
            if(inspectionIndex==0)
            {
                var rename=LabelerBlueUIManager.Instance;
                Require(rename.panel.activeSelf && ReferenceEquals(ReplicaRenameItem.Target,inspectionHostItem),"opening magnifier replaced independent original rename panel");
                rename.inputField.SetTextWithoutNotify("QA independent editor"); rename.inputField.onEndEdit.Invoke("QA independent editor");
                Require(QaWaiting(link),"original rename after selecting magnifier did not submit its own tool binding");
                inspectionStep=20; return false;
            }
            inspectionStep=21;
        }
        if(inspectionStep==20)
        {
            Require(QaResult(link)==(ushort)ShopResult.Applied && GeneralHelper.GetCustomName(inspectionHostItem)=="QA independent editor" &&
                LabelerBlueUIManager.Instance.panel.activeSelf && ui.panel.activeSelf && ReferenceEquals(ui.currentInspectingItem,inspectionItems[0]),
                "independent rename used the later magnifier tool or replaced an original local panel");
            Record("PASS native-editor-tool-independence","original rename window retained its labeler after original magnifier targeting; editing the first window committed once while both native panels stayed open");
            inspectionStep=21;
        }
        if(inspectionStep==21)
        {
            var item=inspectionItems[inspectionIndex];
            var toggles=new[]{ui.toggle1,ui.toggle2,ui.toggle3,ui.toggle4,ui.toggle5};
            int issue=Array.FindIndex(InspectionReasons[inspectionIndex],item.IsTag);
            Require(issue>=0,"native issue helper did not generate an inspectable issue");
            toggles[issue].isOn=true;
            NativeOffscreenCapture.Capture("qa-native-inspection-"+inspectionIndex);
            ui.OnConfront();
            Require(QaWaiting(link) && !ui.panel.activeSelf && !item.IsTag("ALREADY_CONFRONTED_TAG"),"native confrontation ran guest shared effects or failed to submit/close");
            inspectionStep=3; return false;
        }
        if(inspectionStep==3)
        {
            Require(QaResult(link)==(ushort)ShopResult.Applied,"native magnifier confrontation rejected");
            inspectionStep=4;
        }
        if(inspectionStep==4)
        {
            if(world.HasDialogue) { DialogUIManager.Instance.OnAdvancePressed(); inspectionNext=now+0.25; return false; }
            var item=inspectionItems[inspectionIndex];
            if(!item.IsTag("ALREADY_CONFRONTED_TAG")) return false;
            Require(item.itemFeatures.Count(f=>f.identifier=="confrontSucessDiscount")==1 && item.FindItemFeatureByCategory(InspectionCategories[inspectionIndex]).IsExposed(),"original confrontation did not expose authenticity and add one shared discount");
            Record("PASS native-inspection-category","category="+InspectionCategories[inspectionIndex]+" original tool target, input-frame native panel parity, native issue toggle/Confront, original shared dialogue and one exposed/discount result");
            if(++inspectionIndex<3) { inspectionStep=1; return false; }
            var button=(MachineryButton)((MachineryPanelRow)MachineReplica().contentWindow.childElement).Panels.Single(panel=>panel.role==InspectionRole).content;
            AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(button,null); inspectionStep=5; return false;
        }
        if(inspectionStep==5)
        {
            if(world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag(InspectionFixture))) return false;
            Require(!ui.panel.activeSelf,"retired inspection fixture left original guest panel open");
            Require(!LabelerBlueUIManager.Instance.panel.activeSelf && ReplicaRenameItem.Target==null,"retired item left original rename window bound");
            Record("PASS native-editor-retirement","source removed original rename target while its actor-local panel was open; original CloseUI cleared binding without committing a retired draft");
            Record("PASS native-inspection","all three native magnifier structures/confrontations and original dialogue effects; independent source panel verified separately; no guest factory or per-item network adapter");
            inspectionStep=99; return true;
        }
        return false;
    }
    [HarmonyPatch(typeof(InspectableHelper),nameof(InspectableHelper.OnConfrontSuccess))]
    private static class InspectionCommitOracle
    {
        private static void Postfix(GameItem gameItem)
        { if(Enabled && IsHostProduct && gameItem.IsTag(InspectionFixture)) { inspectionCommits.TryGetValue(gameItem,out int count); inspectionCommits[gameItem]=count+1; } }
    }
}
