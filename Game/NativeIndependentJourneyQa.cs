#if PROBABLY_COOP_GRID_QA
using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool independentTravelQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-independent-travel") >= 0;
    private readonly bool independentSameSiteQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-independent-same-site") >= 0;
    private readonly bool independentMerchantQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-independent-merchant") >= 0;
    private readonly bool independentMerchantStaggeredQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-independent-merchant-staggered") >= 0;
    private readonly bool independentShortSleeperQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-independent-short-sleeper") >= 0;
    private readonly bool independentReconnectQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-independent-reconnect") >= 0;
    private readonly bool independentMerchantResumeQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-independent-resume") >= 0;
    private readonly bool independentOfflineRecallQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-independent-offline-recall") >= 0;
    private readonly bool extensionExpeditionQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-extension-expedition") >= 0;
    private int independentStep;
    private double independentFinishAt;
    private GameGridInventory independentShopCounter, independentFoodGround;
    private MachineryIntake independentGroundIntake;
    private const string QaFoodMarker = "processed_meat";
    private GameItemElement journeyFoodHeld;
    private SlotMarker journeyFoodDrop;
    private int journeyFoodUnits;
    private ulong journeyDropFloor;
    private double journeyHoldUntil, journeyHoldDeadline;

    private bool UsesOriginalJourneyGround(EmporiumEntry entry) =>
        independentGroundIntake != null &&
        ReferenceEquals((entry.afterhourWindow.childElement as MachineryPanelRow)?.GetPanel("ground")?.content,
            independentGroundIntake) && ReferenceEquals(independentGroundIntake.content, entry.afterhourInventory);

    private void CompleteIndependentGuest(string result)
    {
        // Keep the link up until the runner has collected the host's final save and inventory checks.
        done = true;
        Record(result, "native guest checks complete; room stays connected for the host checks");
    }

    private void TickIndependentJourney(NativeShop shop, double now)
    {
        var world = host ? NativeWorld.Host : NativeWorld.Guest;
        if (world?.IndependentTravel != true) return;
        var map = MapUIManager.Instance;
        var entry = EmporiumEntry.Instance;
        var store = PlayerStore.Instance;
        const string food = AfterhourLocation.COMMISSARY;
        string otherScene = world.QaOtherTravelLocation;
        if (host)
        {
            string destination = independentSameSiteQa ? food : AfterhourLocation.DUMPING_GROUND;
            switch (independentStep)
            {
                case 0:
                    independentShopCounter = entry.backInvinvElement;
                    var originalRow = entry.afterhourWindow.childElement as MachineryPanelRow;
                    independentGroundIntake = originalRow?.GetPanel("ground")?.content as MachineryIntake;
                    Require(UsesOriginalJourneyGround(entry), "the native ground intake was missing at startup");
                    store.isAfterhoursIntroduced = true;
                    store.isStampIntroduced = true;
                    store.visitLeftTonight = 1;
                    store.storeState = PlayerStore.StoreState.CLOSED;
                    store.commissaryData.redeemedCredit = 100;
                    store.commissaryData.GetItemData(QaFoodMarker).StockCount = 2;
                    independentStep++;
                    break;
                case 1:
                    if (otherScene != food) return;
                    Require(!map.IsOutside && ReferenceEquals(entry.backInvinvElement, independentShopCounter) &&
                        shop?.CanReloadWorld == false, "the guest left but the host's shop or reload guard did not stay in the right state");
                    independentFoodGround = world.QaExpeditionGround(food);
                    if (!ContainsJourneyFood(independentFoodGround)) return;
                    Require(!ContainsJourneyFood(entry.afterhourInventory), "a remote purchase leaked into the host's shop ground");
                    map.Travel(AfterhourLocation.Get(destination));
                    if (map.Current?.Id == destination) independentStep++;
                    break;
                case 2:
                    if (store.commissaryData.GetItemData(QaFoodMarker).StockCount != 0) return;
                    Require(otherScene == food && ReferenceEquals(entry.backInvinvElement, independentShopCounter) &&
                        UsesOriginalJourneyGround(entry) &&
                        ReferenceEquals(independentFoodGround, entry.afterhourInventory) == independentSameSiteQa,
                        "native inventories were not shared or separated by location");
                    Record("PASS expedition grid scope", $"host={destination} guest={otherScene} same grid={independentSameSiteQa}");
                    if (VerifyIndependentTutorialReturn(map))
                    {
                        Require(ContainsJourneyFood(independentFoodGround), "the first return cleared the remaining guest's ground");
                        int day = StoreStation.Instance.dayCounter;
                        Require(shop.ForceDay(day) == ShopResult.Unavailable &&
                                shop.LastNightBlocker == NativeShop.NightBlocker.Outside &&
                                StoreStation.Instance.dayCounter == day,
                            "forced settlement did not respect the guest's still-active outing");
                        Record("PASS settlement waits for independent return", "DAY-102 while the host was home and the guest was still outside");
                        independentStep++;
                    }
                    break;
                case 3:
                    if (otherScene.Length != 0) return;
                    Require(world.QaExpeditionGround(food) == null && independentFoodGround.childItems.Count == 0 &&
                        store.commissaryData.GetItemData(QaFoodMarker).StockCount == 0 &&
                        UsesOriginalJourneyGround(entry) &&
                        ReferenceEquals(entry.backInvinvElement, independentShopCounter) && shop?.CanReloadWorld == true,
                        "the last return failed to retire the used ground or changed shop/stock state");
                    independentFinishAt = now + 2;
                    independentStep++;
                    break;
                case 4:
                    if (now >= independentFinishAt) Finish("PASS independent host travel");
                    break;
            }
            return;
        }

        switch (independentStep)
        {
            case 0:
                if (store.storeState != PlayerStore.StoreState.CLOSED || !AfterhourLocation.Get(food).IsDiscovered() || world.HasPendingWork) return;
                map.Travel(AfterhourLocation.Get(food));
                independentStep++;
                break;
            case 1:
                if (map.Current?.Id != food) return;
                store.commissaryData.BuyItem(QaFoodMarker);
                independentStep++;
                break;
            case 2:
                if (!ContainsJourneyFood(entry.afterhourInventory) || otherScene.Length == 0 ||
                    store.commissaryData.GetItemData(QaFoodMarker).StockCount != 1 || world.HasPendingWork) return;
                Record("PASS simultaneous travel", $"guest={food} peer={otherScene}");
                journeyFoodHeld = entry.afterhourInventory.childItems.OfType<GameItemElement>().First(item => item.identifier == QaFoodMarker);
                journeyFoodDrop = FindJourneyGroundDrop(entry.afterhourInventory, journeyFoodHeld);
                journeyFoodUnits = journeyFoodHeld.unitCount;
                BeginAtNativeSlot(journeyFoodHeld, journeyFoodDrop, holdAcrossFrames: true);
                journeyHoldDeadline = now + 12;
                independentStep++;
                break;
            case 3:
                Require(now < journeyHoldDeadline && ItemMouseDragHandler.current.IsDraggingItem && otherScene.Length != 0,
                    "the guest's ground pickup was lost while the host was outside");
                if (!world.HasConfirmedGesture)
                {
                    Require(journeyHoldUntil == 0, "the host revoked a confirmed pickup while both locations were unchanged");
                    return;
                }
                if (journeyHoldUntil == 0) journeyHoldUntil = now + 0.7;
                if (now < journeyHoldUntil) return;
                journeyDropFloor = world.LastGestureCompletion;
                PositionDragAtSlot(journeyFoodHeld, journeyFoodDrop);
                ReleaseNativeDrag();
                independentStep++;
                break;
            case 4:
                Require(now < journeyHoldDeadline, "the guest's ground move did not settle");
                if (world.HasPendingWork) return;
                Require(world.LastGestureCompletion > journeyDropFloor && world.LastGestureResult == (ushort)ShopResult.Applied &&
                    ReferenceEquals(journeyFoodHeld.parentInventory, entry.afterhourInventory) &&
                    journeyFoodHeld.unitCount == journeyFoodUnits &&
                    journeyFoodHeld.modifiedShape.minX == journeyFoodDrop.itemGridShape.minX &&
                    journeyFoodHeld.modifiedShape.minY == journeyFoodDrop.itemGridShape.minY,
                    "the guest did not keep the confirmed item and placement after releasing it");
                Record("PASS held item across independent scenes", $"guest={food} host={otherScene}; original pickup kept through periodic checks and landed at the chosen cell");
                store.commissaryData.BuyItem(QaFoodMarker);
                independentStep++;
                break;
            case 5:
                if (otherScene.Length != 0 || extensionExpeditionQa && !world.QaExpeditionExtensionReady) return;
                Require(ContainsJourneyFood(entry.afterhourInventory), "a peer's departure cleared the guest ground");
                map.Return();
                var leave = GenericConfirmationUIManager.Instance;
                Require(leave.panel.activeSelf, "the final visitor did not see the native item warning");
                leave.CancelButtonPressed();
                Require(map.IsOutside && ContainsJourneyFood(entry.afterhourInventory), "cancelling the warning changed the scene or items");
                map.Return();
                Require(leave.panel.activeSelf, "return confirmation could not be reopened");
                leave.ConfirmButtonPressed();
                independentStep++;
                break;
            case 6:
                if (!map.IsOutside)
                {
                    Require(store.visitLeftTonight == 0 && store.saveSlotId == -1, "guest return gave another visit or created a local save");
                    CompleteIndependentGuest("PASS independent guest travel");
                }
                break;
        }
    }

    private static bool ContainsJourneyFood(GameGridInventory ground) =>
        ground?.childItems.Any(item => item.identifier == QaFoodMarker) == true;

    private static SlotMarker FindJourneyGroundDrop(GameGridInventory ground, GameItem item)
    {
        var source = item.modifiedShape;
        for (int y = 0; y < ground.inventoryShape.height; y++)
        for (int x = 0; x < ground.inventoryShape.width; x++)
        {
            if (source.minX == x && source.minY == y) continue;
            var shape = new GridShapeBuilder(source).SetPosition(x, y);
            var slot = ground.TryInventorySlot(item, item.unitCount, shape, item.modifiedState);
            if (slot?.IsValid() == true && slot.numTransfer == item.unitCount) return slot;
        }
        throw new InvalidOperationException("The commissary fixture has no alternate ground cell for the pickup.");
    }

    private bool VerifyIndependentTutorialReturn(MapUIManager map)
    {
        var confirm = GenericConfirmationUIManager.Instance;
        if (confirm.panel.activeSelf) return false;
        var tutorial = TutorialUIManager.Instance;
        var stage = AccessTools.Field(typeof(TutorialUIManager), "tutorialStage");
        int previousStage = TutorialStage();
        bool previousHint = tutorial.tutorialSleep.activeSelf;
        stage.SetValue(tutorial, 17);
        tutorial.tutorialSleep.SetActive(false);
        try
        {
            map.Return();
            if (map.IsOutside && confirm.panel.activeSelf) confirm.ConfirmButtonPressed();
            if (map.IsOutside) return false;
            Require(TutorialStage() == 18 && tutorial.tutorialSleep.activeSelf,
                "the native tutorial did not prompt the host to sleep after returning");
            Record("PASS independent tutorial return", "native sleep hint and stage after the host returned");
            return true;
        }
        finally
        {
            stage.SetValue(tutorial, previousStage);
            tutorial.tutorialSleep.SetActive(previousHint);
        }
    }

    private GameItem qaOriginalShopGoods, qaMerchantPurchase, qaRemainingOffer;
    private GameGridInventory qaMerchantFront, qaMerchantBack;
    private (GameGridInventory Grid, GameItem Item, int Quantity, int X, int Y, int Rotation, bool Flipped)[] qaMerchantOffers;
    private int qaOwnedBeforeReturn, qaPurchasedUnits, qaMerchantDay;
    private bool qaSawPeerAtMerchant;
    private bool qaStaggeredVisitCompleted;
    private bool qaSawMerchantDisconnect;
    private string qaOfflineReturnProof;
    private ulong qaOfflineReturnPeer;
    private const string MerchantReconnectFixture = "qa-engineering-visit.json";

    private void ObserveIndependentMerchantDisconnect(RoomSelector room, CoopPanel panel, NativeShop shop)
    {
        if (!host || !independentReconnectQa || independentStep != 2 || qaSawMerchantDisconnect) return;
        var world = NativeWorld.Host;
        const string merchant = AfterhourLocation.DR_JACKSON;
        Require(room.IsHost && room.Code.Length != 0 && world?.QaOtherTravelLocation == merchant &&
            ReferenceEquals(world.QaExpeditionGround(merchant), independentFoodGround) &&
            independentFoodGround.childItems.Contains(qaMerchantPurchase) && qaMerchantFront.childItems.Contains(qaRemainingOffer) &&
            shop?.CanReloadWorld == false && !world.PartnerHasHand,
            "disconnect changed the partner's visit, stock, purchase or day guard");
        var request = world.PrepareOfflinePartnerReturn();
        Require(request.HasValue && request.Value.Location.Id == merchant,
            "a disconnected partner could not be returned without rejoining");
        qaOfflineReturnProof = request.Value.Proof;
        qaOfflineReturnPeer = request.Value.PeerGeneration;
        Record("PASS independent host retained expedition after real disconnect",
            "host stayed at the shop, original destination and inventory still exist, no guest hand lease");
        if (independentOfflineRecallQa)
        {
            panel.QaConfirmOfflinePartnerReturn();
            Require(world.QaOtherTravelLocation.Length == 0 && world.QaExpeditionGround(merchant) == null &&
                shop.CanReloadWorld && world.ReturnOfflinePartner(qaOfflineReturnProof, qaOfflineReturnPeer) == ShopResult.Stale,
                "offline recall did not retire the visit or allowed a duplicate recall");
            Record("PASS independent host recalled partner", "original confirmation and final visitor cleanup ran once");
        }
        qaSawMerchantDisconnect = true;
    }

    private static JObject MerchantVisitStamp(NativeWorld world, GameItem groundItem) => new JObject
    {
        ["day"] = StoreStation.Instance.dayCounter,
        ["cash"] = PlayerStore.Instance.playerCash,
        ["site"] = MapUIManager.Instance.Current?.Id,
        ["remainingVisits"] = PlayerStore.Instance.visitLeftTonight,
        ["entity"] = world.PointerIdentity(groundItem),
        ["item"] = groundItem.identifier,
        ["owned"] = GeneralHelper.IsItemOwned(groundItem),
        ["count"] = groundItem.unitCount,
        ["x"] = groundItem.modifiedShape.minX,
        ["y"] = groundItem.modifiedShape.minY,
        ["orientation"] = groundItem.modifiedShape.orientation,
        ["flipped"] = groundItem.modifiedShape.flipped
    };

    private void TickIndependentMerchant(CoopSession session, NativeShop shop, double now)
    {
        var world = host ? NativeWorld.Host : NativeWorld.Guest;
        if (world?.IndependentTravel != true) return;
        var store = PlayerStore.Instance;
        var entry = EmporiumEntry.Instance;
        var map = MapUIManager.Instance;
        const string merchant = AfterhourLocation.DR_JACKSON;
        string peerSite = world.QaOtherTravelLocation;

        if (host)
        {
            switch (independentStep)
            {
                case 0:
                    qaMerchantDay = StoreStation.Instance.dayCounter;
                    independentShopCounter = entry.backInvinvElement;
                    store.isAfterhoursIntroduced = true;
                    store.storeClientData.isJacksonIntroduced = true;
                    store.visitLeftTonight = 1;
                    store.storeState = PlayerStore.StoreState.CLOSED;
                    store.playerCash = 10000;
                    qaOriginalShopGoods = DirectoryMaster.Item("toilet_paper");
                    GeneralHelper.SetItemOwned(qaOriginalShopGoods, true);
                    Require(GraphUtils.TryAcceptAll(independentShopCounter, qaOriginalShopGoods) == 0 &&
                        ReferenceEquals(qaOriginalShopGoods.parentInventory, independentShopCounter),
                        "the native shop marker could not be placed");
                    independentStep++;
                    break;
                case 1:
                    if (peerSite != merchant) return;
                    Require(!map.IsOutside && ReferenceEquals(entry.backInvinvElement, independentShopCounter) &&
                        ReferenceEquals(qaOriginalShopGoods.parentInventory, independentShopCounter) &&
                        entry.backInvWindow.childElement == independentShopCounter &&
                        store.playerLocation != PlayerStore.PlayerLocation.INVENTOR && shop?.CanReloadWorld == false,
                        "a remote engineer visit rebound the host's shop or opened the reload gate");
                    independentFoodGround = world.QaExpeditionGround(merchant);
                    qaMerchantPurchase = independentFoodGround.childItems.SingleOrDefault(GeneralHelper.IsItemOwned);
                    if (qaMerchantPurchase == null) return;
                    Require(shop.ForceDay(qaMerchantDay) == ShopResult.Unavailable &&
                        shop.LastNightBlocker == NativeShop.NightBlocker.Outside,
                        "settlement was not rejected while the guest was out");
                    Require(store.playerCash < 10000 && store.playerCash > 0 &&
                        !entry.afterhourInventory.childItems.Contains(qaMerchantPurchase),
                        "remote purchase did not share funds or leaked into the host's ground");
                    qaOwnedBeforeReturn = OwnedInQaShop(entry, qaMerchantPurchase.identifier);
                    qaPurchasedUnits = qaMerchantPurchase.unitCount;
                    using (world.EnterExpeditionSite(NativeActorRun.Slot.Partner))
                    {
                        qaMerchantFront = entry.frontInvinvElement;
                        qaMerchantBack = entry.backInvinvElement;
                        qaRemainingOffer = qaMerchantFront.childItems.FirstOrDefault(item => !GeneralHelper.IsItemOwned(item));
                        qaMerchantOffers = MerchantOffers();
                    }
                    Require(qaRemainingOffer != null, "the native engineering stock was not retained");
                    Record("PASS remote engineer purchase", $"cash={store.playerCash}, destination={merchant}, home counter retained");
                    NativeOffscreenCapture.Capture("qa-independent-merchant-host-at-shop");
                    if (independentSameSiteQa)
                    {
                        map.Travel(AfterhourLocation.Get(merchant));
                        Require(map.Current?.Id == merchant, "host could not join the occupied engineering shop");
                    }
                    independentFinishAt = now + 1.5;
                    independentStep++;
                    break;
                case 2:
                    if (now < independentFinishAt) return;
                    if (independentReconnectQa && !qaSawMerchantDisconnect) return;
                    if (independentReconnectQa)
                        Require(world.ReturnOfflinePartner(qaOfflineReturnProof, qaOfflineReturnPeer) == ShopResult.Stale,
                            "an old offline recall was allowed after a fresh connection");
                    if (independentSameSiteQa)
                    {
                        Require(peerSite == merchant && ReferenceEquals(entry.frontInvinvElement, qaMerchantFront) &&
                            ReferenceEquals(entry.frontInvWindow.childElement, qaMerchantFront) &&
                            ReferenceEquals(entry.afterhourInventory, independentFoodGround) &&
                            qaMerchantFront.childItems.Contains(qaRemainingOffer) &&
                            independentFoodGround.childItems.Contains(qaMerchantPurchase),
                            "joining the engineer again replaced the existing counters, ground or stock");
                        NativeOffscreenCapture.Capture("qa-independent-merchant-shared-stock");
                        map.Return();
                        Require(!map.IsOutside && ReferenceEquals(entry.backInvinvElement, independentShopCounter) &&
                            qaMerchantFront.childItems.Contains(qaRemainingOffer) &&
                            independentFoodGround.childItems.Contains(qaMerchantPurchase),
                            "the first return cleared the shop still in use by the guest");
                    }
                    independentStep++;
                    break;
                case 3:
                    if (peerSite.Length != 0) return;
                    bool expectParked = !independentSameSiteQa && !qaStaggeredVisitCompleted;
                    Require(world.QaExpeditionGround(merchant) == null && independentFoodGround.childItems.Count == 0 &&
                        (expectParked ? MerchantOffers().SequenceEqual(qaMerchantOffers) :
                            qaMerchantFront.childItems.Count == 0 && qaMerchantBack.childItems.Count == 0) &&
                        ReferenceEquals(entry.backInvinvElement, independentShopCounter) &&
                        ReferenceEquals(qaOriginalShopGoods.parentInventory, independentShopCounter) &&
                        OwnedInQaShop(entry, qaMerchantPurchase.identifier) == qaOwnedBeforeReturn + qaPurchasedUnits &&
                        shop?.CanReloadWorld == true,
                        "last visitor return did not restore the original shop or save the purchased goods exactly once");
                    if (independentMerchantStaggeredQa && !qaStaggeredVisitCompleted)
                    {
                        map.Travel(AfterhourLocation.Get(merchant));
                        Require(map.Current?.Id == merchant && peerSite.Length == 0 &&
                            ReferenceEquals(entry.frontInvinvElement, qaMerchantFront) &&
                            ReferenceEquals(entry.backInvinvElement, qaMerchantBack) &&
                            MerchantOffers().SequenceEqual(qaMerchantOffers) &&
                            entry.afterhourInventory.childItems.Count == 0 &&
                            !ReferenceEquals(entry.afterhourInventory, independentFoodGround),
                            "a staggered visit changed offer identities, amounts, positions or inherited the old ground");
                        Record("PASS staggered engineering stock", $"same {qaMerchantOffers.Length} offers, fresh ground, other player at shop");
                        NativeOffscreenCapture.Capture("qa-independent-merchant-staggered");
                        independentFinishAt = now + 2;
                        independentStep = 6;
                        break;
                    }
                    var ui = StoreUIManager.Instance;
                    if (ui.blackscreen.gameObject.activeInHierarchy) return;
                    ui.StartBlackscreenSequence();
                    Require(ui.blackscreen.gameObject.activeInHierarchy &&
                        shop.ForceDay(qaMerchantDay) == ShopResult.Unavailable &&
                        shop.LastNightBlocker == NativeShop.NightBlocker.Transition && !shop.PendingSettlement &&
                        StoreStation.Instance.dayCounter == qaMerchantDay,
                        "a forced settlement overlapped an active native transition");
                    independentStep = 7;
                    break;
                case 4:
                    Require(session.Shop.View.Phase != ShopDay.SaveUnverified, "the return passed but its nightly save could not be verified");
                    if (session.Shop.View.Phase != ShopDay.Saved || session.Shop.View.Day != qaMerchantDay + 1) return;
                    Require(StoreStation.Instance.dayCounter == qaMerchantDay + 1 &&
                        CoopRun.VerifyCheckpoint(store, qaMerchantDay + 1) &&
                        qaMerchantFront.childItems.Count == 0 && qaMerchantBack.childItems.Count == 0,
                        "original settlement did not verify the returned goods in the next day's co-op checkpoint");
                    string lateNotice = LocHelper.GetLocalizedMechanic("mech_nightlog_afterhour_debuff");
                    int lateNotices = shop.FullMorningReport.Count(row => row.Contains(lateNotice));
                    Require(lateNotices == (independentShortSleeperQa ? 0 : 1),
                        "the morning report did not match real travelers and short-sleeper eligibility");
                    Record("PASS shared late-opening report", $"count={lateNotices}, short sleeper={independentShortSleeperQa}");
                    Record("PASS merchant settlement", $"entered day {qaMerchantDay + 1} once, native checkpoint verified");
                    independentFinishAt = now + 2;
                    independentStep++;
                    break;
                case 5:
                    if (now >= independentFinishAt) Finish("PASS independent engineer host");
                    break;
                case 6:
                    if (now < independentFinishAt) return;
                    map.Return();
                    Require(!map.IsOutside, "the staggered visitor could not return from the engineer");
                    qaStaggeredVisitCompleted = true;
                    independentStep = 3;
                    break;
                case 7:
                    if (StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy) return;
                    Require(shop.CurrentNightBlocker == NativeShop.NightBlocker.None && !shop.PendingSettlement,
                        "a finished native transition left a stale day blocker or settlement");
                    var night = EndOfDayUIManager.Instance;
                    UnityEngine.Object.FindObjectOfType<StoreBedroll>().GetComponent<Interactable>().OnClick();
                    var button = (TabletButton)AccessTools.Field(typeof(EndOfDayUIManager), "endDayButton").GetValue(night);
                    Require(night.IsOpen && button?.Interactable == true, "native night confirmation did not reopen after both returned");
                    button.button.onClick.Invoke();
                    Require(shop.PendingSettlement, "the native sleep button did not start settlement after the fade");
                    Record("PASS settlement transition gate", "DAY-111 rejected during blackscreen; native button accepted after fade");
                    independentStep = 4;
                    break;
            }
            return;
        }

        switch (independentStep)
        {
            case 0:
                if (independentMerchantResumeQa)
                {
                    if (world.HasPendingWork) return;
                    var before = JObject.Parse(ModHelper.ReadModDataFileText("probably_coop", MerchantReconnectFixture));
                    if (independentOfflineRecallQa)
                    {
                        qaMerchantDay = (int)before["day"];
                        int day = StoreStation.Instance.dayCounter;
                        Require(independentReconnectQa && !map.IsOutside && peerSite.Length == 0 && store.saveSlotId == -1 &&
                            day >= qaMerchantDay && day <= qaMerchantDay + 1 &&
                            (day > qaMerchantDay || store.visitLeftTonight == 0 && store.playerCash == (int)before["cash"]),
                            "a restarted guest was not at the shop after the host recalled it");
                        Record("PASS independent guest rejoined at shop after recall", "home, correct day, no guest slot");
                        independentStep = 4;
                        break;
                    }
                    Require(independentReconnectQa && map.Current?.Id == merchant && peerSite.Length == 0 &&
                        store.saveSlotId == -1 && store.visitLeftTonight == 0,
                        "a restarted guest did not resume its engineering visit without an additional visit or local slot");
                    qaMerchantPurchase = entry.afterhourInventory.childItems.SingleOrDefault(GeneralHelper.IsItemOwned);
                    Require(qaMerchantPurchase != null && JToken.DeepEquals(before, MerchantVisitStamp(world, qaMerchantPurchase)),
                        "the original ground identity, item position, quantity or cash changed across reconnect");
                    qaMerchantDay = StoreStation.Instance.dayCounter;
                    Record("PASS independent guest resumed engineering visit", "same scene, item ID, position, ownership, cash and day");
                    NativeOffscreenCapture.Capture("qa-independent-engineer-rejoined");
                    independentFinishAt = now + 0.5;
                    independentStep = 2;
                    break;
                }
                if (store.storeState != PlayerStore.StoreState.CLOSED || !AfterhourLocation.Get(merchant).IsDiscovered() ||
                    world.HasPendingWork) return;
                qaMerchantDay = StoreStation.Instance.dayCounter;
                map.Travel(AfterhourLocation.Get(merchant));
                independentStep++;
                break;
            case 1:
                if (map.Current?.Id != merchant || store.currentClientInstance == null || world.HasPendingWork) return;
                if (!RunEngineerPurchase(session, now)) return;
                qaMerchantPurchase = (GameItem)world.Objects[engineerEntity];
                var groundSlot = entry.afterhourInventory.TryFindOneValidInventorySlot(qaMerchantPurchase);
                Require(groundSlot?.IsValid() == true && groundSlot.numTransfer == qaMerchantPurchase.unitCount,
                    "the purchased engineering item could not fit on the original ground");
                groundSlot.TryAcceptOnce();
                Require(QaWaiting(session.Native), "moving the purchase to the ground did not issue a native request");
                independentFinishAt = now + 3;
                independentStep++;
                break;
            case 2:
                qaSawPeerAtMerchant |= peerSite == merchant;
                if (QaWaiting(session.Native) || qaMerchantPurchase.parentInventory != entry.afterhourInventory ||
                    now < independentFinishAt || independentSameSiteQa && (!qaSawPeerAtMerchant || peerSite.Length != 0)) return;
                if (!independentMerchantResumeQa)
                {
                    Require(QaResult(session.Native) == (ushort)ShopResult.Applied, "the ground move was rejected");
                    if (independentReconnectQa)
                    {
                        ModHelper.WriteModDataFileText("probably_coop", MerchantReconnectFixture,
                            MerchantVisitStamp(world, qaMerchantPurchase).ToString(Newtonsoft.Json.Formatting.None));
                        Record("READY independent engineer reconnect", "purchase is visible on the ground and ready for guest process termination");
                        independentStep = 6;
                        break;
                    }
                }
                map.Return();
                var leave = GenericConfirmationUIManager.Instance;
                Require(leave.panel.activeSelf, "the final visitor did not get the native engineer ground warning");
                NativeOffscreenCapture.Capture("qa-independent-merchant-final-return");
                leave.CancelButtonPressed();
                Require(map.Current?.Id == merchant && qaMerchantPurchase.parentInventory == entry.afterhourInventory,
                    "cancelling the engineer warning moved the actor or merchandise");
                map.Return();
                Require(leave.panel.activeSelf, "the engineer confirmation did not reopen");
                leave.ConfirmButtonPressed();
                independentStep++;
                break;
            case 3:
                if (map.IsOutside || world.HasPendingWork) return;
                Require(store.visitLeftTonight == 0 && store.saveSlotId == -1,
                    "the engineer return restored a visit or wrote a guest save slot");
                independentStep++;
                break;
            case 4:
                if (independentMerchantStaggeredQa && peerSite == merchant)
                {
                    Require(!map.IsOutside && ReferenceEquals(entry.backInvWindow.childElement, entry.backInvinvElement),
                        "the host's staggered visit changed the guest's shop view");
                    qaSawPeerAtMerchant = true;
                }
                if (world.HasPendingWork || StoreStation.Instance.dayCounter != qaMerchantDay + 1 ||
                    world.MorningReportDay != qaMerchantDay + 1) return;
                Require(store.saveSlotId == -1 && (!independentMerchantStaggeredQa || qaSawPeerAtMerchant),
                    "settlement wrote a guest save slot or the staggered visit did not appear");
                Require(NativeOutingDayRules.HasLateOpeningPenalty(store.healthData) == !independentShortSleeperQa,
                    "the guest's customer estimate did not see the same shared penalty as the original morning report");
                NativeOffscreenCapture.Capture("qa-independent-merchant-next-day");
                CompleteIndependentGuest("PASS independent engineer guest");
                break;
        }
    }

    private (GameGridInventory Grid, GameItem Item, int Quantity, int X, int Y, int Rotation, bool Flipped)[] MerchantOffers() =>
        new[] { qaMerchantFront, qaMerchantBack }.SelectMany(grid => grid.childItems
            .Where(item => !GeneralHelper.IsItemOwned(item))
            .Select(item => (grid, item, item.unitCount, item.modifiedShape.minX, item.modifiedShape.minY,
                (int)item.modifiedShape.orientation, item.modifiedShape.flipped))).ToArray();

    private static int OwnedInQaShop(EmporiumEntry entry, string identifier) =>
        entry.backInvinvElement.childItems.Concat(entry.invElement.childItems).Concat(entry.showcaseElement.childItems)
            .Concat(entry.frontInvinvElement.childItems)
            .Where(item => item.identifier == identifier && GeneralHelper.IsItemOwned(item)).Sum(item => item.unitCount);
}
#endif
