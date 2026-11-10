using System;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private static bool InputConflictRequested => Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-input-conflict") >= 0;
    private GameItem conflictBlocker;
    private ulong conflictHostFloor;
    private bool conflictHostDone;
    private SlotMarker conflictSlot;
    private int conflictStep;
    private double conflictReleaseAt, conflictDeadline;

    private void TickHostInputConflict(ReplicaLink link)
    {
        if (!InputConflictRequested || !coupleMovesSeen || conflictHostDone) return;
        if (conflictBlocker == null)
        {
            if (!NativeWorld.Host.PartnerHasHand) return;
            var target = EmporiumEntry.Instance.backInvinvElement;
            var slot = target.TryFindOneValidInventorySlot(coupleItems[0], keepOrientation: true);
            Require(slot?.IsValid() == true, "host conflict fixture has no free target");
            conflictBlocker = DirectoryMaster.Item("beer").SetShape(new GridShapeBuilder(1, 1).SetData(new byte[] { 1 }, 1));
            GeneralHelper.SetItemOwned(conflictBlocker, true);
            int cellX = -1, cellY = -1;
            for (int y = slot.itemGridShape.minY; y <= slot.itemGridShape.maxY && cellX < 0; y++)
                for (int x = slot.itemGridShape.minX; x <= slot.itemGridShape.maxX; x++)
                    if (slot.itemGridShape.Get(x,y) == 1) { cellX=x; cellY=y; break; }
            Require(cellX >= 0, "conflict fixture item has no occupied cells");
            var shape = new GridShapeBuilder(conflictBlocker.modifiedShape)
                .SetPosition(cellX, cellY);
            var occupied = target.TryInventorySlot(conflictBlocker, 1, shape, conflictBlocker.modifiedState);
            Require(occupied?.IsValid() == true && occupied.TryAcceptOnce() == 1,
                "host could not occupy the guest's predicted target");
            conflictHostFloor = NativeWorld.Host.LastPartnerDropSerial;
            Record("TRACE host-concurrent-placement", $"host inserted a solid cell at {cellX},{cellY} over pending {coupleItems[0].identifier}; native authority will decide the overlap");
            return;
        }
        if (NativeWorld.Host.LastPartnerDropSerial <= conflictHostFloor || NativeWorld.Host.PartnerHasHand) return;
        ushort refusal = NativeWorld.Host.LastPartnerDropResult;
        Require(refusal is (ushort)ShopResult.NoSpace or (ushort)ShopResult.Stale,
            "occupied native target did not reject the actual guest drop: " + (ShopResult)refusal);
        Require(coupleItems[0].parentInventory == EmporiumEntry.Instance.invElement && coupleItems[0].unitCount == 1,
            "rejected placement changed the host source");
        conflictHostDone = true;
        Record("PASS host-concurrent-placement", "host rejected occupied target with " + (ShopResult)refusal + "; exact guest source remained in original inventory");
    }

    private bool RunRapidInputConflict(ReplicaLink link, double now)
    {
        if (conflictStep == 3) return true;
        var item = rapidItems[0];
        if (conflictStep == 0)
        {
            conflictSlot = EmporiumEntry.Instance.backInvinvElement.TryFindOneValidInventorySlot(item, keepOrientation: true);
            Require(conflictSlot?.IsValid() == true, "guest conflict preview has no free target");
            BeginAtNativeSlot(item, conflictSlot,holdAcrossFrames:true);
            conflictReleaseAt = now + 0.15;
            conflictDeadline = now + 20;
            conflictStep = 1;
            return false;
        }
        Require(now < conflictDeadline, "native conflict recovery timed out");
        if (conflictStep == 1)
        {
            if (now < conflictReleaseAt) return false;
            Require(ItemMouseDragHandler.current.IsDraggingItem, "native conflict pickup was lost");
            PositionDragAtSlot(item, conflictSlot);
            ReleaseNativeDrag();
            Require(item.parentInventory == conflictSlot.inventory, "conflict input did not first show the local predicted position");
            NativeOffscreenCapture.Capture("qa-pending-conflict");
            // Pick the same predicted entity up again before the rejected drop
            // can be acknowledged. The second local gesture must respond now,
            // but must never execute after its prerequisite is refused.
            var returning = EmporiumEntry.Instance.invElement.TryInventorySlot(item, 1, rapidOrigins[0], item.modifiedState);
            Require(returning?.IsValid() == true, "dependent conflict move could not reuse its source cell");
            ReleaseAtNativeSlot(item, returning);
            Require(item.parentInventory == EmporiumEntry.Instance.invElement,
                "dependent conflict re-pick waited for the first drop result");
            conflictStep = 2;
            return false;
        }
        if (NativeWorld.Guest.HasPendingWork) return false;
        Require(NativeWorld.Guest.LastGestureResult is (ushort)ShopResult.NoSpace or (ushort)ShopResult.Stale && item.parentInventory == EmporiumEntry.Instance.invElement && item.unitCount == 1,
            "occupied target did not retract only the rejected preview");
        Require(item.modifiedShape.minX == rapidOrigins[0].minX && item.modifiedShape.minY == rapidOrigins[0].minY,
            "rejected preview did not restore the confirmed placement");
        Record("PASS rapid-native-conflict", "original pickup/release and immediate re-pick predicted locally; host occupation rejected the first drop and its unsent dependency; same entity/count/shape restored without a second mutation");
        NativeOffscreenCapture.Capture("qa-rejected-conflict");
        conflictStep = 3;
        return true;
    }
}
