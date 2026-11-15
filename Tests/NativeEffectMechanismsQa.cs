#if PROBABLY_COOP_GRID_QA
using System.Linq;
using UnityEngine;
using ProbablyCoop.Net;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private const string TestAug = "coop_qa_aug";
    private const string HeaterState = "HEATING_PLATE_STATE";
    private int heaterStage, heatedId, augStage, augBalance, augReceipts, previousAugKills;
    private double initialWaterTemperature, nextShotAttempt;
    private double heaterDeadline;
    private bool sawWound, hostHeaterRunning, hostHeaterConfirmed;
    private GameItem effectHeater;

    private System.Collections.Generic.IEnumerable<GameItem> HeaterQaStock() =>
        FullStockQa ? EmporiumEntry.Instance.GetAllItems().Where(world.CanInteract) : SharedStock();

    private GameItem QaHeater() => effectHeater ??= HeaterQaStock().Single(item => item.identifier == "heating_plate");

    private static GameSlotInventory HeaterInput(GameItem heater) =>
        (GameSlotInventory)((MachineryIntake)((MachineryPanelRow)heater.contentWindow.childElement)
            .GetPanel("container").content).content;

    private static MachineryRadioList HeaterModes(GameItem heater) =>
        (MachineryRadioList)((MachineryPanelRow)heater.contentWindow.childElement).GetPanel("control").content;

    private bool RunGuestHeaterQa()
    {
        if (heaterStage == 4) return true;
        var heater = QaHeater();
        var input = HeaterInput(heater);
        var modes = HeaterModes(heater);
        if (heaterDeadline == 0) heaterDeadline = Time.realtimeSinceStartupAsDouble + 14;
        CampaignCheck(Time.realtimeSinceStartupAsDouble < heaterDeadline,
            $"native heater timed out stage={heaterStage} input={input.childItem?.uniqueId ?? 0}/{heatedId} " +
            $"mode={modes.selected} tag={heater.GetTagReadonly(HeaterState).GetInt()}");
        switch (heaterStage)
        {
            case 0:
                var water = HeaterQaStock().First(item => item.identifier == "large_bottled_water" &&
                    WaterHelper.GetTotalVolume(item) > 0);
                heatedId = water.uniqueId;
                initialWaterTemperature = water.GetTagReadonly("CONTAINER_TEMP_TAG").GetDouble();
                ItemMouseDoubleClickHandler.current.OpenContentAction(heater, new Vector2(200, 250));
                CampaignCheck(heater.contentWindow.IsVisible(), "guest opens the original heater window");
                CampaignCheck(input.TryFindOneValidInventorySlot(water)?.TryAcceptOnce() == 1,
                    "guest native heater input accepts the bottle");
                if (FullStockQa) Debug.Log($"[probably_coop STOCK] native heater insert owner={heater.uniqueId} bottle={heatedId}");
                break;
            case 1:
                if (session.Guest.PendingCount != 0 || input.childItem?.uniqueId != heatedId) return false;
                modes.Get(1).Select();
                if (FullStockQa) Debug.Log($"[probably_coop STOCK] native heater mode requested owner={heater.uniqueId}");
                break;
            case 2:
                if (heater.GetTagReadonly(HeaterState).GetInt() != 1 || modes.selected != 1 ||
                    input.childItem.GetTagReadonly("CONTAINER_TEMP_TAG").GetDouble() <= initialWaterTemperature) return false;
                CampaignCheck(EmporiumEntry.Instance.backInvinvElement.TryFindOneValidInventorySlot(input.childItem)
                    ?.TryAcceptOnce() == 1, "guest native heater output can be taken out");
                break;
            case 3:
                if (session.Guest.PendingCount != 0) return false;
                CampaignCheck(input.childItem == null && heater.contentWindow.IsVisible() &&
                    world.TryFindAccessible(heatedId, NetSession.GuestPeer, out var heated) &&
                    heated.GetTagReadonly("CONTAINER_TEMP_TAG").GetDouble() > initialWaterTemperature,
                    "guest retains original window and heated bottle after takeout");
                heater.CloseContentWindow();
                Debug.Log("[probably_coop MECHANISMS] PASS guest native heater insertion, mode, heating and takeout");
                break;
        }
        heaterStage++;
        return heaterStage == 4;
    }

    private bool CheckHostHeaterQa()
    {
        if (hostHeaterConfirmed) return true;
        var heater = QaHeater();
        var input = HeaterInput(heater);
        if (heater.GetTagReadonly(HeaterState).GetInt() != 1) return false;
        if (input.childItem != null)
        {
            hostHeaterRunning = true;
            CampaignCheck(!heater.contentWindow.IsVisible(), "the guest opened only the guest's heater window");
            return false;
        }
        if (!hostHeaterRunning) return false;
        hostHeaterConfirmed = true;
        Debug.Log("[probably_coop MECHANISMS] PASS host native heater mode and takeout with its window closed");
        return true;
    }

    private void SeedShotQa()
    {
        var store = PlayerStore.Instance;
        Shotgun.Unlock();
        previousAugKills = store.runStats.augsShot;
        var client = new StoreClient
        {
            identifier = TestAug, displayName = "QA Visitor", spriteName = SpriteDict.GetRandomScavSpriteName(),
            clientIntent = StoreClient.ClientIntent.SELL, mainDialogue = null, isIntroduced = true,
        };
        AugHelper.MakeDisguisedAug(client, 1);
        CampaignCheck(client.isAug && client.augTier == 1, "original disguised aug fixture");
        store.storeClientManager.AddNextClient(client);
    }

    private bool RunGuestShotQa()
    {
        if (augStage == 5) return true;
        var store = PlayerStore.Instance;
        var ui = StoreUIManager.Instance;
        var client = store.currentClientInstance?.GetClientBlueprint();
        sawWound |= ui.shootingWoundRenderer.gameObject.activeSelf;
        switch (augStage)
        {
            case 0:
                if (client != null || store.lockNextBellFlag > 0) return false;
                augBalance = store.playerCash;
                augReceipts = ShotReceipts();
                Object.FindFirstObjectByType<StoreBell>().OnClick();
                break;
            case 1:
                if (client?.identifier != TestAug || !store.isClientArrived || !ui.shotgun.activeSelf) return false;
                ui.shotgun.GetComponent<Shotgun>().OnClick();
                break;
            case 2:
                if (!ui.isShootingMode || !ui.CanShoot()) return false;
                ui.Shoot();
                ui.Shoot();
                break;
            case 3:
                if (Time.realtimeSinceStartupAsDouble > nextShotAttempt && campaign.ClientDialogue)
                {
                    DialogUIManager.Instance.OnAdvancePressed();
                    nextShotAttempt = Time.realtimeSinceStartupAsDouble + 0.1;
                }
                if (store.playerCash == augBalance) return false;
                CampaignCheck(sawWound && store.playerCash >= augBalance + 100 &&
                    ShotReceipts() == augReceipts + 1, "guest sees native shot animation and exactly one reward receipt");
                break;
            case 4:
                if (ui.isShootingMode || campaign.ShotActive || store.currentClientInstance != null) return false;
                Debug.Log("[probably_coop MECHANISMS] PASS guest original disguised-aug shot, camera and reward");
                break;
        }
        augStage++;
        return augStage == 5;
    }

    private bool CheckHostShotQa()
    {
        var store = PlayerStore.Instance;
        if (store.runStats.augsShot == previousAugKills || campaign.ShotActive) return false;
        CampaignCheck(store.runStats.augsShot == previousAugKills + 1 && store.currentClientInstance == null &&
            !StoreUIManager.Instance.isShootingMode, "native disguised aug settles and dismisses its target once");
        Debug.Log("[probably_coop MECHANISMS] PASS host original disguised-aug rules executed once");
        return true;
    }

    private static int ShotReceipts() => new[] { EmporiumEntry.Instance.frontInvinvElement,
        EmporiumEntry.Instance.backInvinvElement, EmporiumEntry.Instance.invElement }
        .Sum(root => root.childItems.Count(item => item.identifier == "sec_slip"));
}
#endif
