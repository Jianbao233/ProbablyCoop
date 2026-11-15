#if PROBABLY_COOP_GRID_QA
using System;
using System.Linq;
using System.Security.Cryptography;
using ProbablyCoop.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private int campaignFirstDay, campaignAct;
    private int appraisalStep;
    private int estimateStep, estimateBalance, estimateItemId;
    private double estimateAt;
    private uint submittedEpoch = uint.MaxValue;
    private uint searchedEpoch = uint.MaxValue;
    private uint requestedSearchEpoch = uint.MaxValue;
    private int requestedSearchCount;
    private double campaignNextAt;
    private bool campaignAudited, campaignDone;
    private bool guestOpenedShutter, guestClosedShutter, observedGuestClose;
    private double restoreStableAt;
    private readonly bool rejoinCampaign = Environment.GetCommandLineArgs().Contains("--probably-coop-grid-rejoin");
    private readonly bool manualCampaign = Environment.GetCommandLineArgs().Contains("--probably-coop-grid-manual");
    private static readonly bool BothScavengingPerks =
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-grid-both-scavenging-perks") >= 0;

    private static string[] CampaignQaPerks(bool host) => BothScavengingPerks
        ? new[] { "proficient_scavenger", "careful_scavenger" }
        : new[] { host ? "proficient_scavenger" : "careful_scavenger" };

    private static InventoryAddress HostPocket => new InventoryAddress(InventoryRoot.HostCarry, slot: 0);
    private static InventoryAddress PartnerPocket => new InventoryAddress(InventoryRoot.PartnerCarry, slot: 1);
    private static InventoryAddress Backpack(bool host) =>
        new InventoryAddress(host ? InventoryRoot.HostCarry : InventoryRoot.PartnerCarry, slot: 2);

    private void InitializeCampaignQa()
    {
        var actors = NativeActorSession.EnsureForExisting(PlayerStore.Instance);
        var draft = NativeActorPartnerSetup.CreateDraft(actors.Run.Run, CampaignQaPerks(false));
        var setup = NativeActorPartnerSetup.Prepare(actors.Run, draft, null);
        actors.Store.modData[NativeActorPartnerSetup.SaveKey] = setup.Commit();
        actors.Store.modData[NativeEffectCampaign.SaveKey] = NativeEffectCampaign.SaveSchema;
        if (FullStockQa)
        {
            LoadFullStockQa(actors.Store);
            return;
        }
        actors.Store.dumpingGroundMinorWoundImmunitySearchesLeft = 20;
        actors.Store.dumpingGroundMajorWoundImmunitySearchesLeft = 20;
        actors.Store.appraiserLevel = 2;
        var heater = DirectoryMaster.Item("heating_plate");
        CampaignCheck(heater != null, "original heater exists");
        GeneralHelper.SetItemOwned(heater, true);
        CampaignCheck(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.invElement, heater) == 0,
            "original heater fits in shared stock");
        SeedWorkshopQa();
        var certificate = DirectoryMaster.Item("cert3");
        CampaignCheck(certificate != null, "original rank-two certificate factory");
        GeneralHelper.SetItemOwned(certificate, true);
        CampaignCheck(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.backInvinvElement, certificate) == 0,
            "native certificate can be placed in shared stock");
        foreach (bool host in new[] { true, false })
        {
            var bag = DirectoryMaster.Item(BagCatalog(host));
            var cargo = DirectoryMaster.Item(CargoCatalog(host));
            CampaignCheck(bag != null && cargo != null, "native backpack and its distinct cargo exist");
            GeneralHelper.SetItemOwned(bag, true);
            GeneralHelper.SetItemOwned(cargo, true);
            CampaignCheck(GraphUtils.TryAcceptAll(bag, cargo) == 0 &&
                GraphUtils.TryAcceptAll(EmporiumEntry.Instance.backInvinvElement, bag) == 0,
                "original bags with nested cargo fit in the shared counter");
        }
        actors.Store.SaveGame();
        CampaignCheck(actors.VerifiedCheckpoint?.Day == StoreStation.GetDayCounter(),
            "native initial save includes the two independently selected perks");
    }

    private void TickCampaignQa()
    {
        if (campaign == null || !Ready || campaignDone) return;
        if (ShopUiQa)
        {
            TickShopUiQa();
            return;
        }
        if (manualCampaign) return;
        if (FullStockQa)
        {
            TickFullStockQa();
            return;
        }
        var store = PlayerStore.Instance;
        var map = MapUIManager.Instance;
        int day = StoreStation.GetDayCounter();
        if (campaignFirstDay == 0) campaignFirstDay = day;
        if (!campaignAudited)
        {
            CampaignCheck(StartingPerk.IsProficientScavenger() == (BothScavengingPerks || localHost) &&
                StartingPerk.IsCarefulScavenger() == (BothScavengingPerks || !localHost),
                "the local store has the local actor's original perks");
            CampaignCheck(SharedStock()
                .Any(item => item.identifier == "large_bottled_water" && WaterHelper.GetTotalVolume(item) > 0),
                "original water merchant starts with visible liquid facts");
            CampaignCheck(localHost ? store.saveSlotId >= 0 : store.saveSlotId == -1,
                "only the isolated host has a native save slot");
            if (!localHost)
                session.Guest.Bounced += (tx, verdict) =>
                {
                    if (InventoryAction.TryRead(tx.Context, out var action))
                        Debug.Log($"[probably_coop CAMPAIGN] {action.Kind} source={action.Source} target={action.Target} bounced={verdict}");
                };
            campaignAudited = true;
            Debug.Log($"[probably_coop CAMPAIGN] PASS original water merchant and independent perks role={RoleName()} day={day}");
        }
        if (ResumingCampaign || rejoinCampaign)
        {
            VerifyCampaignRestore(4);
            return;
        }
        if (!CheckCampaignAppraisal()) return;
        if (!localHost) ObserveAppraisalService(day);

        if (day == campaignFirstDay + 3)
        {
            CampaignCheck(!map.IsOutside && Epoch == 9 && store.storeState == PlayerStore.StoreState.MORNING,
                "third morning after three shared departures, returns and nights");
            if (localHost)
            {
                var actors = NativeActorSession.EnsureForExisting(store);
                CampaignCheck(actors.AttemptedNights.Count == 3 && actors.VerifiedCheckpoint?.Day == day,
                    "three original nights and a verified native save");
            }
            campaignDone = true;
            Debug.Log($"[probably_coop CAMPAIGN] PASS role={RoleName()} three original nights {CampaignFacts(day)} epoch={Epoch}");
            if (!localHost) RequestExit();
            return;
        }
        if (!localHost)
        {
            if (day == campaignFirstDay && !guestOpenedShutter && campaign.ShutterAvailable &&
                store.storeState == PlayerStore.StoreState.MORNING)
            {
                StoreShutterButton.Instance.OnClick();
                guestOpenedShutter = true;
            }
            if (!map.IsOutside && store.storeState == PlayerStore.StoreState.OPEN)
            {
                if (!RunGuestBusinessQa(day)) return;
                if (day == campaignFirstDay && !RunGuestHeaterQa()) return;
            }
            if (day == campaignFirstDay + 1 && (store.storeState == PlayerStore.StoreState.OPEN || augStage != 0) &&
                !RunGuestShotQa()) return;
            if (day == campaignFirstDay + 2 && (store.storeState == PlayerStore.StoreState.OPEN || workshopStage != 0) &&
                !RunWorkshopQa()) return;
            if (day == campaignFirstDay + 2 && !guestClosedShutter && campaign.ShutterAvailable &&
                store.storeState == PlayerStore.StoreState.OPEN && store.currentClientInstance == null)
            {
                StoreShutterButton.Instance.OnClick();
                if (StoreUIManager.Instance.closeShutterConfirmationPanel.activeSelf)
                    StoreShutterButton.Instance.EndAndClose();
                guestClosedShutter = true;
            }
            if (guestClosedShutter && !observedGuestClose && store.storeState == PlayerStore.StoreState.CLOSED)
            {
                observedGuestClose = true;
                Debug.Log("[probably_coop CAMPAIGN] PASS guest original shutter close and confirmation");
            }
            if (map.IsOutside && !CampaignCarryReady(localHost) && !PrepareCampaignCarry(localHost, day)) return;
            if (map.IsOutside && searchedEpoch != Epoch && session.Guest.PendingCount == 0) CheckGuestSearch(map, store, day);
            if (session.ReadyMask == 1 && submittedEpoch != Epoch && session.Guest.PendingCount == 0)
            {
                var action = session.Proposal;
                CampaignCheck(action.Day == day, "a proposal belongs to the active original day");
                if (action.Kind == TeamTransitionKind.Depart) ChooseCampaignBackpack(localHost, action.Destination);
                else if (action.Kind == TeamTransitionKind.Return) map.ReturnNow();
                else EndOfDayUIManager.Instance.OnEndDayButtonClick();
                submittedEpoch = Epoch;
            }
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (now < campaignNextAt) return;
        switch (campaignAct)
        {
            case 0:
                if (day == campaignFirstDay)
                {
                    if (store.storeState != PlayerStore.StoreState.OPEN) return;
                    Debug.Log("[probably_coop CAMPAIGN] PASS guest original shutter open");
                }
                else
                {
                    CampaignCheck(store.storeState == PlayerStore.StoreState.MORNING, "native morning before opening");
                    StoreShutterButton.Instance.OpenShutter();
                }
                if (day == campaignFirstDay + 1) SeedShotQa();
                SeedBusinessQa(day);
                campaignAct = 1;
                campaignNextAt = now + 2.5;
                break;
            case 1:
                bool guestCloses = day == campaignFirstDay + 2;
                CampaignCheck(store.storeState == PlayerStore.StoreState.OPEN ||
                    guestCloses && store.storeState == PlayerStore.StoreState.CLOSED, "original shutter opened for the day");
                if (businessPurchases != 1 || businessSales != 1) return;
                if (day == campaignFirstDay && !CheckHostHeaterQa()) return;
                if (day == campaignFirstDay + 1 && !CheckHostShotQa()) return;
                if (day == campaignFirstDay + 2 && !CheckHostWorkshopQa()) return;
                if ((day != campaignFirstDay || estimateStep == 0) && store.currentClientInstance != null) return;
                if (day == campaignFirstDay && !RunAppraisalService(now)) return;
                if (guestCloses)
                {
                    if (store.storeState != PlayerStore.StoreState.CLOSED) return;
                }
                else StoreShutterButton.Instance.EndAndClose();
                campaignAct = 2;
                campaignNextAt = now + 2.5;
                break;
            case 2:
                var departure = new TeamTransition(TeamTransitionKind.Depart, day, AfterhourLocation.DUMPING_GROUND);
                if (store.currentClientInstance != null ||
                    !campaign.CanStart(departure, SyncHost.HostPeer)) return;
                ChooseCampaignBackpack(localHost, AfterhourLocation.DUMPING_GROUND);
                CampaignCheck(session.ReadyMask == 1 && !map.IsOutside, "first travel confirmation waits in the shop");
                campaignAct = 3;
                break;
            case 3:
                if (!map.IsOutside) return;
                if (!CampaignCarryReady(localHost) && !PrepareCampaignCarry(localHost, day)) return;
                if (!CampaignCarryReady(false)) return;
                var outingActors = NativeActorSession.EnsureForExisting(store);
                int searches = BothScavengingPerks && day == campaignFirstDay ? 5 : 1;
                if (searches == 5 && store.scavengingAttempts < searches)
                {
                    map.ScavengeButtonClick();
                    return;
                }
                if (outingActors.Run.Partner.scavengingAttempts < searches) return;
                CampaignCheck(outingActors.Run.Partner.scavengingAttempts == searches &&
                    store.scavengingAttempts == (searches == 5 ? searches : 0),
                    "native searches consume only the acting player's attempts");
                if (searches == 5)
                    Debug.Log("[probably_coop CAMPAIGN] PASS both original scavengers exhausted their first-night attempts");
                Debug.Log($"[probably_coop CAMPAIGN] PASS two native carry roots and one arrival day={day}");
                map.ReturnNow();
                campaignAct = 4;
                break;
            case 4:
                if (map.IsOutside) return;
                CampaignCheck(!CampaignCarryReady(true) && !CampaignCarryReady(false), "both pockets returned to shared shop stock");
                CampaignCheck(world.Grid(Backpack(true)).childItems.Count == 0 &&
                    world.Grid(Backpack(false)).childItems.Count == 0 &&
                    SharedStock().Any(item => item.identifier == BagCatalog(true)) &&
                    SharedStock().Any(item => item.identifier == BagCatalog(false)),
                    "both selected backpacks and their native contents returned to the shop");
                EndOfDayUIManager.Instance.OnEndDayButtonClick();
                CampaignCheck(session.ReadyMask == 1 && StoreStation.GetDayCounter() == day, "one vote cannot settle a night");
                campaignAct = 5;
                break;
            case 5:
                if (Epoch % 3 != 0) return;
                CampaignCheck(day == campaignFirstDay + Epoch / 3, "each shared night advances one day");
                campaignNextAt = now + 1.5;
                campaignAct = 0;
                break;
        }
    }

    private void VerifyCampaignRestore(int expectedDay)
    {
        // Original one-second updates rehydrate the controls for persisted tags.
        double now = Time.realtimeSinceStartupAsDouble;
        if (restoreStableAt == 0) restoreStableAt = now + 1.25;
        if (now < restoreStableAt) return;
        var store = PlayerStore.Instance;
        int day = StoreStation.GetDayCounter();
        CampaignCheck(day == expectedDay && !MapUIManager.Instance.IsOutside &&
            store.storeState == PlayerStore.StoreState.MORNING && Epoch == 0 && (localHost
                ? NativeActorSession.Current.VerifiedCheckpoint?.Day == day && NativeActorSession.Current.AttemptedNights.Count == 0
                : store.saveSlotId == -1), "restore the settled morning with the new connection epoch");
        if (ActivityQa && localHost)
            CampaignCheck(store.modData.TryGetValue(EffectItemIds.SaveKey, out string saved) &&
                EffectItemIds.TryRead(saved, out var allocation) && allocation.Guest == world.GuestWatermark,
                "the original disk reload retained the guest allocation watermark");
        campaignDone = true;
        Debug.Log($"[probably_coop CAMPAIGN] PASS role={RoleName()} restore original day {CampaignFacts(day)}");
        if (!localHost) RequestExit();
    }

    private bool CampaignCarryReady(bool hostActor) =>
        world.Grid(hostActor ? HostPocket : PartnerPocket).childItems.Count != 0;

    private void ChooseCampaignBackpack(bool hostActor, string destination)
    {
        var bag = SharedStock().SingleOrDefault(item => item.identifier == BagCatalog(hostActor));
        CampaignCheck(bag != null, "local native backpack is available for this departure");
        EmporiumEntry.Instance.selectedBackpack = bag;
        MapUIManager.Instance.Travel(AfterhourLocation.Get(destination));
    }

    private System.Collections.Generic.IEnumerable<GameItem> SharedStock() =>
        new[] { InventoryRoot.Main, InventoryRoot.BackCounter, InventoryRoot.Showcase }
            .SelectMany(root => world.Grid(new InventoryAddress(root)).childItems);

    private static string BagCatalog(bool host) => host ? "backpack_medium" : "backpack_small";
    private static string CargoCatalog(bool host) => host ? "toilet_paper" : "processed_juice";

    private bool CheckCampaignAppraisal()
    {
        if (appraisalStep == 2) return true;
        var store = PlayerStore.Instance;
        var sharedActor = localHost ? NativeActorState.Capture(store) : campaign.RemoteHost;
        bool blocked = sharedActor.appraisalBlacklist.Contains(AppraisalItemRegistry.HAND);
        if (appraisalStep == 0)
        {
            if (!localHost && !blocked) return false;
            var certificate = world.Grid(new InventoryAddress(InventoryRoot.BackCounter)).childItems
                .Single(item => item.identifier == "cert3");
            CampaignCheck(certificate.CanActivateSlotItem(), "the appraisal certificate is usable in native stock");
            certificate.ActivateSlotItem();
            var toggle = AppraisalToggle();
            CampaignCheck(toggle.isOn != blocked && toggle.interactable && sharedActor.appraiserLevel == 2,
                "original appraisal panel renders the shared category state");
            toggle.isOn = !toggle.isOn;
            appraisalStep = 1;
            return false;
        }
        if (blocked) return false;
        CampaignCheck(AppraisalToggle().isOn && store.appraiserLevel == (localHost ? 2 : 0),
            "the original panel refreshes and personal ranks remain separate");
        CustomUIManager.Instance.CloseWindow(NativeWorld.AppraisalWindowId);
        appraisalStep = 2;
        Debug.Log($"[probably_coop CAMPAIGN] PASS role={RoleName()} native appraisal policy and independent rank");
        return true;
    }

    private static Toggle AppraisalToggle()
    {
        var window = CustomUIManager.Instance.GetWindow(NativeWorld.AppraisalWindowId);
        CampaignCheck(window?.IsAlive == true, "original appraisal window is open");
        var toggles = window.Rect.GetComponentsInChildren<Toggle>(true);
        CampaignCheck(toggles.Length == 3, "original appraisal panel lists all three unlocked categories");
        return toggles[0];
    }

    private bool RunAppraisalService(double now)
    {
        var store = PlayerStore.Instance;
        var front = EmporiumEntry.Instance.frontInvinvElement;
        if (estimateStep == 0)
        {
            var customer = StoreClientListAppraisal.CreateAppraisal1();
            store.currentClientInstance = StoreClientInstance.CreateClientInstance(customer);
            store.isClientArrived = true;
            var dialogue = customer.mainDialogue;
            while (dialogue.nextDialogue != null) dialogue = dialogue.nextDialogue;
            dialogue.ExecuteEndAction();
            CampaignCheck(front.childItems.Count(item => item.IsTag("APPRAISAL_ITEM")) == 2,
                "the original appraisal request creates two native offers");
            var offer = front.childItems.First(item => item.IsTag("APPRAISAL_ITEM"));
            estimateBalance = store.playerCash;
            estimateItemId = offer.uniqueId;
            expectedAppraisalFee = AppraisalRankHelper.GetBaseFee() +
                RNG.CalculatePercentage(AppraisalHelper.GetCorrectBaseValue(offer), AppraisalRankHelper.GetFeePercent(1));
            estimateStep = 1;
            estimateAt = now + 1;
            return false;
        }
        if (estimateStep == 2) return true;
        if (now < estimateAt || store.playerCash == estimateBalance) return false;
        CampaignCheck(expectedAppraisalFee > 0 && store.playerCash == estimateBalance + expectedAppraisalFee &&
            !world.TryFindAccessible(estimateItemId, ProbablyCoop.Net.NetSession.GuestPeer, out _) &&
            front.childItems.Count(item => item.IsTag("SLIP_BODY")) == 1,
            "original perfect appraisal consumes one offer, creates one note and pays the exact fee once");
        estimateStep = 2;
        Debug.Log($"[probably_coop CAMPAIGN] PASS original appraisal settlement from guest fee={expectedAppraisalFee}");
        return false;
    }

    private int expectedAppraisalFee;
    private bool sentEstimate;
    private ulong appraisalQuote;

    private void ObserveAppraisalService(int day)
    {
        if (estimateStep == 2) return;
        CampaignCheck(day == campaignFirstDay, "guest saw the appraisal event before the next day");
        var front = EmporiumEntry.Instance.frontInvinvElement;
        if (estimateStep == 0)
        {
            var offers = front.childItems.Where(item => item.IsTag("APPRAISAL_ITEM")).ToArray();
            if (offers.Length != 2 || businessPhase != 8 || heaterStage != 4) return;
            estimateBalance = PlayerStore.Instance.playerCash;
            estimateItemId = offers[0].uniqueId;
            appraisalQuote = campaign.QuoteNumber;
            var back = EmporiumEntry.Instance.backInvinvElement;
            CampaignCheck(back.TryFindOneValidInventorySlot(offers[0])?.TryAcceptOnce() == 1,
                "the guest moves an appraisal offer to the original inspection counter");
            estimateStep = 1;
            return;
        }
        if (!sentEstimate)
        {
            if (session.Guest.PendingCount != 0 || campaign.QuoteNumber == appraisalQuote ||
                !world.TryFindAccessible(estimateItemId, ProbablyCoop.Net.NetSession.GuestPeer, out var offer)) return;
            var inspector = InspectionUIManager.Instance;
            inspector.OnInspectItem(offer);
            CampaignCheck(inspector.appraisalPanel.activeSelf, "guest opens the original appraisal input");
            inspector.apprisalServiceInputField.text = AppraisalHelper.GetCorrectBaseValue(offer).ToString();
            inspector.OnAppraisalServiceEstimated();
            sentEstimate = true;
            return;
        }
        if (!front.childItems.Any(item => item.IsTag("SLIP_BODY"))) return;
        CampaignCheck(!world.TryFindAccessible(estimateItemId, ProbablyCoop.Net.NetSession.GuestPeer, out _) &&
            PlayerStore.Instance.playerCash > estimateBalance &&
            front.childItems.Count(item => item.IsTag("SLIP_BODY")) == 1,
            "guest got one appraisal fee, one note and the retired item");
        estimateStep = 2;
        Debug.Log("[probably_coop CAMPAIGN] PASS guest received original appraisal settlement facts");
    }

    private bool PrepareCampaignCarry(bool hostActor, int day)
    {
        var map = MapUIManager.Instance;
        CampaignCheck(map.Current?.Id == AfterhourLocation.DUMPING_GROUND &&
            !EmporiumEntry.Instance.storePhysical.activeSelf, "native shared dumping ground view");
        int remaining = BothScavengingPerks ? 5 : hostActor ? 7 : 3;
        CampaignCheck(ScavHelper.GetScavTimeLeft() == remaining,
            $"native role-specific scavenging reset on day {day}");
        if (map.scavTimeRemainingTMP.text != LocHelper.GetLocalizedMechanic("afterhour_attempts_remaining", remaining))
            return false;
        var backpack = world.Grid(Backpack(hostActor)).childItems.SingleOrDefault();
        CampaignCheck(backpack?.identifier == BagCatalog(hostActor) &&
            GraphUtils.FindAllChildrenType<GameItem>(backpack).Any(item => item.identifier == CargoCatalog(hostActor)),
            "local native backpack and nested cargo follow the actor who selected it");
        if (StartingPerk.IsCarefulScavenger())
            CampaignCheck(ScavHelper.GetMinorWoundChance() == 0 && ScavHelper.GetMajorWoundChance() == 0,
                "careful scavenger keeps both injury protections");
        string catalog = hostActor ? "bottled_water" : "small_bottled_water";
        var item = SharedStock().FirstOrDefault(stock => stock.identifier == catalog);
        CampaignCheck(item != null, "original carry item remains in the shared shop inventory");
        var localPocket = world.Grid(hostActor ? HostPocket : PartnerPocket);
        var otherPocket = world.Grid(hostActor ? PartnerPocket : HostPocket);
        var blocked = otherPocket.TryFindOneValidInventorySlot(item);
        CampaignCheck(blocked == null || blocked.TryAcceptOnce() == 0, "the other player's personal slot rejects a drop");
        var drop = localPocket.TryFindOneValidInventorySlot(item);
        CampaignCheck(drop != null && drop.TryAcceptOnce() == 1 && item.parentInventory == localPocket,
            "native slot moves the item into this actor's pocket");
        Debug.Log($"[probably_coop CAMPAIGN] PASS role={RoleName()} native travel inventory and scavenging day={day} attempts={remaining}");
        return true;
    }

    private void CheckGuestSearch(MapUIManager map, PlayerStore store, int day)
    {
        int searches = BothScavengingPerks && day == campaignFirstDay ? 5 : 1;
        if (store.scavengingAttempts >= searches)
        {
            searchedEpoch = Epoch;
            return;
        }
        if (requestedSearchEpoch == Epoch && store.scavengingAttempts <= requestedSearchCount) return;
        requestedSearchEpoch = Epoch;
        requestedSearchCount = store.scavengingAttempts;
        map.ScavengeButtonClick();
    }

    private string RoleName() => localHost ? "host" : "guest";

    private string CampaignFacts(int day)
    {
        using var sha = SHA256.Create();
        byte[] state = world.Capture();
        string digest = BitConverter.ToString(sha.ComputeHash(state)).Replace("-", "");
        CampaignCheck(InventoryCheckpoint.TryDecode(state, out var entries), "decode audited campaign inventory");
        foreach (var entry in entries)
        {
            var place = entry.Placement;
            Debug.Log($"[probably_coop CAMPAIGN] inventory-row {entry.Id}|{entry.CatalogId}|{entry.Units}|" +
                $"{place.Address.Root},{place.Address.OwnerItem},{place.Address.Slot}|{place.Pose.X},{place.Pose.Y},{place.Pose.Transform}|" +
                Convert.ToBase64String(entry.Facts));
        }
        return $"day={day} cash={PlayerStore.Instance.playerCash} run={campaign.RunId:N} inventory={digest}";
    }

    private static void CampaignCheck(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Campaign QA: " + name);
    }
}
#endif
