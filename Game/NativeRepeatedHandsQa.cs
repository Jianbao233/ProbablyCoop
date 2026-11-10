using System.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private GameItemElement repeatedItem;
    private GridShape repeatedOrigin;
    private uint repeatedIdentity;
    private int repeatedStage;
    private double repeatedDeadline;

    // Execute the original pickup/release path for one entity repeatedly,
    // without giving a network tick any opportunity to confirm its first move.
    private bool RunRepeatedHandsQa(ReplicaLink link, double now)
    {
        if (repeatedStage == 3) return true;
        if (repeatedStage == 0)
        {
            repeatedItem = EmporiumEntry.Instance.invElement.childItems.OfType<GameItemElement>()
                .FirstOrDefault(item => GeneralHelper.IsItemOwned(item) && item.unitCount == 1 &&
                    item.modifiedShape.width <= 2 && item.modifiedShape.height <= 2);
            Require(repeatedItem != null, "repeated-pickup fixture lacks a whole item");
            repeatedOrigin = repeatedItem.modifiedShape.Clone();
            repeatedIdentity = NativeWorld.Guest.PointerIdentity(repeatedItem);
            var counter = EmporiumEntry.Instance.backInvinvElement;
            var inventory = EmporiumEntry.Instance.invElement;
            var first = counter.TryFindOneValidInventorySlot(repeatedItem, keepOrientation: true);
            Require(first?.IsValid() == true, "first repeated placement has no space");
            BeginAtNativeSlot(repeatedItem, first);
            var preview = repeatedItem.fakeMiddle;
            var rotator = repeatedItem.fakeRotator.rectTransform;
            repeatedItem.RotCW();
            var (x, y) = repeatedItem.GetCenteredPosition();
            var angle = rotator.localRotation;
            repeatedItem.Validate();
            var (newX, newY) = repeatedItem.GetCenteredPosition();
            Require(repeatedItem.fakeBackground.image.enabled && preview.parent == RenderHandler.current.overlayRoot &&
                Vector2.Distance(new Vector2(x, y), new Vector2(newX, newY)) < 0.1f &&
                Quaternion.Angle(rotator.localRotation, angle) < 0.1f,
                "validating a held replica reset its native drag preview");
            repeatedItem.RotCCW();
            ReleaseNativeDrag();
            Require(repeatedItem.parentInventory == counter, "first repeated placement waited for authority");
            var returning = inventory.TryInventorySlot(repeatedItem, 1, repeatedOrigin, repeatedItem.modifiedState);
            Require(returning?.IsValid() == true, "predicted source cell did not become locally available");
            ReleaseAtNativeSlot(repeatedItem, returning);
            Require(repeatedItem.parentInventory == inventory, "immediate re-pick was swallowed");
            var again = counter.TryFindOneValidInventorySlot(repeatedItem, keepOrientation: true);
            Require(again?.IsValid() == true, "third repeated placement has no space");
            ReleaseAtNativeSlot(repeatedItem, again);
            Require(repeatedItem.parentInventory == counter && link.IndependentInFlight == 1,
                "dependent gestures waited locally or submitted unconfirmed source proofs");
            repeatedStage = 1;
            repeatedDeadline = now + 30;
            Record("TRACE repeated-native-release", "same native entity counter/inventory/counter in one frame; one dispatch and two dependency-bound local moves");
            NativeOffscreenCapture.Capture("qa-repeated-hands-pending");
            return false;
        }
        Require(now < repeatedDeadline, "repeated-pickup reconciliation timed out");
        if (NativeWorld.Guest.HasPendingWork) return false;
        Require(NativeWorld.Guest.PointerIdentity(repeatedItem) == repeatedIdentity && repeatedItem.unitCount == 1,
            "repeated pickup replaced or duplicated the original entity");
        if (repeatedStage == 1)
        {
            Require(repeatedItem.parentInventory == EmporiumEntry.Instance.backInvinvElement,
                "dependent moves did not reconcile to the final counter destination");
            var origin = EmporiumEntry.Instance.invElement.TryInventorySlot(repeatedItem, 1, repeatedOrigin, repeatedItem.modifiedState);
            Require(origin?.IsValid() == true, "repeated fixture cannot restore its original source cell");
            ReleaseAtNativeSlot(repeatedItem, origin);
            repeatedStage = 2;
            return false;
        }
        Require(repeatedItem.parentInventory == EmporiumEntry.Instance.invElement &&
            repeatedItem.modifiedShape.minX == repeatedOrigin.minX && repeatedItem.modifiedShape.minY == repeatedOrigin.minY &&
            repeatedItem.modifiedShape.orientation == repeatedOrigin.orientation && repeatedItem.modifiedShape.flipped == repeatedOrigin.flipped,
            "repeated-pickup restoration changed native placement");
        Record("PASS repeated-native-release", "three original gestures on one entity before the first receipt; dependency proofs reconciled and original identity/count/shape restored");
        repeatedStage = 3;
        return true;
    }
}
