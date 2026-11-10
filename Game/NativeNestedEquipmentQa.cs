using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int nestedEquipmentStep;
    private double nestedEquipmentDeadline;
    private GameItem nestedEquipment;
    private GameInventory nestedEquipmentOrigin;
    private GridShape nestedEquipmentShape;
    private uint nestedEquipmentIdentity;
    private const string NestedBayTag="QA_NESTED_EQUIPMENT_BAY";
    private GameItem qaNestedBay;
    private bool observedNestedEquipment;
    private void SeedNestedEquipmentBay()
    {
        qaNestedBay=DirectoryMaster.Item("machine_bay");
        qaNestedBay.EnableTag(NestedBayTag); GeneralHelper.SetItemOwned(qaNestedBay);
        var slot=EmporiumEntry.Instance.backInvinvElement.TryFindOneValidInventorySlot(qaNestedBay);
        Require(slot?.IsValid()==true && slot.TryAcceptOnce()==qaNestedBay.unitCount,"native QA machine bay does not fit free counter");
    }
    private void CheckHostNestedEquipment()
    {
        if(qaNestedBay==null) return;
        if(GraphUtils.FindAllChildrenType<GameItem>(qaNestedBay).Contains(qaMachine)) observedNestedEquipment=true;
        else if(observedNestedEquipment)
        {
            Require(GraphUtils.FindAllChildrenType<GameItem>(qaNestedBay).Count==0,"native QA machine bay retained unexpected items");
            qaNestedBay.Destroy(); qaNestedBay=null;
        }
    }
    private bool RunNestedEquipmentQa(ReplicaLink link,double now)
    {
        if(nestedEquipmentStep==5) return true;
        if(nestedEquipmentDeadline==0) nestedEquipmentDeadline=now+40;
        Require(now<nestedEquipmentDeadline,"nested original machine timed out at "+nestedEquipmentStep);
        if(NativeWorld.Guest.HasPendingWork) return false;
        var bay=NativeWorld.Guest.Objects.Values.OfType<GameItem>().FirstOrDefault(item=>item.IsTag(NestedBayTag) && item.parentInventory!=null);
        switch(nestedEquipmentStep)
        {
            case 0:
                if(bay==null) return false;
                nestedEquipment=MachineReplica();
                nestedEquipmentOrigin=nestedEquipment.parentInventory;
                nestedEquipmentShape=nestedEquipment.modifiedShape.Clone();
                nestedEquipmentIdentity=NativeWorld.Guest.PointerIdentity(nestedEquipment);
                ReleaseWholeIntoContainer((GameItemElement)nestedEquipment,bay);
                nestedEquipmentStep++; break;
            case 1:
                Require(bay!=null && GraphUtils.FindAllChildrenType<GameItem>(bay).Contains(nestedEquipment),"machine was not accepted into original machine bay");
                Require(NativeWorld.Guest.TryData(Modes(nestedEquipment),out JObject radio) &&
                    (uint)radio["owner"]==nestedEquipmentIdentity,"nested machine controls were assigned to its outer machine bay");
                Modes(nestedEquipment).Select(1,true);
                nestedEquipmentStep++; break;
            case 2:
                if(nestedEquipment.GetTagReadonly("HEATING_PLATE_STATE").GetInt()!=1) return false;
                Modes(nestedEquipment).Select(2,true);
                nestedEquipmentStep++; break;
            case 3:
                if(nestedEquipment.GetTagReadonly("HEATING_PLATE_STATE").GetInt()!=2) return false;
                var slot=nestedEquipmentOrigin.TryInventorySlot(nestedEquipment,1,nestedEquipmentShape,nestedEquipment.modifiedState);
                Require(slot?.IsValid()==true,"nested machine source cell cannot be restored");
                ReleaseAtNativeSlot((GameItemElement)nestedEquipment,slot);
                nestedEquipmentStep++; break;
            case 4:
                if(bay!=null) return false; // Host retires only this disposable fixture after the original item returns.
                Require(ReferenceEquals(nestedEquipment.parentInventory,nestedEquipmentOrigin) &&
                    NativeWorld.Guest.PointerIdentity(nestedEquipment)==nestedEquipmentIdentity &&
                    Container(nestedEquipment).childItem!=null,
                    "nested machine roundtrip changed source/container contents or identity");
                // Vanilla pickup closes the item's content window. Reopen it
                // with the original input path before the following hover test;
                // it must not raycast old coordinates behind a hidden window.
                ItemMouseDoubleClickHandler.current.OpenContentAction(nestedEquipment,new UnityEngine.Vector2(200,250));
                Require(nestedEquipment.contentWindow.IsVisible(),"restored machine cannot reopen its original window");
                Record("PASS native-nested-equipment","loaded original heating plate entered original machine bay; two native radio callbacks used the machine identity, not the bay; original machine and contents restored; disposable bay retired");
                nestedEquipmentStep=5; return true;
        }
        return false;
    }
}
