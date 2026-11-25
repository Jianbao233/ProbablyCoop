using System;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

// Shared native transitions run at a world barrier. Store rules, nights and
// location arrival run once. The partner installs a typed checkpoint locally.
internal sealed partial class NativeEffectCampaign : ISyncWorld, ISyncWholeItemMove
{
    private const byte CheckpointVersion = 8;
    private const int ActorLimit = 192 * 1024;
    internal const string SaveKey = EffectItemIds.RunKey;
    internal const string SaveSchema = EffectItemIds.RunSchema;
    private readonly NativeInventoryGrid inventory;
    private readonly bool host;
    private readonly NativeActorState remoteHost = NativeActorState.CreateDefault();
    private TeamTransition running;
    private NativeEffectTransitionAttempt transitionAttempt;
    private Guid run;

    internal WorldSession Session { private get; set; }
    internal bool Dispatching { get; private set; }
    internal bool DispatchingNight => host && Dispatching && running.Kind == TeamTransitionKind.Sleep;
    internal bool Busy => running.Kind != TeamTransitionKind.None;
    internal NativeActorState RemoteHost => remoteHost;
    internal Guid RunId => run;

    public bool CanCarryUpdatedState(SyncTx tx, SyncOp op) => inventory.CanCarryUpdatedState(tx, op);

    internal NativeEffectCampaign(NativeInventoryGrid inventory, bool host)
    {
        this.inventory = inventory;
        this.host = host;
    }

    internal void StartHost(bool resuming)
    {
        var actors = NativeActorSession.EnsureForExisting(PlayerStore.Instance);
        if (actors.CreatedFromArmedRun == resuming || !CoopRun.IsRegistered(actors.Store) ||
            !actors.Store.modData.TryGetValue(SaveKey, out string schema) || schema != SaveSchema)
            throw new InvalidOperationException("The effect campaign requires its own verified cooperative checkpoint.");
        run = actors.Run.Run;
        inventory.RestoreIdentityWatermarks(actors.Store);
        if (!inventory.AdoptHostInventory()) throw new InvalidOperationException("Cannot bind the original inventory graph.");
    }

    internal void WithdrawDeparture() => Session.WithdrawDeparture();

    internal bool CanCarry(byte actor, int backpack)
    {
        bool needsGun = StoreClientData.IsButcherHunting();
        if (backpack == 0) return !needsGun;
        if (!inventory.TryFindAccessible(backpack, actor, out var item) ||
            !GeneralHelper.IsItemOwned(item) || !item.IsTag("BACKPACK_TAG") ||
            needsGun && !GunHelper.HasGunInside(item)) return false;
        var slot = inventory.Carry.Get(ToSlot(actor), NativeActorInventory.CarrySlot.Backpack);
        return slot.childItem == item ||
            slot.childItem == null && item.MayRemove() && item.MaxNumRemove() >= item.unitCount &&
            slot.TryFindOneValidInventorySlot(item)?.numTransfer == item.unitCount;
    }

    private static NativeActorRun.Slot ToSlot(byte actor) =>
        actor == SyncHost.HostPeer ? NativeActorRun.Slot.Host : NativeActorRun.Slot.Partner;

    internal bool CanStart(TeamTransition action, byte initiator)
    {
        if (!host || Busy || !inventory.Ready || action.Day != StoreStation.GetDayCounter()) return false;
        var actors = NativeActorSession.Current;
        var map = MapUIManager.Instance;
        if (actors?.CanCommit != true || map == null || map.IsInTransit) return false;
        var store = actors.Store;
        switch (action.Kind)
        {
            case TeamTransitionKind.Return:
                return map.IsOutside;
            case TeamTransitionKind.Sleep:
                return !map.IsOutside && store.storeState == PlayerStore.StoreState.CLOSED && store.CanEndDay();
            case TeamTransitionKind.Depart:
                var location = AfterhourLocation.Get(action.Destination);
                if (map.IsOutside || location == null || store.storeState != PlayerStore.StoreState.CLOSED ||
                    !store.isAfterhoursIntroduced || store.visitLeftTonight < 1 ||
                    StoreStation.Instance.storeEventManager.IsEventActive("crackdown")) return false;
                using (actors.Enter(ToSlot(initiator)))
                    return location.IsDiscovered() && location.IsAvailable() &&
                        !HealthData.IsSeriouslyWounded() && !StartingPerkContent.IsAgoraphobiaResting();
            default:
                return false;
        }
    }

