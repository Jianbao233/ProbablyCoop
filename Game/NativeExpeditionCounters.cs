using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ProbablyCoop;

// Dr. Jackson's original code reads the same native fields as the shop.
// Bind his own grids and customer for authority calls, and bind the same
// grids to the two original windows only when the host actually visits.
internal sealed class NativeExpeditionCounters
{
    private static readonly PropertyInfo FrontField = AccessTools.Property(typeof(EmporiumEntry), nameof(EmporiumEntry.frontInvinvElement));
    private static readonly PropertyInfo BackField = AccessTools.Property(typeof(EmporiumEntry), nameof(EmporiumEntry.backInvinvElement));
    private static readonly PropertyInfo BufferField = AccessTools.Property(typeof(EmporiumEntry), nameof(EmporiumEntry.backInvinvElementCounter));
    private static readonly FieldInfo TabletSelection = AccessTools.Field(typeof(NegociationUIManager), "selectedItem");

    private sealed class Counters
    {
        internal readonly GameGridInventory Front, Back, Buffer;
        internal PlayerStore.PlayerLocation Location;
        internal StoreClientInstance Customer, PreviousCustomer;
        internal GameItem DealItem, TabletItem, SelectedItem;
        internal bool Arrived;

        internal Counters(GameGridInventory front, GameGridInventory back, GameGridInventory buffer, PlayerStore.PlayerLocation location)
        {
            Front = front;
            Back = back;
            Buffer = buffer;
            Location = location;
        }
    }

    private sealed class Binding : IDisposable
    {
        private readonly NativeExpeditionCounters owner;
        private readonly Counters previous;

        internal Binding(NativeExpeditionCounters owner, Counters previous)
        {
            this.owner = owner;
            this.previous = previous;
        }

        public void Dispose() => owner.Bind(previous);
    }

    private readonly EmporiumEntry entry;
    private readonly PlayerStore store;
    private readonly TreeNodeRender parked;
    private readonly Counters shop;
    private Counters merchant, bound, shown;
    private bool stockingMerchant;

    internal bool IsOffscreen => !ReferenceEquals(bound, shown);

    internal NativeExpeditionCounters(EmporiumEntry entry, TreeNodeRender parked)
    {
        this.entry = entry;
        this.parked = parked;
        store = PlayerStore.Instance;
        shop = new Counters(entry.frontInvinvElement, entry.backInvinvElement, entry.backInvinvElementCounter, store.playerLocation);
        bound = shown = shop;
    }

    internal bool OpenMerchant()
    {
        if (merchant != null)
        {
            RequireDetachedMerchant();
            return false;
        }
        merchant = new Counters(Create(shop.Front), Create(shop.Back), Create(shop.Buffer), PlayerStore.PlayerLocation.INVENTOR);
        merchant.Front.SetTransparency(0.99f);
        merchant.Back.SetTransparency(0.85f);
        merchant.Back.HideBorder();
        return true;
    }

    private GameGridInventory Create(GameGridInventory original)
    {
        var grid = new GameGridInventory(original.inventoryShape.width, original.inventoryShape.height);
        grid.handler.SetParent(parked);
        grid.slotPriorityFunc = original.slotPriorityFunc;
        grid.mayInventoryAddItemFunc = original.mayInventoryAddItemFunc;
        return grid;
    }

    internal IDisposable Enter(bool atMerchant)
    {
        var next = ForLocation(atMerchant);
        if (ReferenceEquals(bound, next)) return null;
        var previous = bound;
        Bind(next);
        return new Binding(this, previous);
    }

    private Counters ForLocation(bool atMerchant) => atMerchant
        ? merchant ?? throw new InvalidOperationException("The engineering shop has no expedition counters.")
        : shop;

    private void Bind(Counters next)
    {
        if (ReferenceEquals(bound, next)) return;
        var tablet = NegociationUIManager.Instance;
        bound.Location = store.playerLocation;
        bound.Customer = store.currentClientInstance;
        bound.PreviousCustomer = store.lastVisitedClient;
        bound.DealItem = store.currentNegociatedItem;
        bound.Arrived = store.isClientArrived;
        bound.TabletItem = tablet.currentNegociatedItem;
        bound.SelectedItem = (GameItem)TabletSelection.GetValue(tablet);

        FrontField.SetValue(entry, next.Front);
        BackField.SetValue(entry, next.Back);
        BufferField.SetValue(entry, next.Buffer);
        store.playerLocation = next.Location;
        store.currentClientInstance = next.Customer;
        store.lastVisitedClient = next.PreviousCustomer;
        store.currentNegociatedItem = next.DealItem;
        store.isClientArrived = next.Arrived;
        tablet.currentNegociatedItem = next.TabletItem;
        TabletSelection.SetValue(tablet, next.SelectedItem);
        bound = next;
    }

    internal void Show(bool atMerchant)
    {
        var next = ForLocation(atMerchant);
        if (ReferenceEquals(shown, next)) return;
        SwapWindow(entry.frontInvWindow, shown.Front, next.Front);
        SwapWindow(entry.backInvWindow, shown.Back, next.Back);
        Bind(next);
        shown = next;
    }

