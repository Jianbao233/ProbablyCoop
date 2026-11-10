using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed class NativeShop : IShopAuthority, IDisposable
{
    private readonly PlayerStore store;
    private readonly string runId;
    private readonly int slot;
    private readonly ConditionalWeakTable<GameItem, ItemIdentity> itemIds = new ConditionalWeakTable<GameItem, ItemIdentity>();
    private sealed class ItemIdentity { internal uint Id; }
    internal uint ItemId(GameItem item) => item == null ? 0 : itemIds.GetValue(item, _ => new ItemIdentity { Id = ++nextId }).Id;
    private readonly Dictionary<uint, GameItem> items = new Dictionary<uint, GameItem>();
    private readonly HashSet<GameItem> seen = new HashSet<GameItem>();
    private uint nextId;
    private uint heldId;
    private GameItem heldItem;
    internal GameItem HeldForInteraction=>heldItem;
    private bool heldOwnership;
    private double holdExpires;
    private bool committingHeld;
    private bool guestOutside;
    private readonly NativeStoreEvents events = new NativeStoreEvents();
    internal bool GuestOutside => guestOutside;
    private GameItem quotedItem;
    private StoreClientInstance quotedClient;
    private int quotedAmount;
    private ShopAction quotedAction;
    private uint nextQuote;
    private uint offerId;
    private bool allowNativeEnd;
    private int previousDay;
    private byte[] previousSave;
    private double settleDeadline;
    private ShopDay phase;
    private bool checkpointWritten;
    private string[] morningReport = Array.Empty<string>();
    internal string[] FullMorningReport { get; private set; } = Array.Empty<string>();
    internal int FullMorningReportDay { get; private set; }
    private bool failedMutation;

    internal static NativeShop Gate { get; set; }

    internal NativeShop(PlayerStore store)
    {
        this.store = store;
        runId = store.runID;
        slot = store.saveSlotId;
    }

    internal bool Matches(PlayerStore player) => ReferenceEquals(store, player) &&
        player.runID == runId && player.saveSlotId == slot;
    internal bool Settling => phase == ShopDay.Settling;
    internal int GuestMaxRemove(GameItem item)
    {
        bool previous=committingHeld;
        try { committingHeld=true; return item.MaxNumRemove(); }
        finally { committingHeld=previous; }
    }

    private bool Active => PlayerStore.IsInstanceExist() && Matches(PlayerStore.Instance)
        && EmporiumEntry.Instance?.invElement != null && EmporiumEntry.Instance.backInvinvElement != null;

    private bool CanOperate => Active && !failedMutation && NativeWorld.Host?.EndingActive!=true &&
        NativeActorSession.Current?.Faulted!=true && NativeActorSession.Current?.Terminal!=true &&
        phase is not (ShopDay.Settling or ShopDay.SaveUnverified or ShopDay.Ended) &&
        MapUIManager.Instance?.IsOutside == false && ItemMouseDragHandler.current?.IsDraggingItem != true &&
        EndOfDayUIManager.Instance?.IsOpen != true;

    internal void Tick(bool connected)
    {
        if (heldItem != null && (!connected || Time.realtimeSinceStartupAsDouble >= holdExpires))
            ClearHold();
        if (Settling && Time.realtimeSinceStartupAsDouble >= settleDeadline)
            SetDayPhase(ShopDay.SaveUnverified);
    }

    public ShopView Capture()
    {
        if (!Active) return new ShopView();
        var state = new ShopView
        {
            Active = true, Day = Math.Max(0, StoreStation.Instance.dayCounter), Cash = store.GetCash(),
            GuestOutside = guestOutside, CanGoOut = false,
            Shutter = store.storeState switch
            {
                PlayerStore.StoreState.OPEN => 1,
                PlayerStore.StoreState.CLOSED => 2,
                _ => 0
            },
            Phase = failedMutation ? ShopDay.SaveUnverified : phase
        };
        if (!Settling && !failedMutation && phase is not (ShopDay.SaveUnverified or ShopDay.Ended) &&
            TryGetNightPlan(out ulong services, out int cost, out var rows))
        {
            state.Phase = ShopDay.Ready;
            state.NightServices = services;
            state.NightCost = cost;
            state.ServiceCosts = rows;
        }
        if(NativeWorld.Host?.EndingActive==true) state.Phase=ShopDay.Ended;
        if (state.Phase == ShopDay.Saved) state.MorningReport = morningReport;
        seen.Clear();
        items.Clear();
        var entry = EmporiumEntry.Instance;
        if (guestOutside)
        {
            state.SearchesLeft = (byte)(ScavHelper.CanScavenge() ? Math.Max(0, Math.Min(255, ScavHelper.GetScavTimeLeft())) : 0);
            state.MinorWoundChance = (ushort)Mathf.Clamp(Mathf.RoundToInt(ScavHelper.GetMinorWoundChance() * 100), 0, 10000);
            state.MajorWoundChance = (ushort)Mathf.Clamp(Mathf.RoundToInt(ScavHelper.GetMajorWoundChance() * 100), 0, 10000);
            AddItems(state, entry.afterhourInventory, 3);
            AddItems(state, entry.afterhourPocketSlotInvLeft, 4);
            AddItems(state, entry.afterhourPocketSlotInvRight, 5);
        }
        AddItems(state, entry.backInvinvElement, 2);
        if (state.Shutter == 1) AddItems(state, entry.frontInvinvElement, 6);
        state.ShowcaseTier = (byte)Mathf.Clamp(entry.showcaseElement.inventoryShape.width - 7, 0, 2);
        AddItems(state, entry.showcaseElement, 7);
        AddItems(state, entry.invElement, 1);
        if (heldItem != null && (!seen.Contains(heldItem) || GeneralHelper.IsItemOwned(heldItem) != heldOwnership))
            ClearHold();
        state.HeldItem = heldId;

        var item = store.currentNegociatedItem;
        var client = store.currentClientInstance;
        var action = (ShopAction)0;
        int price = 0;
        var blueprint = client?.GetClientBlueprint();
        if (store.isClientArrived && blueprint != null && !string.IsNullOrEmpty(blueprint.displayName))
        {
            state.Customer = blueprint.displayName;
            state.CustomerSprite = string.IsNullOrEmpty(blueprint.spriteName) ? "default" : blueprint.spriteName;
        }
        if (CanOperate && !guestOutside && store.isClientArrived && !NegociationUIManager.IsTradeLocked() &&
            blueprint != null && !IsHeld(item) && IsOrdinary(item) &&
            item.parentInventory == EmporiumEntry.Instance.backInvinvElement)
        {
            long nativePrice = item.GetNegociatedValue();
            if (nativePrice is >= 0 and <= int.MaxValue)
            {
                price = (int)nativePrice;
                var intent = blueprint.clientIntent;
                if (GeneralHelper.IsItemOwned(item))
                {
                    if (intent is StoreClient.ClientIntent.BUY or StoreClient.ClientIntent.SELLNBUY &&
                        store.CanSellItemFromTable() && (!blueprint.useClientBudget || blueprint.GetBudget() >= price))
                        action = ShopAction.Sell;
                }
                else if (intent is StoreClient.ClientIntent.SELL or StoreClient.ClientIntent.SELLNBUY &&
                    store.CanBuyItemFromTable())
                    action = ShopAction.Buy;
            }
        }
        if (!ReferenceEquals(quotedItem, item) || !ReferenceEquals(quotedClient, client) ||
            price != quotedAmount || action != quotedAction)
        {
            quotedItem = item;
            quotedClient = client;
            quotedAmount = price;
            quotedAction = action;
            offerId = action == 0 ? 0 : ++nextQuote;
        }
        if (!guestOutside) events.Capture(state);
        state.OfferId = offerId;
        state.Price = action == 0 ? 0 : price;
        state.OfferAction = action;
        if (action != 0) state.OfferLabel = $"{item.name ?? item.identifier} ×{item.unitCount}";
        return state;
    }

    private void AddItems(ShopView state, GameInventory inventory, byte place)
    {
        foreach (var item in inventory.childItems)
        {
            if (item == null || item.unitCount <= 0 || item.parentInventory != inventory) continue;
            if (state.Items.Count >= ShopProtocol.MaxItems)
            {
                state.MoreItems = true;
                break;
            }
            uint id = ItemId(item);
            if (!seen.Add(item)) continue;
            items.Add(id, item);
            bool owned = GeneralHelper.IsItemOwned(item);
            var replica = new ShopItem
            {
                Id = id, Place = place, Quantity = item.unitCount,
                Label = item.name ?? item.identifier ?? "",
                Atlas = item.spriteAtlasPath ?? "", Sprite = item.spritePath ?? "",
                Owned = owned, Movable = CanHoldFrom(item, owned) && CanOperate && !guestOutside
            };
            var shape = item.modifiedShape;
            if (shape != null && shape.minX is >= 0 and < 24 && shape.minY is >= 0 and < 10 &&
                shape.width > 0 && shape.height > 0 && shape.width * shape.height <= 64)
            {
                ulong cells = 0;
                for (int y = 0; y < shape.height; y++)
                    for (int x = 0; x < shape.width; x++)
                        if (shape.GetLocal(x, y) != 0) cells |= 1UL << (y * shape.width + x);
                if (cells != 0)
                {
                    replica.X = (byte)shape.minX;
                    replica.Y = (byte)shape.minY;
                    replica.Width = (byte)shape.width;
                    replica.Height = (byte)shape.height;
                    replica.Transform = (byte)((shape.flipped ? 4 : 0) | (shape.orientation & 3));
                    replica.Cells = cells;
                }
            }
            state.Items.Add(replica);
        }
    }

    private static bool IsOrdinary(GameItem item) => item != null && item.unitCount > 0 &&
        item.modifiedShape != null && item.contentWindow == null && item is not GameCharacterItem &&
        !item.IsGameItemType("MACHINE") && !item.IsGameItemType("GUN") &&
        !item.IsGameItemType("MODULE") && !item.IsGameItemType("NODE");

    public ShopResult Apply(ShopAction action, uint entity, ShopPlacement placement)
    {
        NativeWorld.Host?.InvalidateTooltipReads();
        if(NativeWorld.Host?.EndingActive==true && action!=ShopAction.Release) return ShopResult.Busy;
        if(action is >= ShopAction.GoScavenge and <= ShopAction.ReturnFromScavenge) return ShopResult.Unavailable;
        if (action == ShopAction.FinishDay) return StartDay();
        if (action == ShopAction.Release && entity != 0 && entity == heldId)
        {
            ClearHold();
            return ShopResult.Applied;
        }
        bool tripOperation = action is >= ShopAction.Scavenge and <= ShopAction.ReturnFromScavenge;
        if (tripOperation)
        {
            if (!guestOutside || !Active || failedMutation ||
                phase is ShopDay.Settling or ShopDay.SaveUnverified or ShopDay.Ended ||
                (action == ShopAction.ReturnFromScavenge && ItemMouseDragHandler.current?.IsDraggingItem == true))
                return ShopResult.Busy;
        }
        else if (!CanOperate || guestOutside) return ShopResult.Busy;
        try
        {
            return action switch
            {
                ShopAction.Move => Move(entity),
                ShopAction.PickUp => Hold(entity),
                ShopAction.Place => Place(entity, placement),
                ShopAction.GoScavenge when entity == 0 => GuestDepart(),
                ShopAction.Scavenge when entity == 0 => GuestScavenge(),
                ShopAction.TakeLoot => CarryLoot(entity, true),
                ShopAction.DropLoot => CarryLoot(entity, false),
                ShopAction.ReturnFromScavenge when entity == 0 => GuestReturn(),
                ShopAction.SelectOffer => SelectOffer(entity),
                >= ShopAction.ToggleShutter and <= ShopAction.ChooseDialogue => events.Apply(store, action, entity),
                ShopAction.Buy or ShopAction.Sell => Trade(action, entity),
                _ => ShopResult.Unavailable
            };
        }
        catch (Exception ex)
        {
            failedMutation = true;
            Debug.LogError($"[probably_coop] native command froze after an exception: {ex}");
            return ShopResult.Failed;
        }
    }

    private bool CanGuestDepart()
    {
        if (guestOutside || heldId != 0 || !CanOperate || TransitUI.IsOpen ||
            !store.isAfterhoursIntroduced || store.storeState != PlayerStore.StoreState.CLOSED ||
            store.visitLeftTonight <= 0 || HealthData.IsSeriouslyWounded() ||
            StoreStation.Instance.storeEventManager.IsEventActive("crackdown") ||
            StartingPerkContent.IsAgoraphobiaResting())
            return false;
        var entry = EmporiumEntry.Instance;
        var location = AfterhourLocation.Get("dumping_ground");
        return location?.IsDiscovered() == true && location.IsAvailable() &&
            entry.afterhourInventory.childItems.Count == 0 && entry.afterhourPocketSlotInvLeft.childItem == null &&
            entry.afterhourPocketSlotInvRight.childItem == null && entry.afterhourPocketSlotInvBackpack.childItem == null;
    }

    private ShopResult GuestDepart()
    {
        if (!CanGuestDepart()) return ShopResult.Unavailable;
        store.visitLeftTonight--;
        store.healthData.wentToAfterhoursLastNight = true;
        store.lastAfterhourOutingDay = StoreStation.Instance.dayCounter;
        ScavHelper.ResetScavenging();
        SerpicoStoryline.OnArriveDumpingGround();
        var visited = store.GetAfterhourState("dumping_ground");
        visited.visits++;
        visited.lastVisitDay = StoreStation.Instance.dayCounter;
        guestOutside = true;
        return ShopResult.Applied;
    }

    private ShopResult GuestScavenge()
    {
        if (!guestOutside || !ScavHelper.CanScavenge()) return ShopResult.Unavailable;
        ScavHelper.ScavengeDumpingGrounds();
        return ShopResult.Applied;
    }

    private ShopResult CarryLoot(uint entity, bool take)
    {
        if (!guestOutside || !items.TryGetValue(entity, out var item) || !GeneralHelper.IsItemOwned(item))
            return ShopResult.Unavailable;
        var entry = EmporiumEntry.Instance;
        if (take)
        {
            if (item.parentInventory != entry.afterhourInventory) return ShopResult.Unavailable;
            var target = entry.afterhourPocketSlotInvLeft.childItem == null ? entry.afterhourPocketSlotInvLeft :
                entry.afterhourPocketSlotInvRight.childItem == null ? entry.afterhourPocketSlotInvRight : null;
            return target == null ? ShopResult.NoSpace : MoveTo(item, target, target.TryFindOneValidInventorySlot(item));
        }
        if (item.parentInventory != entry.afterhourPocketSlotInvLeft &&
            item.parentInventory != entry.afterhourPocketSlotInvRight) return ShopResult.Unavailable;
        var ground = entry.afterhourInventory;
        return MoveTo(item, ground, ground.TryFindOneValidInventorySlot(item, keepOrientation: true));
    }

    private ShopResult GuestReturn()
    {
        if (!guestOutside) return ShopResult.Unavailable;
        var entry = EmporiumEntry.Instance;
        var transferred = new List<(GameItem Item, GameSlotInventory Pocket, GridShape Shape)>();
        foreach (var pocket in new[] { entry.afterhourPocketSlotInvLeft, entry.afterhourPocketSlotInvRight })
        {
            var item = pocket.childItem;
            if (item == null) continue;
            var to = entry.invElement;
            var slot = to.TryFindOneValidInventorySlot(item, keepOrientation: true);
            if (!Fits(slot, to, item.unitCount))
            {
                to = entry.backInvinvElement;
                slot = to.TryFindOneValidInventorySlot(item, keepOrientation: true);
            }
            var shape = item.modifiedShape.Clone();
            var result = MoveTo(item, to, slot);
            if (result != ShopResult.Applied)
            {
                foreach (var moved in transferred)
                {
                    if (RestoreParent(moved.Item, moved.Pocket, moved.Item.parentInventory, moved.Shape, moved.Item.unitCount))
                        continue;
                    failedMutation = true;
                    return ShopResult.Failed;
                }
                return result;
            }
            transferred.Add((item, pocket, shape));
        }
        SerpicoStoryline.OnLeaveDumpingGround(entry.afterhourInventory);
        foreach (var item in entry.afterhourInventory.childItems) entry.afterhourInventory.Expel(item);
        guestOutside = false;
        return ShopResult.Applied;
    }

    [HarmonyPatch(typeof(MapUIManager), nameof(MapUIManager.OpenUI))]
    private static class KeepHostInStoreDuringGuestTrip
    {
        private static bool Prefix() => Gate?.guestOutside != true;
    }

    [HarmonyPatch(typeof(MapUIManager), nameof(MapUIManager.Travel))]
    private static class PreventConcurrentNativeTravel
    {
        private static bool Prefix() => Gate?.guestOutside != true;
    }

    private bool IsHeld(GameItem item) => item != null && ReferenceEquals(heldItem, item) && !committingHeld;

    private void ClearHold()
    {
        heldId = 0;
        heldItem = null;
        holdExpires = 0;
    }

    private bool CanHoldFrom(GameItem item, bool owned)
    {
        if (!IsOrdinary(item)) return false;
        var entry = EmporiumEntry.Instance;
        if (item.parentInventory == entry.invElement || item.parentInventory == entry.showcaseElement)
            return owned;
        if (item.parentInventory == entry.frontInvinvElement && store.storeState != PlayerStore.StoreState.OPEN)
            return false;
        return (item.parentInventory == entry.backInvinvElement || item.parentInventory == entry.frontInvinvElement) &&
            (owned || store.isClientArrived && !NegociationUIManager.IsTradeLocked());
    }

    private ShopResult Hold(uint entity)
    {
        if (heldItem != null || NativeWorld.Host?.PartnerHasHand==true) return ShopResult.Busy;
        if (!items.TryGetValue(entity, out var item) || !CanHoldFrom(item, GeneralHelper.IsItemOwned(item)) ||
            ReferenceEquals(ItemMouseDragHandler.current?.GetCurrentItem(), item) ||
            item.MaxNumRemove() < item.unitCount)
            return ShopResult.Unavailable;
        heldId = entity;
        heldItem = item;
        heldOwnership = GeneralHelper.IsItemOwned(item);
        holdExpires = Time.realtimeSinceStartupAsDouble + 30;
        return ShopResult.Applied;
    }

    private ShopResult Place(uint entity, ShopPlacement placement)
    {
        if (entity == 0 || entity != heldId || !placement.Valid || heldItem == null ||
            GeneralHelper.IsItemOwned(heldItem) != heldOwnership || !CanHoldFrom(heldItem, heldOwnership))
            return ShopResult.Unavailable;
        var entry = EmporiumEntry.Instance;
        var item = heldItem;
        var to = placement.Container switch
        {
            1 when heldOwnership => entry.invElement,
            2 => entry.backInvinvElement,
            6 when store.storeState == PlayerStore.StoreState.OPEN => entry.frontInvinvElement,
            7 when heldOwnership => entry.showcaseElement,
            _ => null
        };
        if (to == null) return ShopResult.Unavailable;
        var shape = new GridShapeBuilder(item.modifiedShape).SetTransform(placement.X, placement.Y,
            (placement.Transform & 4) != 0, placement.Transform & 3);
        committingHeld = true;
        try
        {
            var marker = to.TryInventorySlot(item, item.unitCount,
                new Vector2(float.PositiveInfinity, float.PositiveInfinity), shape, item.modifiedState);
            var result = MoveTo(item, to, marker);
            if (result == ShopResult.Applied) ClearHold();
            return result;
        }
        finally
        {
            committingHeld = false;
        }
    }

    private ShopResult Move(uint entity)
    {
        if (!items.TryGetValue(entity, out var item) || IsHeld(item) || !IsOrdinary(item) ||
            !GeneralHelper.IsItemOwned(item)) return ShopResult.Unavailable;
        var shop = EmporiumEntry.Instance;
        var from = item.parentInventory;
        var to = from == shop.invElement ? shop.backInvinvElement : from == shop.backInvinvElement ? shop.invElement : null;
        return to == null ? ShopResult.Unavailable : MoveTo(item, to, to.TryFindOneValidInventorySlot(item, keepOrientation: true));
    }

    private ShopResult MoveTo(GameItem item, GameInventory to, SlotMarker target)
    {
        if(target!=null && NativeWorld.Host?.BlocksLocalSlot(target)==true) return ShopResult.Busy;
        var from = item.parentInventory;
        var shape = item.modifiedShape.Clone();
        int count = item.unitCount;
        bool owned = GeneralHelper.IsItemOwned(item);
        if (!Fits(target, to, count)) return ShopResult.NoSpace;
        try
        {
            if (target.TryAcceptOnce(count) == count && item.parentInventory == to && item.unitCount == count &&
                GeneralHelper.IsItemOwned(item) == owned) return ShopResult.Applied;
        }
        catch
        {
            RestoreParent(item, from, to, shape, count, owned);
            throw;
        }

        if (!RestoreParent(item, from, to, shape, count, owned))
        {
            failedMutation = true;
            Debug.LogError("[probably_coop] failed to verify inventory rollback; cooperative commands frozen");
        }
        return ShopResult.Failed;
    }

    private static bool RestoreParent(GameItem item, GameInventory from, GameInventory to, GridShape shape, int count, bool owned = true)
    {
        if (item.unitCount != count || GeneralHelper.IsItemOwned(item) != owned) return false;
        if (item.parentInventory == from)
        {
            item.modifiedShape = shape;
            item.ValidateShapeState();
            item.Validate();
            return true;
        }
        if (item.parentInventory == to && !to.Expel(item)) return false;
        if (item.parentInventory != null) return false;
        item.modifiedShape = shape;
        return from.UncheckedAccept(item) && item.parentInventory == from;
    }

    private static bool Fits(SlotMarker marker, GameInventory expected, int count) =>
        marker != null && marker.inventory == expected && marker.targetItem == null &&
        marker.itemGridShape != null && marker.numTransfer >= count;

    private ShopResult SelectOffer(uint entity)
    {
        if (!store.isClientArrived || store.currentClientInstance == null || NegociationUIManager.IsTradeLocked() ||
            !items.TryGetValue(entity, out var item) || !IsOrdinary(item) || IsHeld(item) ||
            item.parentInventory != EmporiumEntry.Instance.backInvinvElement)
            return ShopResult.Unavailable;
        store.currentNegociatedItem = item;
        NegociationUIManager.Instance.UpdateUI(item);
        return ShopResult.Applied;
    }

    private ShopResult Trade(ShopAction action, uint entity)
    {
        var item = store.currentNegociatedItem;
        if (IsHeld(item) || entity == 0 || entity != offerId || action != quotedAction || item != quotedItem ||
            store.currentClientInstance == null || !ReferenceEquals(store.currentClientInstance, quotedClient) ||
            NegociationUIManager.IsTradeLocked() || !IsOrdinary(item) ||
            item.parentInventory != EmporiumEntry.Instance.backInvinvElement ||
            item.GetNegociatedValue() != quotedAmount) return ShopResult.Stale;
        int before = store.GetCash();
        if (action == ShopAction.Buy)
        {
            if (GeneralHelper.IsItemOwned(item) || !store.CanBuyItemFromTable()) return ShopResult.Unavailable;
            // The native purchase changes ownership before attempting to move the
            // item. If stock is full, the purchased item stays on the counter.
            NegociationUIManager.Instance.BuyItem();
        }
        else
        {
            var client = quotedClient.GetClientBlueprint();
            if (!GeneralHelper.IsItemOwned(item) || !store.CanSellItemFromTable() ||
                (client.useClientBudget && client.GetBudget() < quotedAmount) ||
                before > int.MaxValue - quotedAmount) return ShopResult.Unavailable;
            NegociationUIManager.Instance.SellItem();
        }

        bool bought = action == ShopAction.Buy;
        int after = store.GetCash();
        bool transferred = GeneralHelper.IsItemOwned(item) == bought;
        if (transferred && after == (bought ? before - quotedAmount : before + quotedAmount))
            return ShopResult.Applied;
        if (!transferred && after == before) return ShopResult.Unavailable;

        failedMutation = true;
        Debug.LogError("[probably_coop] trade result could not be verified; cooperative commands frozen");
        return ShopResult.Failed;
    }

    private bool TryGetNightPlan(out ulong services, out int total, out int[] rows)
    {
        services = 0;
        total = 0;
        rows = Array.Empty<int>();
        if (!Active || NativeActorSession.Current?.CanCommit==false || store.storeState!=PlayerStore.StoreState.CLOSED || guestOutside || heldId != 0 ||
            NativeWorld.Host?.PartnerHasHand==true || ItemMultiSelectHandler.current?.IsGroupDragging==true ||
            ItemMouseDragHandler.current?.IsDraggingItem == true || MapUIManager.Instance==null ||
            MapUIManager.Instance.IsOutside || MapUIManager.Instance.IsInTransit ||
            !store.CanEndDay() || store.storeService == null || store.storeService.Count > 64) return false;
        long cost = 0;
        var costs = new List<int>();
        for (int i = 0; i < store.storeService.Count; i++)
        {
            var service = store.storeService[i];
            if (!service.IsActive()) continue;
            services |= 1UL << i;
            cost += service.cost;
            costs.Add(service.cost);
        }
        if (cost is < int.MinValue or > int.MaxValue || (cost > 0 && cost > store.GetCash())) return false;
        total = (int)cost;
        rows = costs.ToArray();
        return true;
    }

    private ShopResult StartDay()
    {
        if (Settling || failedMutation || phase is ShopDay.SaveUnverified or ShopDay.Ended ||
            !TryGetNightPlan(out _, out _, out _))
            return ShopResult.Unavailable;
        try
        {
            previousSave = HashSlot();
            if (previousSave == null) return ShopResult.Failed;
            previousDay = StoreStation.Instance.dayCounter;
            checkpointWritten = false;
            morningReport = Array.Empty<string>();
            FullMorningReport=Array.Empty<string>(); FullMorningReportDay=0;
            settleDeadline = Time.realtimeSinceStartupAsDouble + 20;
            NativeWorld.Host?.EndSharedPhone();
            SetDayPhase(ShopDay.Settling);
            Gate = this;
            allowNativeEnd = true;
            EndOfDayUIManager.Instance.OnEndDayButtonClick();
            return ShopResult.Applied;
        }
        catch (Exception ex)
        {
            SetDayPhase(ShopDay.SaveUnverified);
            Debug.LogError($"[probably_coop] day transition exception: {ex}");
            return ShopResult.Failed;
        }
        finally { allowNativeEnd = false; }
    }

    private void VerifyCheckpoint(PlayerStore savedStore)
    {
        if (!Settling || !Matches(savedStore) || StoreStation.Instance.dayCounter != previousDay + 1) return;
        try
        {
            var file = HashSlot();
            if (file == null || file.AsSpan().SequenceEqual(previousSave)) return;
            checkpointWritten = CoopRun.VerifyCheckpoint(store, previousDay + 1);
        }
        catch (Exception ex)
        {
            checkpointWritten = false;
            Debug.LogWarning($"[probably_coop] saved checkpoint could not be read: {ex.Message}");
        }
    }

    private void FinishDay()
    {
        if (Settling)
            SetDayPhase(checkpointWritten && Active && StoreStation.Instance.dayCounter == previousDay + 1
                ? ShopDay.Saved : ShopDay.SaveUnverified);
    }

    private byte[] HashSlot()
    {
        string path = CoopRun.SlotPath(store);
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return stream.Length > 0 ? sha.ComputeHash(stream) : null;
    }

    private void SetDayPhase(ShopDay value)
    {
        phase = value;
        Debug.Log($"[probably_coop] native day status={value}");
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.MaxNumRemove))]
    private static class LockHeldSource
    {
        private static bool Prefix(GameItem __instance, ref int __result)
        {
            if (Gate?.IsHeld(__instance) != true) return true;
            __result = 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.Expel))]
    private static class KeepHeldItemInItsGrid
    {
        private static bool Prefix(GameItem item, ref bool __result)
        {
            if (Gate?.IsHeld(item) != true) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce))]
    private static class LockHeldTarget
    {
        private static bool Prefix(SlotMarker __instance, ref int __result)
        {
            if (Gate?.IsHeld(__instance.targetItem) != true) return true;
            __result = 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.DismissCurrentClient))]
    private static class ReleaseHeldCustomerGoods
    {
        private static void Prefix()
        {
            var shop = Gate;
            if (shop?.heldItem != null && !shop.heldOwnership) shop.ClearHold();
        }
    }

    [HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.SellItem))]
    private static class PreventSaleOfHeldItem
    {
        private static bool Prefix() => Gate?.IsHeld(Gate.store.currentNegociatedItem) != true;
    }

    [HarmonyPatch(typeof(EndOfDayUIManager), nameof(EndOfDayUIManager.OnEndDayButtonClick))]
    private static class PreventDuplicateSettlement
    {
        private static bool Prefix()
        {
            if (Gate?.allowNativeEnd == true) return true;
            if (Gate == null && !NativeGuest.Active) return true;
            NativeDayUi.Submit();
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SaveGame))]
    private static class VerifyNativeSave
    {
        private static void Postfix(PlayerStore __instance) => Gate?.VerifyCheckpoint(__instance);
    }

    [HarmonyPatch(typeof(StartOfDayUIManager), nameof(StartOfDayUIManager.OpenUI))]
    private static class FinishAfterNativeWake
    {
        private static void Prefix()
        {
            var shop = Gate;
            if (shop?.Settling != true || !shop.checkpointWritten) return;
            var logs = shop.store.nightLogs;
            shop.FullMorningReport=logs.ToArray(); shop.FullMorningReportDay=StoreStation.Instance.dayCounter;
            int count = Math.Min(logs.Count, ShopProtocol.MaxReportLines);
            shop.morningReport = logs.GetRange(0, count).ToArray();
        }

        private static void Postfix() => Gate?.FinishDay();
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.ExecuteGameOver))]
    private static class ReportGameOver
    {
        private static void Postfix(PlayerStore __instance)
        {
            var shop = Gate;
            if (shop != null && shop.Settling && shop.Matches(__instance)) shop.SetDayPhase(ShopDay.Ended);
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(Gate, this)) Gate = null;
    }
}

internal static class InventoryIdentityCleanup
{
    internal static void RemoveWhereMissing(this Dictionary<GameItem, uint> ids, HashSet<GameItem> present)
    {
        var remove = new List<GameItem>();
        foreach (var item in ids.Keys)
            if (!present.Contains(item)) remove.Add(item);
        foreach (var item in remove) ids.Remove(item);
    }
}
