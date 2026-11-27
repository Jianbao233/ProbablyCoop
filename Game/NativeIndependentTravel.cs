using System;
using HarmonyLib;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private const string ConfirmedIndependentReturn = "return-confirmed";
    private static readonly System.Reflection.PropertyInfo NativeMapLocation =
        AccessTools.Property(typeof(MapUIManager), nameof(MapUIManager.Current));
    private static readonly System.Reflection.MethodInfo ArriveEarly = TravelHook("FireOnAfterhourArriveEarly");
    private static readonly System.Reflection.MethodInfo ArriveLate = TravelHook("FireOnAfterhourArriveLate");
    private static readonly System.Reflection.MethodInfo LeaveEarly = TravelHook("FireOnAfterhourLeaveEarly");
    private static readonly System.Reflection.MethodInfo LeaveLate = TravelHook("FireOnAfterhourLeaveLate");
    private NativeExpeditionGround splitGround;
    private bool? independentTravelPreference;
    private bool remoteIndependentTravel, remoteFinalLeave, independentTransition;
    private readonly ulong[] earlyMerchantLeave = new ulong[2];
    internal bool IndependentTravel => host ? splitGround != null : remoteIndependentTravel;
    internal NativeExpeditionCounters ExpeditionCounters => splitGround?.Counters;
#if PROBABLY_COOP_GRID_QA
    private string qaRemoteTravelPeerLocation = "";
    internal string QaOtherTravelLocation => host ? ObserveTravelPosition(NativeActorRun.Slot.Partner).Site : qaRemoteTravelPeerLocation;
    internal GameGridInventory QaExpeditionGround(string location) => siteLedger.At(location);
