using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private sealed class PlacementPreview
    {
        internal PendingActions.Entry Action;
        internal GameItemElement Item;
        internal GameInventory ConfirmedParent, Destination;
        internal GridShape ConfirmedShape, PreviewShape;
    }

    private readonly List<PlacementPreview> placementPreviews = new List<PlacementPreview>();
    internal bool HasPendingPlacements => placementPreviews.Count > 0;

    private void PreviewHandPlacement(JObject command, PendingActions.Entry action)
    {
        if ((string)command["kind"] == "containerDrop")
        {
            PreviewContainerPlacement(command, action);
            return;
        }
        if ((string)command["kind"] == "group")
        {
            PreviewGroupPlacement(command, action);
            return;
        }
        if ((string)command["kind"] != "move" || (uint)command["target"] != 0 ||
            !model.Objects.TryGetValue((uint)command["id"], out var value) || value is not GameItemElement item ||
            (int)command["count"] != item.unitCount ||
            !model.Objects.TryGetValue((uint)command["destination"], out var destination) ||
            destination is not GameGridInventory inventory) return;
        var shape = new GridShapeBuilder(item.modifiedShape);
        shape.SetTransform((int)command["x"], (int)command["y"], (bool)command["flip"], (int)command["rotation"]);
        AddPlacementPreview(item, inventory, shape, action);
    }

    private void PreviewContainerPlacement(JObject command,PendingActions.Entry action)
    {
        if(!model.Objects.TryGetValue((uint)command["id"],out var source) || source is not GameItemElement item ||
            (int)command["count"]!=item.unitCount ||
            !model.Objects.TryGetValue((uint)command["target"],out var value) || value is not GameItem target) return;
        if(!GeneralHelper.MayPlayerInsertInto(target)) return;
        var slot=target.TryFindOneValidInventorySlot(item);
        if(slot?.IsValid()!=true || slot.numTransfer<item.unitCount || slot.targetItem!=null ||
            slot.inventory is not GameGridInventory inventory || slot.itemGridShape==null) return;
        AddPlacementPreview(item,inventory,slot.itemGridShape.Clone(),action);
    }

    private void PreviewGroupPlacement(JObject command, PendingActions.Entry action)
    {
        if ((bool?)command["precise"] != true || (uint)command["target"] != 0 ||
            !model.Objects.TryGetValue((uint)command["destination"], out var destination) ||
            destination is not GameGridInventory inventory) return;
        int dx = (int)command["dx"], dy = (int)command["dy"];
        var handler = ItemMultiSelectHandler.current;
        if (!(bool)AccessTools.Method(typeof(ItemMultiSelectHandler), "IsFormationValid")
            .Invoke(handler, new object[] { inventory, dx, dy })) return;
        foreach (JObject row in (JArray)command["items"])
        {
            var item = (GameItemElement)model.Objects[(uint)row["id"]];
            var shape = new GridShapeBuilder(item.modifiedShape);
            shape.SetPosition(item.modifiedShape.minX + dx, item.modifiedShape.minY + dy);
            AddPlacementPreview(item, inventory, shape, action);
        }
    }

    private void AddPlacementPreview(GameItemElement item, GameInventory inventory, GridShape shape, PendingActions.Entry action)
    {
        // Reusing a cell freed by a predicted move depends on that move, even
        // when the two gestures touch different items. Do not submit the later
        // placement until the freeing transaction has actually been projected.
        foreach (var previous in placementPreviews)
            if (!previous.Action.Finished && previous.Action.Id < action.Id &&
                ReferenceEquals(previous.ConfirmedParent, inventory) &&
                SolidCellsOverlap(previous.ConfirmedShape, shape)) action.AddDependency(previous.Action);
        placementPreviews.Add(new PlacementPreview
        {
            Action = action, Item = item, ConfirmedParent = item.parentInventory,
            ConfirmedShape = item.modifiedShape.Clone(), PreviewShape = shape, Destination = inventory
        });
        item.modifiedShape = shape.Clone();
        NativeMirrorProbe.AttachReplica(item, inventory);
        item.Validate();
    }

    private static bool SolidCellsOverlap(GridShape first, GridShape second)
    {
        int minX = System.Math.Max(first.minX, second.minX), maxX = System.Math.Min(first.maxX, second.maxX);
        int minY = System.Math.Max(first.minY, second.minY), maxY = System.Math.Min(first.maxY, second.maxY);
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                if (first.Get(x, y) == 1 && second.Get(x, y) == 1) return true;
        return false;
    }

    // Restore the confirmed local graph before applying another authoritative
    // snapshot, including unchanged records which otherwise skip hydration.
    private void RestorePlacementPreviews()
    {
        for (int index = placementPreviews.Count - 1; index >= 0; index--)
        {
            var preview = placementPreviews[index];
            if (preview.Item.IsDestroyed()) continue;
            preview.Item.modifiedShape = preview.ConfirmedShape.Clone();
            NativeMirrorProbe.AttachReplica(preview.Item, preview.ConfirmedParent);
            preview.Item.Validate();
        }
    }

    private void ReapplyPlacementPreviews(ulong through)
    {
        handActions.Projected(through);
        placementPreviews.RemoveAll(p => p.Action.Finished || p.Item.IsDestroyed());
        foreach (var preview in placementPreviews)
        {
            // The base may have changed while an unrelated request was in flight.
            preview.ConfirmedParent = preview.Item.parentInventory;
            preview.ConfirmedShape = preview.Item.modifiedShape.Clone();
            preview.Item.modifiedShape = preview.PreviewShape.Clone();
            NativeMirrorProbe.AttachReplica(preview.Item, preview.Destination);
            preview.Item.Validate();
        }
    }

}
