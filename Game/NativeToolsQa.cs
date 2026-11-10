using System;
using System.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int toolStep;
    private double toolNext,toolDeadline;
    private GameItem toolSource,toolTarget;
    private LabelerElement labelRow;
    private ItemFeature labelFeature;
    private string expectedLabel,savedLabel;
    private static byte[] firstLabelCommand;
    internal static void RememberFirstLabelCommand(Newtonsoft.Json.Linq.JObject command)
    {
        firstLabelCommand??=System.Text.Encoding.UTF8.GetBytes(command.ToString(Newtonsoft.Json.Formatting.None));
    }
    private uint consumedEntity;
    private void SeedTools()
    {
        if(!HealthData.IsAlcoholAddicted()) PlayerStore.Instance.startingPerks.Add(new StartingPerk { id="alcohol_addiction" });
        foreach(string id in new[]{"screwdriver","system_module_ruined","labeler_blue","labeler","small_bottled_water","red_beer","livestock_feed_dispenser"})
        {
            var item=DirectoryMaster.Item(id); GeneralHelper.SetItemOwned(item,true);
            if(id=="small_bottled_water") WaterHelper.FillWithPureWater(item);
            var slot=EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(item);
            Require(slot?.IsValid()==true && slot.TryAcceptOnce()==item.unitCount,"tool fixture does not fit: "+id);
        }
        Record("FIXTURE item-tools","native screwdriver, ruined module, blue labeler and beer; disposable host has alcohol_addiction required by vanilla consumption");
    }
    private bool partnerToolFixture;
    private void CheckActorToolFixture()
    {
        var actors=NativeActorSession.Current;
        if(partnerToolFixture || actors==null) return;
        actors.Store.modData.TryGetValue(NativeActorPartnerSetup.SaveKey,out var receipt);
        if(!NativeActorPartnerSetup.IsComplete(receipt,actors.Run.Run)) return;
        using var personal=actors.Enter(NativeActorRun.Slot.Partner);
        if(!HealthData.IsAlcoholAddicted()) actors.Run.Partner.startingPerks.Add(new StartingPerk { id="alcohol_addiction" });
        partnerToolFixture=true;
        Record("FIXTURE partner-tools","after real partner creation, explicitly add alcohol_addiction only to the disposable partner for native beer activation");
    }
    private GameItem ToolItem(string id) => NativeWorld.Guest.Objects.Values.OfType<GameItem>().First(i=>i.identifier==id && i.parentInventory!=null);
    private bool RunNativeTools(ReplicaLink link,double now)
    {
        if(toolStep==9) return true;
        if(now<toolNext) return false;
        if(toolStep==0 || toolStep==3)
        {
            toolSource=ToolItem(toolStep==0 ? "screwdriver" : "labeler_blue");
            toolTarget=ToolItem(toolStep==0 ? "system_module_ruined" : "heating_plate");
            Require(toolSource.MaySelectSlotItem() && toolSource.CanSelectSlotItem(),"native tool ability projection missing");
            ItemSelectHandler.current.TrySelectItem(toolSource);
            toolStep++; toolDeadline=now+12; return false;
        }
        if(toolStep==1 || toolStep==4)
        {
            Require(now<toolDeadline,"native tool target queries did not arrive");
            if(!toolSource.MayTarget(toolTarget) || !toolSource.CanTarget(toolTarget)) return false;
            toolSource.Target(toolTarget);
            ItemSelectHandler.current.EndSelect();
            if(toolStep==1) Require(QaWaiting(link),"native tool target did not send intent");
            else
            {
                Require(LabelerBlueUIManager.Instance.panel.activeSelf,"native guest label editor did not open");
                LabelerBlueUIManager.Instance.inputField.SetTextWithoutNotify("合作生产台");
                LabelerBlueUIManager.Instance.inputField.onEndEdit.Invoke("合作生产台");
                Require(QaWaiting(link),"native label edit callback did not send intent");
            }
            toolStep++; toolDeadline=now+12; return false;
        }
        if(toolStep==2 || toolStep==5 || toolStep==7)
        {
            Require(now<toolDeadline,"native item command reply timed out");
            if(QaWaiting(link)) return false;
            Require(QaResult(link)==(ushort)ShopResult.Applied,"native tool/editor/activation result="+QaResult(link)+" step="+toolStep);
            Record("TRACE item-reply","step="+toolStep+" revision="+link.Revision+" consumed="+consumedEntity);
            toolStep=toolStep==2 ? 10 : toolStep==5 ? 11 : 8; toolNext=now+1; return false;
        }
        if(toolStep==10)
        {
            Require(!NativeWorld.Guest.Objects.Values.OfType<GameItem>().Any(i=>i.identifier=="system_module_ruined" && i.parentInventory!=null),"native dismantle retained source");
            foreach(string id in new[]{"common_electronic","printer_plastic","junk"}) Require(ToolItem(id)!=null,"native dismantle output missing: "+id);
            Record("PASS native-tool-target","original tool selection and target -> host native dismantle; source removed and all outputs replicated");
            toolStep=3; return false;
        }
        if(toolStep==11)
        {
            var renamed=(GameItemElement)ToolItem("heating_plate");
            Require(GeneralHelper.GetCustomName(renamed)=="合作生产台","native label edit did not update shared name");
            Require(renamed.GetTooltip().nakedText.Contains("合作生产台") && renamed.GetTooltipAdvanced().nakedText.Contains("合作生产台"),
                "first visible original tooltip after label mutation still showed the previous item name");
            Record("PASS tooltip-after-mutation","ordinary and advanced native tooltip match the authoritative rename in the applied projection");
            Require(LabelerBlueUIManager.Instance.panel.activeSelf,"snapshot closed local label editor");
            LabelerBlueUIManager.Instance.CloseUI();
            Record("PASS native-label-editor","actor-local original label panel + onEndEdit -> host name; independent machine window retained");
            toolSource=ToolItem("labeler");
            toolTarget=ToolItem("small_bottled_water");
            ItemSelectHandler.current.TrySelectItem(toolSource);
            toolStep=12; toolDeadline=now+12; return false;
        }
        if(toolStep is >=12 and <=18)
            return TickLabelDropdown(link,now);
        if(toolStep==6)
        {
            Require(!QaWaiting(link),"closing original editor unexpectedly submitted a duplicate edit");
            Require(HealthData.IsAlcoholAddicted(),"starting perk query projection missing");
            toolSource=ToolItem("red_beer");
            Require(toolSource.MayActivateSlotItem() && toolSource.CanActivateSlotItem(),"native consumable ability missing");
            Require(NativeWorld.Guest.TryData(toolSource,out var consumable),"consumable has no network identity"); consumedEntity=(uint)consumable["entity"];
            toolSource.ActivateSlotItem(); Require(QaWaiting(link),"native item activation did not issue intent");
            toolStep=7; toolDeadline=now+12; return false;
        }
        if(NativeWorld.Guest.Objects.TryGetValue(consumedEntity,out var remaining) && remaining is GameItem { parentInventory: not null })
        { Require(now<toolDeadline,"consumed entity still present after reply and snapshot deadline: "+consumedEntity+" revision="+link.Revision); return false; }
        Require(ToolItem("empty_beer_bottle")!=null,"native consumable replacement missing");
        Record("PASS native-item-activation","consumable transformed once by host; native empty bottle replicated");
        toolStep=9; return true;
    }
    private bool TickLabelDropdown(ReplicaLink link,double now)
    {
        Require(now<toolDeadline,"two native label changes timed out at step "+toolStep);
        var ui=LabelerUIManager.Instance;
        if(toolStep==18)
        {
            if(QaWaiting(link)) return false;
            Require(QaResult(link)==(ushort)ShopResult.Stale && labelFeature.fakeCondition.identifier==savedLabel,"old label intent changed a subsequent edit");
            Record("PASS stale-native-label","old edit after the same feature changed rejected; saved condition retained");
            toolStep=6; toolNext=now+0.25; return false;
        }
        if(toolStep==12)
        {
            if(!toolSource.MayTarget(toolTarget) || !toolSource.CanTarget(toolTarget)) return false;
            toolSource.Target(toolTarget);
            ItemSelectHandler.current.EndSelect();
            Require(ui.panel.activeSelf,"native condition label panel did not open");
            labelFeature=toolTarget.FindItemFeatureByCategory("CATEGORY_WATER_PURITY");
            labelRow=ui.verticalLayoutGroup.GetComponentsInChildren<LabelerElement>().Single(e=>e.GetCategory()=="CATEGORY_WATER_PURITY");
        }
        else if(toolStep==15)
        {
            // The native window saved its last labels on close. Leave it open
            // with a different selection, then repeat the saved choice.
            labelRow=ui.verticalLayoutGroup.GetComponentsInChildren<LabelerElement>().Single(e=>e.GetCategory()=="CATEGORY_WATER_PURITY");
        }
        else
        {
            if(QaWaiting(link)) return false;
            Require(QaResult(link)==(ushort)ShopResult.Applied,"native label change rejected at step "+toolStep+": "+QaResult(link));
            var live=toolTarget.FindItemFeatureByCategory("CATEGORY_WATER_PURITY");
            if(live?.fakeCondition?.identifier!=expectedLabel) return false;
            Require(ReferenceEquals(live,labelFeature) && ui.panel.activeSelf,"projection replaced the feature of an open native label row");
            if(toolStep==14)
            {
                savedLabel=expectedLabel;
                ui.CloseUI();
                Record("PASS native-condition-labels","two successive edits on the same original dropdown and feature, both host results applied");
                ui.OpenUI(toolTarget);
                toolStep=15; toolNext=now+0.15; toolDeadline=now+12; return false;
            }
            if(toolStep==16)
            {
                Require(ui.repeatButton.activeSelf,"original Repeat button is unavailable");
                ui.Repeat(); Require(QaWaiting(link),"native Repeat did not submit saved labels");
                expectedLabel=savedLabel; toolStep=17; toolDeadline=now+12; return false;
            }
            if(toolStep==17)
            {
                Require(labelRow.GetSelectedConditionIdentifier()==savedLabel &&
                    ReferenceEquals(labelRow,ui.verticalLayoutGroup.GetComponentsInChildren<LabelerElement>().Single(e=>e.GetCategory()=="CATEGORY_WATER_PURITY")),
                    "original dropdown did not follow host Repeat on the same row");
                ui.CloseUI();
                Require(!QaWaiting(link),"authoritative dropdown refresh emitted a new request");
                Record("PASS native-repeat-label-ui","host restored saved condition; open original dropdown updated in place without resubmitting");
                Require(firstLabelCommand!=null && link.Submit(firstLabelCommand,now),"could not replay obsolete label intent");
                toolStep=18; toolDeadline=now+12; return false;
            }
        }
        int choice=(labelRow.dropdown.value+1)%labelRow.dropdown.options.Count;
        labelRow.dropdown.value=choice;
        expectedLabel=labelRow.GetSelectedConditionIdentifier();
        Require(QaWaiting(link),"native dropdown did not submit a label change");
        toolStep++; toolDeadline=now+12; return false;
    }
}