    internal void Tick()
    {
        if (!inventory.Ready) return;
        UpdateTransitionUi();
        UpdateScratchView();
        if (!host)
        {
            UpdateShutterView();
            UpdateAppraisalView();
            UpdateReplicaPhone();
            UpdateReplicaLogistics();
            PresentShot();
            return;
        }
        if (!Busy)
        {
            RefreshStockForCommerce();
            PublishCommerce();
            TickHostPhone();
            while (Session.TryTakeRequest(out var request))
            {
                if (request.Day != StoreStation.GetDayCounter()) continue;
                if (request.Action == AuthorityAction.Scavenge) Scavenge(NetSession.GuestPeer);
                else if (request.Action == AuthorityAction.SetAppraisalCategory) SetAppraisal(request);
                else if (request.Action == AuthorityAction.Business) ExecuteBusiness(request.Business);
                else if (request.Action == AuthorityAction.Machine) ExecuteMachine(request.Machine);
                else if (request.Action == AuthorityAction.Item) ExecuteItemUse(NetSession.GuestPeer, request.ItemUse);
                else if (request.Action == AuthorityAction.Activity) ExecuteActivity(request.Activity);
            }
            if (!Session.TryBeginTransition(out running, out _)) return;
            transitionAttempt = new NativeEffectTransitionAttempt(run, Time.realtimeSinceStartupAsDouble);
            Execute(running);
            return;
        }
        var actors = NativeActorSession.EnsureForExisting(PlayerStore.Instance);
        if (actors.Terminal || actors.Faulted)
            throw new InvalidOperationException(actors.Failure ?? $"The native campaign ended during a shared transition ({actors.Ending}).");
        bool complete = running.Kind switch
        {
            TeamTransitionKind.Depart => !MapUIManager.Instance.IsInTransit &&
                MapUIManager.Instance.Current?.Id == running.Destination,
            TeamTransitionKind.Return => !MapUIManager.Instance.IsInTransit && !MapUIManager.Instance.IsOutside,
            TeamTransitionKind.Sleep => StoreStation.GetDayCounter() == running.Day + 1 &&
                actors.VerifiedCheckpoint?.Day == running.Day + 1 &&
                actors.VerifiedCheckpoint.Run == run && actors.VerifiedCheckpoint.Slot == actors.NativeSaveSlot,
            _ => false,
        };
        if (transitionAttempt.TryComplete(actors.Run.Run, complete, Time.realtimeSinceStartupAsDouble, PublishSettled))
        {
            running = default;
            transitionAttempt = null;
        }
    }

    private bool PublishSettled() => inventory.AdoptHostInventory() && Session.CompleteTransition(CaptureSettled());

    private void Execute(TeamTransition action)
    {
        Dispatching = true;
        try
        {
            switch (action.Kind)
            {
                case TeamTransitionKind.Depart:
                    SetBackpack(SyncHost.HostPeer, Session.HostBackpack);
                    SetBackpack(NetSession.GuestPeer, Session.GuestBackpack);
                    MapUIManager.Instance.Travel(AfterhourLocation.Get(action.Destination));
                    break;
                case TeamTransitionKind.Return:
                    MapUIManager.Instance.ReturnNow();
                    break;
                case TeamTransitionKind.Sleep:
                    EndOfDayUIManager.Instance.OnEndDayButtonClick();
                    break;
            }
        }
        finally { Dispatching = false; }
    }

    private void SetBackpack(byte actor, int id)
    {
        GameItem backpack = null;
        if (id != 0 && !inventory.TryFindAccessible(id, actor, out backpack))
            throw new InvalidOperationException("A confirmed backpack disappeared before departure.");
        inventory.Carry.SetSelected(ToSlot(actor), backpack);
    }

    internal byte[] Capture()
    {
        if (!host || Busy || !inventory.Ready) return null;
        if (!inventory.AdoptHostInventory()) throw new InvalidOperationException("Cannot refresh the native graph for the joining player.");
        return CaptureSettled();
    }

    private byte[] CaptureSettled()
    {
        var actors = NativeActorSession.EnsureForExisting(PlayerStore.Instance);
        var store = actors.Store;
        string personal = NativeActorProjection.Capture(run, NativeActorState.Capture(store), actors.Run.Partner)
            .ToString(Formatting.None);
        if (System.Text.Encoding.UTF8.GetByteCount(personal) > ActorLimit)
            throw new InvalidDataException("The two roles exceed the checkpoint limit.");
        var writer = new SyncWriter();
        writer.Byte(CheckpointVersion);
        writer.Bytes(run.ToByteArray());
        writer.UInt((uint)StoreStation.GetDayCounter());
        writer.UInt((uint)inventory.GuestWatermark);
        publishedShop = ShopFacts.Capture(store).Encode();
        publishedActors = System.Text.Encoding.UTF8.GetBytes(personal);
        writer.Bytes(publishedShop);
        writer.Text(MapUIManager.Instance.Current?.Id);
        writer.Bytes(publishedActors);
        var graph = inventory.Capture();
#if PROBABLY_COOP_GRID_QA
        RecordCheckpointInventory(graph);
#endif
        writer.Bytes(graph);
        publishedCommerce = CaptureCommerce();
        writer.Bytes(publishedCommerce);
        publishedFacilities = CaptureFacilities();
        writer.Bytes(publishedFacilities);
        publishedPhone = CapturePhone();
        writer.Bytes(publishedPhone);
        WriteMorningReport(writer);
        return writer.ToArray();
    }

