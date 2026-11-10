using System;
using System.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private GameItem qaMachine,qaWater;
    private int machineStage;
    private double machineNext,machineDeadline,waterStart;
    private bool hostMachineChecked;
    private Newtonsoft.Json.Linq.JObject emptyHeaterIntent;
    private ulong queuedRadioSerial;
    private int emptyHeaterMode;
    private bool staleHeaterSubmitted;
    private object[] machineStableParts;
    private void SeedMachine()
    {
        qaMachine=DirectoryMaster.Item("heating_plate");
        qaWater=DirectoryMaster.Item("large_bottled_water");
        WaterHelper.FillWithPureWater(qaWater);
        foreach(var item in new[]{qaMachine,qaWater})
        {
            GeneralHelper.SetItemOwned(item,true);
            var slot=EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(item);
            Require(slot?.IsValid()==true && slot.TryAcceptOnce()==item.unitCount,"machine QA fixture does not fit stock: "+item.identifier);
        }
        Require(WaterHelper.GetTotalVolume(qaWater)>0,"water fixture empty");
        SeedDynamicChoices();
        waterStart=qaWater.GetTagReadonly("CONTAINER_TEMP_TAG").GetDouble();
        Record("PASS machine-fixture","native heating_plate and large_bottled_water; no save");
    }
    private GameItem MachineReplica() => NativeWorld.Guest.Objects.Values.OfType<GameItem>().Single(i=>i.identifier=="heating_plate");
    private static GameSlotInventory Container(GameItem machine) => (GameSlotInventory)((MachineryIntake)((MachineryPanelRow)machine.contentWindow.childElement).Panels.Single(p=>p.role=="container").content).content;
    private static MachineryRadioList Modes(GameItem machine) => (MachineryRadioList)((MachineryPanelRow)machine.contentWindow.childElement).Panels.Single(p=>p.role=="control").content;
    private bool PrepareMachine(ReplicaLink link,double now)
    {
        if(machineStage==6) return true;
        if(!VerifyGuestTooltipReads(now)) return false;
        if(now<machineNext) return false;
        var machine=MachineReplica();
        if(machineStage==0)
        {
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine,new Vector2(200,250));
            Require(machine.contentWindow.IsVisible(),"guest original machine window did not open");
            machineStage++; machineNext=now+1; return false;
        }
        if(machineStage==1)
        {
            var water=NativeWorld.Guest.Objects.Values.OfType<GameItem>().Single(i=>i.identifier=="large_bottled_water");
            waterStart=water.GetTagReadonly("CONTAINER_TEMP_TAG").GetDouble();
            var slot=Container(machine).TryFindOneValidInventorySlot(water);
            Require(slot?.IsValid()==true,"original machine rejects water preview");
            emptyHeaterMode=Modes(machine).selected;
            var intent=NativeWorld.Guest.RadioIntent(Modes(machine),emptyHeaterMode==1 ? 2 : 1);
            Require(intent!=null,"missing original heater control before loading");
            emptyHeaterIntent=intent;
            slot.TryAcceptOnce(); Require(QaWaiting(link),"native slot did not send move request");
            machineStage++; machineDeadline=now+12; return false;
        }
        if(machineStage==2 || machineStage==4)
        {
            if(QaWaiting(link) || machineStage==4 && (QaCompleted(link)<queuedRadioSerial+1 ||
                machine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()!=2))
            { Require(now<machineDeadline,"machine command timed out"); return false; }
            Require(QaResult(link)==(ushort)ShopResult.Applied,"machine command rejected: "+QaResult(link)+" phase="+machineStage);
            if(machineStage==4)
            {
                Require(QaCompleted(link)==queuedRadioSerial+1 && Modes(machine).selected==2,"queued choice duplicated or drifted");
                Record("PASS queued-machine-choice","two quick original clicks, exactly two accepted requests; final native host mode projected");
            }
            machineStage++; machineNext=now+1; return false;
        }
        if(machineStage==3)
        {
            Require(Container(machine).childItem?.identifier=="large_bottled_water","water move missing from authoritative nested slot");
            Require(machine.contentWindow.IsVisible(),"snapshot closed independent guest window");
            if(!staleHeaterSubmitted)
            {
                int desired=(int)emptyHeaterIntent["index"];
                int previousRule=machine.GetTagReadonly("HEATING_PLATE_STATE").GetInt();
                NativeWorld.Guest.SubmitRadioIntent(Modes(machine),emptyHeaterIntent,desired);
                Require(QaWaiting(link) && Modes(machine).selected==desired &&
                    machine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()==previousRule,
                    "pre-load machine choice failed to preview without applying the rule");
                staleHeaterSubmitted=true; machineDeadline=now+12; return false;
            }
            if(QaWaiting(link)) { Require(now<machineDeadline,"old equipment control did not receive a reply"); return false; }
            Require(QaResult(link)==(ushort)ShopResult.Stale && Modes(machine).selected==emptyHeaterMode,
                "old control proof was accepted after a different item entered the same machine");
            Record("PASS stale-equipment-contents","pre-load radio preview rejected after native water transfer; original selection restored");
            Modes(machine).Get(emptyHeaterMode==1 ? 0 : 1).SetOn(true,true);
            Require(QaWaiting(link),"native radio did not send control request");
            queuedRadioSerial=QaIssued(link);
            Modes(machine).Get(2).SetOn(true,true);
            Require(QaIssued(link)==queuedRadioSerial && Modes(machine).selected==2,"quick second choice did not stay local until confirmation");
            machineStage++; machineDeadline=now+12; return false;
        }
        Require(machine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()==2,"native heating mode not replicated");
        machineStableParts=StableMachineParts(machine);
        Record("PASS machine-native-input","original item window, nested slot drop and radio -> host callbacks; guest window remains independently open");
        machineStage=6;
        return true;
    }
    private void CheckHostMachine()
    {
        if(qaMachine==null || hostMachineChecked || qaWater.parentInventory!=Container(qaMachine) ||
            qaMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()!=2) return;
        Require(!qaMachine.contentWindow.IsVisible(),"guest machine UI unexpectedly opened host UI");
        if(qaWater.GetTagReadonly("CONTAINER_TEMP_TAG").GetDouble()<=waterStart) return;
        hostMachineChecked=true;
        Record("PASS host-independent-machine","native machine heating while host window closed");
    }
    private void VerifyMachineProgress()
    {
        var machine=MachineReplica(); var water=Container(machine).childItem;
        Require(water!=null && water.GetTagReadonly("CONTAINER_TEMP_TAG").GetDouble()>waterStart,"native machine did not keep producing through customer trade");
        Require(machine.contentWindow.IsVisible(),"customer trade closed guest machine window");
        Require(guestPointerChecked,"native pointer context was not exercised");
        var currentParts=StableMachineParts(machine);
        string changedParts=machineStableParts==null ? "missing baseline" : string.Join(",",
            Enumerable.Range(0,Math.Min(currentParts.Length,machineStableParts.Length))
            .Where(i=>!ReferenceEquals(currentParts[i],machineStableParts[i]))
            .Select(i=>$"{i}:{machineStableParts[i]?.GetType().Name}->{currentParts[i]?.GetType().Name}"));
        Require(machineStableParts!=null && currentParts.Length==machineStableParts.Length &&
            currentParts.Zip(machineStableParts,ReferenceEquals).All(same=>same),
            "nested production readout rebuilt machine shape/types/features/window or native layout/render identities: "+changedParts);
        Record("PASS selective-machine-projection","nested water heating and trade preserve machine data plus original row/panel/text/intake/control/render identities");
        var text=(MachineryText)((MachineryPanelRow)machine.contentWindow.childElement).Panels.Single(p=>p.role=="temp").content;
        Require(text.GetText()!=LocHelper.GetLocalizedMechanic("mech_heating_plate_temp_na"),"remote-only viewer has unavailable temperature");
        Record("PASS machine-trade-parallel",$"water {waterStart}->{water.GetTagReadonly("CONTAINER_TEMP_TAG").GetDouble()}; native readout={text.GetText()}");
        NativeOffscreenCapture.Capture("qa-machine");
    }
    private static object[] StableMachineParts(GameItem machine)
    {
        // This interval deliberately includes inventory sort and undo. Those
        // operations replace modifiedShape, even when undo restores equal
        // coordinates (verified by RunSortQa). Only the base shape and stable
        // native UI/data objects are expected to retain reference identity.
        var parts=new System.Collections.Generic.List<object> {
            machine.shape,machine.itemTypes,machine.itemFeatures,machine.contentWindow };
        var row=(MachineryPanelRow)machine.contentWindow.childElement;
        parts.Add(row); parts.Add(row.handler);
        foreach(var panel in row.Panels)
        {
            parts.Add(panel); parts.Add(panel.handler); parts.Add(panel.label); parts.Add(panel.label?.handler);
            parts.Add(panel.content); parts.Add(panel.content?.handler);
            if(panel.content is MachineryIntake intake) { parts.Add(intake.content); parts.Add(intake.content?.handler); }
        }
        return parts.ToArray();
    }
}
