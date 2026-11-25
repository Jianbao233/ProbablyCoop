#if PROBABLY_COOP_GRID_QA
using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool pairedOpeningQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-paired-opening") >= 0;
    private readonly bool openingRentQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-opening-rent") >= 0;
    private readonly bool labelLocaleQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-label-locale") >= 0;
    private readonly bool reverseOpeningQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-reverse-opening") >= 0;
    private readonly bool fenceOpeningQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-fence-opening") >= 0;
    private NewGameData.StartType PairedHostCareer => openingRentQa ? NewGameData.StartType.GUNSMITH :
        fenceOpeningQa ? NewGameData.StartType.FENCE : reverseOpeningQa ? NewGameData.StartType.DRUG_DEALER : NewGameData.StartType.WATER_MERCHANT;
    private NewGameData.StartType PairedPartnerCareer => openingRentQa ? NewGameData.StartType.AQUAFARMER :
        fenceOpeningQa ? NewGameData.StartType.GUNSMITH : reverseOpeningQa ? NewGameData.StartType.WATER_MERCHANT : NewGameData.StartType.DRUG_DEALER;
    private int pairedStep, pairedTravelStep, securityFrames;
    private int rentStep;
    private bool labelLocaleChecked, labelLocaleOpened;
    private GameItem labelLocaleWater, labelLocaleTool;
    private string labelLocaleSelected;
    private double securityAt;
    private Vector3 officerLastPosition;
    private bool officerSeen;

    private void TickPairedOpening(RoomSelector room, CoopPanel panel, NativeShop shop, bool inMenu, double now)
    {
        Require(!Application.isBatchMode && now - started < 130, "paired opening or security movement timed out");
        var lobby = panel.QaOpeningLobby;
        if (host && pairedStep == 0 && inMenu && room.IsHost)
        {
            Require(CoopRun.ListCheckpoints().Count == 0, "paired opening requires an empty isolated save profile");
            ModHelper.WriteModDataFileText("probably_coop", "qa-room.txt", room.Code);
            pairedStep = 1;
        }
        if (NativeJoinWizardQa.Enabled && (NativeJoinWizardQa.Blocked || NativeJoinWizardQa.LateFailure))
        {
            if (!host && pairedStep == 3 && inMenu && !room.IsGuest &&
                (NativeJoinWizardQa.Blocked || room.Status == "sync_failed"))
            {
                if (NativeJoinWizardQa.LateFailure) panel.VerifyJoinFailureQa();
                Require(!NativeGuest.Active && !PlayerStore.HasInstance &&
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "EmporiumMenu",
                    $"failed join cleanup incomplete: guest={NativeGuest.Active} store={PlayerStore.HasInstance}");
                Finish("PASS guest join review recovery");
                return;
            }
            if (host && (pairedStep is 3 or 4) && shop != null && room.IsHost && room.Session?.State != LinkState.Connected)
            {
                Require(CoopRun.VerifyCheckpoint(PlayerStore.Instance, 1), "rejected guest changed the host checkpoint");
                Finish("PASS host join review retained room");
                return;
            }
        }
        if (room.Session?.State != LinkState.Connected) return;
        if (host && pairedStep == 1 && inMenu)
        {
            panel.QaStartPairedOpening();
            var menu = MainMenuUIController.Instance;
            Require(NewGameSpecializationUI.IsOpen, "co-op start did not open the original career window");
            menu.OnScenarioContinue(StartingScenario.GetByStartType(PairedHostCareer));
            if (menu.confirmPanelUIManager.panel.activeSelf && menu.confirmPanelUIManager.yesAction != null)
                menu.confirmPanelUIManager.OnYesClick();
            Require(NewGamePerksUI.IsOpen, "co-op start did not open the original host perk window");
            ChooseNativePerk("lucky");
            AccessTools.Method(typeof(NewGamePerksUI), "OnContinueClicked").Invoke(null, null);
            Require(NewGameDifficultyUI.IsOpen, "co-op start did not open the original difficulty window");
            AccessTools.Method(typeof(NewGameDifficultyUI), "OnStartClicked").Invoke(null, null);
            pairedStep = 2;
        }
        if (host && pairedStep == 2 && lobby.QaValidated)
        {
            lobby.QaLaunch();
            pairedStep = 3;
        }
        if (!host && pairedStep == 0 && lobby.HasGuestDraft)
        {
            lobby.QaChoosePerks(StartingScenario.GetByStartType(PairedPartnerCareer).id);
            Require(NewGamePerksUI.IsOpen, "paired guest perk view did not open");
            var visible = (List<StartingPerk>)AccessTools.Field(typeof(NewGamePerksUI), "sorted").GetValue(null);
            ChooseNativePerk("working_pipes");
            if (labelLocaleQa) ChooseNativePerk("maimed");
            AccessTools.Method(typeof(NewGamePerksUI), "OnContinueClicked").Invoke(null, null);
            Record("PASS guest-full-opening-picker", $"native menu contained {visible.Count} perks and submitted starting plumbing");
            pairedStep = 3;
        }
        if (pairedStep < 3 || (host ? shop == null || NativeWorld.Host?.PartnerSetupRequiredForQa != false : NativeWorld.Guest?.Ready != true))
            return;
        if (openingRentQa)
        {
            TickOpeningRentQa(now);
            return;
        }
        if (pairedStep == 3)
        {
            var store = PlayerStore.Instance;
            var itemIds = EmporiumEntry.Instance.invElement.childItems.Select(item => item.identifier).ToHashSet();
            Require(store.startType == PairedHostCareer && NativeOpening.UsesCareer(store, PairedPartnerCareer) &&
                EmporiumEntry.IsWaterTapUnlocked(), "the selected careers or the partner's shared opening perk were missing");
            if (fenceOpeningQa)
            {
                Require(store.networkUpgrade["RETIRED_GUNSMITH"].state == 2 && store.networkUpgrade["GUN_PERMIT_I"].state == 2 &&
                    itemIds.Contains("gun_part"), "the partner gunsmith did not receive the original opening goods and technology");
                if (host) CheckFenceGunsmithSchedule();
            }
            else
            {
                Require(itemIds.Contains("water_jug") && itemIds.Contains("chemistry_guide") &&
                    store.networkUpgrade["RETIRED_CHEMIST"].state == 2 && store.networkUpgrade["WATER_TRADER"].state == 2,
                    "the water merchant and chemist did not receive both original packages and technologies");
                var cartel = store.storeReputations.Single(rep => rep.factionId == StoreReputation.FACTION_CARTEL);
                Require(cartel.perks.Any(perk => perk.perkId == "CARTEL_AUTHORIZED_EXIT"),
                    "the chemist career started without the native cartel exit path");
                if (host) CheckPairedCareerSchedule();
                Record("PASS paired-career-reputation", "both processes have one cartel faction and its original exit perk");
            }
            Require(host ? CoopRun.VerifyCheckpoint(store, 1) : store.saveSlotId == -1,
                "two-player start did not isolate saves and create a host checkpoint");
            Require(StartingPerk.IsPerkActive("lucky") == host,
                "the host's personal starting perk leaked across roles or was not applied");
            if (labelLocaleQa)
            {
                Require(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND).IsDiscovered() == host,
                    "map discovery used a different actor's travel restriction");
                Record("PASS personal-map-discovery", "native dumping ground discovery followed the local actor's starting perk");
            }
            Record("PASS paired-opening", $"host={PairedHostCareer} partner={PairedPartnerCareer}, plumbing, day one, shared cash {store.playerCash}");
            NativeOffscreenCapture.Capture("qa-paired-opening");
            pairedStep = 4;
        }
        if (labelLocaleQa && !TickLabelLocaleQa()) return;
        if (!TickPairedTravel()) return;
        if (securityAt == 0) securityAt = now + 4;
        var ui = StoreUIManager.Instance;
        if (host)
        {
            if (pairedStep == 4 && now >= securityAt)
            {
                ui.StartArrestSequence();
                pairedStep = 5;
                securityAt = now + 5;
            }
            if (pairedStep == 5 && now >= securityAt)
            {
                ui.ClearArrestState();
                Record("PASS security-dispatch", "original pair dispatched and cleared");
                Record("PASS host paired opening and security", "allowing the partner to finish observing before exit");
                pairedStep = 6;
                securityAt = now + 8;
            }
            if (pairedStep == 6 && now >= securityAt) Finish("PASS host paired opening and security");
            return;
        }
        var officer = ui.secGameObject;
        if (officer.gameObject.activeSelf)
        {
            var position = officer.transform.localPosition;
            var path = officer.GetComponent<DOTweenPath>().tween;
            if (officerSeen && path != null && path.IsActive() && path.IsPlaying() &&
                Vector3.Distance(officerLastPosition, position) > 0.001f)
            {
                securityFrames++;
                if (securityFrames == 12) NativeOffscreenCapture.Capture("qa-security-local-path");
            }
            officerSeen = true;
            officerLastPosition = position;
        }
        else if (officerSeen)
        {
            Require(securityFrames >= 12, $"guest saw only {securityFrames} changing officer frames");
            if (ui.secGameObject2.gameObject.activeSelf) return;
            Record("PASS local-security-motion", $"{securityFrames} moving frames on local native path; both officers hidden");
            Finish("PASS guest paired opening and security");
        }
    }

    private void CheckPairedCareerSchedule()
    {
        var station = StoreStation.Instance;
        var queueProbe = new StoreClientManager();
        int originalDay = station.dayCounter;
        try
        {
            queueProbe.HandleCartelClient();
            Require(queueProbe.clientStack.Count(row => row.identifier == "cartelIntro") == 1,
                "the partner chemist's day-one introduction was missing or repeated");
            queueProbe.clientStack.Clear();
            station.dayCounter = 3;
            queueProbe.HandleCartelClient();
            queueProbe.HandleContentUnlockClient();
            foreach (string expected in new[] { "street_dealer", "shadyFarmer", "retired_water_merchant" })
                Require(queueProbe.clientStack.Count(row => row.identifier == expected) == 1,
                    $"both career schedules should keep exactly one copy of {expected}");
            queueProbe.clientStack.Clear();
            station.dayCounter = 4;
            queueProbe.HandleCartelClient();
            Require(queueProbe.clientStack.Count(row => row.identifier == "cartelHandlerCheckin") == 1,
                "the partner chemist's first recurring obligation was missing or repeated");
            Record("PASS paired-career-schedule", "native day 1 introduction, day 3 chemist and water merchant in the same queue, and day 4 check-in");
        }
        finally { station.dayCounter = originalDay; }
    }

    private void CheckFenceGunsmithSchedule()
    {
        var station = StoreStation.Instance;
        var queueProbe = new StoreClientManager();
        int originalDay = station.dayCounter;
        try
        {
            queueProbe.HandleSkipClient();
            foreach (string expected in new[] { "janitorUEIntroductionFence", "ll_gun_buyer" })
                Require(queueProbe.clientStack.Count(row => row.identifier == expected) == 1,
                    $"the combined day-one opening schedule should have exactly one {expected}");
            queueProbe.clientStack.Clear();
            station.dayCounter = 2;
            queueProbe.HandleSkipClient();
            Require(queueProbe.clientStack.Count(row => row.identifier == "ll_gun_seller") == 1,
                "the partner gunsmith's day-two seller did not follow the day-one buyer");
            Record("PASS fence-gunsmith-schedule", "original underground exchange introduction and gun buyer on day 1, then gun seller on day 2");
        }
        finally { station.dayCounter = originalDay; }
    }

    private bool TickLabelLocaleQa()
    {
        if (labelLocaleChecked) return true;
        const string category = "CATEGORY_WATER_PURITY";
        var entry = EmporiumEntry.Instance;
        Require(LocHelper.GetCurrentLocaleCode() == (host ? "en" : "zh"), "test profiles need different languages");
        if (host)
        {
            if (labelLocaleWater == null)
            {
                foreach (string id in new[] { "labeler", "labeler_blue", "small_bottled_water" })
                {
                    var item = DirectoryMaster.Item(id);
                    GeneralHelper.SetItemOwned(item, true);
                    if (id == "small_bottled_water")
                    {
                        WaterHelper.FillWithPureWater(item);
                        GeneralHelper.SetCustomName(item, "H2O cold ★");
                        labelLocaleWater = item;
                        var original = item.FindItemFeatureByCategory(category).GetCurrentItemCondition();
                        labelLocaleSelected = ItemConditionList.conditionLookup[category].First(option => option.identifier != original.identifier).identifier;
                    }
                    Require(TryPlaceInEmptyCell(item, entry.backInvinvElement) ||
                        TryPlaceInEmptyCell(item, entry.invElement), "test label inventory full: " + id);
                }
                Record("PASS label-locale-host", "English host seeded native printers, a standard label and a custom name");
                return false;
            }
            if (labelLocaleWater.FindItemFeatureByCategory(category).fakeCondition?.identifier != labelLocaleSelected)
                return false;
            Record("PASS label-locale-edit-host", "guest dropdown changed the native label condition using the same identifier");
        }
        else
        {
            var items = entry.backInvinvElement.childItems.Concat(entry.invElement.childItems).ToArray();
            var water = items.FirstOrDefault(item => GeneralHelper.GetCustomName(item) == "H2O cold ★");
            if (water == null) return false;
            foreach (string id in new[] { "labeler", "labeler_blue" })
            {
                var printers = items.OfType<GameItemElement>().Where(item => item.identifier == id).ToArray();
                string localName = LocHelper.GetLocalizedItem("item_" + id + "_name");
                Require(printers.Length > 0 && printers.All(printer => printer.name == localName &&
                    printer.GetTooltip().nakedText.Contains(localName) && printer.GetTooltipAdvanced().nakedText.Contains(localName)),
                    "remote labeler text did not use the local language: " + id);
            }
            var ui = LabelerUIManager.Instance;
            if (!labelLocaleOpened)
            {
                if (labelLocaleTool == null)
                {
                    labelLocaleTool = items.First(item => item.identifier == "labeler");
                    ItemSelectHandler.current.TrySelectItem(labelLocaleTool);
                    return false;
                }
                if (!labelLocaleTool.MayTarget(water) || !labelLocaleTool.CanTarget(water)) return false;
                labelLocaleTool.Target(water);
                ItemSelectHandler.current.EndSelect();
                Require(ui.panel.activeSelf, "original guest targeting did not open the label editor");
                labelLocaleOpened = true;
                return false;
            }
            var condition = water.FindItemFeatureByCategory(category).GetCurrentItemCondition();
            if (labelLocaleSelected != null && labelLocaleSelected != condition.identifier) return false;
            var native = ItemConditionList.conditionLookup[category].Single(entry => entry.identifier == condition.identifier);
            var row = ui.verticalLayoutGroup.GetComponentsInChildren<LabelerElement>().Single(entry => entry.GetCategory() == category);
            Require(condition.modValue == native.modValue && row.currentFeatureDisplay.text.Contains(native.display) &&
                native.display.Any(c => c is >= '\u4e00' and <= '\u9fff') && water.GetDisplayName(preferCustomName: true) == "H2O cold ★",
                "standard label, numeric condition or custom name did not match");
            if (labelLocaleSelected == null)
            {
                NativeOffscreenCapture.Capture("qa-cross-language-label");
                var choices = ItemConditionList.conditionLookup[category];
                int next = choices.FindIndex(option => option.identifier != condition.identifier);
                Require(next >= 0, "fixture needs another label condition");
                labelLocaleSelected = choices[next].identifier;
                row.dropdown.SetValueWithoutNotify(next);
                row.OnDropdownValueChanged();
                return false;
            }
            ui.CloseUI();
            Record("PASS label-locale-guest", "Chinese original label row and printer names; guest edit in Chinese confirmed against English authority; custom name unchanged");
        }
        labelLocaleChecked = true;
        return true;
    }

    private void TickOpeningRentQa(double now)
    {
        var store = PlayerStore.Instance;
        if (rentStep == 0)
        {
            Require(store.startType == NewGameData.StartType.GUNSMITH && NativeOpening.UsesCareer(store, NewGameData.StartType.AQUAFARMER),
                "the native opening did not apply both costly careers");
            Require(store.playerCash == 0 && OpeningRent.Describe(store) == (30, 300), "default four-week opening debt is not 30/60/90/120");
            Record("PASS opening-rent-initial", "both careers initialized; no negative cash; first rent surcharge 30 of 300");
            if (!host)
            {
                rentStep = 1;
                return;
            }
            Require(CoopRun.VerifyCheckpoint(store, 1), "debt was not saved in the day-one co-op checkpoint");
            securityAt = now + 3;
            rentStep = 1;
        }
        if (!host)
        {
            if (OpeningRent.Describe(store).Balance != 0 || !store.IsPropertyPaid) return;
            Finish("PASS guest paired opening and security");
            return;
        }
        if (rentStep != 1 || now < securityAt) return;
        store.playerCash = 1000000;
        int cash = store.playerCash;
        store.OnRentNotPaid();
        Require(store.playerCash == cash && OpeningRent.Describe(store) == (60, 300),
            "rent grace changed the total or lost this week's debt");
        int rent = store.GetRentValue();
        store.OnRentPaid();
        Require(store.GetRentValue() == rent && store.playerCash == cash, "duplicate same-day rent callback charged again");
        StoreStation.Instance.dayCounter += 7;
        store.OnRentPaid();
        Require(store.playerCash == cash - rent && OpeningRent.Describe(store) == (90, 210),
            "next rent did not settle the two previous instalments exactly once");
        int quote = store.GetStoreBuyPrice();
        cash = store.playerCash;
        store.BuyOutStore();
        Require(store.IsPropertyPaid && store.playerCash == cash - quote && OpeningRent.Describe(store) == (0, 0),
            "native buyout did not retire the remaining debt");
        Record("PASS opening-rent-grace-payment-buyout", "native grace, duplicate callback, next payment, and buyout all matched the ledger");
        Record("PASS host paired opening and security", "opening rent branch");
        rentStep = 2;
    }

    private static void ChooseNativePerk(string id)
    {
        var type = typeof(NewGamePerksUI);
        var perks = (List<StartingPerk>)AccessTools.Field(type, "sorted").GetValue(null);
        int index = perks.FindIndex(perk => perk.id == id);
        Require(index >= 0, "perk not present in native picker: " + id);
        AccessTools.Field(type, "page").SetValue(null, index / 36);
        AccessTools.Method(type, "Rebuild").Invoke(null, null);
        AccessTools.Method(type, "OnSlotClicked").Invoke(null, new object[] { id });
        Require(NewGameData.Instance.startingPerks.Any(perk => perk.id == id), "native perk selection ignored: " + id);
    }

    private bool TickPairedTravel()
    {
        var map = MapUIManager.Instance;
        var store = PlayerStore.Instance;
        var world = host ? NativeWorld.Host : NativeWorld.Guest;
        const string destination = AfterhourLocation.DUMPING_GROUND;
        var location = AfterhourLocation.Get(destination);
        if (host)
        {
            if (pairedTravelStep == 0)
            {
                StoreUIManager.Instance.CloseAllUI();
                store.storeState = PlayerStore.StoreState.CLOSED;
                store.isAfterhoursIntroduced = true;
                store.visitLeftTonight = 1;
                map.Travel(location);
                Require(world.TripReady == 1, "host did not record its departure intent");
                pairedTravelStep = 1;
            }
            if (pairedTravelStep == 1 && map.Current?.Id == destination)
            {
                Require(store.GetAfterhourState(destination).visits == 1, "native location initialized more than once");
                var ground = EmporiumEntry.Instance.afterhourInventory;
                Require(TryPlaceInEmptyCell(DirectoryMaster.Item("processed_meat"), ground), "cannot seed an empty test ground");
                pairedTravelStep = 2;
            }
            if (pairedTravelStep == 2 && world.ProposedTrip == "return" && world.TripReady == 2)
            {
                map.Return();
                pairedTravelStep = 3;
            }
            if (pairedTravelStep == 3 && !map.IsOutside && !map.IsInTransit)
            {
                Require(EmporiumEntry.Instance.afterhourInventory.childItems.Count == 0, "native return did not clear the shared ground");
                store.storeState = PlayerStore.StoreState.MORNING;
                pairedTravelStep = 4;
                Record("PASS host-paired-ground", "both travel confirmations accepted, original location ran once and cleared after return");
            }
        }
        else
        {
            if (pairedTravelStep == 0 && world.ProposedTrip == destination && world.TripReady == 1)
            {
                map.Travel(location);
                pairedTravelStep = 1;
            }
            if (pairedTravelStep == 1 && map.Current?.Id == destination &&
                EmporiumEntry.Instance.afterhourInventory.childItems.Any(item => item.identifier == "processed_meat"))
            {
                NativeOffscreenCapture.Capture("qa-paired-ground");
                map.Return();
                pairedTravelStep = 2;
            }
            if (pairedTravelStep == 2 && !map.IsOutside)
            {
                Require(EmporiumEntry.Instance.afterhourInventory.childItems.Count == 0, "guest still has cleared ground items after return");
                pairedTravelStep = 4;
                Record("PASS guest-paired-ground", "partner projected the host's real ground at the original location and returned to the store");
            }
        }
        return pairedTravelStep == 4;
    }
}
#endif