    private void SwapWindow(PixelWindow window, GameGridInventory oldGrid, GameGridInventory next)
    {
        if (!ReferenceEquals(window.childElement, oldGrid))
            throw new InvalidOperationException("The original counter window was rebound by another operation.");
        window.Attach(next);
        oldGrid.handler.SetParent(parked);
    }

    internal void StockMerchant()
    {
        if (merchant == null || !ReferenceEquals(bound, merchant))
            throw new InvalidOperationException("Native merchant stock needs its own bound counters.");

        // These are the authority steps in DrJacksonLocation.Arrive. Its
        // window activation, sprite and stage positioning run in presentation.
        entry.ExpandFrontCounterGrid();
        stockingMerchant = true;
        try
        {
            if (AfterhourEvents.IsActive("special_stock")) StoreClientList.PlaceInventorSpecialStock();
            else StoreClientList.PlaceInventorInventory();
        }
        finally { stockingMerchant = false; }
        store.currentClientInstance = StoreClientInstance.CreateClientInstance(StoreClientList.CreateInventor());
        store.isClientArrived = true;
        store.daySinceLastBrowseUpgrade = 0;
    }

    internal void IncludeShopCounterGoods(List<GameItem> owned)
    {
        if (!stockingMerchant) return;
        // The original arrival parks the home's back counter in the native
        // buffer before stock generation, so one-off offers still see it.
        var existing = new HashSet<GameItem>(owned);
        foreach (var grid in new[] { shop.Front, shop.Back, shop.Buffer })
            foreach (var item in GraphUtils.FindAllChildrenType<GameItem>(grid))
                if (GeneralHelper.IsItemOwned(item) && existing.Add(item)) owned.Add(item);
    }

    internal GameItem[] OwnedMerchantItems(GameGridInventory ground)
    {
        if (merchant == null) return Array.Empty<GameItem>();
        return merchant.Front.childItems.Concat(merchant.Back.childItems).Concat(merchant.Buffer.childItems)
            .Where(GeneralHelper.IsItemOwned).Concat(ground.childItems).ToArray();
    }

    internal bool ReturnMerchantItems(GameGridInventory ground)
    {
        // Native merchant returns prioritize the shop back counter. Keep
        // leftovers at the merchant if the actual shared shop is full.
        if (!ReferenceEquals(bound, shop)) throw new InvalidOperationException("Return engineering goods into the shared shop.");
        var goods = OwnedMerchantItems(ground);
        bool complete = true;
        foreach (var item in goods)
        {
            var previous = item.parentInventory;
            int left = GraphUtils.TryAcceptAll(shop.Back, item);
            if (left > 0) left = GraphUtils.TryAcceptAll(entry.invElement, item, left);
            if (left > 0) left = GraphUtils.TryAcceptAll(entry.showcaseElement, item, left);
            if (left > 0) left = GraphUtils.TryAcceptAll(shop.Front, item, left);
            if (left == 0) continue;
            if (!ReferenceEquals(item.parentInventory, previous))
                throw new InvalidOperationException("A partial merchant return lost an item from its original grid.");
            complete = false;
        }
        return complete;
    }

    internal void ClearMerchantStock()
    {
        if (merchant == null || !ReferenceEquals(bound, merchant))
            throw new InvalidOperationException("Native merchant cleanup needs its own bound counters.");
        entry.ClearFrontInvNonOwned();
        entry.ClearBackInvNonOwned();
    }

    internal void CloseMerchant()
    {
        RequireDetachedMerchant();
        if (merchant.Front.childItems.Count != 0 || merchant.Back.childItems.Count != 0 || merchant.Buffer.childItems.Count != 0)
            throw new InvalidOperationException("Cannot release an engineering shop with items.");
        UnityEngine.Object.Destroy(merchant.Front.handler.gameObject);
        UnityEngine.Object.Destroy(merchant.Back.handler.gameObject);
        UnityEngine.Object.Destroy(merchant.Buffer.handler.gameObject);
        merchant = null;
    }

    internal void ParkMerchant()
    {
        RequireDetachedMerchant();
        // Keep the goods and merchant, but retire the last visitor's quote.
        merchant.DealItem = merchant.TabletItem = merchant.SelectedItem = null;
    }

    internal void CloseParkedMerchant()
    {
        if (merchant == null) return;
        RequireDetachedMerchant();
        using (Enter(true)) ClearMerchantStock();
        CloseMerchant();
    }

    private void RequireDetachedMerchant()
    {
        if (merchant == null || ReferenceEquals(shown, merchant) || ReferenceEquals(bound, merchant))
            throw new InvalidOperationException("The engineering shop must be detached.");
    }
}

[HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.GetAllOwnedItems))]
internal static class ExpeditionMerchantStockOwnership
{
    private static void Postfix(List<GameItem> __result) =>
        NativeWorld.Host?.ExpeditionCounters?.IncludeShopCounterGoods(__result);
}

[HarmonyPatch]
internal static class ExpeditionLocalTradeWindow
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.OpenUI));
        yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.CloseUI));
    }

    private static bool Prefix() => NativeWorld.Host?.ExpeditionCounters?.IsOffscreen != true;
}