#endif

    private static System.Reflection.MethodInfo TravelHook(string name) => AccessTools.Method(typeof(ModHook), name);

    private void EnableIndependentTravel()
    {
        if (splitGround != null || !HasActorProjection || actorRun == null ||
            MapUIManager.Instance?.IsOutside != false) return;
        bool enabled = independentTravelPreference ??= CoopSettings.IndependentTravel;
#if PROBABLY_COOP_GRID_QA
        enabled |= NativeBackgroundQa.Enabled &&
            Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-independent-travel") >= 0;
#endif
        if (!enabled || !siteLedger.EveryoneHome) return;
        var entry = EmporiumEntry.Instance;
        if (entry?.afterhourWindow?.childElement == null || entry.afterhourInventory == null) return;
        int day = StoreStation.Instance.dayCounter;
        if (!siteLedger.BeginNight(day, actorRun.OutingDay(NativeActorRun.Slot.Host) == day,
            actorRun.OutingDay(NativeActorRun.Slot.Partner) == day)) return;
        splitGround = new NativeExpeditionGround(entry);
    }

    // Native services and inventory rules read the active MapUIManager and
    // EmporiumEntry. Rebind their data for a synchronous authority operation.
    // No window or active stage is switched by a remote command.
    private sealed class NativeOutingContext : IDisposable
    {
        private readonly MapUIManager map;
        private readonly EmporiumEntry entry;
        private readonly AfterhourLocation previousLocation;
        private readonly GameGridInventory previousGround;
        private readonly IDisposable counterBinding;

        internal NativeOutingContext(NativeExpeditionGround inventory, AfterhourLocation location, GameGridInventory ground)
        {
            map = MapUIManager.Instance;
            entry = EmporiumEntry.Instance;
            previousLocation = map.Current;
            previousGround = entry.afterhourInventory;
            counterBinding = inventory.Counters.Enter(location is DrJacksonLocation);
            try
            {
                NativeMapLocation.SetValue(map, location);
                entry.afterhourInventory = ground;
            }
            catch { counterBinding?.Dispose(); throw; }
        }

        public void Dispose()
        {
            try
            {
                entry.afterhourInventory = previousGround;
                NativeMapLocation.SetValue(map, previousLocation);
            }
            finally { counterBinding?.Dispose(); }
        }
    }

    internal IDisposable EnterExpeditionSite(NativeActorRun.Slot actor)
    {
        if (splitGround == null || !host) return null;
        string site = siteLedger.Where((int)actor).Site;
        var location = site.Length == 0 ? null : AfterhourLocation.Get(site);
        var ground = site.Length == 0 ? splitGround.AtShop : siteLedger.At(site);
        if (ground == null || site.Length != 0 && location == null)
            throw new InvalidOperationException("The participant's expedition site has no bound native state.");
        var map = MapUIManager.Instance;
        return ReferenceEquals(map.Current, location) && ReferenceEquals(EmporiumEntry.Instance.afterhourInventory, ground)
            ? null : new NativeOutingContext(splitGround, location, ground);
    }

    private void TickRemoteScavenging()
    {
        if (splitGround == null || siteLedger.Where((int)NativeActorRun.Slot.Partner).Site != AfterhourLocation.DUMPING_GROUND ||
            siteLedger.Where((int)NativeActorRun.Slot.Host).Site == AfterhourLocation.DUMPING_GROUND) return;
        using var actor = EnterPartnerOperation();
        using var site = EnterExpeditionSite(NativeActorRun.Slot.Partner);
        SerpicoStoryline.TickDumpingGround(EmporiumEntry.Instance.afterhourInventory);
    }

    private ushort IndependentTravelRequest(string destination, byte player, string expected, uint backpack, string backpackProof)
    {
        if (player is not (1 or 2) || independentTransition || shop.BlocksWorldCommands || EndingActive ||
            NativeActorSession.Current is not { } session || session.Faulted || session.Terminal)
            return (ushort)ShopResult.Busy;
        if (destination == "cancel") return (ushort)ShopResult.Applied;
        var actor = (NativeActorRun.Slot)(player - 1);
        var position = ObserveTravelPosition(actor);
        if ((player == 2 || expected != null) && expected != TravelProof(actor, position))
            return (ushort)ShopResult.Stale;
        bool holding = actor == NativeActorRun.Slot.Partner ? PartnerHasHand :
            ItemMouseDragHandler.current?.IsDraggingItem == true || ItemMultiSelectHandler.current?.IsGroupDragging == true;
        if (holding || shop.HeldForInteraction != null || session.Activities.HasExecuting || session.Activities.BlockingChannel != null ||
            NegociationUIManager.IsTradeLocked())
            return (ushort)ShopResult.Busy;

        independentTransition = true;
        try
        {
            using var role = session.Enter(actor);
            return destination is "return" or ConfirmedIndependentReturn
                ? ReturnIndependent(session, actor, position, destination == ConfirmedIndependentReturn)
                : DepartIndependent(session, actor, position, destination, backpack, backpackProof);
        }
        catch (Exception error)
        {
            session.MarkFault(error);
            throw;
        }
        finally { independentTransition = false; }
    }

    private int IndependentVisitsLeft(NativeActorRun.Slot actor)
    {
        // Rev13 grants a visit when the shop closes. After the Host uses it,
        // the original field is zero while the other actor may still go out.
        if (siteLedger.VisitsLeft((int)NativeActorRun.Slot.Host) != 0 && PlayerStore.Instance.visitLeftTonight <= 0)
            return 0;
        return siteLedger.VisitsLeft((int)actor);
    }

    internal bool CanReturnOfflinePartner => host && !closed && !peerConnected && !independentTransition &&
        splitGround != null && shop?.CanOfferForcedDay == true && MapUIManager.Instance?.IsOutside == false &&
        siteLedger.Where((int)NativeActorRun.Slot.Host).Site.Length == 0 &&
        siteLedger.Where((int)NativeActorRun.Slot.Partner).Site.Length != 0;

    internal (AfterhourLocation Location, string Proof, ulong PeerGeneration, bool HasGroundItems)? PrepareOfflinePartnerReturn()
    {
        if (!CanReturnOfflinePartner) return null;
        const NativeActorRun.Slot partner = NativeActorRun.Slot.Partner;
        var position = ObserveTravelPosition(partner);
        var location = AfterhourLocation.Get(position.Site);
        if (location == null) return null;
        return (location, TravelProof(partner, position), peerGeneration,
            siteLedger.At(position.Site)?.childItems.Count > 0);
    }

    internal ShopResult ReturnOfflinePartner(string proof, ulong expectedPeerGeneration)
    {
        if (!CanReturnOfflinePartner || expectedPeerGeneration != peerGeneration) return ShopResult.Stale;
        if (ItemMouseDragHandler.current?.IsDraggingItem == true || ItemMultiSelectHandler.current?.IsGroupDragging == true)
            return ShopResult.Busy;
        return (ShopResult)IndependentTravelRequest(ConfirmedIndependentReturn, 2, proof, 0, "");
    }

    private ushort DepartIndependent(NativeActorSession session, NativeActorRun.Slot actor,
        TravelSiteLedger<GameGridInventory>.Position position, string destination, uint backpack, string backpackProof)
    {
        var location = AfterhourLocation.Get(destination);
        var store = session.Store;
        if (position.Site.Length != 0 || IndependentVisitsLeft(actor) == 0 ||
            location is not (DumpingGroundLocation or CommissaryLocation or DrJacksonLocation) || !location.IsDiscovered() ||
            !location.IsAvailable() || store.storeState != PlayerStore.StoreState.CLOSED || !store.isAfterhoursIntroduced ||
            HealthData.IsSeriouslyWounded() || StartingPerkContent.IsAgoraphobiaResting() ||
            StoreStation.Instance.storeEventManager.IsEventActive("crackdown")) return (ushort)ShopResult.Unavailable;
        using (EnterExpeditionSite(actor))
        {
            if (!ValidTravelBackpack(backpack, backpackProof, out var selected)) return (ushort)ShopResult.Stale;
            var carry = session.Carry;
            var previous = carry.Selected(actor);
            carry.SetSelected(actor, selected);
            if (!carry.TransferSelectedBackpack(actor).Complete)
            {
                carry.SetSelected(actor, previous);
                return (ushort)ShopResult.NoSpace;
            }
            // The native shop departure collects the story locker left on
            // the home counter. Keep that shared operation, without clearing
            // the counter or hiding the view of the participant who stays in.
            LouOperativeHelper.OnLeaveStore();
        }

        session.Activities.RetireActorScene(actor);
        bool firstArrival = siteLedger.At(destination) == null;
        if (!siteLedger.TryEnter((int)actor, position, destination, () => splitGround.Create(location), out var ground))
            throw new InvalidOperationException("An admitted native departure lost its expedition reservation.");
        int day = StoreStation.Instance.dayCounter;
        session.Run.RecordOuting(actor, day);
        bool merchant = location is DrJacksonLocation;
        bool newStock = firstArrival && merchant && splitGround.Counters.OpenMerchant();
        using (new NativeOutingContext(splitGround, location, ground))
        {
            ArriveEarly.Invoke(null, new object[] { destination });
            if (newStock) splitGround.Counters.StockMerchant();
            else if (firstArrival && !merchant) location.Arrive();
            else if (location is DumpingGroundLocation) ScavHelper.ResetScavenging();
            var state = store.GetAfterhourState(destination);
            state.visits++;
            state.lastVisitDay = day;
            ArriveLate.Invoke(null, new object[] { destination });
        }
        if (actor == NativeActorRun.Slot.Host)
        {
            TutorialUIManager.Instance?.OnLeaveStore();
            splitGround.Show(ground);
            splitGround.Counters.Show(merchant);
            PresentTravelLocation(destination);
        }
        nextCapture = 0;
        return (ushort)ShopResult.Applied;
    }

    private ushort ReturnIndependent(NativeActorSession session, NativeActorRun.Slot actor,
        TravelSiteLedger<GameGridInventory>.Position position, bool confirmed)
    {
        if (!siteLedger.TryPrepareLeave((int)actor, position, out var leaving))
            return (ushort)ShopResult.Unavailable;
        if (leaving.LastVisitor && !confirmed && leaving.State.childItems.Count != 0)
            return (ushort)ShopResult.Stale;
        var location = AfterhourLocation.Get(position.Site);
        bool merchant = location is DrJacksonLocation;
        bool keepMerchantStock = merchant && siteLedger.VisitsLeft(1 - (int)actor) != 0;
        if (merchant && leaving.LastVisitor && !TryReturnMerchantGoods(actor, leaving.State))
            return (ushort)ShopResult.NoSpace;
        using (new NativeOutingContext(splitGround, location, leaving.State))
        {
            int leavingActor = (int)actor;
            if (!merchant || earlyMerchantLeave[leavingActor] != position.Generation)
            {
                LeaveEarly.Invoke(null, new object[] { position.Site });
                if (merchant) earlyMerchantLeave[leavingActor] = position.Generation;
            }
            if (leaving.LastVisitor)
            {
                if (merchant)
                {
                    // A leave hook can place or award items after the first transfer.
                    if (!TryReturnMerchantGoods(actor, leaving.State)) return (ushort)ShopResult.NoSpace;
                    if (!keepMerchantStock) splitGround.Counters.ClearMerchantStock();
                }
                else
                {
                    // Commissary's Leave only closes the local trading UI.
                    if (location is not CommissaryLocation || actor == NativeActorRun.Slot.Host) location.Leave();
                    NativeActorTravelReturn.ClearGround(leaving.State, location.CountsDumpedWaste);
                }
            }
        }
        if (!siteLedger.TryLeave((int)actor, leaving, confirmed || leaving.State.childItems.Count == 0))
            throw new InvalidOperationException("An admitted native return lost its expedition reservation.");
        session.Activities.RetireActorScene(actor);
        using (new NativeOutingContext(splitGround, null, splitGround.AtShop))
        {
            var carry = session.Carry.ReturnCarryToShop(actor);
            if (!carry.Complete)
                StoreUIManager.Instance.Notify("店铺空间不足，未放下的物品仍保留在你的随身栏。");
        }
        if (actor == NativeActorRun.Slot.Host)
        {
            session.Store.visitLeftTonight = 0;
            splitGround.Show(null);
            splitGround.Counters.Show(false);
            bool closeCommissary = location is CommissaryLocation && !leaving.LastVisitor;
            if (closeCommissary) CommissaryUIManager.Instance.CloseUI();
            PresentTravelLocation("");
            // The ledger consumed the visit already. Restore the actual host's
            // native fixtures and return hooks without charging it a second time.
            EmporiumEntry.Instance.OnArriveStore(fromMap: true);
            TutorialUIManager.Instance?.OnReturnFromAfterhours();
        }
        if (leaving.LastVisitor)
        {
            if (keepMerchantStock) splitGround.Counters.ParkMerchant();
            else if (merchant) splitGround.Counters.CloseMerchant();
            splitGround.Release(leaving.State);
        }
        // The original late hook runs at home, after the native map return.
        // A reward created by a Mod at this point belongs to the returning
        // player's shop context, even when the host is still outside.
        using (new NativeOutingContext(splitGround, null, splitGround.AtShop))
            LeaveLate.Invoke(null, new object[] { position.Site });
        nextCapture = 0;
        return (ushort)ShopResult.Applied;
    }

    private bool TryReturnMerchantGoods(NativeActorRun.Slot actor, GameGridInventory ground)
    {
        using var shopContext = new NativeOutingContext(splitGround, null, splitGround.AtShop);
        if (splitGround.Counters.ReturnMerchantItems(ground)) return true;

        nextCapture = 0;
        tripRefusal = "trip_merchant_no_space";
        if (actor == NativeActorRun.Slot.Partner)
            StoreUIManager.Instance.Notify(ModHelper.GetLocalized("probably_coop", tripRefusal));
        return false;
    }

    internal void RequestIndependentReturn()
    {
        var actor = host ? NativeActorRun.Slot.Host : NativeActorRun.Slot.Partner;
        string proof = host ? TravelProof(actor, ObserveTravelPosition(actor)) : remoteTripProof;
        bool warning = host
            ? siteLedger.TryPrepareLeave((int)actor, siteLedger.Where((int)actor), out var departure) && departure.LastVisitor
            : remoteFinalLeave;
        var map = MapUIManager.Instance;
        if (warning && map.Current != null && map.CheckItemLeftOnGround(
                () => ProposeTrip(ConfirmedIndependentReturn, proofOverride: proof), map.Current.GetLeaveWarning()))
            return;
        ProposeTrip("return", proofOverride: proof);
    }
}
