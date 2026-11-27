using System;

namespace ProbablyCoop;

// A destination owns its physical grid. The host can lend the original
// window to that grid, or bind it to a native call without changing the view.
internal sealed class NativeExpeditionGround : IDisposable
{
    private readonly EmporiumEntry entry;
    private readonly GameGridInventory shopGround;
    private readonly PixelWindow window;
    private readonly MachineryIntake intake;
    private readonly TreeNodeRender parked;
    private GameGridInventory shown;
    internal NativeExpeditionCounters Counters { get; }

    internal NativeExpeditionGround(EmporiumEntry source)
    {
        entry = source;
        shopGround = source.afterhourInventory;
        shown = shopGround;
        window = source.afterhourWindow;
        if (window?.childElement is not MachineryPanelRow row ||
            row.GetPanel("ground")?.content is not MachineryIntake original ||
            shopGround == null || !ReferenceEquals(original.content, shopGround))
            throw new InvalidOperationException("The original afterhours ground window is unavailable.");
        intake = original;
        parked = TreeNodeRender.Instantiate();
        parked.gameObject.SetActive(false);
        Counters = new NativeExpeditionCounters(entry, parked);
    }

    internal GameGridInventory Create(AfterhourLocation location)
    {
        var grid = new GameGridInventory(shopGround.inventoryShape.width, shopGround.inventoryShape.height);
        grid.handler.SetParent(parked);
        grid.slotPriorityFunc = shopGround.slotPriorityFunc;
        grid.mayInventoryAddItemFunc = (item, _) => GeneralHelper.IsItemOwned(item) &&
            (!GeneralHelper.IsHazardousWaste(item) || location.AllowsHazardousWaste);
        return grid;
    }

    internal GameGridInventory AtShop => shopGround;

    internal void Show(GameGridInventory grid)
    {
        grid ??= shopGround;
        if (ReferenceEquals(shown, grid)) return;
        var previous = shown;
        if (!ReferenceEquals(intake.content, previous))
            throw new InvalidOperationException("The original afterhours ground inventory was rebound by another operation.");
        intake.SetContent(grid);
        previous.handler.SetParent(parked);
        shown = grid;
        entry.afterhourInventory = grid;
        window.Validate();
    }

    internal void Release(GameGridInventory grid)
    {
        if (ReferenceEquals(grid, shopGround) || ReferenceEquals(grid, shown) || grid.childItems.Count != 0)
            throw new InvalidOperationException("Cannot release a live expedition inventory.");
        UnityEngine.Object.Destroy(grid.handler.gameObject);
    }

    public void Dispose()
    {
        // Scene disposal owns the remainder. Never reattach or dispose the
        // original window as part of an ordinary peer disconnect.
        if (parked != null) UnityEngine.Object.Destroy(parked.gameObject);
    }
}