    internal bool Install(byte[] checkpoint)
    {
        if (host || checkpoint == null || checkpoint.Length >
            InventoryCheckpoint.MaxBytes + ActorLimit + CommerceLimit + FacilitiesLimit + PhoneLimit + MorningLimit + 256)
            return false;
        var reader = new SyncReader(checkpoint);
        if (reader.Byte() != CheckpointVersion) return false;
        byte[] runId = reader.Bytes(16);
        uint day = reader.UInt();
        uint guestWatermark = reader.UInt();
        byte[] shopBytes = reader.Bytes(ShopFacts.MaxBytes);
        string scene = reader.Text(128);
        byte[] personal = reader.Bytes(ActorLimit);
        byte[] graph = reader.Bytes(InventoryCheckpoint.MaxBytes);
        byte[] customer = reader.Bytes(CommerceLimit);
        byte[] facilityBytes = reader.Bytes(FacilitiesLimit), phoneBytes = reader.Bytes(PhoneLimit);
        if (!TryReadMorningReport(reader, day, out var report) || !reader.AtEnd || runId.Length != 16 ||
            day == 0 || day > int.MaxValue || guestWatermark > int.MaxValue || !EffectItemIds.ValidGuest((int)guestWatermark) ||
            !ShopFacts.TryRead(shopBytes, out var shop) ||
            !InventoryCheckpoint.TryDecode(graph, out var entries) || !TryReadCommerce(customer, out var commerce) ||
            !TryReadFacilities(facilityBytes, out var facilities, out var locations) ||
            !TryReadPhone(phoneBytes, out var phone, out var contacts))
            return false;
        var incomingRun = new Guid(runId);
        if (incomingRun == Guid.Empty || run != Guid.Empty && run != incomingRun) return false;
        var location = scene.Length == 0 ? null : TeamTransition.ValidLocation(scene) ? AfterhourLocation.Get(scene) : null;
        if (scene.Length != 0 && location == null) return false;
        if (!TryPrepareActors(personal, incomingRun, out var actors)) return false;
        NativeEffectLocationView.PrepareGrid(location);
        PresentUpgradeGeometry(facilities);
        if (!inventory.Load(entries)) return false;
        inventory.SeedGuestAllocator((int)guestWatermark);
        bool waking = run != Guid.Empty && day > StoreStation.GetDayCounter() && report != null;
        actors.Commit();
        appraisalChanged = true;
        run = incomingRun;
        StoreStation.Instance.dayCounter = (int)day;
        ApplyShop(shop, animated: false);
        ApplyFacilities(facilityBytes, facilities, locations);
        NativeEffectLocationView.Present(location);
        ApplyCommerce(commerce, restoring: true);
        ApplyPhone(phoneBytes, phone, contacts);
        if (waking) PresentMorningReport(report);
#if PROBABLY_COOP_GRID_QA
        RecordCheckpointInventory(inventory.Capture());
#endif
        return true;
    }

#if PROBABLY_COOP_GRID_QA
    private void RecordCheckpointInventory(byte[] graph)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        string digest = BitConverter.ToString(sha.ComputeHash(graph)).Replace("-", "");
        Debug.Log($"[probably_coop CAMPAIGN] checkpoint-inventory role={(host ? "host" : "guest")} sha={digest}");
    }
#endif

    internal void Retire()
    {
        CloseTransitionUi();
        running = default;
        transitionAttempt = null;
        run = Guid.Empty;
        RetireShot();
        ReleaseCustomer();
        RestoreHostPhone();
        if (host) NativeActorSession.Current?.Dispose();
    }

    internal void StoreIdentityWatermarks(PlayerStore store) => inventory.StoreIdentityWatermarks(store);
}

// A native transition can finish while its disconnected frontend is not ticking.
// A settled attempt from the same run must be confirmed before checking its deadline.
internal sealed class NativeEffectTransitionAttempt
{
    private readonly Guid run;
    private readonly double startedAt;
    private bool confirmed;

    internal NativeEffectTransitionAttempt(Guid run, double startedAt)
    {
        if (run == Guid.Empty) throw new ArgumentException("A transition requires a native run.", nameof(run));
        this.run = run;
        this.startedAt = startedAt;
    }

