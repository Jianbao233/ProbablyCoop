using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    private static readonly EntityKey FacilitiesKey = EntityKey.Scalar("shop.facilities");
    private const int FacilitiesLimit = 96 * 1024;
    private static readonly System.Reflection.FieldInfo WildSelection = AccessTools.Field(typeof(WildNetworkUI), "selectedId");
    private static readonly System.Reflection.MethodInfo WildBuy = AccessTools.Method(typeof(WildNetworkUI), "OnUseClicked");
    private static readonly System.Reflection.MethodInfo WildClose = AccessTools.Method(typeof(WildNetworkUI), "OnWindowClosed");
    private static readonly System.Reflection.MethodInfo ServiceToggle = AccessTools.Method(typeof(EndOfDayUIManager), "OnToggled");
    private static readonly AccessTools.FieldRef<EndOfDayUIManager, List<(StoreService service, TabletCheckbox checkbox)>> ServiceRows =
        AccessTools.FieldRefAccess<EndOfDayUIManager, List<(StoreService, TabletCheckbox)>>("rows");
    private static readonly AccessTools.FieldRef<StoreEventManager, List<StoreEvent>> ActiveSceneEvents =
        AccessTools.FieldRefAccess<StoreEventManager, List<StoreEvent>>("activeEvents");
    private static readonly AccessTools.FieldRef<StoreReputation, double> ReputationAmount =
        AccessTools.FieldRefAccess<StoreReputation, double>("amount");
    private byte[] publishedFacilities;
    private JObject facilityView;
    private JArray replicaRequests, replicaEvents;
    private ulong wildInvitation, commissaryInvitation, presentedWild, presentedCommissary;
    private bool remoteWild, remoteCommissary, presentingActivity;
    internal bool PresentingActivity => presentingActivity;
    internal bool RemoteWild => remoteWild;

    private static ulong ActivityProof(byte[] bytes)
    {
        if (bytes == null) return 0;
        ulong result = 14695981039346656037UL;
        foreach (byte value in bytes) result = unchecked((result ^ value) * 1099511628211UL);
        return result == 0 ? 1 : result;
    }

    private ulong FacilityProof(ActivityAction action, string id)
    {
        if (facilityView == null) return 0;
        JToken Row(string key) => ((JArray)facilityView[key]).FirstOrDefault(row => (string)row[0] == id);
        JToken facts = action switch
        {
            ActivityAction.BrowseUpgrades => facilityView["browsed"],
            ActivityAction.BuyUpgrade => new JArray(Row("upgrades"), facilityView["favor"], facilityView["difficulty"]),
            ActivityAction.CloseUpgrades => new JArray(facilityView["wild"], facilityView["wildActive"]),
            ActivityAction.CommissaryBuy => new JArray(Row("stock"), facilityView["tokens"]),
            ActivityAction.RedeemStamps => facilityView["tokens"],
            ActivityAction.RedeemLottery => facilityView["cash"],
            ActivityAction.RequestDelivery => ((JArray)facilityView["requests"]).FirstOrDefault(row => (string)row["id"] == id),
            ActivityAction.ToggleService => Row("services"),
            ActivityAction.ReadNewspaper => facilityView["events"],
            _ => null,
        };
        return facts == null ? 0 : ActivityProof(Utf8.GetBytes(facts.ToString(Formatting.None)));
    }

    internal bool RequestActivity(ActivityAction action, string text = "", long value = 0, GameItem owner = null)
    {
        ulong proof = owner != null ? inventory.ControlProof(owner) :
            action <= ActivityAction.PhoneChoice ? ActivityProof(publishedPhone) : FacilityProof(action, text);
        var intent = new ActivityIntent(action, proof, owner?.uniqueId ?? 0, value, text);
        return intent.Valid && Session.Request(AuthorityAction.Activity, StoreStation.GetDayCounter(), activity: intent);
    }

    private void ExecuteActivity(ActivityIntent intent)
    {
        var actors = NativeActorSession.Current;
        if (actors == null || actors.Terminal || actors.Faulted || actors.NightActive) return;
        if (intent.Action <= ActivityAction.PhoneChoice)
        {
            RunGuestPhone(intent, actors);
            return;
        }
        if (intent.Action == ActivityAction.ProductionTarget)
        {
            ExecuteProductionTarget(NetSession.GuestPeer, intent);
            return;
        }
        if (intent.Action is ActivityAction.HopperRun or ActivityAction.LogisticsSetting)
        {
            RunGuestLogistics(intent, actors);
            return;
        }
        if (intent.Proof != FacilityProof(intent.Action, intent.Text) || !BeginAuthorityAction()) return;
        bool completed = false;
        try
        {
            using (actors.Enter(NativeActorRun.Slot.Partner)) completed = ApplyFacilityIntent(intent, actors.Store);
        }
        finally { EndAuthorityAction(completed); }
#if PROBABLY_COOP_GRID_QA
        UnityEngine.Debug.Log($"[probably_coop ACTIVITY] original {intent.Action} applied={completed}");
#endif
    }

    private bool ApplyFacilityIntent(ActivityIntent intent, PlayerStore store)
    {
        var map = MapUIManager.Instance;
        var commissary = CommissaryUIManager.Instance;
        switch (intent.Action)
        {
            case ActivityAction.BrowseUpgrades:
                if (map.IsOutside || store.daySinceLastBrowseUpgrade == 0) return false;
                store.daySinceLastBrowseUpgrade = 0;
                return true;
            case ActivityAction.BuyUpgrade:
                if (map.IsOutside || !store.networkUpgrade.TryGetValue(intent.Text, out var upgrade) ||
                    !upgrade.IsReady() || upgrade.IsUnlocked() || NetworkUpgrade.IsHiddenForRun(upgrade.id) ||
                    NetworkUpgrade.IsLockedInDemo(upgrade.id) || upgrade.GetCost() != intent.Value ||
                    intent.Value > (long)store.playerCash + store.wildFavor)
                    return false;
                var previous = WildSelection.GetValue(null);
                try
                {
                    WildSelection.SetValue(null, upgrade.id);
                    WildBuy.Invoke(null, null);
                }
                finally
                {
                    WildSelection.SetValue(null, previous);
                    WildNetworkUI.RefreshAll();
                }
                return true;
            case ActivityAction.CloseUpgrades:
                if (!remoteWild) return false;
                remoteWild = false;
                if (WildNetworkUI.IsOpen) WildNetworkUI.Close();
                else WildClose.Invoke(null, null);
                return true;
            case ActivityAction.CommissaryBuy:
                if (!AtCommissary() || store.commissaryData.GetItemData(intent.Text) == null ||
                    !CommissaryUIManager.CanBuy(intent.Text)) return false;
                store.commissaryData.BuyItem(intent.Text);
                if (commissary.panel.activeSelf) commissary.RefreshUI();
                return true;
            case ActivityAction.RedeemStamps:
            case ActivityAction.RedeemLottery:
                if (!AtCommissary()) return false;
                bool lottery = intent.Action == ActivityAction.RedeemLottery;
                string tag = lottery ? "LOTTERY_IS_WINNING_TAG" : "FOOD_STAMP_TAG";
                if (TouchesHand(EmporiumEntry.Instance.GetAllOwnedItems().Where(item => item.IsTag(tag)))) return false;
                if (lottery) commissary.OnRedeemAllLottery();
                else
                {
                    CommissaryData.RedeemAllStampInInventory();
                    if (commissary.panel.activeSelf) commissary.RefreshUI();
                }
                return true;
            case ActivityAction.RequestDelivery:
                if (map.IsOutside) return false;
                var order = store.storeRequests.FirstOrDefault(request => request.id == intent.Text);
                if (!CanDeliver(order)) return false;
                order.FillRequest();
                if (PCUIManager.Instance.panel.activeSelf) PCUIManager.Instance.OnChange();
                PCUIManager.Instance.storePC.RefreshState();
                return true;
            case ActivityAction.ToggleService:
                if (map.IsOutside || store.storeState != PlayerStore.StoreState.CLOSED || !store.CanEndDay()) return false;
                var service = store.storeService.FirstOrDefault(candidate => candidate.id == intent.Text);
                if (service == null || !service.unlocked || service.isMandatory || service.IsOnCooldown() ||
                    service.isUsing == (intent.Value != 0)) return false;
                ServiceToggle.Invoke(EndOfDayUIManager.Instance, new object[] { service, intent.Value != 0 });
                RefreshServiceControls();
                return true;
            case ActivityAction.ReadNewspaper:
                var manager = StoreStation.Instance.storeEventManager;
                if (ActiveSceneEvents(manager).Any(row => !row.isHiddenFromNewsPaper)) return false;
                manager.ActivateEvent(EventListCosmetic.AllQuiet(), force: true);
                return true;
            default:
                return false;
        }
    }

    private bool AtCommissary() => MapUIManager.Instance.Current is CommissaryLocation ||
        !MapUIManager.Instance.IsOutside && (remoteCommissary || CommissaryUIManager.Instance.panel.activeSelf);
    internal bool NewspaperIsEmpty() =>
        !ActiveSceneEvents(StoreStation.Instance.storeEventManager).Any(row => !row.isHiddenFromNewsPaper);

    internal bool CanDeliver(StoreRequest request) => request != null &&
        PlayerStore.Instance.storeRequests.Contains(request) && request.CanFillRequest() &&
        !TouchesHand(EmporiumEntry.Instance.GetAllOwnedItems().Where(request.CanThisItemFillRequest)
            .Reverse().Take(request.requiredItemCount));

    internal bool HandOffWild()
    {
        if (!RemotePhoneExecution && !executingGuestBusiness) return false;
        NetworkUpgrade.AddMissingAfterLoad();
        PlayerStore.Instance.daySinceLastBrowseUpgrade = 0;
        if (!remoteWild) wildInvitation++;
        remoteWild = true;
        return true;
    }

    internal bool HandOffCommissary()
    {
        if (!RemotePhoneExecution) return false;
        commissaryInvitation++;
        remoteCommissary = true;
        return true;
    }

    private byte[] CaptureFacilities()
    {
        var store = PlayerStore.Instance;
        var facts = new JObject
        {
            ["cash"] = store.playerCash, ["favor"] = store.wildFavor, ["browsed"] = store.daySinceLastBrowseUpgrade,
            ["wild"] = wildInvitation, ["wildActive"] = remoteWild,
            ["commissary"] = commissaryInvitation, ["commissaryActive"] = remoteCommissary,
            ["afterhours"] = store.isAfterhoursIntroduced, ["stamp"] = store.isStampIntroduced,
            ["jackson"] = store.storeClientData.isJacksonIntroduced,
            ["difficulty"] = Difficulty.ResolveId(store.difficultyId, store.isHardMode), ["skipIntro"] = store.skipIntro,
            ["rent"] = store.rentValue, ["mortgage"] = store.mortgageWeeklyPayment,
            ["payingMortgage"] = store.isPayingMortgage, ["propertyPaid"] = store.IsPropertyPaid,
            ["displayCaseIntroduced"] = store.isDisplayCaseIntroduced,
            ["reputation"] = new JArray(store.storeReputations.OrderBy(row => row.factionId, StringComparer.Ordinal)
                .Select(row => new JArray(row.factionId, row.GetReputationExact(),
                    new JArray(row.perks.Where(perk => perk.isUnlocked).Select(perk => perk.perkId))))),
            ["vending"] = store.isVendingMachineUnlocked, ["fountain"] = store.isVendingFountainUnlocked,
            ["upgrades"] = new JArray(store.networkUpgrade.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new JArray(pair.Key, pair.Value.state, pair.Value.cooldownCurrent, pair.Value.cost))),
            ["services"] = new JArray(store.storeService.OrderBy(service => service.id, StringComparer.Ordinal)
                .Select(service => new JArray(service.id, service.unlocked, service.introduced, service.isUsing,
                    service.currentCooldown, service.cost))),
            ["locations"] = JObject.FromObject(store.afterhourState),
            ["events"] = new JArray(ActiveSceneEvents(StoreStation.Instance.storeEventManager).OrderBy(row => row.identifier, StringComparer.Ordinal)
                .Select(row => NativeWorld.CaptureEvent(row))),
            ["requests"] = JArray.FromObject(store.storeRequests),
            ["tokens"] = store.commissaryData.redeemedCredit,
            ["stock"] = new JArray(store.commissaryData.commissaryItemDatas
                .Select(row => new JArray(row.identifier, row.StockCount, row.price))),
        };
        byte[] result = Utf8.GetBytes(facts.ToString(Formatting.None));
        if (result.Length > FacilitiesLimit) throw new InvalidDataException("Shared facilities exceed the effect limit.");
        facilityView = facts;
        return result;
    }

    private static bool TryReadFacilities(byte[] data, out JObject facts, out Dictionary<string, AfterhourLocationState> locations)
    {
        facts = null;
        locations = null;
        if (data == null || data.Length == 0 || data.Length > FacilitiesLimit) return false;
        try
        {
            var value = JObject.Parse(Utf8.GetString(data));
            if (!Integer(value["cash"], int.MinValue) || !Integer(value["favor"]) || !Integer(value["browsed"]) ||
                !Integer(value["tokens"]) || !Integer(value["rent"]) || !Integer(value["mortgage"]) ||
                value["wild"]?.Type != JTokenType.Integer || (ulong)value["wild"] > long.MaxValue ||
                value["commissary"]?.Type != JTokenType.Integer || (ulong)value["commissary"] > long.MaxValue ||
                !ReadText(value, "difficulty", 96) || !TeamTransition.ValidLocation((string)value["difficulty"]) ||
                new[] { "wildActive", "commissaryActive", "afterhours", "stamp", "jackson", "vending", "fountain",
                    "skipIntro", "payingMortgage", "propertyPaid", "displayCaseIntroduced" }
                    .Any(key => value[key]?.Type != JTokenType.Boolean) ||
                !FacilityRows(value["reputation"], 48, 3, row =>
                    row[1].Type is JTokenType.Float or JTokenType.Integer && !double.IsNaN((double)row[1]) &&
                    !double.IsInfinity((double)row[1]) && row[2] is JArray perks && perks.Count <= 128 &&
                    perks.All(perk => perk.Type == JTokenType.String && TeamTransition.ValidLocation((string)perk))) ||
                !FacilityRows(value["upgrades"], 384, 4, row => Integer(row[1], 0, 2) && Integer(row[2]) && Integer(row[3])) ||
                !FacilityRows(value["stock"], 64, 3, row => Integer(row[1]) && Integer(row[2])) ||
                !FacilityRows(value["services"], 96, 6, row =>
                    row[1].Type == JTokenType.Boolean && row[2].Type == JTokenType.Boolean && row[3].Type == JTokenType.Boolean &&
                    Integer(row[4], int.MinValue) && Integer(row[5], int.MinValue)) ||
                value["locations"] is not JObject events || events.Count > 48 ||
                value["events"] is not JArray active || active.Count > 128 ||
                active.Any(node => node is not JObject row || row["fields"] is not JObject ||
                    row["negotiation"] is not JArray changes || changes.Count > 128) ||
                value["requests"] is not JArray requests || requests.Count > 128 ||
                requests.Any(node => node is not JObject order || !ReadText(order, "id", 96) ||
                    !ReadText(order, "requiredItem", 128) || !Integer(order["requiredItemCount"], 1) ||
                    !Integer(order["duration"], int.MinValue) || !Integer(order["creditReward"], int.MinValue) ||
                    !Integer(order["reputationReward"], int.MinValue))) return false;
            var decoded = events.ToObject<Dictionary<string, AfterhourLocationState>>();
            if (decoded == null || decoded.Any(pair => !TeamTransition.ValidLocation(pair.Key) || pair.Value == null ||
                pair.Value.counters == null || pair.Value.counters.Count > 128 ||
                pair.Value.activeEvents == null || pair.Value.activeEvents.Count > 64 ||
                pair.Value.activeEvents.Any(row => row == null || !TeamTransition.ValidLocation(row.eventId)))) return false;
            facts = value;
            locations = decoded;
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    private static bool FacilityRows(JToken token, int limit, int columns, Func<JArray, bool> valid)
    {
        if (token is not JArray rows || rows.Count > limit) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return rows.All(node => node is JArray row && row.Count == columns && row[0].Type == JTokenType.String &&
            TeamTransition.ValidLocation((string)row[0]) && keys.Add((string)row[0]) && valid(row));
    }

    private static bool Integer(JToken token, int min = 0, int max = int.MaxValue) =>
        token?.Type == JTokenType.Integer && (long)token >= min && (long)token <= max;

    private static void PresentUpgradeGeometry(JObject facts)
    {
        var shop = EmporiumEntry.Instance;
        var upgrades = ((JArray)facts["upgrades"]).Cast<JArray>().Where(row => (int)row[1] == 2)
            .Select(row => (string)row[0]).ToHashSet(StringComparer.Ordinal);
        if (upgrades.Contains("SHOWCASE_II"))
        {
            if (shop.showcaseElement.inventoryShape.width != 9) shop.UpgradeDisplayCaseII();
        }
        else if (upgrades.Contains("SHOWCASE_I") && shop.showcaseElement.inventoryShape.width != 8)
            shop.UpgradeDisplayCaseI();
        if (upgrades.Contains("DOSSIER_UPGRADE")) shop.UpgradeDossier();
        if (upgrades.Contains("TRASHCAN_UPGRADE")) shop.UpgradeTrashcan();
    }

    private void ApplyFacilities(byte[] bytes, JObject facts, Dictionary<string, AfterhourLocationState> locations)
    {
        var store = PlayerStore.Instance;
        bool previousTap = EmporiumEntry.IsWaterTapUnlocked();
        bool previousVending = store.isVendingMachineUnlocked, previousFountain = store.isVendingFountainUnlocked;
        bool initial = publishedFacilities == null;
        bool requestsChanged = initial || !JToken.DeepEquals(replicaRequests, facts["requests"]);
        bool eventsChanged = initial || !JToken.DeepEquals(replicaEvents, facts["events"]);
        bool reputationChanged = initial || !JToken.DeepEquals(facilityView?["reputation"], facts["reputation"]);
        publishedFacilities = bytes;
        facilityView = facts;
        store.wildFavor = (int)facts["favor"];
        store.daySinceLastBrowseUpgrade = (int)facts["browsed"];
        store.isAfterhoursIntroduced = (bool)facts["afterhours"];
        store.isStampIntroduced = (bool)facts["stamp"];
        store.storeClientData.isJacksonIntroduced = (bool)facts["jackson"];
        store.difficultyId = (string)facts["difficulty"];
        store.isHardMode = store.difficultyId == Difficulty.HARD;
        store.skipIntro = (bool)facts["skipIntro"];
        store.rentValue = (int)facts["rent"];
        store.mortgageWeeklyPayment = (int)facts["mortgage"];
        store.isPayingMortgage = (bool)facts["payingMortgage"];
        store.IsPropertyPaid = (bool)facts["propertyPaid"];
        store.isDisplayCaseIntroduced = (bool)facts["displayCaseIntroduced"];
        foreach (JArray row in (JArray)facts["reputation"])
        {
            var reputation = store.storeReputations.FirstOrDefault(candidate => candidate.factionId == (string)row[0]);
            if (reputation == null) continue;
            ReputationAmount(reputation) = (double)row[1];
            var perks = ((JArray)row[2]).Values<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var perk in reputation.perks) perk.isUnlocked = perks.Contains(perk.perkId);
        }
        if (reputationChanged) ReplicaReputationView.Refresh();
        store.isVendingMachineUnlocked = (bool)facts["vending"];
        store.isVendingFountainUnlocked = (bool)facts["fountain"];
        store.afterhourState = locations;
        if (eventsChanged)
        {
            replicaEvents = (JArray)facts["events"];
            ActiveSceneEvents(StoreStation.Instance.storeEventManager) =
                replicaEvents.Cast<JObject>().Select(NativeWorld.ReadEvent).ToList();
            if (NewsUIManager.Instance.uiGameObject.activeSelf) NewsUIManager.Instance.PopulateUI();
        }
        if (requestsChanged)
        {
            bool added = ((JArray)facts["requests"]).Any(row => !store.storeRequests.Any(request => request.id == (string)row["id"]));
            replicaRequests = (JArray)facts["requests"];
            store.storeRequests = replicaRequests.ToObject<List<StoreRequest>>();
            if (PCUIManager.Instance.panel.activeSelf) PCUIManager.Instance.OnChange();
            if (added && !PCUIManager.Instance.panel.activeSelf) PCUIManager.Instance.storePC.SetState(2);
            else PCUIManager.Instance.storePC.RefreshState();
        }
        foreach (JArray row in (JArray)facts["upgrades"])
        {
            if (!store.networkUpgrade.TryGetValue((string)row[0], out var upgrade)) continue;
            upgrade.state = (int)row[1];
            upgrade.cooldownCurrent = (int)row[2];
            upgrade.cost = (int)row[3];
        }
        NativeWorld.ApplyMarketplaceScreen();
        foreach (JArray row in (JArray)facts["services"])
        {
            var service = store.storeService.FirstOrDefault(candidate => candidate.id == (string)row[0]);
            if (service == null) continue;
            service.unlocked = (bool)row[1];
            service.introduced = (bool)row[2];
            service.isUsing = (bool)row[3];
            service.currentCooldown = (int)row[4];
            service.cost = (int)row[5];
        }
        RefreshServiceControls();
        store.commissaryData.redeemedCredit = (int)facts["tokens"];
        foreach (JArray row in (JArray)facts["stock"])
        {
            var item = store.commissaryData.GetItemData((string)row[0]);
            if (item == null) continue;
            item.StockCount = (int)row[1];
            item.price = (int)row[2];
        }
        bool tap = EmporiumEntry.IsWaterTapUnlocked();
        if (initial || previousTap != tap) EmporiumEntry.Instance.ApplyFaucetState(tap);
        if (initial || previousVending != store.isVendingMachineUnlocked)
        {
            StoreVendingMachine.Instance.gameObject.SetActive(store.isVendingMachineUnlocked);
            if (store.isVendingMachineUnlocked) StoreVendingMachine.Instance.UpdateSprite();
        }
        if (initial || previousFountain != store.isVendingFountainUnlocked)
        {
            StoreVendingFountain.Instance.gameObject.SetActive(store.isVendingFountainUnlocked);
            if (store.isVendingFountainUnlocked) StoreVendingFountain.Instance.UpdateSprite();
        }
        remoteWild = (bool)facts["wildActive"];
        remoteCommissary = (bool)facts["commissaryActive"];
        ulong wild = (ulong)facts["wild"], commissary = (ulong)facts["commissary"];
        presentingActivity = true;
        try
        {
            if (remoteWild && wild != presentedWild)
            {
                PhoneUIManager.Instance.CloseUI();
                WildNetworkUI.Open(debug: true);
            }
            else WildNetworkUI.RefreshAll();
            if (remoteCommissary && commissary != presentedCommissary)
            {
                PhoneUIManager.Instance.CloseUI();
                CommissaryUIManager.Instance.OpenUI();
            }
            else if (CommissaryUIManager.Instance.panel.activeSelf) CommissaryUIManager.Instance.RefreshUI();
        }
        finally { presentingActivity = false; }
        presentedWild = wild;
        presentedCommissary = commissary;
    }

    private static void RefreshServiceControls()
    {
        var ui = EndOfDayUIManager.Instance;
        if (!ui.IsOpen) return;
        foreach (var row in ServiceRows(ui))
        {
            row.checkbox.SetOn(row.service.isUsing);
            row.checkbox.SetInteractable(!row.service.isMandatory && !row.service.IsOnCooldown());
        }
        ui.RefreshUI();
    }
}
