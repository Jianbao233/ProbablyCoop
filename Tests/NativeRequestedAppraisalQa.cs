using System;
using System.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string GuildPromotion = AppraisalRankHelper.PROMOTION_CLIENT_ID;
    private const string GuildFixtureTool = "QA_GUILD_MAGNIFIER";
    private const string GuildFixtureOffer = "QA_GUILD_OFFER";
    private readonly bool requestedAppraisalQa =
        Environment.GetCommandLineArgs().Contains("--probably-coop-qa-appraisal-regression");
    private int guildGuestStep, guildCash, guildExpectedCash, guildPromotionCash;
    private bool guildForecastChecked;
    private double guildNext, guildHostFinish;
    private double guildInitialProgress;
    private GameItem guildGuestOffer;
    private GameItemElement guildOffer;

    private void TickRequestedAppraisalHost(RoomSelector room,double now)
    {
        var shop = PlayerStore.Instance;
        var session = NativeActorSession.Current;
        var manager = shop.storeClientManager;
        var id = shop.currentClientInstance?.GetClientBlueprint().identifier;
        if (stage == 0)
        {
            NotebookUIManager.Instance.CloseUI();
            SeedAppraisalQa();
            SeedFrontendItem("magnifier",GuildFixtureTool);
            stage = 1;
        }
        CheckHostAppraisalQa();
        if (stage == 1 && AppraisalQaHostDone)
        {
            if (shop.storeState == PlayerStore.StoreState.MORNING)
            {
                if (shop.CanStartDay()) StoreShutterButton.Instance.OnClick();
                return;
            }
            Require(shop.storeState == PlayerStore.StoreState.OPEN, "appraisal needs the open shop");
            session.Run.Partner.appraiserLevel = AppraisalRankHelper.RANK_JUNIOR;
            session.Run.Partner.appraisalProgress = 0;
            AppraisalRankHelper.SetRank(AppraisalRankHelper.RANK_SENIOR);
            guildInitialProgress = AppraisalRankHelper.GetThreshold() -
                5 * Difficulty.Rules.appraisalProgressGainMultiplier;
            shop.appraisalProgress = guildInitialProgress;
            manager.clientStack.Clear();
            manager.AddNextClient(StoreClientListAppraisal.CreateAppraisal1());
            guildCash = shop.playerCash;
            stage = 2;
        }
        if (stage == 2 && id == "scavAppraisal")
        {
            var offers = EmporiumEntry.Instance.GetAllNonOwnedItem().Where(i => i.IsTag("APPRAISAL_ITEM")).ToArray();
            if (offers.Length < 2) return;
            Require(offers.Length == 2, "original appraisal request failed to produce its two items");
            guildOffer = (GameItemElement)offers[0];
            guildOffer.EnableTag(GuildFixtureOffer);
            guildExpectedCash = guildCash + AppraisalRankHelper.GetBaseFee() +
                RNG.CalculatePercentage(AppraisalHelper.GetCorrectBaseValue(guildOffer), AppraisalRankHelper.GetFeePercent(1));
            stage = 3;
        }
        if (stage == 3 && guildOffer.IsDestroyed())
        {
            Require(shop.playerCash == guildExpectedCash &&
                shop.appraisalProgress == AppraisalRankHelper.GetThreshold() &&
                shop.appraisalProgress > guildInitialProgress &&
                shop.appraisalPromotionDay == StoreStation.Instance.dayCounter + 1 &&
                session.Run.Partner.appraisalProgress == 0,
                "guest estimate did not pay the original senior fee once and schedule the shop's next promotion");
            Record("PASS bug-27-host","guest estimate retired the selected original offer and paid "+(guildExpectedCash-guildCash)+
                " credits once; progress and scheduled promotion belong to the shop");
            stage = 4;
        }
        if (stage == 4 && id != "scavAppraisal")
        {
            // Advance the due date for the fixture, then use the original scheduling
            // method and original customer dialogue without changing the game day.
            shop.appraisalPromotionDay = StoreStation.Instance.dayCounter;
            manager.clientStack.Clear();
            AppraisalRankHelper.HandlePromotionClient(manager);
            AppraisalRankHelper.HandlePromotionClient(manager);
            Require(manager.clientStack.Count(c => c.identifier == GuildPromotion) == 1,
                "ordinary senior promotion was missing or duplicated");
            guildPromotionCash = shop.playerCash - AppraisalRankHelper.GetPromotionCost(AppraisalRankHelper.RANK_MASTER);
            stage = 5;
        }
        if (stage == 5 && shop.appraiserLevel == AppraisalRankHelper.RANK_MASTER)
        {
            Require(shop.playerCash == guildPromotionCash && shop.appraisalPromotionDay == -1 &&
                session.Run.Partner.appraiserLevel == AppraisalRankHelper.RANK_JUNIOR &&
                EmporiumEntry.Instance.GetAllItems().Count(i => i.identifier == "cert4" && GeneralHelper.IsItemOwned(i)) == 1 &&
                EmporiumEntry.Instance.GetAllItems().All(i => AppraisalRankHelper.GetCertificateRank(i) is -1 or 3),
                "guest promotion did not replace the shared certificate with one master certificate at the original price");
            stage = 6;
        }
        if (stage == 6 && id != GuildPromotion)
        {
            for (int day = 0; day < 3; day++)
            {
                // Exercise both routes from old saves: an appointment that has
                // already been materialized and a postponed invitation still due.
                manager.clientStack.Add(StoreClientListAppraisal.CreatePromotionOffer());
                manager.clientStack.Add(StoreClientListStory.CreateStoryAppraisalBoard());
                shop.QueueFuturClient("appraisalBoardIntro2",0);
                shop.HandleFutureClientQueue();
                AppraisalRankHelper.HandlePromotionClient(manager);
                manager.AddNextClient(StoreClientListAppraisal.CreatePromotionOffer());
                Require(manager.clientStack.All(c => c.identifier is not (GuildPromotion or "appraisalBoardIntro" or "appraisalBoardIntro2")),
                    "stale association offer reached the master-rank queue");
            }
            Require(shop.appraiserLevel == AppraisalRankHelper.RANK_MASTER && shop.playerCash == guildPromotionCash,
                "stale offers changed the master rank or charged the shop again");
            guildForecastChecked = true;
            guildHostFinish = now + 8;
            Record("PASS bug-30-host","guest accepted senior-to-master once; master certificate and price verified; "+
                "three queue cycles rejected saved promotions and postponed association visits");
            stage = 7;
        }
        if (guildForecastChecked && now >= guildHostFinish) Finish("PASS requested-appraisal host");
    }

    private bool TickRequestedAppraisalGuest(ReplicaLink link,double now)
    {
        if (now < guildNext) return false;
        var world = NativeWorld.Guest;
        var shop = PlayerStore.Instance;
        var dialogue = DialogUIManager.Instance;
        var id = shop.currentClientInstance?.GetClientBlueprint().identifier;
        var counter = EmporiumEntry.Instance;
        if (guildGuestStep == 0)
        {
            NotebookUIManager.Instance.CloseUI();
            if (!RunGuestAppraisalQa(link,now)) return false;
            Record("PASS bug-09","original certificate and already-open category panels synchronized in both directions");
            guildGuestStep++;
        }
        if (QaWaiting(link)) return false;
        if (guildGuestStep == 5 && id == "scavAppraisal")
        {
            if (world.HasDialogue) dialogue.OnAdvancePressed();
            else UnityEngine.Object.FindObjectOfType<StoreDismissButton>().OnClick();
            guildNext = now + 0.35;
            return false;
        }
        if (guildGuestStep is 1 or 5)
        {
            if (shop.storeState != PlayerStore.StoreState.OPEN || id != null || world.QueuedClients == 0) return false;
            if (guildGuestStep == 5 && world.RemoteHostActor?.appraisalPromotionDay != StoreStation.Instance.dayCounter)
                return false;
            UnityEngine.Object.FindObjectOfType<StoreBell>().OnClick();
            guildNext = now + 0.3;
            guildGuestStep++;
            return false;
        }
        if (guildGuestStep == 2)
        {
            if (id == null) { guildGuestStep = 1; return false; }
            if (id != "scavAppraisal" || world.PendingMainDialogue) return false;
            if (world.HasDialogue) { dialogue.OnAdvancePressed(); guildNext = now + 0.3; return false; }
            guildGuestOffer = counter.GetAllNonOwnedItem().FirstOrDefault(i => i.IsTag(GuildFixtureOffer));
            if (guildGuestOffer == null) return false;
            var slot = counter.backInvinvElement.TryFindOneValidInventorySlot(guildGuestOffer);
            Require(slot?.IsValid() == true,"original appraisal target does not fit the guest inspection counter");
            ReleaseAtNativeSlot((GameItemElement)guildGuestOffer,slot);
            guildGuestStep++;
            return false;
        }
        if (guildGuestStep == 3)
        {
            if (guildGuestOffer.parentInventory != counter.backInvinvElement) return false;
            var tool = FrontendItem(GuildFixtureTool);
            ItemSelectHandler.current.TrySelectItem(tool);
            // The selected tool asks the host for native target eligibility.
            if (!tool.MayTarget(guildGuestOffer) || !tool.CanTarget(guildGuestOffer)) return false;
            OriginalScannerTarget(tool,guildGuestOffer);
            var inspector = InspectionUIManager.Instance;
            Require(inspector.panel.activeSelf && inspector.appraisalPanel.activeSelf &&
                ReferenceEquals(inspector.currentInspectingItem,guildGuestOffer),"guest magnifier did not open the native estimate panel");
            int value = AppraisalHelper.GetCorrectBaseValue(guildGuestOffer);
            guildExpectedCash = shop.playerCash + AppraisalRankHelper.GetRankData(AppraisalRankHelper.RANK_SENIOR).baseFee +
                RNG.CalculatePercentage(value,AppraisalRankHelper.GetRankData(AppraisalRankHelper.RANK_SENIOR).feePercent[0]);
            inspector.StartEditingAppraisalField();
            inspector.apprisalServiceInputField.text = value.ToString();
            inspector.FinishEditingAppraisalField();
            NativeOffscreenCapture.Capture("qa-requested-appraisal-estimate");
            inspector.OnAppraisalServiceEstimated();
            Require(QaWaiting(link),"guest native estimate button did not submit a request");
            guildGuestStep++;
            return false;
        }
        if (guildGuestStep == 4)
        {
            if (guildGuestOffer.parentInventory != null || shop.playerCash != guildExpectedCash) return false;
            Record("PASS bug-27-guest","original slot placement, magnifier, estimate and exact fee visible on the guest");
            guildGuestStep++;
            return false;
        }
        if (guildGuestStep == 6)
        {
            if (id == null) { guildGuestStep = 5; return false; }
            if (id != GuildPromotion || world.PendingMainDialogue) return false;
            if (dialogue.dialogChoiceBubbles[0].gameObject.activeSelf && dialogue.dialogChoiceBubbles[0].IsInteractable())
            {
                dialogue.SelectChoice(0);
                guildGuestStep++;
            }
            else if (world.HasDialogue) dialogue.OnAdvancePressed();
            guildNext = now + 0.35;
            return false;
        }
        if (guildGuestStep == 7)
        {
            if (world.HasDialogue) { dialogue.OnAdvancePressed(); guildNext = now + 0.35; return false; }
            if (id != null || world.RemoteHostActor?.appraiserLevel != AppraisalRankHelper.RANK_MASTER) return false;
            Require(counter.GetAllItems().Count(i => i.identifier == "cert4" && GeneralHelper.IsItemOwned(i)) == 1 &&
                AppraisalRankHelper.GetRank() == AppraisalRankHelper.RANK_JUNIOR,"master certificate/rank or guest personal state diverged");
            Record("PASS bug-30-guest","accepted the original choice from a junior partner; received the store's master certificate");
            return true;
        }
        return false;
    }
}
