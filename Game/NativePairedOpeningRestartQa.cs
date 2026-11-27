#if PROBABLY_COOP_GRID_QA
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string OpeningRestartProof = "qa-opening-restart.json";
    private readonly bool openingRestartQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-opening-restart") >= 0;
    private readonly bool verifyOpeningRestartQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-opening-verify-reload") >= 0;

    private void CaptureOpeningBeforeRestart()
    {
        if (pairedStep != 3) return;
        var store = PlayerStore.Instance;
        Require(StoreStation.Instance.dayCounter == 1, "the paired opening must start on day one");
        if (openingRentQa && host)
        {
            if (NativeActorSession.Current?.CanCommit != true) return;
            Require(OpeningRent.Describe(store) == (30, 300), "initial opening debt is incorrect");
            store.playerCash = 10_000; // Seed cash for the native rent payment in this isolated fixture.
            int rent = store.GetRentValue();
            store.OnRentPaid();
            Require(store.playerCash == 10_000 - rent && OpeningRent.Describe(store) == (60, 270),
                "the first native rent payment did not consume the first opening installment");
            store.SaveGame();
        }
        if (openingRentQa && OpeningRent.Describe(store) != (60, 270)) return;
        Require(host ? CoopRun.VerifyCheckpoint(store, 1) : store.saveSlotId == -1,
            "the restart fixture requires a verified host checkpoint and an unsaved guest");
        ModHelper.WriteModDataFileText("probably_coop", OpeningRestartProof,
            CaptureOpeningRestartProof(store).ToString(Newtonsoft.Json.Formatting.None));
        pairedStep = 4;
        Record("READY paired opening process restart",
            openingRentQa ? "first rent payment saved with the native save writer" : "day-one gifts, technologies and partner selection saved");
    }

    private void TickRestoredOpening(RoomSelector room, CoopPanel panel, NativeShop shop, bool inMenu)
    {
        if (host && inMenu && room.IsHost && pairedStep == 0)
        {
            var checkpoint = CoopRun.ListCheckpoints().Single(value => !value.Historical);
            Require(checkpoint.HasActors && !checkpoint.NeedsRecovery && checkpoint.Day == 1,
                "the original paired opening checkpoint was unavailable");
            ModHelper.WriteModDataFileText("probably_coop", "qa-room.txt", room.Code);
            pairedStep = 1;
            panel.Resume(checkpoint);
            return;
        }
        if (pairedStep == 4 || inMenu || room.Session?.State != LinkState.Connected ||
            (host ? shop == null || NativeWorld.Host?.PartnerSetupRequiredForQa != false : NativeWorld.Guest?.Ready != true))
            return;
        var store = PlayerStore.Instance;
        var expected = JObject.Parse(ModHelper.ReadModDataFileText("probably_coop", OpeningRestartProof));
        Require(JToken.DeepEquals(expected, CaptureOpeningRestartProof(store)),
            "restored opening differs from its pre-restart careers, inventory, technology, perks or rent ledger");
        Require(host ? CoopRun.VerifyCheckpoint(store, 1) : store.saveSlotId == -1,
            "the resumed run did not retain its original verified host checkpoint");
        if (openingRentQa && host)
        {
            store.OnRentPaid();
            Require(JToken.DeepEquals(expected, CaptureOpeningRestartProof(store)),
                "the restored rent ledger charged again on the same day");
        }
        if (openingRentQa) CheckRestoredRentCalendar(store);
        if (host && !openingRentQa && !modOpeningQa) CheckPairedCareerSchedule();
        NativeOffscreenCapture.Capture("qa-paired-opening-after-process-restart");
        pairedStep = 4;
        if (host) Record("PASS paired opening restart host", "original save restored, available for the guest");
        else Finish("PASS paired opening restart guest");
    }

    private void CheckRestoredRentCalendar(PlayerStore store)
    {
        var calendar = AdvCalendarUIManager.Instance;
        bool refreshing = NativeWorld.RefreshingCalendar;
        NativeWorld.RefreshingCalendar = true;
        try
        {
            calendar.OpenUI();
            calendar.OnCalendarButtonClicked();
            var debt = OpeningRent.Describe(store);
            string expected = ModHelper.GetLocalized("probably_coop", "opening_rent_calendar",
                store.GetRentValue(), debt.Active, Math.Max(0, store.temporaryRent - debt.Active),
                store.dayUntilRent, debt.Balance);
            Require(debt == (60, 270) && calendar.landlordTMP.text == expected,
                "the native rent calendar lost the outstanding opening balance after restart");
            NativeOffscreenCapture.Capture("qa-paired-opening-rent-calendar");
            Record("PASS paired opening rent calendar",
                $"rent={store.GetRentValue()} dueIn={store.dayUntilRent} installment={debt.Active} balance={debt.Balance}");
        }
        finally
        {
            calendar.CloseUI();
            NativeWorld.RefreshingCalendar = refreshing;
        }
    }

    private JObject CaptureOpeningRestartProof(PlayerStore store)
    {
        Require(ModHelper.GetStartingScenarioId() == PairedHostCareerId &&
            ModHelper.IsStartingScenario(PairedPartnerCareerId) &&
            (modOpeningQa || NativeOpening.UsesCareer(store, PairedPartnerCareer)), "either original career was missing");
        var shopStock = EmporiumEntry.Instance.GetAllOwnedItems();
        string[] gifts = modOpeningQa ? new[] { "water_jug" } :
            openingRentQa ? new[] { "ras", "gun_part" } : new[] { "water_jug", "chem_finisher" };
        string[] upgrades = modOpeningQa ? new[] { "WATER_TRADER", "GUTTERFLOW_TAP" } :
            openingRentQa ? new[] { "RETIRED_GUNSMITH", "AQUACULTURE" } : new[] { "WATER_TRADER", "RETIRED_CHEMIST" };
        Require(gifts.All(id => shopStock.Any(item => item.identifier == id)) &&
            upgrades.All(id => store.networkUpgrade[id].state == 2) && EmporiumEntry.IsWaterTapUnlocked(),
            "opening gifts, career technologies or shared plumbing are missing");
        if (modOpeningQa)
        {
            Require(shopStock.Count(item => GeneralHelper.GetCustomName(item) == ModCareerItem) == 1 &&
                store.IsWaterMerchantStart() && ModHelper.IsStartingPerkActive(ModSharedPerk) &&
                ModHelper.IsStartingPerkActive(ModPersonalPerk) == !host &&
                store.playerCash == 0 && OpeningRent.Describe(store) == (3, 30),
                "the native and registered careers must retain their goods, roles and rent after restart");
        }
        var plan = OpeningRent.Describe(store);
        return new JObject
        {
            ["slot"] = store.saveSlotId,
            ["run"] = host ? store.runID : "",
            ["day"] = StoreStation.Instance.dayCounter,
            ["host"] = PairedHostCareerId,
            ["partner"] = PairedPartnerCareerId,
            ["cash"] = store.playerCash,
            ["rent"] = new JArray(plan.Active, plan.Balance, store.GetRentValue(), store.dayUntilRent),
            ["stock"] = new JArray(shopStock.OrderBy(item => item.identifier, StringComparer.Ordinal)
                .ThenBy(item => item.unitCount).Select(item => new JArray(item.identifier, item.unitCount))),
            ["localPerks"] = new JArray(store.startingPerks.Select(perk => perk.id).OrderBy(id => id, StringComparer.Ordinal))
        };
    }
}
#endif
