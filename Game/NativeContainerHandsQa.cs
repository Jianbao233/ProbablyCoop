using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private GameItemElement containerHandBag, containerHandChild;
    private GameInventory containerHandOrigin, containerChildOrigin;
    private GridShape containerHandShape;
    private uint containerBagIdentity, containerChildIdentity;
    private int containerHandsStage;
    private double containerHandsDeadline;

    private bool RunContainerHandsQa(ReplicaLink link,double now)
    {
        if(containerHandsStage==5) return true;
        if(containerHandsStage==0)
        {
            containerHandBag=EmporiumEntry.Instance.invElement.childItems.OfType<GameItemElement>()
                .Single(item=>item.identifier=="satchel" && GraphUtils.FindAllChildrenType<GameItem>(item).Count==2);
            containerHandChild=GraphUtils.FindAllChildrenType<GameItem>(containerHandBag).OfType<GameItemElement>()
                .First(item=>item.unitCount==1);
            var modAction=containerHandBag.examineWindow?.childElement as AdvButtonElement;
            Require(modAction?.text=="QA mod action" && modAction.isDisabled && modAction.description=="External content action",
                "advanced content control did not reach the guest safely in the initial native inventory");
            Record("PASS content-mod-layout-entry","real initial network snapshot contains an item with an advanced content control; original UI element reconstructed without an executable callback");
            containerHandOrigin=containerHandBag.parentInventory;
            containerChildOrigin=containerHandChild.parentInventory;
            containerHandShape=containerHandBag.modifiedShape.Clone();
            containerBagIdentity=NativeWorld.Guest.PointerIdentity(containerHandBag);
            containerChildIdentity=NativeWorld.Guest.PointerIdentity(containerHandChild);
            var counter=EmporiumEntry.Instance.backInvinvElement;
            var bagSlot=counter.TryFindOneValidInventorySlot(containerHandBag,keepOrientation:true);
            Require(bagSlot?.IsValid()==true,"container hand fixture has no counter space");
            ReleaseAtNativeSlot(containerHandBag,bagSlot);
            Require(containerHandBag.parentInventory==counter,"moving a container waited for authority");
            var childSlot=EmporiumEntry.Instance.invElement.TryFindOneValidInventorySlot(containerHandChild,keepOrientation:true);
            Require(childSlot?.IsValid()==true,"container contents fixture has no stock space");
            ReleaseAtNativeSlot(containerHandChild,childSlot);
            Require(containerHandChild.parentInventory==EmporiumEntry.Instance.invElement,
                "pending container move swallowed the contents pickup");
            ReleaseWholeIntoContainer(containerHandChild,containerHandBag);
            Require(containerHandChild.parentInventory==containerChildOrigin && link.IndependentInFlight==1,
                "container insertion did not preview immediately or crossed its unresolved dependencies");
            containerHandsStage=1; containerHandsDeadline=now+40;
            Record("TRACE container-native-chain","one frame: move satchel, remove its child, insert child again; one host dispatch and independent native UI feedback");
            NativeOffscreenCapture.Capture("qa-container-chain-pending");
            return false;
        }
        Require(now<containerHandsDeadline,"container hand dependency chain timed out");
        if(NativeWorld.Guest.HasPendingWork) return false;
        if(containerHandsStage==3)
        {
            // A content-defined accept rule need not exist on the guest. A
            // false local hover query must still allow the exact host drop.
            var accept=containerHandChild.mayInventoryAddItemFunc;
            try
            {
                containerHandChild.mayInventoryAddItemFunc=(_,__)=>false;
                Require(!containerHandBag.MayHaveValidInventorySlot(containerHandChild),
                    "QA did not hide the custom slot acceptance from the guest");
                ReleaseWholeIntoContainer(containerHandChild,containerHandBag);
                Require(containerHandChild.parentInventory==containerHandOrigin && link.IndependentInFlight!=0,
                    "custom slot drop was swallowed or previewed at an unconfirmed location");
            }
            finally { containerHandChild.mayInventoryAddItemFunc=accept; }
            containerHandsStage=4;
            return false;
        }
        Require(NativeWorld.Guest.PointerIdentity(containerHandBag)==containerBagIdentity &&
            NativeWorld.Guest.PointerIdentity(containerHandChild)==containerChildIdentity && containerHandChild.unitCount==1 &&
            containerHandChild.parentInventory==containerChildOrigin &&
            GraphUtils.FindAllChildrenType<GameItem>(containerHandBag).Count==2,
            "container chain changed native identities, ownership graph or quantities");
        if(containerHandsStage==4)
        {
            Record("PASS closed-container-custom-rule","native pickup/release declared the item hit despite missing local slot acceptance; authority placed the same entity once");
            containerHandsStage=5;
            return true;
        }
        if(containerHandsStage==1)
        {
            Require(containerHandBag.parentInventory==EmporiumEntry.Instance.backInvinvElement,
                "host did not confirm the final container location");
            var origin=containerHandOrigin.TryInventorySlot(containerHandBag,1,containerHandShape,containerHandBag.modifiedState);
            Require(origin?.IsValid()==true,"container chain cannot restore its source cell");
            ReleaseAtNativeSlot(containerHandBag,origin);
            containerHandsStage=2;
            return false;
        }
        Require(containerHandBag.parentInventory==containerHandOrigin &&
            containerHandBag.modifiedShape.minX==containerHandShape.minX && containerHandBag.modifiedShape.minY==containerHandShape.minY,
            "container chain did not restore its native origin");
        Record("PASS container-native-chain","original pickup/release moved container and its content independently before first ACK; host applied source/contents proofs in order and exact entities restored");
        var stock=containerHandOrigin.TryFindOneValidInventorySlot(containerHandChild);
        Require(stock?.IsValid()==true,"custom slot drop fixture has no source cell");
        ReleaseAtNativeSlot(containerHandChild,stock);
        containerHandsStage=3;
        return false;
    }

    private static void ReleaseWholeIntoContainer(GameItemElement item,GameItem target)
    {
        var drag=ItemMouseDragHandler.current;
        AccessTools.Field(typeof(ItemMouseDragHandler),"currentItem").SetValue(drag,item);
        AccessTools.Field(typeof(ItemMouseDragHandler),"lastMouseClickedKey").SetValue(drag,KeyCode.Mouse0);
        AccessTools.Method(typeof(ItemMouseDragHandler),"StartDrag").Invoke(drag,null);
        Require(drag.IsDraggingItem && drag.GetCurrentItem()==item,"container-chain original pickup was swallowed");
        AccessTools.Field(typeof(ItemMouseDragHandler),"lastInventory").SetValue(drag,null);
        AccessTools.Field(typeof(ItemMouseDragHandler),"lastItem").SetValue(drag,target);
        AccessTools.Method(typeof(ItemMouseDragHandler),"OnEventRelease").Invoke(drag,new object[]{new HashSet<KeyCode>{KeyCode.Mouse0}});
        Require(!drag.IsDraggingItem && drag.GetCurrentItem()==null,"container-chain original release retained the hand");
    }
}
