#if PROBABLY_COOP_GRID_QA
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private static readonly bool ActivityQa = QaArgs.Contains("--probably-coop-grid-activity-qa");
    private const string ActivityTag = "QA_ACTIVITY_";
    private const string TestRequest = "coop_qa_pc_delivery";
    private const string TestBarter = "coop_qa_barter";
    private const string TestReport = "coop_qa_security";
    private static readonly string[] QaLocations = { "dr_jackson", "commissary", AfterhourLocation.DUMPING_GROUND };
    private int activityPhase, outingIndex, outingPhase, activityCash, engineerItem, engineerPrice, commissaryTokens, commissaryStock;
    private int engineerBalance, upgradePrice, upgradeFavor;
    private int phoneAbortStage;
    private bool presetSeen, hostNativePhoneHidden, buyingVending, automaticHopperSeen;
    private double activityStarted, nextActivity, outingStarted;
    private int activityParked;
    private int activityNight;
    private int wrongfulReports, reportBalance;
    private double nightStarted;
    private bool nightVoted, sawOfficers, sawGlass;

    private static void SeedActivityQa(EmporiumEntry shop, PlayerStore store)
    {
        GameItem Place(string catalog, string tag, params GraphNodeStorage[] destinations)
        {
            var item = DirectoryMaster.Item(catalog);
            CampaignCheck(item != null, "activity fixture catalog " + catalog);
            GeneralHelper.SetItemOwned(item, true);
            item.EnableTag(ActivityTag + tag);
            foreach (var destination in destinations)
                if (GraphUtils.TryAcceptAll(destination, item) == 0) break;
            CampaignCheck(item.parentInventory != null, "activity fixture fits " + tag);
            return item;
        }
        var bags = new[] { "A", "B", "C" }.Select(id =>
            Place("backpack_medium", id, shop.showcaseElement, shop.invElement, shop.backInvinvElement)).ToArray();
        Place("intake_hopper", "IN_A", bags[0]);
        Place("outlet_hopper", "OUT_B", bags[1]);
        Place("intake_hopper", "IN_B", bags[1]);
        Place("outlet_hopper", "OUT_C", bags[2]);
        Place("nutrifruit", "CARGO", bags[0]);
        Place("storage_coordinator", "PRESET", shop.invElement, shop.showcaseElement, shop.backInvinvElement);
        Place("toilet_paper", "PARKED", shop.backInvinvElement);
        Place("toilet_paper", "PAYMENT", shop.backInvinvElement);
        Place("food_stamp", "STAMP", shop.backInvinvElement, shop.invElement)
            .AddItemFeature(ItemFeatureList.StampAuthenticity());

        var wild = store.PhoneClientDict[StorePhoneClient.WILD_CLIENT_NUMBER];
        wild.phoneState = StorePhoneClient.PhoneState.Regular;
        wild.dialedBefore = true;
        store.networkUpgrade["SHOWCASE_I"].state = 1;
        store.wildFavor = 17;
        store.isAfterhoursIntroduced = store.isStampIntroduced = store.storeClientData.isJacksonIntroduced = true;
        store.commissaryData.redeemedCredit = 100;
        store.commissaryData.GetItemData("processed_meat").StockCount = 3;
        var janitorial = StoreService.GetJanitorialService();
        janitorial.unlocked = true;
        janitorial.isUsing = false;
        store.storeRequests.Add(new StoreRequest
        {
            id = TestRequest, title = "QA station order", description = "Deliver one medical bandage.",
            factionId = "FACTION_LOWER_LEVEL", requiredItem = "bandage_item", duration = 3, creditReward = 77,
        });
    }

    private GameItem ActivityItem(string tag) => FullStockItems().SingleOrDefault(item => item.IsTag(ActivityTag + tag)) ??
        throw new InvalidOperationException("Campaign QA: missing activity fixture " + tag);
    private static bool NativeShopHasClient(string id) => PlayerStore.Instance.currentClientInstance?.GetClientBlueprint().identifier == id;
    private static void ActivityResult(string message) => Debug.Log("[probably_coop ACTIVITIES] PASS " + message);

    private void NextActivity(double now, double pause = 0)
    {
        Debug.Log($"[probably_coop ACTIVITIES] role={RoleName()} finished-step={activityPhase}");
        activityPhase++;
        nextActivity = now + pause;
    }

    private bool RunGuestActivityQa(double now)
    {
        if (activityStarted == 0) activityStarted = now;
        CampaignCheck(now < activityStarted + 145, $"guest activities step={activityPhase} outing={outingIndex}/{outingPhase}");
        if (now < nextActivity || session.Guest.PendingCount > 0) return false;
        var store = PlayerStore.Instance;
        var phone = PhoneUIManager.Instance;
        var call = DialoguePhoneUIManager.Instance;
        var shop = EmporiumEntry.Instance;
        var tablet = NegociationUIManager.Instance;
        if (activityPhase == 0 && !CheckPhoneHangup(phone)) return false;
        switch (activityPhase)
        {
            case 0:
                UnityEngine.Object.FindFirstObjectByType<StorePhoneObject>().OnClick();
                var one = phone.phonePanel.GetComponentsInChildren<PhoneButton>().Single(button => button.number == 1);
                one.SimulateClick();
                break;
            case 1:
                if (phone.statusDisplay.text != LocHelper.GetLocalizedUI("ui_phone_no_answer")) return false;
                var contact = phone.phoneBookUIManager.layoutGroup.GetComponentsInChildren<ContactElement>()
                    .Single(entry => entry.number == StorePhoneClient.WILD_CLIENT_NUMBER);
                contact.OnQuickDial();
                ActivityResult("guest original keypad to no-answer, then phone-book quick dial");
                break;
            case 2:
                if (!call.dialogChoiceBubbles[0].gameObject.activeInHierarchy) return false;
                CampaignCheck(call.clientNameBubble.activeSelf && !string.IsNullOrEmpty(call.clientNameTMP.text),
                    "the original phone caller name and choice are visible on the guest");
                call.dialogChoiceBubbles[0].OnDialogChoiceClick();
                break;
            case 3:
                if (!WildNetworkUI.IsOpen)
                {
                    call.OnAdvancePressed();
                    nextActivity = now + 0.35;
                    return false;
                }
                CampaignCheck(!phone.phonePanel.activeSelf, "native Wild upgrade window takes over the guest phone");
                activityCash = store.playerCash;
                upgradePrice = store.networkUpgrade["SHOWCASE_I"].GetCost();
                upgradeFavor = store.wildFavor;
                AccessTools.Method(typeof(WildNetworkUI), "OnSlotClicked").Invoke(null, new object[] { "SHOWCASE_I" });
                var frame = (WildNetworkFrame)AccessTools.Field(typeof(WildNetworkUI), "frame").GetValue(null);
                CampaignCheck(frame.useButton.interactable, "original Wild purchase control is ready");
                frame.useButton.onClick.Invoke();
                frame.useButton.onClick.Invoke();
                break;
            case 4:
                if (!buyingVending)
                {
                    if (!NetworkUpgrade.IsUnlocked("SHOWCASE_I")) return false;
                    CampaignCheck(store.playerCash == activityCash - Math.Max(0, upgradePrice - upgradeFavor) &&
                        store.wildFavor == Math.Max(0, upgradeFavor - upgradePrice) &&
                        shop.showcaseElement.inventoryShape.width == 8 && shop.showcaseElement.inventoryShape.height == 6,
                        $"duplicate Wild purchase once cash={activityCash}->{store.playerCash} shown={upgradePrice} favor={upgradeFavor}->{store.wildFavor} size={shop.showcaseElement.inventoryShape.width}x{shop.showcaseElement.inventoryShape.height}");
                    buyingVending = true;
                    activityCash = store.playerCash;
                    upgradePrice = store.networkUpgrade["VENDING_MACHINE"].GetCost();
                    AccessTools.Method(typeof(WildNetworkUI), "OnSlotClicked").Invoke(null, new object[] { "VENDING_MACHINE" });
                    var vendingFrame = (WildNetworkFrame)AccessTools.Field(typeof(WildNetworkUI), "frame").GetValue(null);
                    CampaignCheck(vendingFrame.useButton.interactable, "original vending upgrade button is ready");
                    vendingFrame.useButton.onClick.Invoke();
                    return false;
                }
                if (!store.isVendingMachineUnlocked) return false;
                var vending = shop.vendingMachineElement.childItem;
                CampaignCheck(vending != null && store.playerCash == activityCash - upgradePrice &&
                    GraphUtils.FindAllChildrenType<GameItem>(vending).Any(part => part.IsID("vending_machine_core")),
                    "native vending upgrade synchronizes its installed machine and module");
                StoreVendingMachine.Instance.OnClick();
                CampaignCheck(vending.contentWindow.IsVisible(), "guest uses original installed vending machine window");
                vending.CloseContentWindow();
                WildNetworkUI.Close();
                PCUIManager.Instance.storePC.OnClick();
                var order = PCUIManager.Instance.gridLayout.GetComponentsInChildren<StoreRequestElement>()
                    .Single(row => row.storeRequest.id == TestRequest);
                CampaignCheck(order.acceptButton.interactable, "original PC order is available for the stocked item");
                activityCash = store.playerCash;
                order.OnClick();
                order.OnClick();
                ActivityResult("guest phone choice and native Wild panel, purchase, geometry and close");
                break;
            case 5:
                if (store.storeRequests.Any(order => order.id == TestRequest)) return false;
                CampaignCheck(store.playerCash == activityCash + 77, "native PC delivery grants its reward once");
                PCUIManager.Instance.HideUI();
                UnityEngine.Object.FindFirstObjectByType<StoreBell>().OnClick();
                ActivityResult("guest original PC delivery consumed stock and paid once");
                break;
            case 6:
                if (!NativeShopHasClient(TestBarter) || !store.isClientArrived || StoreClientMono.Instance.IsMoving()) return false;
                if (campaign.ClientDialogue)
                {
                    DialogUIManager.Instance.OnAdvancePressed();
                    return false;
                }
                tablet.OpenUI();
                var next = (Button)AccessTools.Field(typeof(NegociationUIManager), "selectorNext").GetValue(tablet);
                CampaignCheck(next != null && next.interactable &&
                    shop.frontInvinvElement.childItems.Any(item => item.IsTag(ActivityTag + "OFFER_A")),
                    "original barter selector and first offered item");
                next.onClick.Invoke();
                break;
            case 7:
                var barter = store.currentClientInstance?.GetClientBlueprint()?.barterOffer;
                if (barter?.currentSetIndex != 1) return false;
                CampaignCheck(shop.frontInvinvElement.childItems.Any(item => item.IsTag(ActivityTag + "OFFER_B")) &&
                    !FullStockItems().Any(item => item.IsTag(ActivityTag + "OFFER_A")), "next offer replaces the old goods");
                activityCash = store.playerCash;
                CampaignCheck(TradeButton(tablet)?.Interactable == true, "original barter accept is available");
                TradeButton(tablet).button.onClick.Invoke();
                TradeButton(tablet).button.onClick.Invoke();
                break;
            case 8:
                if (store.currentClientInstance?.GetClientBlueprint()?.isBusinessComplete != true) return false;
                CampaignCheck(store.playerCash == activityCash && GeneralHelper.IsItemOwned(ActivityItem("OFFER_B")),
                    "native barter accepts the selected goods once without cash movement");
                CampaignCheck(!FullStockItems().Any(item => item.IsTag(ActivityTag + "PAYMENT")),
                    "native barter removes the offered payment from stock");
                tablet.CloseUI();
                ActivityResult("guest native barter selector, replacement goods and accepted offer");
                HopperHelper.Link(ActivityItem("IN_A"), ActivityItem("OUT_B"));
                HopperHelper.Link(ActivityItem("IN_B"), ActivityItem("OUT_C"));
                break;
            case 9:
                var intake = ActivityItem("IN_A");
                if (intake.state.GetTag(HopperHelper.LINK_INT).GetInt() != ActivityItem("OUT_B").uniqueId ||
                    ActivityItem("IN_B").state.GetTag(HopperHelper.LINK_INT).GetInt() != ActivityItem("OUT_C").uniqueId) return false;
                intake.ActivateSlotItem();
                ClickHopperTransfer();
                break;
            case 10:
                if (ActivityItem("CARGO").parentInventory?.GetParentItem() != ActivityItem("B")) return false;
                CustomUIManager.Instance.CloseWindow("intake_hopper");
                ActivityItem("IN_B").ActivateSlotItem();
                ClickHopperTransfer();
                break;
            case 11:
                if (ActivityItem("CARGO").parentInventory?.GetParentItem() != ActivityItem("C")) return false;
                CustomUIManager.Instance.CloseWindow("intake_hopper");
                ActivityItem("PRESET").ActivateSlotItem();
                var first = ActivityItem("A");
                var last = ActivityItem("C");
                MachineStorageCoordinator.SavePresets(ActivityItem("PRESET"), new List<MachineStorageCoordinator.Preset>
                {
                    new MachineStorageCoordinator.Preset { name = "QA native rooms", keyCode = (int)KeyCode.F9,
                        itemIds = new List<int> { first.uniqueId, last.uniqueId } },
                });
                ActivityResult("guest native hopper buttons moved the same item through three containers");
                break;
            case 12:
                var coordinator = ActivityItem("PRESET");
                if (string.IsNullOrEmpty(coordinator.state.GetTag(MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING).GetString()))
                    return false;
                var presets = MachineStorageCoordinator.LoadPresets(coordinator);
                CampaignCheck(presets.Count == 1 && presets[0].keyCode == (int)KeyCode.F9 && presets[0].itemIds.Count == 2,
                    "original preset has its key and both confirmed targets");
                MachineStorageCoordinator.TogglePresets(presets);
                CampaignCheck(ActivityItem("A").contentWindow.IsVisible() && ActivityItem("C").contentWindow.IsVisible(),
                    "native preset opens only the guest's windows");
                MachineStorageCoordinator.TogglePresets(presets);
                CampaignCheck(!ActivityItem("A").contentWindow.IsVisible() && !ActivityItem("C").contentWindow.IsVisible(),
                    "original preset also closes both guest windows");
                MachineStorageCoordinator.SavePresets(coordinator, new List<MachineStorageCoordinator.Preset>());
                break;
            case 13:
                var item = ActivityItem("PRESET");
                if (item.state.GetTag(MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING).GetString().Contains("QA native rooms"))
                    return false;
                CustomUIManager.Instance.CloseWindow("storage_coordinator");
                ActivityResult("guest native preset save, confirmation, local hotkey action and removal");
                var autoIntake = ActivityItem("IN_A");
                autoIntake.ActivateSlotItem();
                var toggle = CustomUIManager.Instance.GetWindow("intake_hopper").root.GetComponentsInChildren<Toggle>(true)[0];
                toggle.isOn = true;
                break;
            case 14:
                if (!ActivityItem("IN_A").state.GetTag(HopperHelper.ON_ADD_BOOL).GetBool()) return false;
                var cargo = ActivityItem("CARGO");
                var destination = (GameInventory)ActivityItem("A").contentWindow.childElement;
                CampaignCheck(destination.TryFindOneValidInventorySlot(cargo)?.TryAcceptOnce() == 1,
                    "native deposit into first hopper's container");
                break;
            case 15:
                if (ActivityItem("CARGO").parentInventory?.GetParentItem() != ActivityItem("B")) return false;
                CustomUIManager.Instance.CloseWindow("intake_hopper");
                ActivityItem("IN_B").ActivateSlotItem();
                ClickHopperTransfer();
                break;
            case 16:
                if (ActivityItem("CARGO").parentInventory?.GetParentItem() != ActivityItem("C")) return false;
                CustomUIManager.Instance.CloseWindow("intake_hopper");
                ActivityResult("guest deposit triggered the automatic first hop and the same unit reached the final container");
                break;
            case 17:
                if (campaign.ClientDialogue)
                {
                    DialogUIManager.Instance.OnAdvancePressed();
                    nextActivity = now + 0.25;
                    return false;
                }
                UnityEngine.Object.FindFirstObjectByType<StoreBell>().OnClick();
                break;
            case 18:
                if (!NativeShopHasClient(TestReport) || !store.isClientArrived) return false;
                if (campaign.ClientDialogue)
                {
                    DialogUIManager.Instance.OnAdvancePressed();
                    nextActivity = now + 0.25;
                    return false;
                }
                reportBalance = store.playerCash;
                StoreSecButton.Instance.OnClick();
                StoreSecButton.Instance.OnClick();
                break;
            case 19:
                var ui = StoreUIManager.Instance;
                sawOfficers |= ui.secGameObject.gameObject.activeSelf && ui.secGameObject2.gameObject.activeSelf;
                sawGlass |= ui.StoreGlass.IsClosed;
                if (store.playerCash == reportBalance || ui.StoreGlass.IsClosed)
                {
                    if (campaign.ClientDialogue) DialogUIManager.Instance.OnAdvancePressed();
                    nextActivity = now + 0.25;
                    return false;
                }
                CampaignCheck(sawOfficers && sawGlass && store.playerCash == reportBalance - 50,
                    "guest original officers and glass show the one native security outcome");
                ActivityResult("guest original security call, officer animation, glass and one fine");
                break;
            default:
                return RunGuestActivityOutings(now);
        }
        NextActivity(now, 0.25);
        return false;
    }

    private bool CheckPhoneHangup(PhoneUIManager phone)
    {
        if (phoneAbortStage == 3) return true;
        if (phoneAbortStage == 0)
        {
            UnityEngine.Object.FindFirstObjectByType<StorePhoneObject>().OnClick();
            phone.phonePanel.GetComponentsInChildren<PhoneButton>().Single(button => button.number == 2).SimulateClick();
        }
        else if (phoneAbortStage == 1)
        {
            if (phone.statusDisplay.text != LocHelper.GetLocalizedUI("ui_phone_calling")) return false;
            phone.phonePanel.GetComponentsInChildren<PhoneButton>().Single(button => button.number == -1).SimulateClick();
        }
        else
        {
            if (campaign.HasPhoneView || phone.statusDisplay.text != LocHelper.GetLocalizedUI("ui_phone_ready")) return false;
            ActivityResult("guest original hangup interrupted a call before it connected");
        }
        phoneAbortStage++;
        return phoneAbortStage == 3;
    }

    private static void ClickHopperTransfer()
    {
        var window = CustomUIManager.Instance.GetWindow("intake_hopper");
        CampaignCheck(window?.IsAlive == true, "original hopper control window");
        var label = LocHelper.GetLocalizedMechanic("mech_hopper_btn_transfer_now");
        window.root.GetComponentsInChildren<Button>(true).Single(button =>
            button.GetComponentInChildren<TextMeshProUGUI>(true)?.text == label).onClick.Invoke();
    }

    private bool RunHostActivityQa(double now)
    {
        if (activityStarted == 0) activityStarted = now;
        CampaignCheck(now < activityStarted + 145, $"host activities step={activityPhase} outing={outingIndex}/{outingPhase}");
        var store = PlayerStore.Instance;
        if (campaign.RemotePhoneExecution && PhoneUIManager.Instance.phonePanel.GetComponent<CanvasGroup>()?.alpha == 0)
            hostNativePhoneHidden = true;
        switch (activityPhase)
        {
            case 0:
                if (store.storeRequests.Any(order => order.id == TestRequest)) return false;
                CampaignCheck(NetworkUpgrade.IsUnlocked("SHOWCASE_I") && store.isVendingMachineUnlocked &&
                    !EmporiumEntry.Instance.vendingMachineElement.childItem.contentWindow.IsVisible() &&
                    store.wildFavor == 0 && hostNativePhoneHidden &&
                    !WildNetworkUI.IsOpen && !PCUIManager.Instance.panel.activeSelf, "host has shared effects and local panels stay closed");
                var client = new StoreClient
                {
                    identifier = TestBarter, displayName = "QA barter", spriteName = SpriteDict.GetRandomScavSpriteName(),
                    clientIntent = StoreClient.ClientIntent.BARTER, isIntroduced = true,
                };
                var barter = new BarterOffer { ownerClient = client };
                barter.conditions.Add(new BarterCondition { requiredItemTag = ActivityTag + "PAYMENT" });
                barter.AddOfferSet(() => PlaceQaOffer("nutrifruit", "OFFER_A"));
                barter.AddOfferSet(() => PlaceQaOffer("processed_meat", "OFFER_B"));
                client.barterOffer = barter;
                client.OnArrival = barter.InstanciateOffer;
                client.mainDialogue.SetText(client.displayName, "Two alternative offers.");
                client.acceptDealDialogue = new Dialogue().SetText(client.displayName, "Deal.");
                store.storeClientManager.AddNextClient(client);
                ActivityResult("host phone actor, upgrade and PC outcomes");
                break;
            case 1:
                if (!NativeShopHasClient(TestBarter) || !store.currentClientInstance.GetClientBlueprint().isBusinessComplete) return false;
                CampaignCheck(store.currentClientInstance.GetClientBlueprint().barterOffer.currentSetIndex == 1 &&
                    GeneralHelper.IsItemOwned(ActivityItem("OFFER_B")), "host completed the second native barter offer");
                ActivityResult("host original second barter offer completed");
                break;
            case 2:
                var preset = ActivityItem("PRESET");
                var rows = MachineStorageCoordinator.LoadPresets(preset);
                presetSeen |= rows.Count == 1;
                var cargoParent = ActivityItem("CARGO").parentInventory?.GetParentItem();
                automaticHopperSeen |= HopperHelper.IsOnAdd(ActivityItem("IN_A")) && cargoParent == ActivityItem("B");
                if (!presetSeen || rows.Count != 0 || !automaticHopperSeen || cargoParent != ActivityItem("C")) return false;
                CampaignCheck(!ActivityItem("A").contentWindow.IsVisible() && !ActivityItem("C").contentWindow.IsVisible(),
                    "guest preset never changes the source windows");
                var report = new StoreClient
                {
                    identifier = TestReport, displayName = "QA security visitor", spriteName = SpriteDict.GetRandomScavSpriteName(),
                    clientIntent = StoreClient.ClientIntent.BUY, isIntroduced = true, arrestable = true, dismissable = true,
                };
                report.mainDialogue.SetText(report.displayName, "A routine visit.");
                store.storeClientManager.AddNextClient(report);
                wrongfulReports = store.wrongfulReportCount;
                reportBalance = store.playerCash;
                ActivityResult("host confirmed both hopper hops and native preset write/removal");
                break;
            case 3:
                if (store.wrongfulReportCount == wrongfulReports || StoreUIManager.Instance.StoreGlass.IsClosed) return false;
                CampaignCheck(store.wrongfulReportCount == wrongfulReports + 1 && store.playerCash == reportBalance - 50,
                    "host native report causes exactly one wrongful report consequence");
                ActivityResult("host original security consequence executed once");
                var parked = ActivityItem("PARKED");
                var counter = EmporiumEntry.Instance.backInvinvElement;
                if (parked.parentInventory != counter)
                    CampaignCheck(counter.TryFindOneValidInventorySlot(parked)?.TryAcceptOnce() == 1, "parked merchant fixture fits in back counter");
                activityParked = parked.uniqueId;
                StoreShutterButton.Instance.EndAndClose();
                break;
            default:
                return RunHostActivityOutings(now);
        }
        NextActivity(now);
        return false;
    }

    private static void PlaceQaOffer(string catalog, string tag)
    {
        var item = DirectoryMaster.Item(catalog);
        item.EnableTag(ActivityTag + tag);
        PlayerStore.Instance.AddDirectSellingItemToTable(item);
    }

    private bool RunHostActivityOutings(double now)
    {
        var store = PlayerStore.Instance;
        var map = MapUIManager.Instance;
        if (outingIndex == QaLocations.Length) return FinishActivityNight(now);
        if (outingStarted == 0) outingStarted = now;
        CampaignCheck(now < outingStarted + 35, $"host outing {outingIndex}/{outingPhase}");
        switch (outingPhase)
        {
            case 0:
                if (store.currentClientInstance != null || store.storeState != PlayerStore.StoreState.CLOSED) return false;
                if (outingIndex == 0)
                {
                    AfterhourEvents.Start("special_stock", 2);
                    AfterhourEvents.Start("fresh_dump", 2);
                    engineerBalance = store.playerCash;
                }
                if (store.visitLeftTonight == 0) store.visitLeftTonight = 1; // fixture allows each original site on one night
                EmporiumEntry.Instance.selectedBackpack = null;
                map.Travel(AfterhourLocation.Get(QaLocations[outingIndex]));
                if (session.ReadyMask != 1) return false;
                outingPhase++;
                break;
            case 1:
                if (map.Current?.Id != QaLocations[outingIndex]) return false;
                if (outingIndex == 0 && store.playerCash >= engineerBalance ||
                    outingIndex == 1 && store.commissaryData.GetStock("processed_meat") != 2 ||
                    outingIndex == 2 && NativeActorSession.Current.Run.Partner.scavengingAttempts == 0) return false;
                if (outingIndex == 0)
                    CampaignCheck(EmporiumEntry.Instance.backInvinvElementCounter.childItems.Any(item => item.uniqueId == activityParked),
                        "host engineer preserves the parked counter item");
                map.ReturnNow();
                outingPhase++;
                break;
            case 2:
                if (map.IsOutside) return false;
                if (outingIndex == 0)
                    CampaignCheck(EmporiumEntry.Instance.backInvinvElement.childItems.Any(item => item.uniqueId == activityParked),
                        "original return restores the parked item identity");
                ActivityResult("host original joint outing and return " + QaLocations[outingIndex]);
                outingIndex++;
                outingPhase = 0;
                outingStarted = 0;
                break;
        }
        return false;
    }

    private bool RunGuestActivityOutings(double now)
    {
        if (outingIndex == QaLocations.Length) return FinishActivityNight(now);
        if (outingStarted == 0) outingStarted = now;
        CampaignCheck(now < outingStarted + 35, $"guest outing {outingIndex}/{outingPhase}");
        var map = MapUIManager.Instance;
        var store = PlayerStore.Instance;
        var shop = EmporiumEntry.Instance;
        if (outingPhase == 3 && !map.IsOutside)
        {
            if (outingIndex == 0)
            {
                CampaignCheck(ActivityItem("PARKED").parentInventory == shop.backInvinvElement,
                    "guest original counter contents restored after engineer return");
                CampaignCheck(shop.docInvWindow.rectTransform.gameObject.activeInHierarchy &&
                    shop.cassettePlayerWindow.rectTransform.gameObject.activeInHierarchy,
                    "guest dossier and cassette player restored on returning to the shop");
                ActivityResult("guest shop fixtures hidden at the engineer and restored on return");
            }
            ActivityResult("guest original joint outing and return " + QaLocations[outingIndex]);
            outingIndex++;
            outingPhase = 0;
            outingStarted = 0;
            return false;
        }
        if (session.ReadyMask == 1 && submittedEpoch != Epoch)
        {
            if (session.Proposal.Kind == TeamTransitionKind.Depart && session.Proposal.Destination == QaLocations[outingIndex])
            {
                shop.selectedBackpack = null;
                map.Travel(AfterhourLocation.Get(session.Proposal.Destination));
                submittedEpoch = Epoch;
                return false;
            }
            if (session.Proposal.Kind == TeamTransitionKind.Return && outingPhase == 3)
            {
                map.ReturnNow();
                submittedEpoch = Epoch;
                return false;
            }
        }
        if (outingPhase == 3) return false;
        if (map.Current?.Id != QaLocations[outingIndex]) return false;
        if (outingIndex == 0)
        {
            var tablet = NegociationUIManager.Instance;
            if (outingPhase == 0)
            {
                CampaignCheck(!shop.docInvWindow.rectTransform.gameObject.activeInHierarchy &&
                    !shop.cassettePlayerWindow.rectTransform.gameObject.activeInHierarchy &&
                    shop.frontInvWindow.rectTransform.gameObject.activeInHierarchy &&
                    shop.backInvWindow.rectTransform.gameObject.activeInHierarchy,
                    "guest engineer keeps the trade counters and hides the shop fixtures");
                CampaignCheck(AfterhourEvents.IsActive("special_stock") && shop.frontInvinvElement.inventoryShape.height == 21 &&
                    shop.backInvinvElementCounter.childItems.Any(item => item.IsTag(ActivityTag + "PARKED")),
                    "guest engineer uses the original expanded stock grid and parked counter");
                var machine = shop.frontInvinvElement.childItems.First(item => item.IsID("machine_bay_ext"));
                engineerItem = machine.uniqueId;
                priorQuote = campaign.QuoteNumber;
                CampaignCheck(shop.backInvinvElement.TryFindOneValidInventorySlot(machine)?.TryAcceptOnce() == 1,
                    "native engineer item moved to the buying counter");
                var interactable = map.Current.StageObject.GetComponentInChildren<StoreTablet>();
                CampaignCheck(interactable != null, "native engineer negotiation tablet");
                interactable.OnClick();
                outingPhase++;
            }
            else if (outingPhase == 1)
            {
                if (campaign.QuoteNumber <= priorQuote || TradeButton(tablet)?.Interactable != true) return false;
                var machine = shop.backInvinvElement.childItems.Single(item => item.uniqueId == engineerItem);
                engineerPrice = (int)machine.GetNegociatedValue();
                engineerBalance = store.playerCash;
                TradeButton(tablet).button.onClick.Invoke();
                outingPhase++;
            }
            else if (store.playerCash != engineerBalance)
            {
                CampaignCheck(store.playerCash == engineerBalance - engineerPrice &&
                    world.TryFindAccessible(engineerItem, NetSession.GuestPeer, out var machine) && GeneralHelper.IsItemOwned(machine),
                    "guest received the original engineer purchase and paid the shown price");
                outingPhase = 3;
            }
        }
        else if (outingIndex == 1)
        {
            var commissary = CommissaryUIManager.Instance;
            if (outingPhase == 0)
            {
                commissary.OpenUI();
                CampaignCheck(ActivityItem("STAMP").FindItemFeatureByCategory("CATEGORY_GENUINE_STAMP")?
                    .realCondition?.identifier == "genuineStamp", "the original stamp's authenticity survived replication");
                commissaryTokens = store.commissaryData.redeemedCredit;
                commissaryStock = store.commissaryData.GetStock("processed_meat");
                commissary.OnRedeemAllButtonClick();
                outingPhase++;
            }
            else if (outingPhase == 1 && store.commissaryData.redeemedCredit > commissaryTokens)
            {
                commissaryTokens = store.commissaryData.redeemedCredit;
                var product = commissary.elementParent.GetComponentsInChildren<CommissaryElement>(true)
                    .Single(row => row.id == "processed_meat");
                product.OnBuyClick();
                product.OnBuyClick();
                outingPhase++;
            }
            else if (outingPhase == 2 && store.commissaryData.GetStock("processed_meat") != commissaryStock)
            {
                CampaignCheck(store.commissaryData.GetStock("processed_meat") == commissaryStock - 1 &&
                    store.commissaryData.redeemedCredit == commissaryTokens - store.commissaryData.GetItemData("processed_meat").price &&
                    shop.afterhourInventory.childItems.Any(item => item.IsID("processed_meat")),
                    "native commissary redeem and one purchase synchronize tokens, stock and delivered item");
                outingPhase = 3;
            }
        }
        else if (outingPhase == 0)
        {
            CampaignCheck(AfterhourEvents.IsActive("fresh_dump"), "guest sees the original active dump event");
            ScavHelper.ScavengeDumpingGrounds();
            outingPhase = 1;
        }
        else if (store.scavengingAttempts == 1)
        {
            CampaignCheck(campaign.RemoteHost.scavengingAttempts == 0, "only the searching role spends an attempt");
            outingPhase = 3;
        }
        return false;
    }

    private bool FinishActivityNight(double now)
    {
        var ui = EndOfDayUIManager.Instance;
        var service = StoreService.GetJanitorialService();
        int day = StoreStation.GetDayCounter();
        if (activityNight == 0)
        {
            activityNight = day;
            nightStarted = now;
            ui.OpenUI();
        }
        CampaignCheck(now < nightStarted + 30, "full-stock native night and service settlement timed out");
        if (day == activityNight + 1)
        {
            CampaignCheck(localHost ? NativeActorSession.Current.VerifiedCheckpoint?.Day == day &&
                NativeActorSession.Current.AttemptedNights.Count == 1 : PlayerStore.Instance.saveSlotId < 0,
                "full-stock night settled once and saved only on the host");
            if (!localHost)
                CampaignCheck(AdvCalendarUIManager.Instance.nightlyReportSection.activeInHierarchy &&
                    StartOfDayUIManager.Instance.contentGroupObject.transform.childCount > 0,
                    "guest sees the original next-morning report with the host's event lines");
            ActivityResult(RoleName() + " original services checkbox, two confirmations and verified native morning");
            return true;
        }
        var rows = (List<(StoreService service, TabletCheckbox checkbox)>)AccessTools.Field(typeof(EndOfDayUIManager), "rows").GetValue(ui);
        var checkbox = rows.Single(row => row.service == service).checkbox;
        if (!localHost)
        {
            if (!service.isUsing && !checkbox.IsOn) checkbox.Toggle();
            if (!service.isUsing || session.ReadyMask != 1 || nightVoted) return false;
        }
        else if (!service.isUsing || nightVoted) return false;
        CampaignCheck(checkbox.IsOn, "both original settlement panels show the selected service");
        nightVoted = true;
        ui.OnEndDayButtonClick();
        return false;
    }
}
#endif
