using System.Collections.Generic;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private HeldInteraction<GraphNodeStorage> hostHand;
    private HeldInteraction<GraphNodeStorage> CaptureHostHand(bool includePartner)
    {
        var held=new List<GraphNodeStorage>();
        if(ItemMouseDragHandler.current?.IsDraggingItem==true)
        {
            var item=ItemMouseDragHandler.current.GetCurrentItem();
            if(item!=null) held.Add(item);
        }
        if(ItemMultiSelectHandler.current?.IsGroupDragging==true)
            held.AddRange(NativeGroupTransfer.Selected(ItemMultiSelectHandler.current));
        // The legacy panel's acknowledged reservation remains valid until its
        // own release/timeout. Native dragging uses the host's original handler.
        if(NativeShop.Gate?.HeldForInteraction is GameItem reserved) held.Add(reserved);
        if(includePartner && PartnerHasHand)
            foreach(var entry in partnerSources.Values) held.Add(entry.Item);
        return new HeldInteraction<GraphNodeStorage>(held,node=>node.children,node=>node.parents);
    }
    private bool HandTouches(GameItem item)=>hostHand?.TouchesItem(item)==true;
    private bool HandInside(GameInventory inventory)=>hostHand?.TouchesContents(inventory)==true;
    private bool HandDestination(GameInventory inventory)=>hostHand?.TouchesDestination(inventory)==true;

    internal bool BlocksRemoteDealStash(SlotMarker slot) => ReferenceEquals(RemoteUiExecution,this) &&
        (HandTouches(slot.item) || HandTouches(slot.targetItem) || HandDestination(slot.inventory));

    internal bool AllowNativeWholesale()
    {
        var entry=EmporiumEntry.Instance;
        var held=CaptureHostHand(true);
        if(!held.TouchesContents(entry.frontInvinvElement) && !held.TouchesContents(entry.backInvinvElement)) return true;
        if(!ReferenceEquals(RemoteUiExecution,this)) CommandNotice("请先放下柜台物品，再确认批发交易。");
        return false;
    }
}
