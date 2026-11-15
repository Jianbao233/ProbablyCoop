using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    private static readonly EntityKey ShopKey = EntityKey.Scalar("shop.facts");
    private static readonly EntityKey ActorsKey = EntityKey.Scalar("shop.actors");
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private byte[] publishedShop, publishedActors;
    private bool publishingAction;
    private bool finishShutterAnimation;
    private bool appraisalChanged;
    private GameItem appraisalCertificate;
    internal bool ApplyingGuestAppraisal { get; private set; }
    internal bool ExecutingStockActivation { get; private set; }

    internal bool RunningNativeRules => Busy || publishingAction;

    // Inspect the implementation of the active delegate. A mod may preserve
    // the item id while replacing the callback with its own code.
    internal static Type StockActivationSource(GameItem item)
    {
        var callback = item.onActivateSlotItemFunc;
        if (callback == null || callback.GetInvocationList().Length != 1 || item.parentInventory?.onActivateSlotItemFunc != null ||
            callback.Method.Module.Assembly != typeof(GameItem).Assembly) return null;
        Type source = callback.Method.DeclaringType;
        while (source?.DeclaringType != null) source = source.DeclaringType;
        if (source == typeof(ConsumableHelper) || source == typeof(FoodItemDirectory) ||
            source == typeof(MedsItemDirectory) || source == typeof(AquacultureItems) ||
            source == typeof(LiquidLuckHelper) || source == typeof(WineHelper) ||
            source == typeof(GutterflowPelletHelper) || source == typeof(IndustrialFishFeedHelper) ||
            source == typeof(BlackLampHelper) || source == typeof(LouBombHelper) ||
            source == typeof(MaterialDirectory)) return source;
        if (source == typeof(MiscItemDirectory) && (item.IsID("scratcher_stack") || item.IsTag(InsStampHelper.FOOD_STAMP_TAG) ||
            item.IsTag(ScratcherHelper.SCRATCHER_TAG) && ScratcherHelper.CanRedeemPrize(item)) ||
            source == typeof(AmenitiesItemDirectory) && item.IsTag(LotteryHelper.LOTTERY_PAPER_TAG) &&
            LotteryHelper.CanRedeemPrize(item)) return source;
        return null;
    }

    internal static bool ValidStockActivation(GameItem item, Type source) =>
        source != typeof(MiscItemDirectory) || !item.IsTag(InsStampHelper.FOOD_STAMP_TAG) ||
        item.FindItemFeatureByCategory(ItemFeatureList.CATEGORY_GENUINE_STAMP)?.realCondition?.identifier != null;

    internal bool RequestItemUse(GameItem item, ItemUse use, byte argument = 0)
    {
        var intent = new ItemIntent(use, item.uniqueId, inventory.ControlProof(item), argument);
        if (!intent.Valid) return false;
        return host ? ExecuteItemUse(SyncHost.HostPeer, intent) :
            Session.Request(AuthorityAction.Item, StoreStation.GetDayCounter(), itemUse: intent);
    }

    internal ulong StockProof(GameItem item) => inventory.ControlProof(item);

    private bool ExecuteItemUse(byte actor, ItemIntent intent)
    {
        var actors = NativeActorSession.Current;
        bool fromDrag = intent.Use is ItemUse.FillAtTap or ItemUse.EmptyAtDrain;
        if (actors == null || actors.Terminal || actors.Faulted || actors.NightActive ||
            !inventory.TryFindAccessible(intent.Item, actor, out var item) || inventory.ControlProof(item) != intent.Proof ||
            !GeneralHelper.IsItemEffectivelyOwned(item) || TouchesHand(new[] { item }, fromDrag ? item : null, actor) ||
            !BeginAuthorityAction()) return false;
        bool finished = false;
        try
        {
            using (actors.Enter(ToSlot(actor)))
                finished = ApplyItemUse(item, intent);
            return finished;
        }
        finally { EndAuthorityAction(finished); }
    }

    private bool ApplyItemUse(GameItem item, ItemIntent intent)
    {
        switch (intent.Use)
        {
            case ItemUse.Activate:
                Type source = StockActivationSource(item);
                if (source == null || !ValidStockActivation(item, source) ||
                    !item.MayActivateSlotItem() || !item.CanActivateSlotItem()) return false;
                ExecutingStockActivation = true;
                try { item.ActivateSlotItem(); }
                finally { ExecutingStockActivation = false; }
                return true;
            case ItemUse.Unload:
                if (item.contentWindow == null || item.contentWindow.isLocked || item.IsTag("IS_UNLOAD_DISABLED_TAG")) return false;
                item.TryUnloadAll(intent.Argument != 0);
                return true;
            case ItemUse.Pin:
                if (!ItemPinHelper.MayPin(item) || ItemPinHelper.IsPinned(item) == (intent.Argument != 0)) return false;
                ItemPinHelper.SetPinned(item, intent.Argument != 0);
                return true;
            case ItemUse.FillAtTap:
            case ItemUse.EmptyAtDrain:
                if (MapUIManager.Instance.IsOutside || !item.IsTag("LIQUID_CONTAINER_TAG")) return false;
                bool fill = intent.Use == ItemUse.FillAtTap;
                if (fill && (!EmporiumEntry.IsWaterTapUnlocked() || WaterHelper.IsFull(item))) return false;
                var sink = fill ? EmporiumEntry.Instance.faucet : EmporiumEntry.Instance.drain;
                if (sink == null || !item.MayTarget(sink) || !item.CanTarget(sink)) return false;
                item.Target(sink);
                return true;
            default:
                return ApplyScratch(item, intent.Use, intent.Argument);
        }
    }

    internal bool BeginAuthorityAction()
    {
        if (!host || Busy || publishingAction || Session.Host?.Closed != false ||
            !Session.Host.IsAttached(NetSession.GuestPeer)) return false;
        publishingAction = true;
        return true;
    }

    internal void EndAuthorityAction(bool succeeded)
    {
        if (!publishingAction) throw new InvalidOperationException("There is no authoritative action to complete.");
        publishingAction = false;
        if (!succeeded) return;
        using var frame = NativePerformance.MeasureHostEffectFrame(host);
        using var performance = NativePerformance.Measure("host.effect_capture", NativePerformance.FrameWork.Capture);
        InventoryChange[] changes;
        using (NativePerformance.Measure("host.effect_inventory"))
            if (!inventory.CaptureHostChanges(out changes))
                throw new InvalidOperationException("Original action left an unsupported inventory graph.");
        var ops = new List<SyncOp>(changes.Length + 5);
        foreach (var change in changes) ops.Add(change.ToOp());
        var actors = NativeActorSession.EnsureForExisting(PlayerStore.Instance);
        byte[] shop = ShopFacts.Capture(actors.Store).Encode();
        byte[] personal = Utf8.GetBytes(NativeActorProjection.Capture(run,
            NativeActorState.Capture(actors.Store), actors.Run.Partner).ToString(Formatting.None));
        if (personal.Length > ActorLimit) throw new InvalidDataException("The two roles exceed the action limit.");
        if (!shop.AsSpan().SequenceEqual(publishedShop)) ops.Add(SyncOp.Put(ShopKey, shop));
        if (!personal.AsSpan().SequenceEqual(publishedActors)) ops.Add(SyncOp.Put(ActorsKey, personal));
        CommerceChanged();
        AppendCommerce(ops);
        byte[] facilities = CaptureFacilities();
        if (!facilities.AsSpan().SequenceEqual(publishedFacilities)) ops.Add(SyncOp.Put(FacilitiesKey, facilities));
        AppendPhone(ops);
        if (ops.Count == 0) return;
        if (!SyncCodec.Fits(ops) || changes.Length > 0 && !inventory.RecordLocal(changes))
            throw new InvalidOperationException("Original action exceeded the incremental transaction limits.");
        publishedShop = shop;
        publishedActors = personal;
        publishedFacilities = facilities;
        Session.Host.Local(ops);
    }

    internal void RequestScavenge()
    {
        if (MapUIManager.Instance?.Current?.Id != AfterhourLocation.DUMPING_GROUND || !ScavHelper.CanScavenge())
            return;
        if (host) Scavenge(SyncHost.HostPeer);
        else Session.Request(AuthorityAction.Scavenge, StoreStation.GetDayCounter());
    }

    private void Scavenge(byte actor)
    {
        var actors = NativeActorSession.Current;
        if (actors == null || actors.Terminal || actors.Faulted ||
            MapUIManager.Instance?.Current?.Id != AfterhourLocation.DUMPING_GROUND || !BeginAuthorityAction()) return;
        bool finished = false;
        try
        {
            using (actors.Enter(actor == SyncHost.HostPeer ? NativeActorRun.Slot.Host : NativeActorRun.Slot.Partner))
                ScavHelper.ScavengeDumpingGrounds();
            finished = true;
        }
        finally { EndAuthorityAction(finished); }
    }

    internal void OpenAppraisal(GameItem certificate)
    {
        if (!inventory.CanInteract(certificate) || !certificate.CanActivateSlotItem()) return;
        appraisalCertificate = certificate;
        NativeWorld.RenderAppraisal(remoteHost, sound: true);
    }

    internal void RequestAppraisal(AppraisalItemType category, bool allowed)
    {
        if (!inventory.CanInteract(appraisalCertificate) || !AppraisalViewOpen || category == null) return;
        Session.Request(AuthorityAction.SetAppraisalCategory, StoreStation.GetDayCounter(),
            appraisalCertificate.uniqueId, category.id, allowed);
        appraisalChanged = true;
    }

    private void SetAppraisal(AuthorityRequest request)
    {
        var actors = NativeActorSession.Current;
        if (actors == null || actors.Terminal || actors.Faulted ||
            !inventory.TryFindAccessible(request.Item, NetSession.GuestPeer, out var certificate) ||
            AppraisalRankHelper.GetCertificateRank(certificate) < 0 || !GeneralHelper.IsItemEffectivelyOwned(certificate))
            return;
        var type = AppraisalItemRegistry.Get(request.Category);
        int rank = AppraisalRankHelper.GetRank();
        if (type == null || !AppraisalItemRegistry.CanBlock(type, rank) ||
            !request.Allowed && AppraisalItemRegistry.IsLastAllowed(type, rank) ||
            request.Allowed != AppraisalItemRegistry.IsBlocked(type, rank) || !BeginAuthorityAction()) return;
        bool finished = false;
        ApplyingGuestAppraisal = true;
        try
        {
            NativeWorld.ApplyAppraisalCategory(type, request.Allowed);
            finished = true;
        }
        finally
        {
            ApplyingGuestAppraisal = false;
            EndAuthorityAction(finished);
        }
    }

    internal static bool AppraisalViewOpen =>
        CustomUIManager.Instance?.GetWindow(NativeWorld.AppraisalWindowId)?.IsAlive == true;

    private void UpdateAppraisalView()
    {
        if (appraisalCertificate == null) return;
        if (!AppraisalViewOpen)
        {
            appraisalCertificate = null;
            return;
        }
        if (!inventory.CanInteract(appraisalCertificate))
        {
            CustomUIManager.Instance.CloseWindow(NativeWorld.AppraisalWindowId);
            appraisalCertificate = null;
            return;
        }
        if (!appraisalChanged) return;
        appraisalChanged = false;
        NativeWorld.RenderAppraisal(remoteHost, sound: false);
    }

    public bool TryApply(SyncTx tx)
    {
        if (host)
        {
            var arrivals = HopperArrivals(tx);
            if (!inventory.TryApply(tx)) return false;
            ScheduleHopperArrivals(arrivals);
            StockChanged();
            RefreshLogisticsViews(tx);
            return true;
        }
        if (tx.Origin != SyncHost.HostPeer)
        {
            if (!inventory.TryApply(tx)) return false;
            RefreshLogisticsViews(tx);
            return true;
        }
        var itemOps = new List<SyncOp>();
        ShopFacts? shop = null;
        NativeActorProjection.Prepared actors = null;
        JObject commerce = null;
        JObject facilities = null, phone = null;
        Dictionary<string, AfterhourLocationState> locations = null;
        Dictionary<long, StorePhoneClient> contacts = null;
        byte[] facilitiesBytes = null, phoneBytes = null;
        foreach (var op in tx.Ops)
        {
            if (op.Key.Space == SyncSpace.Item)
            {
                itemOps.Add(op);
                continue;
            }
            if (op.Kind != SyncOpKind.Put) return false;
            if (op.Key.Equals(ShopKey) && !shop.HasValue && ShopFacts.TryRead(op.Data, out var value))
                shop = value;
            else if (op.Key.Equals(ActorsKey) && actors == null && TryPrepareActors(op.Data, run, out var personal))
                actors = personal;
            else if (op.Key.Equals(CommerceKey) && commerce == null && TryReadCommerce(op.Data, out var state))
                commerce = state;
            else if (op.Key.Equals(FacilitiesKey) && facilities == null && TryReadFacilities(op.Data, out facilities, out locations))
                facilitiesBytes = op.Data;
            else if (op.Key.Equals(PhoneKey) && phone == null && TryReadPhone(op.Data, out phone, out contacts))
                phoneBytes = op.Data;
            else return false;
        }
        if (facilities != null) PresentUpgradeGeometry(facilities);
        if (itemOps.Count > 0 && !inventory.TryApply(new SyncTx
            { Origin = tx.Origin, Barrier = tx.Barrier, Serial = tx.Serial, Ops = itemOps })) return false;
        if (actors != null)
        {
            actors.Commit();
            appraisalChanged = true;
        }
        if (shop.HasValue) ApplyShop(shop.Value, animated: true);
        if (facilities != null) ApplyFacilities(facilitiesBytes, facilities, locations);
        if (commerce != null) ApplyCommerce(commerce, restoring: false);
        if (phone != null) ApplyPhone(phoneBytes, phone, contacts);
        if (itemOps.Count != 0) RefreshLogisticsViews(tx);
        return true;
    }

    public bool TryConfirm(SyncTx tx) => inventory.TryConfirm(tx);
    public void Revert(SyncTx tx) => inventory.Revert(tx);
    public bool CanHold(byte actor, EntityKey key) => inventory.CanHold(actor, key);
    public byte[] Read(EntityKey key) => key.Equals(ShopKey) ? publishedShop :
        key.Equals(ActorsKey) ? publishedActors : key.Equals(CommerceKey) ? publishedCommerce :
        key.Equals(FacilitiesKey) ? publishedFacilities : key.Equals(PhoneKey) ? publishedPhone : inventory.Read(key);

    private bool TryPrepareActors(byte[] personal, Guid expectedRun, out NativeActorProjection.Prepared prepared)
    {
        prepared = null;
        if (personal == null || personal.Length == 0 || personal.Length > ActorLimit) return false;
        try
        {
            prepared = NativeActorProjection.Read(JObject.Parse(Utf8.GetString(personal)), expectedRun)
                .PrepareGuest(PlayerStore.Instance, remoteHost);
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private void ApplyShop(ShopFacts facts, bool animated)
    {
        var store = PlayerStore.Instance;
        bool wasOpen = store.storeState == PlayerStore.StoreState.OPEN;
        bool opened = facts.State == PlayerStore.StoreState.OPEN;
        store.storeState = facts.State;
        store.playerLocation = facts.Location;
        store.playerCash = facts.Cash;
        store.visitLeftTonight = facts.Visits;
        store.scratchersStartedTotal = facts.ScratchStarted;
        store.scratchersFinishedTotal = facts.ScratchFinished;
        store.scratcherWinningsTotal = facts.ScratchWinnings;
        store.isGamblersCoinRewardGiven = facts.GamblersCoinAwarded;
        store.dealMakerActivatedThisCustomer = facts.DealActivated;
        store.dealMakerBargainBonus = facts.DealBonus;
        var ui = StoreUIManager.Instance;
        if (wasOpen && !opened) ui.closeShutterConfirmationPanel.SetActive(false);
        ui.storeCreditDisplay.creditDisplayText.text = facts.Cash + GeneralHelper.CreditIcon();
        if (!animated || wasOpen != opened)
        {
            finishShutterAnimation = animated;
            NativeShopPresentation.PresentShutter(StoreShutterButton.Instance, opened, animated);
            if (opened) EmporiumEntry.Instance.ShowFrontArea();
            else if (!animated) EmporiumEntry.Instance.HideFrontArea();
        }
    }

    private void UpdateShutterView()
    {
        if (!finishShutterAnimation || StoreShutterButton.Instance is not StoreShutterButton door) return;
        bool opened = PlayerStore.Instance.storeState == PlayerStore.StoreState.OPEN;
        float position = NativeShopPresentation.Fraction(door);
        if (opened ? position < 0.999f : position > 0.001f) return;
        finishShutterAnimation = false;
        if (opened) door.blockerMaskObject.gameObject.SetActive(false);
        else EmporiumEntry.Instance.HideFrontArea();
    }

    private readonly struct ShopFacts
    {
        internal const int MaxBytes = 40;
        internal readonly PlayerStore.StoreState State;
        internal readonly PlayerStore.PlayerLocation Location;
        internal readonly int Cash, Visits;
        internal readonly int ScratchStarted, ScratchFinished, ScratchWinnings;
        internal readonly bool GamblersCoinAwarded, DealActivated;
        internal readonly int DealBonus;

        private ShopFacts(PlayerStore.StoreState state, PlayerStore.PlayerLocation location, int cash, int visits,
            int scratchStarted, int scratchFinished, int scratchWinnings, bool gamblersCoinAwarded,
            bool dealActivated, int dealBonus)
        {
            State = state;
            Location = location;
            Cash = cash;
            Visits = visits;
            ScratchStarted = scratchStarted;
            ScratchFinished = scratchFinished;
            ScratchWinnings = scratchWinnings;
            GamblersCoinAwarded = gamblersCoinAwarded;
            DealActivated = dealActivated;
            DealBonus = dealBonus;
        }

        internal static ShopFacts Capture(PlayerStore store) =>
            new ShopFacts(store.storeState, store.playerLocation, store.playerCash, store.visitLeftTonight,
                store.scratchersStartedTotal, store.scratchersFinishedTotal, store.scratcherWinningsTotal,
                store.isGamblersCoinRewardGiven, store.dealMakerActivatedThisCustomer, store.dealMakerBargainBonus);

        internal byte[] Encode()
        {
            var writer = new SyncWriter();
            writer.Byte((byte)State);
            writer.Byte((byte)Location);
            writer.Long(Cash);
            writer.Long(Visits);
            writer.UInt((uint)ScratchStarted);
            writer.UInt((uint)ScratchFinished);
            writer.UInt((uint)ScratchWinnings);
            writer.Bool(GamblersCoinAwarded);
            writer.Bool(DealActivated);
            writer.Long(DealBonus);
            return writer.ToArray();
        }

        internal static bool TryRead(byte[] data, out ShopFacts facts)
        {
            facts = default;
            if (data == null || data.Length > MaxBytes) return false;
            var reader = new SyncReader(data);
            var state = (PlayerStore.StoreState)reader.Byte();
            var location = (PlayerStore.PlayerLocation)reader.Byte();
            long cash = reader.Long(), visits = reader.Long();
            uint started = reader.UInt(), finished = reader.UInt(), winnings = reader.UInt();
            bool gamblersCoinAwarded = reader.Bool();
            bool dealActivated = reader.Bool();
            long dealBonus = reader.Long();
            if (!reader.Ok || !reader.AtEnd || state > PlayerStore.StoreState.MORNING ||
                location > PlayerStore.PlayerLocation.INVENTOR || cash < int.MinValue || cash > int.MaxValue ||
                visits < int.MinValue || visits > int.MaxValue || started > int.MaxValue || finished > int.MaxValue ||
                winnings > int.MaxValue || dealBonus < int.MinValue || dealBonus > int.MaxValue) return false;
            facts = new ShopFacts(state, location, (int)cash, (int)visits, (int)started, (int)finished, (int)winnings,
                gamblersCoinAwarded, dealActivated, (int)dealBonus);
            return true;
        }
    }
}
