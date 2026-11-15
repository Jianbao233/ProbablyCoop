#if PROBABLY_COOP_GRID_QA
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Sync;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Localization;
using UnityEngine.UI;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private static readonly bool ShopUiQa = QaArgs.Contains("--probably-coop-grid-shop-ui-qa");
    private const string ShopUiOrder = "qa_shop_ui_delivery", ShopUiFaction = "FACTION_LOWER_LEVEL";
    private int shopUiStep, shopUiTravelStep, shopUiDay, observedQueue;
    private bool shopUiTradingDone;
    private double shopUiStarted, shopUiReputation;
    private float shopUiMusic;
    private GameItem shopUiDocument;
    private ReputationElement shopUiReputationRow;
    private int[] shopUiCounterIds;
    private int shopUiBackpack, shopUiAvailableBags;
    private CustomUIWindow shopUiObservedInvite;
    private int shopUiInviteFrame;
    private bool awaitingWindowsTravel;
    private Button windowsTravelButton;
    private int shopUiArrivalFrame;
    private StoreBell shopUiBell;
    private bool shopUiBellWasPlaying, shopUiHostRang, shopUiOpening, shopUiClosing;
    private int shopUiBellStarts, shopUiDialogueThemes;

    private static void SeedShopUiQa(PlayerStore store)
    {
        store.isAfterhoursIntroduced = true;
        if (NativeEffectInputGuideQa.Enabled) SeedWindowsInputStock(EmporiumEntry.Instance);
        store.storeRequests.Add(new StoreRequest
        {
            id = ShopUiOrder, title = "Deliver a bandage", description = "Native feedback box QA",
            factionId = ShopUiFaction, requiredItem = "bandage_item", duration = 3,
            creditReward = 77, reputationReward = 12,
        });
    }

    private void TickShopUiQa()
    {
        var store = PlayerStore.Instance;
        var shop = EmporiumEntry.Instance;
        if (shopUiDay == 0)
        {
            shopUiDay = StoreStation.GetDayCounter();
            shopUiStarted = Time.realtimeSinceStartupAsDouble;
            var fixture = JObject.Parse(File.ReadAllText(QaArgs[FullStockArg + 1]));
            int expected = ((JArray)JObject.Parse((string)fixture["docInvJSON"])["saveItems"]).Count - 1;
            CampaignCheck(GraphUtils.FindAllChildrenType<GameItem>(shop.dossier.contentWindow.child).Count == expected &&
                expected > 0 && shop.dossier.spritePath != "dossier_0", "the original dossier has all archived files and its filled sprite");
            shopUiDocument = FindShopUiDocument();
            shopUiMusic = EmporiumOption.Instance.musicVolume;
            shopUiReputation = StoreReputation.GetStoreReputation(ShopUiFaction).GetReputationExact();
            shopUiBell = Object.FindFirstObjectByType<StoreBell>();
            ShopUiResult($"loaded {expected} archive dossier entries");
        }
        CampaignCheck(Time.realtimeSinceStartupAsDouble < shopUiStarted + 115,
            $"shop UI step={shopUiStep} travel={shopUiTravelStep} role={RoleName()}");
        ObserveShopCues(store);
        if (!localHost && session.Guest.PendingCount != 0) return;
        if (!shopUiTradingDone)
            shopUiTradingDone = localHost ? TickHostShopUi(store, shop) : TickGuestShopUi(store, shop);
        if (shopUiTradingDone) TickShopUiTravel(store, shop);
    }

    private bool TickHostShopUi(PlayerStore store, EmporiumEntry shop)
    {
        switch (shopUiStep)
        {
            case 0:
                if (shopUiDocument.parentInventory != shop.backInvinvElement) return false;
                if (NativeEffectInputGuideQa.Enabled)
                    CampaignCheck(WindowsWaterPourCompleted(shop), "host received the same amount after the guest's original bottle drag");
                CampaignCheck(!ReputationUIManager.Instance.uiPanel.activeSelf &&
                    !PaperUIManager.Instance.contractPanel.activeSelf && !shop.dossier.contentWindow.IsVisible() &&
                    Mathf.Approximately(EmporiumOption.Instance.musicVolume, shopUiMusic),
                    "guest's feedback, documents and cassette volume leave host UI and audio independent");
                ShopUiResult("received the dossier move while keeping host UI and music independent");
                break;
            case 1:
                if (shopUiDocument.parentInventory != shop.dossier.contentWindow.child) return false;
                StoreShutterButton.Instance.OnClick();
                CampaignCheck(store.storeState == PlayerStore.StoreState.OPEN, "host opened the original shutter");
                store.storeClientManager.clientStack.Clear();
                SeedBusinessQa(shopUiDay, verifyDialogueTheme: true);
                break;
            case 2:
                if (!shopUiHostRang && store.lockNextBellFlag == 0 && !store.storeRequests.Any(order => order.id == ShopUiOrder))
                {
                    ClickShopFixture<StoreBell>();
                    shopUiHostRang = true;
                }
                if (businessPurchases != 1 || businessSales != 1 || store.currentClientInstance != null ||
                    store.lockShutterButtonFlag > 0 || store.storeRequests.Any(request => request.id == ShopUiOrder)) return false;
                CampaignCheck(store.storeClientManager.clientStack.Count == 0 &&
                    shopUiBellStarts >= 2 &&
                    StoreReputation.GetStoreReputation(ShopUiFaction).GetReputationExact() > shopUiReputation,
                    "original business used both queued customers and PC delivery awarded reputation");
                StoreShutterButton.Instance.OnClick();
                ShopUiResult("processed the two queued customers and the guest's reputation reward");
                break;
            default:
                return store.storeState == PlayerStore.StoreState.CLOSED;
        }
        shopUiStep++;
        return false;
    }

    private bool TickGuestShopUi(PlayerStore store, EmporiumEntry shop)
    {
        switch (shopUiStep)
        {
            case 0:
                if (NativeEffectInputGuideQa.Enabled)
                {
                    if (!VerifyWindowsItemInput(shop)) return false;
                }
                else VerifyNativeDragFeedback(shop);
                ClickShopFixture<FeedbackBox>();
                var reputationUi = ReputationUIManager.Instance;
                var reputation = StoreReputation.GetStoreReputation(ShopUiFaction);
                CampaignCheck(reputationUi.uiPanel.activeSelf, "clicking the native Feedback Box opens the guest reputation UI");
                shopUiReputationRow = reputationUi.layout.GetComponentsInChildren<ReputationElement>()
                    .Single(row => row.reputationTitle.text == reputation.factionDisplay);
                CampaignCheck(shopUiReputationRow.reputationNumber.text == shopUiReputation.ToString("F2") + "/200",
                    "the original feedback row displays the shop reputation");
                ShopUiResult("opened the original Feedback Box and found its reputation row");

                ItemMouseDoubleClickHandler.current.OpenContentAction(shop.dossier, new Vector2(450, 260));
                CampaignCheck(shop.dossier.contentWindow.IsVisible(), "opened the original dossier window");
                var contract = ((GameInventory)shop.dossier.contentWindow.child).childItems.Single(item => item.IsID("mentor_contract"));
                ActivateShopChoice(contract);
                CampaignCheck(PaperUIManager.Instance.contractPanel.activeSelf, "read the original contract from the guest dossier");
                PaperUIManager.Instance.CloseContract();

                var deck = shop.cassettePlayerElement.childItem;
                var volume = MachineButton(deck, shopUiMusic > 0.1f ? 4 : 5);
                CampaignCheck(volume != null, "original cassette has local volume controls");
                volume.Select();
                CampaignCheck(!Mathf.Approximately(EmporiumOption.Instance.musicVolume, shopUiMusic),
                    "guest cassette volume responds locally");
                DoOriginalDrop(shop.backInvinvElement, shopUiDocument);
                break;
            case 1:
                CampaignCheck(shopUiDocument.parentInventory == shop.backInvinvElement,
                    "host accepted a guest move out of the dossier");
                DoOriginalDrop((GameInventory)shop.dossier.contentWindow.child, shopUiDocument);
                break;
            case 2:
                CampaignCheck(shopUiDocument.parentInventory == shop.dossier.contentWindow.child,
                    "host accepted the same document back into the dossier");
                if (store.storeState != PlayerStore.StoreState.OPEN || campaign.QueuedCustomers != 2 ||
                    !ObserveShopQueue(2)) return false;
                EmporiumOption.Instance.musicVolume = shopUiMusic;
                shop.dossier.CloseContentWindow();
                PCUIManager.Instance.storePC.OnClick();
                var order = PCUIManager.Instance.panel.GetComponentsInChildren<StoreRequestElement>()
                    .Single(row => row.storeRequest.id == ShopUiOrder);
                CampaignCheck(order.acceptButton.interactable, "native PC delivery can be accepted");
                order.acceptButton.onClick.Invoke();
                break;
            case 3:
                var live = StoreReputation.GetStoreReputation(ShopUiFaction).GetReputationExact();
                if (live <= shopUiReputation || store.storeRequests.Any(order => order.id == ShopUiOrder)) return false;
                CampaignCheck(ReputationUIManager.Instance.uiPanel.activeSelf && shopUiReputationRow.isActiveAndEnabled &&
                    shopUiReputationRow.reputationNumber.text == live.ToString("F2") + "/200",
                    "open feedback window updates its original row after the PC delivery");
                ShopUiResult("read the dossier, moved the same document out and back, and saw the live feedback reward");
                PCUIManager.Instance.HideUI();
                ReputationUIManager.Instance.CloseUI();
                break;
            case 4:
                if (store.storeState == PlayerStore.StoreState.OPEN)
                    ObserveShopQueue(campaign.QueuedCustomers);
                if (!RunGuestBusinessQa(shopUiDay, hostCallsFirst: true)) return false;
                CampaignCheck(observedQueue == 0b111, "native counter and hover text showed queue counts 2, 1 and 0");
                CampaignCheck(shopUiDialogueThemes == 3 && shopUiBellStarts >= 2,
                    "both original dialogue palettes and the two directions of bell playback were visible locally");
                ShopUiResult("native customer counter and hover text matched 2 → 1 → 0");
                ShopUiResult("original dialogue sprites match the upper faction and the explicit dark style; both bells played");
                break;
            default:
                if (store.storeState != PlayerStore.StoreState.CLOSED) return false;
                if (NativeShopPresentation.Fraction(StoreShutterButton.Instance) > 0.001f) return false;
                CampaignCheck(shopUiOpening && shopUiClosing, "guest sees intermediate frames in both original shutter animations");
                ShopUiResult("original shutter opened and closed with intermediate animation frames on the guest");
                var counter = Object.FindFirstObjectByType<StoreClientCounter>();
                return counter != null && counter.clientCounterTMP.text == "" && !counter.IsTooltipEnabled();
        }
        shopUiStep++;
        return false;
    }

    private void TickShopUiTravel(PlayerStore store, EmporiumEntry shop)
    {
        var map = MapUIManager.Instance;
        switch (shopUiTravelStep)
        {
            case 0:
                CampaignCheck(store.visitLeftTonight > 0, "the original evening permits a joint outing");
                shopUiCounterIds = FrontCounterIds(shop);
                shopUiAvailableBags = shop.GetAllOwnedItemsByTag("BACKPACK_TAG").Count(world.CanInteract);
                CampaignCheck(shopUiAvailableBags > 0, "the full-stock save has a real shared backpack");
                if (!localHost) OpenShopDoorChoices(shop, map);
                break;
            case 1:
                if (localHost)
                {
                    if (ClickableTransitionPanel() is not { } invitation) return;
                    CheckTripInvite(map, shop, invitation);
                    ClickShopUi(invitation.Get("confirm").button);
                    CampaignCheck(shop.responseWindowClosable.IsVisible() &&
                        shop.responseInventoryClosable.childItems.Any(IsEmptyHandChoice) &&
                        shop.responseInventoryClosable.childItems.Count == shopUiAvailableBags,
                        "host sees original gear choices with the guest's reserved backpack omitted");
                    ShopUiResult("received the guest invitation at the shop and opened the original gear choices");
                }
                else if (!StartOriginalBackpackTrip(shop, map)) return;
                break;
            case 2:
                if (localHost)
                {
                    StoreUIManager.Instance.ResolveEscape();
                    CampaignCheck(!shop.responseWindowClosable.IsVisible(), "native gear picker can be closed before confirming");
                }
                else
                {
                    if (OpenTransitionPanel() is not { } waiting) return;
                    CampaignCheck(waiting.Get("readiness").text.text == NativeDayUi.ReadinessText(2, true) &&
                        !TransitUI.IsOpen && !map.IsOutside, "the initiating guest can see that the host has yet to confirm");
                }
                break;
            case 3:
                if (localHost)
                {
                    if (OpenTransitionPanel() == null) return;
                    ClickShopFixture<StoreBedroll>();
                    CampaignCheck(EndOfDayUIManager.Instance.IsOpen && OpenTransitionPanel() == null,
                        "opening the original day-end tablet dismisses the unconfirmed trip");
                    EndOfDayUIManager.Instance.OnCloseButtonClick();
                }
                else if (OpenTransitionPanel() != null) return;
                CampaignCheck(shopUiCounterIds.SequenceEqual(FrontCounterIds(shop)) && !map.IsOutside,
                    "cancelling the trip preserves the shop and its stock");
                ShopUiResult("cancelled the pending trip from the original bedroll path");
                break;
            case 4:
                if (localHost) OpenShopDoorChoices(shop, map);
                else if (ClickableTransitionPanel() is not { } invitation) return;
                else
                {
                    CheckTripInvite(map, shop, invitation);
                    ClickShopUi(invitation.Get("confirm").button);
                    CampaignCheck(shop.responseWindowClosable.IsVisible() &&
                        shop.responseInventoryClosable.childItems.Count == shopUiAvailableBags,
                        "guest sees the original gear picker and cannot choose the same backpack");
                }
                break;
            case 5:
                if (localHost)
                {
                    if (!StartOriginalBackpackTrip(shop, map)) return;
                }
                else
                {
                    ActivateTravelChoice(shop.responseInventoryClosable.childItems.Single(IsEmptyHandChoice));
                    CampaignCheck(shop.selectedBackpack == null && !TransitUI.IsOpen,
                        "empty-hand selection confirms the offered destination without opening a second map");
                    ShopUiResult("accepted the host invitation through the original empty-hand gear choice");
                }
                break;
            case 6:
                if (map.Current?.Id != AfterhourLocation.DUMPING_GROUND || map.IsInTransit) return;
                if (shopUiArrivalFrame == 0) shopUiArrivalFrame = Time.frameCount;
                if (Time.frameCount <= shopUiArrivalFrame + 2 ||
                    StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy) return;
                CampaignCheck(!shop.trashInvWindow.rectTransform.gameObject.activeInHierarchy,
                    "the shop trashcan is hidden at the dumping ground");
                CampaignCheck((shop.afterhourPocketSlotInvBackpack.childItem?.uniqueId ?? 0) == (localHost ? shopUiBackpack : 0) &&
                    ScavHelper.GetScavTimeLeft() > 0, "arrived with the chosen personal gear and native scavenging available");
                if (NativeEffectInputGuideQa.Enabled)
                {
                    if (!VerifyWindowsCarryInput(shop)) return;
                }
                else VerifyNativeCarryHeader(shop);
                ShopUiResult(localHost ? "arrived at the dumping ground carrying the selected backpack" :
                    "arrived at the same dumping ground without a backpack");
                break;
            case 7:
                if (localHost)
                {
                    var returnButton = map.UIScavengingPanel.GetComponentsInChildren<Button>()
                        .Single(button => Enumerable.Range(0, button.onClick.GetPersistentEventCount())
                            .Any(i => button.onClick.GetPersistentMethodName(i) == nameof(MapUIManager.LeaveScavenging)));
                    ClickShopUi(returnButton);
                }
                else
                {
                    if (ClickableTransitionPanel() is not { } returning) return;
                    CampaignCheck(returning.Get("destination").text.text == ModHelper.GetLocalized("probably_coop", "transition_return"),
                        "guest sees the return invitation in the shared location");
                    ClickShopUi(returning.Get("confirm").button);
                }
                break;
            case 8:
                if (map.IsOutside || map.IsInTransit || StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy) return;
                CampaignCheck(shop.trashInvWindow.rectTransform.gameObject.activeInHierarchy,
                    "the shop trashcan is visible again on return");
                CampaignCheck(shop.afterhourPocketSlotInvBackpack.childItem == null && !TransitUI.IsOpen,
                    "both players returned to the shop and cleared their travel selections");
                if (localHost) ClickShopFixture<StoreBedroll>();
                else
                {
                    if (ClickableTransitionPanel() is not { } night) return;
                    CampaignCheck(night.Get("destination").text.text == ModHelper.GetLocalized("probably_coop", "transition_sleep"),
                        "guest sees the night confirmation");
                    ClickShopUi(night.Get("confirm").button);
                }
                CampaignCheck(EndOfDayUIManager.Instance.IsOpen, "opens the original day-end tablet");
                ShopUiResult("hid the shop trashcan during travel and restored it on return");
                break;
            case 9:
                var endDay = EndOfDayUIManager.Instance.UI.GetComponentsInChildren<TabletButton>()
                    .SingleOrDefault(button => button.label.text == LocHelper.GetLocalizedUI("ui_end_day"));
                if (endDay?.Interactable != true) return;
                ClickShopUi(endDay.button);
                break;
            case 10:
                if (StoreStation.GetDayCounter() == shopUiDay) return;
                CampaignCheck(StoreStation.GetDayCounter() == shopUiDay + 1 &&
                    store.storeState == PlayerStore.StoreState.MORNING && shopUiDocument.parentInventory == shop.dossier.contentWindow.child,
                    "settled exactly one day and kept the shared dossier");
                ShopUiResult($"returned to the shop and settled day={StoreStation.GetDayCounter()} cash={store.playerCash}");
                campaignDone = true;
                break;
        }
        shopUiTravelStep++;
    }

    private void ChooseOriginalBackpack(EmporiumEntry shop)
    {
        ActivateTravelChoice(shop.responseInventoryClosable.childItems.First(item => !IsEmptyHandChoice(item)));
        CampaignCheck(shop.selectedBackpack != null, "the original backpack choice selects a real item");
        shopUiBackpack = shop.selectedBackpack.uniqueId;
    }

    private void CheckTripInvite(MapUIManager map, EmporiumEntry shop, CustomUIWindow invitation)
    {
        var destination = AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND).GetTitle();
        CampaignCheck(!map.IsOutside && !TransitUI.IsOpen && !shop.responseWindowClosable.IsVisible() &&
            invitation.Get("destination").text.text.Contains(destination) &&
            invitation.Get("readiness").text.text == NativeDayUi.ReadinessText(localHost ? (byte)2 : (byte)1, !localHost) &&
            !string.IsNullOrEmpty(invitation.Get("peer_bag").text.text),
            "a player who has not opened the map can see where the partner wants to go and who is ready");
    }

    private static CustomUIWindow OpenTransitionPanel() =>
        CustomUIManager.Instance.GetWindow(NativeEffectCampaign.TransitionWindowId) is { IsVisible: true } window ? window : null;

    private CustomUIWindow ClickableTransitionPanel()
    {
        var window = OpenTransitionPanel();
        if (window != shopUiObservedInvite)
        {
            shopUiObservedInvite = window;
            shopUiInviteFrame = Time.frameCount;
        }
        return Time.frameCount > shopUiInviteFrame + 2 ? window : null;
    }

    private static int[] FrontCounterIds(EmporiumEntry shop) =>
        shop.frontInvinvElement.childItems.Select(item => item.uniqueId).OrderBy(id => id).ToArray();

    private void ObserveShopCues(PlayerStore store)
    {
        bool ringing = shopUiBell.audioSource.isPlaying;
        if (ringing && !shopUiBellWasPlaying) shopUiBellStarts++;
        shopUiBellWasPlaying = ringing;
        float position = NativeShopPresentation.Fraction(StoreShutterButton.Instance);
        if (position <= 0.001f || position >= 0.999f) return;
        if (store.storeState == PlayerStore.StoreState.OPEN) shopUiOpening = true;
        else if (store.storeState == PlayerStore.StoreState.CLOSED) shopUiClosing = true;
    }

    private void CheckShopDialoguePalette(DialogUIManager ui, string theme)
    {
        var palette = StoreUIManager.Instance.dialogBubbleList;
        CampaignCheck(ui.clientNameBubbleImage.sprite == palette.GetSprite(theme) &&
            ui.clientBubble.backgroundImage.sprite == palette.GetSprite(theme + "p"),
            "native name and body both use the customer's " + theme + " dialogue palette");
        shopUiDialogueThemes |= theme == "upper" ? 1 : 2;
    }

    private void VerifyNativeDragFeedback(EmporiumEntry shop)
    {
        var drag = ItemMouseDragHandler.current;
        var item = shop.invElement.childItems.OfType<GameItemElement>()
            .First(candidate => world.CanInteract(candidate) && candidate.MayRemove());
        var from = item.parentInventory;
        var setItem = HarmonyLib.AccessTools.Field(typeof(ItemMouseDragHandler), "currentItem");
        var start = HarmonyLib.AccessTools.Method(typeof(ItemMouseDragHandler), "StartDrag");
        var manager = (HighlightManager)HarmonyLib.AccessTools.Field(typeof(ItemMouseDragHandler), "highlightManager").GetValue(drag);
        var paint = HarmonyLib.AccessTools.Method(typeof(NativeDragTargetHighlight), "Highlight");
        setItem.SetValue(drag, item);
        start.Invoke(drag, null);
        try
        {
            CampaignCheck(drag.IsDraggingItem && HoldsLocalItem(item) && item.fakeBackground.image.enabled,
                "native pickup holds the original shared item");
            var group = item.fakeMiddle.GetComponent<CanvasGroup>();
            CampaignCheck(group != null && !group.blocksRaycasts, "the held preview cannot intercept the destination");
            var center = item.GetCenteredPosition();
            item.ValidateSelect();
            CampaignCheck(item.fakeBackground.image.enabled && center == item.GetCenteredPosition(),
                "native validation during a held gesture retains the visible preview");
            var grid = shop.invElement;
            var slot = new SlotMarker { inventory = grid, itemGridShape = new GridShapeBuilder(1, 1).SetDataFill(1).Build() };
            var nodes = (List<TreeNodeRender>)paint.Invoke(null, new object[] { grid, slot, RenderHandler.BackgroundColor.Green, true });
            CampaignCheck(nodes?.Count == 1 && nodes[0].image.enabled, "native item target paints the expected cell");
            manager.Clear();
            CampaignCheck(!nodes[0].image.enabled, "the native hover clear removes the old target cell");
        }
        finally { drag.Cancel(); }
        CampaignCheck(!drag.IsDraggingItem && !HoldsLocalItem(item) && !item.fakeBackground.image.enabled &&
            item.parentInventory == from, "cancelling the native gesture leaves the item and clears the held preview");
        ShopUiResult("native pickup and cancellation preserve the item and clear its hover highlights");
    }

    private void VerifyNativeCarryHeader(EmporiumEntry shop)
    {
        var window = shop.afterhourWindow;
        var header = (TreeNodeRender)HarmonyLib.AccessTools.Field(typeof(PixelWindow), "machineryHeader").GetValue(window);
        var pocket = shop.afterhourPocketSlotInvLeft;
        var backpack = shop.afterhourPocketSlotInvBackpack.childItem;
        CampaignCheck(window.IsVisible() && header.image.raycastTarget && header.eventHandler != null,
            "the original carry header is interactive");
        Canvas.ForceUpdateCanvases();
        var hits = new List<RaycastResult>();
        PointerEventData pointer = null;
        for (int x = 1; x < 8; x++)
        {
            pointer = ShopPointerAt(header.rectTransform, new Vector2(x / 8f, 0.5f));
            EventSystem.current.RaycastAll(pointer, hits);
            if (hits.Count != 0 && ExecuteEvents.GetEventHandler<IBeginDragHandler>(hits[0].gameObject) == header.gameObject)
                break;
        }
        CampaignCheck(hits.Count != 0 && ExecuteEvents.GetEventHandler<IBeginDragHandler>(hits[0].gameObject) == header.gameObject,
            "pointer hits the visible carry header at " + pointer.position + " with " +
            string.Join(" / ", hits.Take(4).Select(hit => hit.gameObject.transform.parent?.name + "/" + hit.gameObject.name)));
        pointer.pointerPressRaycast = hits[0];
        pointer.pointerCurrentRaycast = hits[0];
        pointer.pressPosition = pointer.position;
        var original = window.rectTransform.anchoredPosition;
        ExecuteEvents.Execute(header.gameObject, pointer, ExecuteEvents.beginDragHandler);
        pointer.dragging = true;
        pointer.delta = new Vector2(40, 120);
        pointer.position += pointer.delta;
        ExecuteEvents.Execute(header.gameObject, pointer, ExecuteEvents.dragHandler);
        ExecuteEvents.Execute(header.gameObject, pointer, ExecuteEvents.endDragHandler);
        var moved = window.rectTransform.anchoredPosition;
        shop.Validate();
        CampaignCheck((moved - original).sqrMagnitude > 4 && (window.rectTransform.anchoredPosition - moved).sqrMagnitude < 2 &&
            pocket == shop.afterhourPocketSlotInvLeft && backpack == shop.afterhourPocketSlotInvBackpack.childItem,
            "original layout keeps the moved personal carry window and its inventory");
        ShopUiResult("dragged the original carry header without changing the pocket or backpack");
    }

    private bool StartOriginalBackpackTrip(EmporiumEntry shop, MapUIManager map)
    {
        if (awaitingWindowsTravel)
        {
            if (TransitUI.IsOpen) return false;
            awaitingWindowsTravel = false;
            windowsTravelButton = null;
            CampaignCheck(!map.IsOutside && !map.IsInTransit, "the original travel button waits for a joint transition");
            ShopUiResult("Windows mouse clicked the original travel button and entered joint confirmation");
            return true;
        }
        if (!TransitUI.IsOpen)
        {
            ChooseOriginalBackpack(shop);
            return false;
        }
        var transit = TransitUI.Root.GetComponent<TransitFrame>();
        var card = transit.cardsHost.GetComponentsInChildren<TransitCardView>()
            .Single(view => view.location.Id == AfterhourLocation.DUMPING_GROUND);
        ClickShopUi(card);
        CampaignCheck(transit.travelButton.IsInteractable(), "the native travel button is enabled for the dumping ground");
        if (NativeEffectInputGuideQa.Enabled)
        {
            windowsTravelButton = transit.travelButton;
            awaitingWindowsTravel = true;
            return false;
        }
        ClickShopUi(transit.travelButton);
        CampaignCheck(!map.IsOutside && !map.IsInTransit, "the original travel button waits for a joint transition");
        return true;
    }

    private bool ObserveShopQueue(int expected)
    {
        if (expected is < 0 or > 2) return false;
        var counter = Object.FindFirstObjectByType<StoreClientCounter>();
        if (counter == null || counter.clientCounterTMP.text != expected.ToString() || !counter.IsTooltipEnabled()) return false;
        string tooltip = new LocalizedString("UI", "inte_client_counter").GetLocalizedString(
            new Dictionary<string, object> { ["count"] = expected });
        if (counter.GetTooltip() != tooltip) return false;
        observedQueue |= 1 << expected;
        return true;
    }

    private static void OpenShopDoorChoices(EmporiumEntry shop, MapUIManager map)
    {
        ClickShopFixture<StoreDoor>();
        if (map.confirmationPanel.activeSelf) map.OnGoOutsideConfirm();
        CampaignCheck(shop.responseWindowClosable.IsVisible() &&
            shop.responseInventoryClosable.childItems.Any(IsEmptyHandChoice),
            "the original door shows a selectable no-backpack entry");
    }

    private static bool IsEmptyHandChoice(GameItem item) =>
        item.name == LocHelper.GetLocalizedMechanic("response_afterhours_no_backpack");

    private static GameItem FindShopUiDocument() =>
        ((GameInventory)EmporiumEntry.Instance.dossier.contentWindow.child).childItems.Single(item => item.IsID("cigarette_guide"));

    private static void ActivateShopChoice(GameItem item)
    {
        CampaignCheck(item.MayActivateSlotItem() && item.CanActivateSlotItem(), "the original item can be activated");
        item.ActivateSlotItem();
    }

    private static void ActivateTravelChoice(GameItem item)
    {
        var element = (GameItemElement)item;
        Canvas.ForceUpdateCanvases();
        var rect = element.background.rectTransform;
        for (int x = 1; x < 4; x++)
            for (int y = 1; y < 4; y++)
            {
                var pointer = ShopPointerAt(rect, new Vector2(x * 0.25f, y * 0.25f));
                if (RenderHandler.RaycastElement<GameItem>(pointer.position) != item) continue;
                ActivateShopChoice(item);
                return;
            }
        var center = ShopPointerAt(rect);
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(center, hits);
        CampaignCheck(false, $"Unity pointer could not reach the backpack at {center.position} " +
            $"hits={string.Join(" / ", hits.Take(5).Select(hit => hit.gameObject.name))}");
    }

    private static void ClickShopUi(Component control)
    {
        CampaignCheck(control != null && control.gameObject.activeInHierarchy &&
            (control is not Button button || button.IsInteractable()), "the clicked UI control is visible and enabled");
        Canvas.ForceUpdateCanvases();
        var pointer = ShopPointerAt((RectTransform)control.transform);
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointer, hits);
        CampaignCheck(hits.Count != 0, $"Unity pointer hits {control.name} at {pointer.position}");
        var hit = hits[0];
        var click = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
        if (click != control.gameObject)
        {
            string details = string.Join(" / ", hits.Take(6).Select(row =>
                $"{row.gameObject.transform.parent?.name}/{row.gameObject.name}({row.sortingOrder})"));
            var canvas = control.GetComponentInParent<Canvas>();
            CampaignCheck(false, $"Unity pointer reaches {control.name} at {pointer.position}, hit {details}, " +
                $"target canvas={canvas.name}/{canvas.sortingOrder}/{canvas.renderMode} screen={Screen.width}x{Screen.height}");
        }
        pointer.pointerCurrentRaycast = hit;
        pointer.pointerPressRaycast = hit;
        ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.Execute(click, pointer, ExecuteEvents.pointerClickHandler);
    }

    private static PointerEventData ShopPointerAt(RectTransform rect, Vector2? point = null)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return new PointerEventData(EventSystem.current)
        {
            position = RectTransformUtility.WorldToScreenPoint(camera,
                rect.TransformPoint(Rect.NormalizedToPoint(rect.rect, point ?? new Vector2(0.5f, 0.5f)))),
            button = PointerEventData.InputButton.Left,
            clickCount = 1,
        };
    }

    private static void ClickShopFixture<T>() where T : Component
    {
        var fixture = Object.FindFirstObjectByType<T>()?.GetComponent<Interactable>();
        CampaignCheck(fixture != null && fixture.isAvailable && !Interactable.isPaused, $"original {typeof(T).Name} is available");
        fixture.OnClick();
    }

    private void ShopUiResult(string message) => Debug.Log($"[probably_coop SHOP UI] PASS role={RoleName()} {message}");
}
#endif