    internal bool TryComplete(Guid currentRun, bool settled, double now, Func<bool> publish)
    {
        if (confirmed) return false;
        if (currentRun != run)
            throw new InvalidOperationException("The native campaign identity changed during a shared transition.");
        if (settled)
        {
            if (!publish())
                throw new InvalidOperationException("The native campaign could not publish its settled state.");
            confirmed = true;
            return true;
        }
        if (now - startedAt > 20)
            throw new InvalidOperationException("The native campaign did not settle its shared transition.");
        return false;
    }
}

// Only the native view changes here. Location arrival/leave and their original
// economy, narrative and inventory callbacks run on the authority.
internal static class NativeEffectLocationView
{
    private static readonly System.Reflection.MethodInfo SetCurrent =
        AccessTools.PropertySetter(typeof(MapUIManager), nameof(MapUIManager.Current));
    private static readonly System.Reflection.MethodInfo Show =
        AccessTools.Method(typeof(MapUIManager), "ShowStage");
    private static readonly System.Reflection.MethodInfo Hide =
        AccessTools.Method(typeof(MapUIManager), "HideStage");
    private static readonly System.Reflection.MethodInfo Open =
        AccessTools.Method(typeof(MapUIManager), "OpenLocationPanel");
    private static readonly System.Reflection.MethodInfo Close =
        AccessTools.Method(typeof(MapUIManager), "CloseLocationPanel");
    private static readonly System.Reflection.MethodInfo EnsureJacksonTablet =
        AccessTools.Method(typeof(DrJacksonLocation), "EnsureSheetTablet");

    internal static void PrepareGrid(AfterhourLocation location)
    {
        var shop = EmporiumEntry.Instance;
        if (location is DrJacksonLocation)
        {
            if (shop.frontInvinvElement.inventoryShape.height != 21) shop.ExpandFrontCounterGrid();
        }
        else if (shop.frontInvinvElement.inventoryShape.height != 4) shop.ResetFrontCounterGrid();
    }

    internal static void Present(AfterhourLocation location)
    {
        var map = MapUIManager.Instance;
        if (map.Current == location) return;
        var shop = EmporiumEntry.Instance;
        shop.selectedBackpack = null;
        if (map.Current is CommissaryLocation) CommissaryUIManager.Instance.CloseUI();
        if (map.Current is DrJacksonLocation) NegociationUIManager.Instance.CloseUI();
        map.CloseUI(restoreStoreUI: false);
        shop.CloseItemWindowUIWindow();
        Close.Invoke(map, null);
        Hide.Invoke(map, null);
        SetCurrent.Invoke(map, new object[] { location });
        bool atShop = location == null;
        shop.storePhysical.SetActive(atShop);
        shop.storeInteractables.SetActive(atShop);
        var ui = StoreUIManager.Instance;
        ui.storeCalendar.dayTMP.gameObject.SetActive(atShop);
        ui.clientCounter.SetActive(atShop);
        ui.storeCreditDisplay.creditDisplayText.gameObject.SetActive(atShop || location is DrJacksonLocation);
        StoreShutterButton.Instance.blockerGameObject.transform.parent.gameObject.SetActive(atShop);
        shop.invWindow.rectTransform.gameObject.SetActive(atShop);
        shop.trashInvWindow.rectTransform.gameObject.SetActive(atShop);
        shop.docInvWindow.rectTransform.gameObject.SetActive(atShop);
        shop.cassettePlayerWindow.rectTransform.gameObject.SetActive(atShop);
        shop.showcaseWindow.rectTransform.gameObject.SetActive(atShop);
        shop.hiddenWindow.rectTransform.gameObject.SetActive(atShop);
        shop.frontInvWindow.rectTransform.gameObject.SetActive(location?.ShowsStoreCounters == true ||
            atShop && PlayerStore.Instance.storeState == PlayerStore.StoreState.OPEN);
        shop.backInvWindow.rectTransform.gameObject.SetActive(atShop || location.ShowsStoreCounters);
        if (atShop)
        {
            shop.HideAfterhourInv();
            ui.ResetClientPosition();
            map.ResetCreditDisplayPosition();
            StoreSoundPlayer.Instance.PlayStoreAmbient();
        }
        else
        {
            Show.Invoke(map, new object[] { location });
            Open.Invoke(map, new object[] { location });
            if (location is DrJacksonLocation jackson)
            {
                EnsureJacksonTablet.Invoke(jackson, null);
                ui.SetClientPositionToDrJackson();
                map.SetCreditDisplayPositionToDrJackson();
            }
            if (location.UsesAfterhourBag) shop.ShowAfterhourInv();
            else shop.HideAfterhourInv();
            if (!string.IsNullOrEmpty(location.AmbienceId)) StoreSoundPlayer.Instance.PlayAmbient(location.AmbienceId);
        }
        shop.Validate();
    }
}
