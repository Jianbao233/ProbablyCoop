using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    // API/class-path fixture: generated options exercise both original window
    // types. This is not evidence that every individual production recipe runs.
    private int dynamicQaStep,qaChoiceCalls;
    private int responseRootsBefore;
    private double dynamicQaDeadline;
    private GameItem firstChoiceIcon;
    private Action<GameItem> hostChoiceCallback;
    private bool hostChoiceOpen,hostChoiceVerified;
    private const string ChoiceRole="qaDynamicChoices";
    private static GameItem ChoicePreview(string name)
    {
        var item=DirectoryMaster.Item("processed_meat");
        item.name=name; item.SetAllMayFalse(); item.EnableTag("CHOICE_PREVIEW_TAG");
        return item;
    }
    private void SeedDynamicChoices()
    {
        var overlay=new MachineryButton("QA recipes",()=>
        {
            qaMachine.EnableTag("QA_CHOICE_OPEN");
            OverlayHandler.current.ShowChooseItem(new List<GameItem>{ChoicePreview("Recipe A"),ChoicePreview("Recipe B")},
                "QA remote recipes",(Func<GameItem,bool>)(item=>
                {
                    qaChoiceCalls++;
                    Require(qaChoiceCalls<=2,"native dynamic callback repeated");
                    qaMachine.EnableTag(qaChoiceCalls==1 ? "QA_CHOICE_RETRY" : "QA_CHOICE_SELECTED");
                    return qaChoiceCalls==2;
                }),()=> { qaMachine.EnableTag("QA_CHOICE_CANCELLED"); SeedNestedEquipmentBay(); });
        });
        var response=new MachineryButton("QA responses",()=>
        {
            var readOnly=EmporiumEntry.PrepChoice(DirectoryMaster.Item("processed_meat"),"Read only",canActivate:false);
            var enabled=EmporiumEntry.PrepChoice(DirectoryMaster.Item("processed_meat"),"Execute choice",choiceAction:()=>
            {
                Require(!qaMachine.IsTag("QA_RESPONSE_SELECTED"),"response callback repeated");
                qaMachine.EnableTag("QA_RESPONSE_SELECTED");
                EmporiumEntry.Instance.HideResponseWindow();
            });
            EmporiumEntry.Instance.ShowResponseWindow(new StoreResponse { title="QA remote responses",closable=true,
                choices=new List<(GameItem,Action)>{readOnly,enabled} });
        });
        ((MachineryPanelRow)qaMachine.contentWindow.childElement).Add(new MachineryPanel("API choices",
            new MachineryStack(true).Add(overlay).Add(response),ChoiceRole));
    }
    private static GameGridScrollableInventory OverlayChoices()=>(GameGridScrollableInventory)AccessTools.Field(typeof(OverlayHandler),"itemChooseInventory").GetValue(OverlayHandler.current);
    private static PixelWindow OverlayChoiceWindow()=>(PixelWindow)AccessTools.Field(typeof(OverlayHandler),"itemChooseWindow").GetValue(OverlayHandler.current);
    private void CheckHostDynamicChoices()
    {
        if(!qaMachine.IsTag("QA_CHOICE_OPEN") || hostChoiceVerified) return;
        if(!hostChoiceOpen)
        {
            OverlayHandler.current.ShowChooseItem(new List<GameItem>{ChoicePreview("Host private view")},"QA host independent",
                (Action<GameItem>)(_=>throw new InvalidOperationException("guest executed host's callback")));
            hostChoiceCallback=RecipeSelectHandler.current.callback;
            hostChoiceOpen=true;
        }
        Require(OverlayChoiceWindow().IsVisible() && ReferenceEquals(hostChoiceCallback,RecipeSelectHandler.current.callback),
            "remote chooser replaced the host's original popup/callback");
        if(!qaMachine.IsTag("QA_CHOICE_CANCELLED")) return;
        Require(qaChoiceCalls==2 && qaMachine.IsTag("QA_RESPONSE_SELECTED"),"dynamic callback outcomes incomplete");
        OverlayHandler.current.HideChooseItem();
        hostChoiceVerified=true; qaMachine.EnableTag("QA_HOST_CHOICES_INDEPENDENT");
        Record("PASS host-choice-independence","original host overlay and callback survive remote overlay, retry, response selection and cancel");
    }
    private bool RunDynamicChoicesQa(ReplicaLink link,double now)
    {
        if(dynamicQaStep==8) return true;
        if(dynamicQaDeadline==0) dynamicQaDeadline=now+45;
        Require(now<dynamicQaDeadline,"dynamic original-window choice timed out at "+dynamicQaStep);
        if(QaWaiting(link)) return false;
        var machine=MachineReplica();
        var buttons=((MachineryStack)((MachineryPanelRow)machine.contentWindow.childElement).Panels.Single(p=>p.role==ChoiceRole).content)
            .elements.OfType<MachineryButton>().ToArray();
        void Click(int index)=>AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(buttons[index],null);
        switch(dynamicQaStep)
        {
            case 0: Click(0); dynamicQaStep++; break;
            case 1:
                if(!OverlayChoiceWindow().IsVisible() || OverlayChoices().childItems.Count!=2) break;
                firstChoiceIcon=OverlayChoices().childItems[0];
                Require(NativeLocalItemChooser.Owns((GameItemElement)firstChoiceIcon),"remote preview is executable shared item");
                NativeOffscreenCapture.Capture("qa-dynamic-recipe-choice");
                RecipeSelectHandler.current.callback(firstChoiceIcon); dynamicQaStep++; break;
            case 2:
                if(!machine.IsTag("QA_CHOICE_RETRY") || OverlayChoices().childItems.Count!=2 ||
                    ReferenceEquals(firstChoiceIcon,OverlayChoices().childItems[0])) break;
                RecipeSelectHandler.current.callback(OverlayChoices().childItems[1]); dynamicQaStep++; break;
            case 3:
                if(!machine.IsTag("QA_CHOICE_SELECTED") || OverlayChoiceWindow().IsVisible()) break;
                Record("PASS native-dynamic-recipe-choice","original chooser, remote callback retry uses new generation, selection committed twice as requested");
                responseRootsBefore=GraphHandler.windowGraphNodeRoots.Count;
                Click(1); dynamicQaStep++; break;
            case 4:
                if(!NativeLocalItemChooser.ResponseVisible) break;
                var icons=NativeLocalItemChooser.ResponseItems;
                Require(icons.Count==2 && !icons[0].CanActivateSlotItem() && icons[1].CanActivateSlotItem(),"native response ability parity missing");
                icons[0].ActivateSlotItem();
                Require(!QaWaiting(link),"read-only native response sent an operation");
                NativeOffscreenCapture.Capture("qa-dynamic-response-choice");
                icons[1].ActivateSlotItem(); dynamicQaStep++; break;
            case 5:
                if(!machine.IsTag("QA_RESPONSE_SELECTED") || NativeLocalItemChooser.ResponseVisible) break;
                Require(GraphHandler.windowGraphNodeRoots.Count==responseRootsBefore,"local response window leaked native graph roots");
                Record("PASS native-dynamic-response-choice","original response window preserves read-only/activation semantics; selected native callback runs once");
                Click(0); dynamicQaStep++; break;
            case 6:
                if(!OverlayChoiceWindow().IsVisible() || OverlayChoices().childItems.Count!=2) break;
                ((Action)AccessTools.Field(typeof(OverlayHandler),"currentCancelAction").GetValue(OverlayHandler.current))();
                dynamicQaStep++; break;
            case 7:
                if(!machine.IsTag("QA_HOST_CHOICES_INDEPENDENT")) break;
                Record("PASS native-dynamic-choice-cancel","original Cancel delivers one remote cancellation without touching host's original UI");
                dynamicQaStep=8; return true;
        }
        return false;
    }
}
