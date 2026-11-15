namespace ProbablyCoop;

// Keep native graph and rendering behavior, including the UI a machine slot
// borrows from its contents. The initiating action already ran slot callbacks.
internal static class NativeInventoryMembership
{
    internal static bool Supported(GameInventory inventory) =>
        inventory is GameGridInventory || inventory is GameSlotInventory || inventory is GameGridScrollableInventory;

    internal static bool Add(GameInventory inventory, GameItem item)
    {
        if (item.parentInventory != null || inventory is GameSlotInventory && inventory.childItems.Count != 0)
            return false;

        var begin = inventory.onSlotAddItemFunc;
        var end = inventory.onSlotAddItemEndFunc;
        var late = inventory.onSlotAddItemLateFunc;
        var itemAdded = item.onSlotAddItemFunc;
        var scroll = inventory as GameGridScrollableInventory;
        bool stack = scroll?.alwaysStack == true;
        try
        {
            inventory.onSlotAddItemFunc = null;
            inventory.onSlotAddItemEndFunc = null;
            inventory.onSlotAddItemLateFunc = null;
            item.onSlotAddItemFunc = null;
            if (scroll != null) scroll.alwaysStack = false;
            return inventory.UncheckedAccept(item);
        }
        finally
        {
            inventory.onSlotAddItemFunc = begin;
            inventory.onSlotAddItemEndFunc = end;
            inventory.onSlotAddItemLateFunc = late;
            item.onSlotAddItemFunc = itemAdded;
            if (scroll != null) scroll.alwaysStack = stack;
        }
    }

    internal static bool Remove(GameInventory inventory, GameItem item)
    {
        var begin = inventory.onSlotRemoveItemFunc;
        var end = inventory.onSlotRemoveItemEndFunc;
        var late = inventory.onSlotRemoveItemLateFunc;
        var itemRemoved = item.onSlotRemoveItemFunc;
        try
        {
            inventory.onSlotRemoveItemFunc = null;
            inventory.onSlotRemoveItemEndFunc = null;
            inventory.onSlotRemoveItemLateFunc = null;
            item.onSlotRemoveItemFunc = null;
            return inventory.Expel(item);
        }
        finally
        {
            inventory.onSlotRemoveItemFunc = begin;
            inventory.onSlotRemoveItemEndFunc = end;
            inventory.onSlotRemoveItemLateFunc = late;
            item.onSlotRemoveItemFunc = itemRemoved;
        }
    }
}
