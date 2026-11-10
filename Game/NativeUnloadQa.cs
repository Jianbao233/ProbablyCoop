using System.Linq;
using System.Collections.Generic;
using HarmonyLib;
using ProbablyCoop.Core;

namespace ProbablyCoop;
internal sealed partial class NativeBackgroundQa
{
    private int unloadStep;
    private double unloadNext,unloadUntil;
    private GameItem unloadBag,unloadPinned,unloadFree;
    private const string PortionTag="PROBABLY_COOP_QA_PORTION";
    private uint portionIdentity;
    private GameItemElement hostTestBag;
    private int hostHandStage, hostOriginalMode, guestIndependentMode;
    private MachineryRadioList guestObservedModes;
    private int guestOriginalMode;
    private bool hostOptionChanged,wasDisabled,wasHidden;
    private string originalDescription;
    private const string RadioQaDescription="合伙测试：当前不可用";
    private ulong hostHandStartRequest;
    private bool refusedGuestPickup;
    private double hostHandDeadline;
    private int otherHandStage,otherHandMode;
    private double otherHandSince;
    private void SeedUnload()
    {
        var bag=DirectoryMaster.Item("satchel"); GeneralHelper.SetItemOwned(bag,true);
        var content=GraphUtils.FindChildrenType<GameInventory>(bag.contentWindow).First();
        for(int i=0;i<2;i++)
        {
            var item=DirectoryMaster.Item("toilet_paper"); GeneralHelper.SetItemOwned(item,true);
            Require(GraphUtils.TryAcceptAll(content,item)==0,"unload fixture does not fit bag");
        }
        Require(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.invElement,bag)==0,"unload fixture does not fit stock");
        // Reproduce the content-mod entry blocker on a real network item.
        var contentPanel=new PixelWindow(96,28,true,title:"QA Content");
        contentPanel.Attach(new AdvButtonElement(text:"QA mod action",width:84,height:18,
            callback:()=>throw new System.InvalidOperationException("QA content callback ran during layout replication"))
            .SetDescription("External content action"));
        bag.SetExamineWindow(contentPanel);
        hostTestBag=(GameItemElement)bag;
        Require(DirectoryMaster.Has<GameItem>("small_pistol_ammo"),"missing native ammunition fixture blueprint");
        var portion=DirectoryMaster.Item("small_pistol_ammo");
        GeneralHelper.SetItemOwned(portion,true);
        portion.EnableTag(PortionTag);
        Require(TryPlaceInEmptyCell(portion,EmporiumEntry.Instance.invElement),"partial stack fixture does not fit stock");
        // Exercise the native split path with a controlled starting quantity.
        // Its ordinary drop table need not generate this three-unit pile.
        portion.SetStack(3);
        Record("FIXTURE partial-drag","registered ammunition placed in a real free cell; QA sets its starting quantity to three");
        Record("FIXTURE unload","native satchel with two owned toilet paper items; QA only");
    }
    private bool RunUnloadQa(ReplicaLink link,double now)
    {
        if(unloadStep==9) return true;
        if(now<unloadNext || link.HasUnacknowledgedRequests) return false;
        if(unloadStep==0)
        {
            unloadBag=EmporiumEntry.Instance.invElement.childItems.Single(i=>i.identifier=="satchel");
            var items=GraphUtils.FindAllChildrenType<GameItem>(unloadBag).ToArray();
            Require(items.Length==2,"unload fixture projection missing"); unloadPinned=items[0]; unloadFree=items[1];
            TriggerNativeItemMenu(unloadPinned,"buttonPin"); Require(link.HasUnacknowledgedRequests,"native pin menu emitted no request");
            unloadStep=1; unloadUntil=now+30; return false;
        }
        Require(now<unloadUntil,"native pin/unload timeout");
        if(unloadStep==8)
        {
            Require(unloadPinned.parentInventory==unloadBag.parentInventory && unloadFree.parentInventory==unloadBag.parentInventory &&
                !GraphUtils.FindAllChildrenType<GameItem>(unloadBag).Any() && unloadPinned.unitCount+unloadFree.unitCount==2,"second unload lost, duplicated or retained contents");
            Record("PASS native-context-pin-unload","original right-click pin/unpin/unload callbacks; host skips pinned item, then unloads exactly two items without duplication");
            var machine=MachineReplica();
            if(!machine.contentWindow.IsVisible())
                ItemMouseDoubleClickHandler.current.OpenContentAction(machine,new UnityEngine.Vector2(240,260));
            guestObservedModes=Modes(machine);
            guestOriginalMode=machine.GetTagReadonly("HEATING_PLATE_STATE").GetInt();
            guestIndependentMode=guestOriginalMode==1 ? 0 : 1;
            guestObservedModes.Get(guestIndependentMode).SetOn(true,true);
            Require(link.HasUnacknowledgedRequests,"independent original radio did not emit a request");
            unloadStep=20; unloadNext=now+0.7; return false;
        }
        if(unloadStep==20)
        {
            Require(QaLatestResult(link)==(ushort)ShopResult.Applied,"independent machine blocked by host holding a different item");
            var machine=MachineReplica();
            if(machine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()!=guestIndependentMode) return false;
            Require(ReferenceEquals(guestObservedModes,Modes(machine)) && machine.contentWindow.IsVisible(),
                "radio update replaced original open machine control");
            var oldOption=guestObservedModes.Get(guestOriginalMode);
            if(oldOption.description!=RadioQaDescription) return false;
            Require(oldOption.isDisabled && oldOption.isHidden && guestObservedModes.selected==guestIndependentMode,
                "native radio option presentation differs from host");
            Record("PASS native-radio-in-place","selected, disabled, hidden and description projected on original open native control");
            NativeOffscreenCapture.Capture("qa-concurrent-production");
            var drag=ItemMouseDragHandler.current;
            AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,unloadBag);
            AccessTools.Method(typeof(ItemMouseDragHandler),"StartDrag").Invoke(drag,null);
            Require(drag.IsDraggingItem && drag.GetCurrentItem()==unloadBag,"guest pickup preview did not start");
            unloadStep=22; unloadNext=now+0.5; return false;
        }
        if(unloadStep==22)
        {
            if(ItemMouseDragHandler.current.IsDraggingItem) return false;
            Require(QaLatestResult(link)==(ushort)ShopResult.Busy && unloadBag.parentInventory==EmporiumEntry.Instance.invElement &&
                !GraphUtils.FindAllChildrenType<GameItem>(unloadBag).Any(),"losing original guest pickup affected the host-held entity");
            Record("PASS simultaneous-native-pickup","original guest pickup was refused and its preview retired while the native host still held the same bag");
            ReleaseIntoBag(link,unloadFree,1);
            unloadStep=21; unloadNext=now+0.7; return false;
        }
        if(unloadStep==21)
        {
            Require(QaLatestResult(link)==(ushort)ShopResult.Busy && unloadFree.parentInventory==unloadBag.parentInventory &&
                !GraphUtils.FindAllChildrenType<GameItem>(unloadBag).Any(),"host-held target did not refuse deposit without mutation");
            Record("PASS concurrent-native-hands","guest radio committed while host held bag; deposit into held bag refused, exact item retained");
            ReleaseIntoBag(link,unloadFree,1);
            unloadStep=10; unloadNext=now+0.6; return false;
        }
        if(unloadStep==10)
        {
            Require(QaLatestResult(link)==(ushort)ShopResult.Applied,"native container drop rejected: "+QaLatestResult(link));
            var contents=GraphUtils.FindAllChildrenType<GameItem>(unloadBag);
            if(!contents.Contains(unloadFree)) return false;
            Require(contents.Count==1 && unloadFree.unitCount==1 && unloadPinned.parentInventory==unloadBag.parentInventory,
                "native drop changed an independent item or quantity");
            Record("PASS native-container-drop","original StartDrag/EndDrag into satchel; host inserted exact entity once; local preview preserved ownership until reply");
            ItemMouseDoubleClickHandler.current.OpenContentAction(unloadBag,new UnityEngine.Vector2(360,240));
            Require(unloadBag.contentWindow.IsVisible(),"original satchel window did not open after deposit");
            NativeOffscreenCapture.Capture("qa-container-deposit");
            unloadBag.contentWindow.Hide();
            TriggerNativeItemMenu(unloadBag,"buttonUnload");
            Require(link.HasUnacknowledgedRequests,"native unload after container deposit emitted no intent");
            unloadStep=11; unloadNext=now+0.6; return false;
        }
        if(unloadStep==11)
        {
            Require(QaLatestResult(link)==(ushort)ShopResult.Applied,"follow-up unload rejected");
            if(unloadFree.parentInventory!=unloadBag.parentInventory) return false;
            Require(!GraphUtils.FindAllChildrenType<GameItem>(unloadBag).Any(),"follow-up unload retained contents");
            var portion=EmporiumEntry.Instance.invElement.childItems.Single(i=>i.IsTag(PortionTag));
            portionIdentity=NativeWorld.Guest.PointerIdentity(portion);
            Require(portion.unitCount==3 && portionIdentity!=0,"incomplete source for partial drop");
            ReleaseIntoBag(link,portion,1);
            unloadStep=12; unloadNext=now+0.6; return false;
        }
        if(unloadStep==12)
        {
            Require(QaLatestResult(link)==(ushort)ShopResult.Applied,"partial native drop rejected: "+QaLatestResult(link));
            var placed=GraphUtils.FindAllChildrenType<GameItem>(unloadBag).SingleOrDefault();
            if(placed==null) return false;
            var remaining=EmporiumEntry.Instance.invElement.childItems.Single(i=>i.IsTag(PortionTag));
            uint placedId=NativeWorld.Guest.PointerIdentity(placed), remainingId=NativeWorld.Guest.PointerIdentity(remaining);
            Require(remaining.unitCount==2 && placed.unitCount==1 && placed.identifier==remaining.identifier &&
                placedId!=0 && remainingId!=0 && placedId!=remainingId && (placedId==portionIdentity || remainingId==portionIdentity),
                "partial drop failed native identity and quantity conservation");
            Record("PASS native-partial-container-drop","original scroll 3->1 and button release; two left in stock, one in satchel, two distinct real entities");
            var drag=ItemMouseDragHandler.current;
            AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,unloadBag);
            AccessTools.Method(typeof(ItemMouseDragHandler),"StartDrag").Invoke(drag,null);
            Require(drag.IsDraggingItem && drag.GetCurrentItem()==unloadBag,"original guest hold did not start");
            otherHandMode=MachineReplica().GetTagReadonly("HEATING_PLATE_STATE").GetInt();
            unloadUntil=now+38; unloadStep=13; return false;
        }
        if(unloadStep==13)
        {
            var drag=ItemMouseDragHandler.current;
            Require(drag.IsDraggingItem && drag.GetCurrentItem()==unloadBag,"guest lost original preview while holding");
            if(MachineReplica().GetTagReadonly("HEATING_PLATE_STATE").GetInt()==otherHandMode) return false;
            NativeOffscreenCapture.Capture("qa-guest-held-bag");
            AccessTools.Method(typeof(ItemMouseDragHandler),"AbortDrag").Invoke(drag,null);
            ((GameItemElement)unloadBag).ToggleFakeBackground(false);
            unloadBag.Validate();
            unloadStep=14; return false;
        }
        if(unloadStep==14)
        {
            if(MachineReplica().GetTagReadonly("HEATING_PLATE_STATE").GetInt()!=otherHandMode) return false;
            Require(unloadBag.parentInventory==EmporiumEntry.Instance.invElement &&
                GraphUtils.FindAllChildrenType<GameItem>(unloadBag).Single().unitCount==1,"cancelled guest reservation altered inventory");
            Record("PASS native-guest-hand-release","guest kept original bag preview through renewal and unrelated host production; cancellation retained bag and contents");
            unloadStep=9; return true;
        }
        if(unloadStep%2==1)
        {
            Require(QaLatestResult(link)==(ushort)ShopResult.Applied,"native pin/unload rejected: "+QaLatestResult(link));
            unloadStep++; unloadNext=now+0.8; return false;
        }
        if(unloadStep==2)
        {
            Require(ItemPinHelper.IsPinned(unloadPinned),"pin was not projected");
            TriggerNativeItemMenu(unloadBag,"buttonUnload"); Require(link.HasUnacknowledgedRequests,"native unload menu emitted no request"); unloadStep=3; return false;
        }
        if(unloadStep==4)
        {
            Require(unloadFree.parentInventory==unloadBag.parentInventory && unloadPinned.parentInventory!=unloadBag.parentInventory && ItemPinHelper.IsPinned(unloadPinned),"unload failed to retain pinned item");
            TriggerNativeItemMenu(unloadPinned,"buttonPin"); Require(link.HasUnacknowledgedRequests,"native unpin menu emitted no request"); unloadStep=5; return false;
        }
        if(unloadStep==6)
        {
            Require(!ItemPinHelper.IsPinned(unloadPinned),"unpin was not projected");
            TriggerNativeItemMenu(unloadBag,"buttonUnload"); Require(link.HasUnacknowledgedRequests,"second native unload emitted no request"); unloadStep=7; return false;
        }
        return false;
    }
    private void CheckHostContainerHand(ReplicaLink link,double now)
    {
        if(hostTestBag==null || link==null) return;
        if(hostHandStage==2) { CheckPartnerPickup(now); return; }
        var drag=ItemMouseDragHandler.current;
        if(hostHandStage==0)
        {
            // The second native unload empties this bag before the guest
            // observes the new state and starts its independent radio action.
            if(GraphUtils.FindAllChildrenType<GameItem>(hostTestBag).Count!=0) return;
            var calendar=AdvCalendarUIManager.Instance;
            if(calendar.UIPanel.gameObject.activeInHierarchy) calendar.CloseUI();
            var morning=StartOfDayUIManager.Instance;
            if(morning.UIPanel.activeInHierarchy) morning.OnStartDayButtonClicked();
            Require(!calendar.UIPanel.gameObject.activeInHierarchy && !morning.UIPanel.activeInHierarchy,
                "host native inventory obscured by morning report");
            hostOriginalMode=qaMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt();
            hostHandStartRequest=QaCompleted(link);
            AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,hostTestBag);
            AccessTools.Method(typeof(ItemMouseDragHandler),"StartDrag").Invoke(drag,null);
            Require(drag.IsDraggingItem && drag.GetCurrentItem()==hostTestBag,"host native pickup did not start");
            hostHandDeadline=now+20; hostHandStage=1;
            return;
        }
        Require(now<hostHandDeadline && drag.IsDraggingItem && drag.GetCurrentItem()==hostTestBag,
            "host native hand ended before the concurrent operations");
        if(!hostOptionChanged && qaMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()!=hostOriginalMode)
        {
            var option=Modes(qaMachine).Get(hostOriginalMode);
            wasDisabled=option.isDisabled; wasHidden=option.isHidden; originalDescription=option.description;
            option.SetDescription(RadioQaDescription);
            option.SetDisabled(true); option.SetHidden(true);
            hostOptionChanged=true;
        }
        if(QaCompleted(link)<=hostHandStartRequest || link.CompletedResult!=(ushort)ShopResult.Busy) return;
        if(!refusedGuestPickup)
        {
            refusedGuestPickup=true; hostHandStartRequest=QaCompleted(link);
            return;
        }
        Require(hostOriginalMode!=qaMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt() &&
            GraphUtils.FindAllChildrenType<GameItem>(hostTestBag).Count==0,"independent machine result or held item changed incorrectly");
        var originalInventory=hostTestBag.parentInventory;
        NativeOffscreenCapture.Capture("qa-host-held-bag");
        var previousOption=Modes(qaMachine).Get(hostOriginalMode);
        previousOption.SetDescription(originalDescription);
        previousOption.SetDisabled(wasDisabled); previousOption.SetHidden(wasHidden);
        AccessTools.Method(typeof(ItemMouseDragHandler),"AbortDrag").Invoke(drag,null);
        hostTestBag.ToggleFakeBackground(false);
        hostTestBag.Validate();
        Require(!drag.IsDraggingItem && hostTestBag.parentInventory==originalInventory,"native host hand did not release in place");
        hostHandStage=2;
        Record("PASS native-host-held-conflict","real host hand retained bag during guest radio and rejected container action; released without moving the bag");
    }
    private void CheckPartnerPickup(double now)
    {
        if(otherHandStage==3) return;
        var world=NativeWorld.Host;
        var drag=ItemMouseDragHandler.current;
        if(otherHandStage==0)
        {
            if(world?.PartnerTouches(hostTestBag)!=true) return;
            otherHandSince=now; otherHandMode=qaMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt();
            AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,hostTestBag);
            AccessTools.Method(typeof(ItemMouseDragHandler),"StartDrag").Invoke(drag,null);
            Require(!drag.IsDraggingItem,"host picked up partner's reserved satchel");
            AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,null);
            otherHandStage=1;
        }
        if(otherHandStage==1)
        {
            Require(world.PartnerTouches(hostTestBag),"original guest reservation expired while still dragging");
            if(now-otherHandSince<PickupLease.LifetimeSeconds+1) return;
            int changed=otherHandMode==1 ? 0 : 1;
            Modes(qaMachine).Get(changed).SetOn(true,true);
            Require(qaMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()==changed,"unrelated host radio failed during guest pickup");
            otherHandStage=2; return;
        }
        if(world.PartnerTouches(hostTestBag)) return;
        AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,hostTestBag);
        AccessTools.Method(typeof(ItemMouseDragHandler),"StartDrag").Invoke(drag,null);
        Require(drag.IsDraggingItem && drag.GetCurrentItem()==hostTestBag,"host could not pick up the released original item");
        AccessTools.Method(typeof(ItemMouseDragHandler),"AbortDrag").Invoke(drag,null);
        hostTestBag.ToggleFakeBackground(false); hostTestBag.Validate();
        Modes(qaMachine).Get(otherHandMode).SetOn(true,true);
        otherHandStage=3;
        Record("PASS native-guest-hand-conflict","original host pickup refused for the guest's satchel; held longer than TTL, host used another machine and picked up the same bag after cancellation");
    }
    private void ReleaseIntoBag(ReplicaLink link,GameItem item,int amount)
    {
        var drag=ItemMouseDragHandler.current;
        var source=item.parentInventory;
        int before=item.unitCount;
        AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,item);
        AccessTools.Field(typeof(ItemMouseDragHandler),"lastMouseClickedKey").SetValue(drag,UnityEngine.KeyCode.Mouse0);
        AccessTools.Method(typeof(ItemMouseDragHandler),"StartDrag").Invoke(drag,null);
        Require(drag.IsDraggingItem && !link.HasUnacknowledgedRequests,"original pickup did not establish a local drag preview");
        var count=AccessTools.Field(typeof(ItemMouseDragHandler),"currentRemove");
        int adjustment=amount-(int)count.GetValue(drag);
        if(adjustment!=0) AccessTools.Method(typeof(ItemMouseDragHandler),"OnEventMouseScroll").Invoke(drag,new object[]{new UnityEngine.Vector2(0,adjustment)});
        Require((int)count.GetValue(drag)==amount,"native scroll did not select requested amount");
        AccessTools.Field(typeof(ItemMouseDragHandler),"lastInventory").SetValue(drag,null);
        AccessTools.Field(typeof(ItemMouseDragHandler),"lastItem").SetValue(drag,unloadBag);
        AccessTools.Method(typeof(ItemMouseDragHandler),"OnEventRelease").Invoke(drag,new object[]{new HashSet<UnityEngine.KeyCode>{UnityEngine.KeyCode.Mouse0}});
        Require(!drag.IsDraggingItem && drag.GetCurrentItem()==null && link.HasUnacknowledgedRequests && item.unitCount==before,
            "original release did not retire the local hand or changed quantity before authority");
        if(amount==before)
            Require(NativeWorld.Guest.HasPendingPlacements && GraphUtils.FindAllChildrenType<GameItem>(unloadBag).Contains(item),
                "whole-item container release did not immediately preview its native destination");
        else Require(item.parentInventory==source,"partial drop changed the confirmed source before split authority");
    }
    private static void TriggerNativeItemMenu(GameItem item,string button)
    {
        var handler=ItemContextHandler.current; handler.Check();
        AccessTools.Field(typeof(ItemContextHandler),"currentInventory").SetValue(handler,null);
        AccessTools.Field(typeof(ItemContextHandler),"currentItem").SetValue(handler,item);
        AccessTools.Method(typeof(ItemContextHandler),"UpdateConditions").Invoke(handler,null);
        var element=AccessTools.Field(typeof(ItemContextHandler),button).GetValue(handler);
        AccessTools.Method(typeof(ItemContextHandler),"TriggerButton").Invoke(handler,new[]{element});
    }
}
