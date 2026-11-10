using System;
using System.Linq;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int actorNetworkStage;
    private HealthData actorNetworkHealth;
    private System.Collections.Generic.List<StartingPerk> actorNetworkPerks;
    private float actorNetworkDeadline;

    private bool RunActorNetworkQa(float now)
    {
        var world = NativeWorld.Guest;
        if (!NativeWorld.ActorProjectionQaRequested) return true;
        if (world?.HasActorProjection != true || world.RemoteHostActor == null) return false;
        if (actorNetworkStage == 3) return true;
        if (actorNetworkStage > 0 && now > actorNetworkDeadline)
            throw new TimeoutException("Independent actor network projection");
        var store = PlayerStore.Instance;
        var remote = world.RemoteHostActor;
        Require(store.saveSlotId == -1, "actor guest loaded a native slot");
        Require(store.healthData.luck == remote.healthData.luck + 17, "partner luck replaced by common world facts");
        Require(HealthData.GetLuck() == store.healthData.luck + LotteryHelper.GetCarriedLuckBonus() +
            LiquidLuckHelper.GetActiveLuckBonus(store.healthData), "native local luck does not read projected actor");
        Require(StartingPerk.IsPerkActive("lucky") != remote.startingPerks.Any(perk => perk.id == "lucky"),
            "native perk query still reads host actor");
        Require(store.playerCash == world.View.Cash && StoreStation.Instance.dayCounter == world.View.Day,
            "actor projection split shared cash or calendar");
        if (actorNetworkStage == 0)
        {
            Require(store.healthData.woundState == 6 && HealthData.IsSeriouslyWounded(), "initial partner wounds lost");
            actorNetworkHealth = store.healthData;
            actorNetworkPerks = store.startingPerks;
            if (!world.RequestQaActorWound()) return false;
            actorNetworkStage = 1; actorNetworkDeadline = now + 15;
            Record("PASS actor-network-native-queries", "partner luck/perks/wounds independent; original cash/day shared; guest slot=-1");
            return false;
        }
        Require(ReferenceEquals(actorNetworkHealth, store.healthData) && ReferenceEquals(actorNetworkPerks, store.startingPerks),
            "actor projection replaced native objects retained by the frontend");
        if (actorNetworkStage == 1)
        {
            if (store.healthData.woundState != 8) return false;
            Require(store.healthData.isWoundedFresh && !store.healthData.isWoundStable && HealthData.IsSeriouslyWounded(),
                "native personal mutation did not survive complete guest projection");
            var calendar = AdvCalendarUIManager.Instance;
            NativeWorld.RefreshingCalendar = true;
            try { calendar.OpenUI(); calendar.OnStatusButtonClicked(); }
            finally { NativeWorld.RefreshingCalendar = false; }
            actorNetworkStage = 2;
            return false;
        }
        var ui = AdvCalendarUIManager.Instance;
        Require(ui.UIPanel.activeSelf && ui.statusSection.activeSelf &&
            ui.activePerksContainer.GetComponentsInChildren<StartingPerkElement>().Length == store.startingPerks.Count,
            "original status UI did not render partner perks");
        NativeOffscreenCapture.Capture("qa-actor-personal-status");
        ui.CloseUI();
        Record("PASS actor-network-projection", "real guest request -> native ReceiveMinorWound on partner -> complete snapshot; host unchanged; native health/list identities stable; original status UI verified");
        actorNetworkStage = 3;
        return true;
    }
}
