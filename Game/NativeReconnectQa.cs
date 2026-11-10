using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;
internal sealed partial class NativeBackgroundQa
{
    private readonly bool reconnectQa=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-reconnect")>=0;
    private readonly bool resumedQa=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-resume")>=0;
    private bool resumeLoaded,hostAwaitingRejoin,hostRejoined;
    private double hostRejoinUntil;
    private double groupRestartUntil;
    private bool observedRestartGroup;
    private GameItemElement restartContendedItem,rejoinPickupItem;
    private GameInventory restartContendedParent;
    private GridShape restartContendedShape;
    private int restartContendedCount,rejoinPickupStep;
    private double rejoinPickupUntil;
    private bool restartContentionReleased;

    private void CheckRestartContention(bool disconnected)
    {
        if(!reconnectQa || hostRejoined || restartContentionReleased) return;
        var world=NativeWorld.Host;
        var drag=ItemMouseDragHandler.current;
        if(!disconnected)
        {
            // Earlier bulk-move cases also reserve two items. The explicit
            // original journal fixture is armed only at the restart boundary.
            if(qaMachine.GetTagReadonly("LOG_CONTENT_TAG").GetString()!="QA reconnect frontend") return;
            if(restartContendedItem!=null || world.PartnerHandCount!=2) return;
            Require(!drag.IsDraggingItem,"source already dragging at reconnect contention boundary");
            restartContendedItem=EmporiumEntry.Instance.invElement.childItems.OfType<GameItemElement>().First(world.PartnerTouches);
            restartContendedParent=restartContendedItem.parentInventory;
            restartContendedShape=restartContendedItem.modifiedShape.Clone(); restartContendedCount=restartContendedItem.unitCount;
            AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,restartContendedItem);
            AccessTools.Method(typeof(ItemMouseDragHandler),"StartDrag").Invoke(drag,null);
            Require(!drag.IsDraggingItem && restartContendedItem.parentInventory==restartContendedParent && restartContendedItem.unitCount==restartContendedCount,
                "original source pickup bypassed the partner's active group reservation");
            AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,null);
            Record("PASS native-contention-before-disconnect","source original pickup of partner-held entity refused; exact parent/count unchanged while guest group remains reserved");
            return;
        }
        Require(restartContendedItem!=null && !world.PartnerHasHand,"contention disconnect did not release the actual reserved entity");
        AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,restartContendedItem);
        AccessTools.Method(typeof(ItemMouseDragHandler),"StartDrag").Invoke(drag,null);
        Require(drag.IsDraggingItem && drag.GetCurrentItem()==restartContendedItem,"original source pickup stayed blocked after peer loss");
        AccessTools.Method(typeof(ItemMouseDragHandler),"AbortDrag").Invoke(drag,null);
        Require(!drag.IsDraggingItem && restartContendedItem.parentInventory==restartContendedParent && restartContendedItem.unitCount==restartContendedCount &&
            restartContendedItem.modifiedShape.minX==restartContendedShape.minX && restartContendedItem.modifiedShape.minY==restartContendedShape.minY,
            "disconnect pickup/cancel changed the committed entity");
        restartContentionReleased=true;
        Record("PASS native-contention-disconnect-release","same source entity accepts original pickup after real guest process exits; native cancel preserves parent/count/position");
    }

    private bool RunRejoinedContentionPickup(double now)
    {
        if(!resumedQa || rejoinPickupStep==3) return true;
        var drag=ItemMouseDragHandler.current; var world=NativeWorld.Guest;
        if(rejoinPickupStep==2)
        {
            Require(now<rejoinPickupUntil,"rejoined contention cancel did not release its reservation");
            if(world.HasPendingWork) return false;
            rejoinPickupStep=3; return true;
        }
        if(rejoinPickupStep==0)
        {
            uint id=uint.Parse(ModHelper.ReadModDataFileText("probably_coop","qa-reconnect-pickup.txt"));
            Require(world.Objects.TryGetValue(id,out var value) && value is GameItemElement,"rejoined contention entity missing");
            rejoinPickupItem=(GameItemElement)value;
            AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,rejoinPickupItem);
            AccessTools.Field(typeof(ItemMouseDragHandler),"lastMouseClickedKey").SetValue(drag,KeyCode.Mouse0);
            AccessTools.Method(typeof(ItemMouseDragHandler),"StartDrag").Invoke(drag,null);
            Require(drag.IsDraggingItem && drag.GetCurrentItem()==rejoinPickupItem,"rejoined original pickup swallowed");
            KeepNativeQaDragAcrossFrames(drag);
            rejoinPickupUntil=now+15; rejoinPickupStep=1; return false;
        }
        Require(now<rejoinPickupUntil && drag.IsDraggingItem,"rejoined contention pickup did not receive fresh reservation");
        if(!world.HasConfirmedGesture) return false;
        AccessTools.Method(typeof(ItemMouseDragHandler),"AbortDrag").Invoke(drag,null);
        Record("PASS native-contention-rejoined-pickup","fresh guest original pickup of previously reserved entity confirmed by source; native cancel ends local gesture");
        rejoinPickupStep=2; return false;
    }
    private bool restartFrontendRequested,restartFrontendClosed;
    private double restartFrontendUntil;
    private static JObject ReconnectStamp() => new JObject {
        ["day"]=StoreStation.Instance.dayCounter,["cash"]=PlayerStore.Instance.playerCash,
        ["hostActor"]=NativeWorld.Guest.RemoteHostActor?.Serialize(),
        ["partnerActor"]=NativeWorld.Guest.HasActorProjection ? NativeActorState.Capture(PlayerStore.Instance).Serialize() : null,
        ["items"]=new JArray(NativeWorld.Guest.Objects.Where(p=>p.Value is GameItem).OrderBy(p=>p.Key).Select(p=> {
            var i=(GameItem)p.Value;
            return new JObject { ["entity"]=p.Key,["id"]=i.identifier,["count"]=i.unitCount,["owned"]=GeneralHelper.IsItemOwned(i),
                ["parent"]=NativeWorld.Guest.Objects.FirstOrDefault(v=>ReferenceEquals(v.Value,i.parentInventory)).Key,
                ["x"]=i.modifiedShape.minX,["y"]=i.modifiedShape.minY,["r"]=i.modifiedShape.orientation,["flip"]=i.modifiedShape.flipped };
        })) };
    private void ResumeNativeQa(ShopLink link)
    {
        if(!resumedQa || resumeLoaded) return;
        var checkpoint=JObject.Parse(ModHelper.ReadModDataFileText("probably_coop","qa-reconnect.json"));
        Require(JToken.DeepEquals(checkpoint,ReconnectStamp()),"restarted native guest differs from pre-disconnect committed cash/day/entity inventory");
        Require(NativeWorld.Guest.AuditProjection(out var detail) && PlayerStore.Instance.saveSlotId==-1,"restarted guest did not hydrate native replica without loading a slot");
        Require(!NativeWorld.Guest.HasFrontendBindings && !LogUIManager.Instance.UIPanel.activeSelf,
            "new guest inherited the previous connection's standalone frontend lease");
        Record("PASS native-frontend-peer-reset","fresh native client inherited no previous actor's standalone modal binding or locally closed journal");
        chosen=link.View.Items.First(i=>i.Owned);
        resumeLoaded=true; stage=18;
        Record("PASS real-native-guest-reconnect","new Unity process and handshake; committed IDs, parents, positions, quantities, ownership, cash and day preserved; "+detail);
    }
    private bool RequestNativeQaRestart()
    {
        if(!reconnectQa || resumedQa) return false;
        var handler=ItemMultiSelectHandler.current;
        double now=Time.realtimeSinceStartupAsDouble;
        if(!restartFrontendClosed)
        {
            if(!restartFrontendRequested)
            {
                if(NativeWorld.Guest.HasPendingWork) return true;
                FrontendPrimeClick(); restartFrontendRequested=true; restartFrontendUntil=now+12; return true;
            }
            Require(now<restartFrontendUntil,"standalone frontend did not open before reconnect fixture");
            if(!LogUIManager.Instance.UIPanel.activeSelf || FirstLogLine()!="QA reconnect frontend") return true;
            Require(NativeWorld.Guest.HasFrontendBindings,"standalone view was not bound before restart");
            LogUIManager.Instance.CloseUI(); // Local close retains the old server lease until peer loss.
            restartFrontendClosed=true;
            Record("PASS native-frontend-before-restart","original standalone journal opened and locally closed; old actor binding remains for the disconnect reset test");
        }
        if(groupRestartUntil==0)
        {
            var items=EmporiumEntry.Instance.invElement.childItems.OfType<GameItemElement>()
                .Where(i=>GeneralHelper.IsItemOwned(i) && i.MayRemove() && i.MaxNumRemove()>0).Take(2).ToArray();
            Require(items.Length==2,"missing original items for group disconnect");
            var selection=NativeGroupTransfer.Selected(handler); selection.Clear(); selection.AddRange(items);
            QaGroupButton.Held=true;
            AccessTools.Method(typeof(ItemMultiSelectHandler),"StartGroupDrag").Invoke(handler,null);
            Require(handler.IsGroupDragging && items.All(handler.IsGroupDraggingItem),"original guest group drag did not start");
            groupRestartUntil=now+12;
            return true;
        }
        Require(now<groupRestartUntil && handler.IsGroupDragging,"group was not confirmed before disconnect");
        if(!NativeWorld.Guest.HasConfirmedGesture) return true;
        var held=NativeGroupTransfer.Selected(handler).First();
        ModHelper.WriteModDataFileText("probably_coop","qa-reconnect-pickup.txt",NativeWorld.Guest.PointerIdentity(held).ToString());
        Record("PASS native-group-disconnect","exiting with original two-item group drag confirmed in the host projection");
        ModHelper.WriteModDataFileText("probably_coop","qa-reconnect.json",ReconnectStamp().ToString());
        Record("PASS real-native-reconnect-checkpoint","guest exits after original native sale and machine work; launcher will start a fresh original game process");
        Finish("PASS guest restart requested",77); return true;
    }
}
