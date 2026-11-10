using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Only the existing opt-in disposable-profile runner calls these probes. The
// guest uses original item/slot entry points, then waits for authenticated
// command receipts and the complete authoritative graph; no direct wire edits.
internal sealed partial class NativeBackgroundQa
{
    private const string ActorGameplayPrefix = "PROBABLY_COOP_QA_ACTOR_";
    private const string ActorGameplaySourceId = ActorGameplayPrefix + "SOURCE_ID";
    private const string ActorGameplayReady = ActorGameplayPrefix + "READY";
    private const string ActorGameplayFinished = ActorGameplayPrefix + "FINISHED";
    private readonly Dictionary<string, GameItem> actorGameplayItems = new Dictionary<string, GameItem>();
    private readonly Dictionary<string, int> actorGameplayIds = new Dictionary<string, int>();
    private readonly List<(GameInventory Inventory, Action<GameItem, GameInventory, SlotMarker> Callback)> actorGameplayObservers =
        new List<(GameInventory, Action<GameItem, GameInventory, SlotMarker>)>();
    private JObject actorGameplayHostBefore, actorGameplayPartnerBefore;
    private bool actorGameplaySeeded, actorGameplayHostDone, actorGameplayCheckpointPrepared;
    private int actorGameplayMoveMask, actorGameplayGuestStage;
    private ulong actorGameplayRequest;
    private double actorGameplayUntil;
    private uint actorGameplayMovingEntity;
    private JObject actorGameplayGuestHostBefore, actorGameplayGuestPartnerBefore;

    private void SeedActorGameplayHost()
    {
        Require(Enabled && IsHostProduct && host, "actor gameplay seed outside disposable host QA");
        Require(!actorGameplaySeeded, "actor gameplay seeded twice");
        var session = NativeActorSession.Current;
        Require(session != null && ReferenceEquals(session.Store, PlayerStore.Instance), "actor gameplay requires active native actor session");
        Require(!MapUIManager.Instance.IsOutside, "actor gameplay must seed in store");
        foreach (var actor in new[] { NativeActorRun.Slot.Host, NativeActorRun.Slot.Partner })
            foreach (var place in new[] { NativeActorInventory.CarrySlot.LeftPocket, NativeActorInventory.CarrySlot.RightPocket, NativeActorInventory.CarrySlot.Backpack })
                Require(session.Carry.Get(actor, place).childItem == null, "actor gameplay cannot overwrite existing carry");

        ActorGameplayCreate("HOST_BAG", "backpack_small");
        ActorGameplayCreate("PARTNER_BAG", "backpack_medium");
        ActorGameplayCreate("HOST_MARKER", "packet_red_cigarette");
        ActorGameplayCreate("PARTNER_MARKER", "packet_red_cigarette");
        ActorGameplayCreate("PARTNER_CONSUME", "packet_red_cigarette");
        ActorGameplayCreate("HOST_TOOLBOX", "toolbox");
        ActorGameplayCreate("HOST_COORDINATOR", "storage_coordinator");
        ActorGameplayPlace(ActorGameplayBag(actorGameplayItems["HOST_BAG"]), actorGameplayItems["HOST_MARKER"]);
        ActorGameplayPlace(ActorGameplayBag(actorGameplayItems["PARTNER_BAG"]), actorGameplayItems["PARTNER_MARKER"]);
        using (session.Enter(NativeActorRun.Slot.Host))
        {
            ActorGameplayPlace(session.Carry.Get(NativeActorRun.Slot.Host, NativeActorInventory.CarrySlot.Backpack), actorGameplayItems["HOST_BAG"]);
            ActorGameplayPlace(session.Carry.Get(NativeActorRun.Slot.Host, NativeActorInventory.CarrySlot.LeftPocket), actorGameplayItems["HOST_TOOLBOX"]);
            ActorGameplayPlace(session.Carry.Get(NativeActorRun.Slot.Host, NativeActorInventory.CarrySlot.RightPocket), actorGameplayItems["HOST_COORDINATOR"]);
        }
        using (session.Enter(NativeActorRun.Slot.Partner))
        {
            ActorGameplayPlace(session.Carry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.Backpack), actorGameplayItems["PARTNER_BAG"]);
            ActorGameplayPlace(session.Carry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.LeftPocket), actorGameplayItems["PARTNER_CONSUME"]);
        }
        ActorGameplayObserve(session.Carry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.RightPocket), 1);
        ActorGameplayObserve(ActorGameplayBag(actorGameplayItems["PARTNER_BAG"]), 2);
        ActorGameplayObserve(session.Carry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.LeftPocket), 4);
        using (session.Enter(NativeActorRun.Slot.Host))
            ActorGameplayQueryProbe(actorGameplayItems["HOST_TOOLBOX"], actorGameplayItems["HOST_COORDINATOR"]);
        using (session.Enter(NativeActorRun.Slot.Partner)) ActorGameplayQueryProbe(null, null);
        actorGameplaySeeded = true;
        Record("FIXTURE actor-gameplay", ActorGameplayEvidence("seed", session.Carry).ToString(Formatting.None));
        Record("PASS actor-source-query-isolation", "original Toolbox/Coordinator reach queries: Host finds own pockets; explicit Partner scope excludes them; synchronous outside-query branch only, not travel coverage");
    }

    private void CheckActorGameplayHost()
    {
        if (!actorGameplaySeeded || actorGameplayHostDone) return;
        var session = NativeActorSession.Current;
        Require(session != null, "actor gameplay session disappeared");
        if (actorGameplayHostBefore == null)
        {
            if (!session.Store.modData.TryGetValue(NativeActorPartnerSetup.SaveKey, out string setup) ||
                !NativeActorPartnerSetup.IsComplete(setup, session.Run.Run)) return;
            // Real role selection must finish first; an unconfigured Partner
            // deliberately has no perks and cannot accept seeded personal data.
            session.Store.healthData.daysSinceNicotineConsumption = 17;
            session.Run.Partner.healthData.daysSinceNicotineConsumption = 9;
            if (!session.Run.Partner.startingPerks.Any(perk => perk.id == "nicotine_addiction"))
                session.Run.Partner.startingPerks.Add(new StartingPerk { id = "nicotine_addiction" });
            if(saveRoundTrip) VerifyScavengeVisit(session);
            actorGameplayHostBefore = NativeActorState.Capture(session.Store).Serialize();
            actorGameplayPartnerBefore = session.Run.Partner.Serialize();
            actorGameplayItems["PARTNER_BAG"].EnableTag(ActorGameplayReady);
            Record("FIXTURE actor-personal-consumption", "real Partner setup complete; QA nicotine perk and distinguishable counters now seeded");
        }
        Require(JToken.DeepEquals(actorGameplayHostBefore, NativeActorState.Capture(session.Store).Serialize()),
            "partner carry/consumption changed host personal state");
        ActorGameplayCheckBag(session.Carry, NativeActorRun.Slot.Host, actorGameplayItems["HOST_BAG"], actorGameplayItems["HOST_MARKER"]);
        ActorGameplayCheckBag(session.Carry, NativeActorRun.Slot.Partner, actorGameplayItems["PARTNER_BAG"], actorGameplayItems["PARTNER_MARKER"]);
        Require(ReferenceEquals(actorGameplayItems["HOST_TOOLBOX"].parentInventory, session.Carry.Get(NativeActorRun.Slot.Host, NativeActorInventory.CarrySlot.LeftPocket)) &&
            ReferenceEquals(actorGameplayItems["HOST_COORDINATOR"].parentInventory, session.Carry.Get(NativeActorRun.Slot.Host, NativeActorInventory.CarrySlot.RightPocket)),
            "partner command moved host pocket objects");
        if (actorGameplayItems["PARTNER_CONSUME"].parentInventory != null) return;
        Require(actorGameplayMoveMask == 7, "partner consumed before all three original slot moves were observed on authority");
        var expected = (JObject)actorGameplayPartnerBefore.DeepClone();
        expected["healthData"]["daysSinceNicotineConsumption"] = -3;
        Require(JToken.DeepEquals(expected, session.Run.Partner.Serialize()), "native nicotine affected another personal field or the wrong actor");
        foreach (var observer in actorGameplayObservers) observer.Inventory.onSlotAddItemEndFunc -= observer.Callback;
        actorGameplayObservers.Clear();
        Record("PASS actor-source-native-consumption", ActorGameplayEvidence("consumed", session.Carry).ToString(Formatting.None));
        // Free the original carry slots before the older full travel suite.
        // Keep both bags and their distinct contents alive for save/rejoin.
        foreach (var actor in new[] { NativeActorRun.Slot.Host, NativeActorRun.Slot.Partner })
        {
            using (session.Enter(actor))
            {
                var result = session.Carry.ReturnCarryToShop(actor);
                Require(result.Complete, "actor QA return failed: " + actor + " remaining=" + result.RemainingUnits + " reason=" + result.Reason);
            }
        }
        foreach(string role in new[]{"HOST_BAG","PARTNER_BAG","HOST_TOOLBOX","HOST_COORDINATOR"})
            Require(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.hirelingInv,actorGameplayItems[role])==0,
                "actor QA staging cannot clear the shared counter: "+role);
        Record("FIXTURE actor-retained-stock","four returned fixtures staged in the original shared hireling inventory; counter capacity reserved for the following native drag cases");
        actorGameplayItems["PARTNER_BAG"].EnableTag(ActorGameplayFinished);
        actorGameplayHostDone = true;
        Record("PASS actor-carry-returned", ActorGameplayEvidence("returned-shared", session.Carry).ToString(Formatting.None));
    }

    private bool RunActorGameplayGuest(CoopSession session, double now)
    {
        Require(Enabled && IsGuestProduct && !host, "actor gameplay guest outside disposable QA");
        if (actorGameplayGuestStage == 6) return true;
        var world = NativeWorld.Guest;
        if (world?.Ready != true || !world.HasActorProjection || world.ActorCarry == null) return false;
        var link = session.Native;
        if (actorGameplayGuestStage == 0)
        {
            if (world.HasPendingWork || QaWaiting(link)) return false;
            if (ActorGameplayFind(world, "PARTNER_BAG")?.IsTag(ActorGameplayReady) != true) return false;
            foreach (string role in new[] { "HOST_BAG", "PARTNER_BAG", "HOST_MARKER", "PARTNER_MARKER", "PARTNER_CONSUME", "HOST_TOOLBOX", "HOST_COORDINATOR" })
            {
                var item = ActorGameplayFind(world, role);
                Require(item != null, "missing actor gameplay fixture: " + role);
                actorGameplayItems.Add(role, item);
                actorGameplayIds.Add(role, item.GetTagReadonly(ActorGameplaySourceId).GetInt());
            }
            Require(PlayerStore.Instance.saveSlotId == -1, "actor gameplay guest loaded a native save");
            NativeActorCarryUi.Tick();
            Require(NativeActorCarryUi.Available && NativeActorCarryUi.IsOpen &&
                ((MachineryPanelRow)EmporiumEntry.Instance.afterhourWindow.childElement).GetPanel("ground").isHidden &&
                !GeneralHelper.MayPlayerInsertInto(EmporiumEntry.Instance.afterhourInventory),
                "original personal carry UI missing or exposed shared ground indoors");
            NativeOffscreenCapture.Capture("qa-actor-personal-carry");
            ActorGameplayCheckBag(world.ActorCarry, NativeActorRun.Slot.Host, actorGameplayItems["HOST_BAG"], actorGameplayItems["HOST_MARKER"]);
            ActorGameplayCheckBag(world.ActorCarry, NativeActorRun.Slot.Partner, actorGameplayItems["PARTNER_BAG"], actorGameplayItems["PARTNER_MARKER"]);
            Require(ReferenceEquals(EmporiumEntry.Instance.afterhourPocketSlotInvBackpack, world.ActorCarry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.Backpack)),
                "guest original backpack UI is not its own actor root");
            Require(ReferenceEquals(actorGameplayItems["PARTNER_CONSUME"].parentInventory, world.ActorCarry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.LeftPocket)),
                "partner original pocket did not receive fixture");
            ActorGameplayQueryProbe(null, null);
            actorGameplayGuestHostBefore = world.RemoteHostActor.Serialize();
            actorGameplayGuestPartnerBefore = NativeActorState.Capture(PlayerStore.Instance).Serialize();
            Require(PlayerStore.Instance.healthData.daysSinceNicotineConsumption == 9 && HealthData.CanUseNicotine(),
                "guest native nicotine query does not use Partner personal state");
            actorGameplayMovingEntity = world.PointerIdentity(actorGameplayItems["PARTNER_CONSUME"]);
            Require(actorGameplayMovingEntity != 0, "partner pocket item lacks network identity");
            // An original activation call still submits a normal authenticated
            // command; the authority must reject this foreign carry entity.
            actorGameplayItems["HOST_MARKER"].ActivateSlotItem();
            ActorGameplayIssued(link, now, 1);
            Record("PASS actor-guest-native-roots", ActorGameplayEvidence("guest-initial", world.ActorCarry).ToString(Formatting.None));
            return false;
        }
        Require(now < actorGameplayUntil, "actor gameplay timed out stage=" + actorGameplayGuestStage + " request=" + actorGameplayRequest);
        if (QaWaiting(link) || QaCompleted(link) < actorGameplayRequest || world.HasPendingWork) return false;
        Require(JToken.DeepEquals(actorGameplayGuestHostBefore, world.RemoteHostActor.Serialize()), "guest projection changed Host personal facts");
        if (actorGameplayGuestStage == 1)
        {
            Require(QaResult(link) == (ushort)ShopResult.Unavailable, "foreign carry activation was not rejected: " + QaResult(link));
            Require(actorGameplayItems["HOST_MARKER"].parentInventory == ActorGameplayBag(actorGameplayItems["HOST_BAG"]) &&
                JToken.DeepEquals(actorGameplayGuestPartnerBefore, NativeActorState.Capture(PlayerStore.Instance).Serialize()),
                "rejected foreign consumption changed ownership or actor state");
            Record("PASS actor-foreign-consumption-rejected", "normal original ActivateSlotItem request rejected Unavailable; both actor states and source item preserved");
            ActorGameplayMoveGuest(link, world.ActorCarry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.RightPocket), now, 2);
            return false;
        }
        Require(QaResult(link) == (ushort)ShopResult.Applied, "actor original operation rejected stage=" + actorGameplayGuestStage + " result=" + QaResult(link));
        if (actorGameplayGuestStage <= 4)
        {
            var moving = actorGameplayItems["PARTNER_CONSUME"];
            GameInventory expected = actorGameplayGuestStage == 2
                ? world.ActorCarry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.RightPocket)
                : actorGameplayGuestStage == 3 ? ActorGameplayBag(actorGameplayItems["PARTNER_BAG"])
                : world.ActorCarry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.LeftPocket);
            Require(ReferenceEquals(moving.parentInventory, expected) && world.PointerIdentity(moving) == actorGameplayMovingEntity && moving.unitCount == 1,
                "actor slot move lost parent, identity or quantity stage=" + actorGameplayGuestStage);
            Require(JToken.DeepEquals(actorGameplayGuestPartnerBefore, NativeActorState.Capture(PlayerStore.Instance).Serialize()), "plain carry move changed Partner personal facts");
            if (actorGameplayGuestStage == 2)
                ActorGameplayMoveGuest(link, ActorGameplayBag(actorGameplayItems["PARTNER_BAG"]), now, 3);
            else if (actorGameplayGuestStage == 3)
                ActorGameplayMoveGuest(link, world.ActorCarry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.LeftPocket), now, 4);
            else
            {
                Require(moving.MayActivateSlotItem() && moving.CanActivateSlotItem(),
                    "Partner original frontend ability queries do not expose its nicotine action");
                moving.ActivateSlotItem();
                ActorGameplayIssued(link, now, 5);
                Record("PASS actor-three-original-slot-moves", "left pocket -> right pocket -> own backpack -> left pocket; same entity after every confirmed native request");
            }
            return false;
        }
        var consumedExpected = (JObject)actorGameplayGuestPartnerBefore.DeepClone();
        consumedExpected["healthData"]["daysSinceNicotineConsumption"] = -3;
        Require(JToken.DeepEquals(consumedExpected, NativeActorState.Capture(PlayerStore.Instance).Serialize()), "Partner original nicotine result not projected exactly");
        Require(actorGameplayItems["PARTNER_CONSUME"].parentInventory == null &&
            !world.Objects.Values.OfType<GameItem>().Any(item => item.parentInventory != null && item.IsTag(ActorGameplayPrefix + "PARTNER_CONSUME")),
            "consumed Partner entity remains in the physical graph");
        if (!actorGameplayItems["PARTNER_BAG"].IsTag(ActorGameplayFinished)) return false;
        foreach (var actor in new[] { NativeActorRun.Slot.Host, NativeActorRun.Slot.Partner })
            foreach (var place in new[] { NativeActorInventory.CarrySlot.LeftPocket, NativeActorInventory.CarrySlot.RightPocket, NativeActorInventory.CarrySlot.Backpack })
                Require(world.ActorCarry.Get(actor, place).childItem == null, "actor QA return did not clear both original carry banks");
        Record("PASS actor-gameplay-real-guest", ActorGameplayEvidence("guest-consumed-and-returned", world.ActorCarry).ToString(Formatting.None));
        actorGameplayGuestStage = 6;
        return true;
    }

    // Call once after the three joint journeys have returned and immediately
    // before the save/rejoin/night suite. Never replace occupied native slots.
    private void PrepareActorGameplayCheckpointHost()
    {
        if (!actorGameplaySeeded) return;
        Require(actorGameplayHostDone, "actor checkpoint prepared before gameplay finished");
        if (actorGameplayCheckpointPrepared) return;
        var session = NativeActorSession.Current;
        Require(session != null && !MapUIManager.Instance.IsOutside, "actor checkpoint must prepare at settled store");
        foreach (var actor in new[] { NativeActorRun.Slot.Host, NativeActorRun.Slot.Partner })
        {
            var slot = session.Carry.Get(actor, NativeActorInventory.CarrySlot.Backpack);
            Require(slot.childItem == null, "actor checkpoint would overwrite an existing bag: " + actor);
            using (session.Enter(actor)) ActorGameplayPlace(slot, actorGameplayItems[actor == NativeActorRun.Slot.Host ? "HOST_BAG" : "PARTNER_BAG"]);
        }
        actorGameplayCheckpointPrepared = true;
        CheckActorGameplayCheckpointHost();
        Record("FIXTURE actor-nonempty-checkpoint", ActorGameplayEvidence("repacked-before-save", session.Carry).ToString(Formatting.None));
    }

    // These are post-boundary oracles, not SaveGame wrappers. Main QA calls them
    // only after it has observed the real save/load/rejoin/night completion.
    private void CheckActorGameplayCheckpointHost()
    {
        if (!actorGameplayCheckpointPrepared) return;
        var session = NativeActorSession.Current;
        Require(session != null, "actor checkpoint lost session");
        ActorGameplayCheckRetained(session.Carry);
        Record("PASS actor-checkpoint-host-graph", ActorGameplayEvidence("host-boundary", session.Carry).ToString(Formatting.None));
    }

    private void CheckActorGameplayCheckpointGuest()
    {
        if (actorGameplayGuestStage != 6) return;
        var world = NativeWorld.Guest;
        Require(world?.Ready == true && world.HasActorProjection && !world.HasPendingWork, "actor checkpoint guest projection is incomplete");
        ActorGameplayCheckRetained(world.ActorCarry);
        Record("PASS actor-checkpoint-guest-graph", ActorGameplayEvidence("guest-boundary", world.ActorCarry).ToString(Formatting.None));
    }

    private bool ResumeActorGameplayGuest()
    {
        Require(Enabled && IsGuestProduct && !host, "actor gameplay resume outside disposable guest QA");
        if (actorGameplayGuestStage == 6) return true;
        var world = NativeWorld.Guest;
        if (world?.Ready != true || !world.HasActorProjection || world.ActorCarry == null || world.HasPendingWork) return false;
        foreach (string role in new[] { "HOST_BAG", "PARTNER_BAG", "HOST_MARKER", "PARTNER_MARKER" })
        {
            var item = ActorGameplayFind(world, role);
            Require(item != null, "resumed actor fixture missing: " + role);
            int sourceId = item.GetTagReadonly(ActorGameplaySourceId).GetInt();
            Require(sourceId > 0 && sourceId == item.uniqueId, "resumed actor persistent identity changed: " + role);
            actorGameplayItems.Add(role, item);
            actorGameplayIds.Add(role, sourceId);
        }
        actorGameplayGuestStage = 6;
        foreach (var actor in new[] { NativeActorRun.Slot.Host, NativeActorRun.Slot.Partner })
            foreach (var place in new[] { NativeActorInventory.CarrySlot.LeftPocket, NativeActorInventory.CarrySlot.RightPocket, NativeActorInventory.CarrySlot.Backpack })
                Require(world.ActorCarry.Get(actor, place).childItem == null, "pre-trip rejoin unexpectedly restored occupied carry");
        foreach (string prefix in new[] { "HOST", "PARTNER" })
        {
            var bag = actorGameplayItems[prefix + "_BAG"];
            var marker = actorGameplayItems[prefix + "_MARKER"];
            Require(ReferenceEquals(marker.parentInventory, ActorGameplayBag(bag)) && marker.unitCount == 1,
                "pre-trip rejoin lost the retained private bag content: " + prefix);
        }
        Record("PASS actor-gameplay-resume", "pre-trip rejoin: fresh Guest restored four original native identities, two retained bags/contents in shared stock, and both carry banks empty; consumed fixture correctly absent");
        return true;
    }

    // Optional restore-only runner hook. Call after a real Host LoadGame has
    // restored its validated on-disk actor checkpoint, never instead of load.
    private void RestoreActorGameplayCheckpointHost()
    {
        Require(Enabled && IsHostProduct && host, "actor disk restore outside disposable host QA");
        var session = NativeActorSession.Current;
        Require(session != null && session.VerifiedCheckpoint != null && !session.CreatedFromArmedRun &&
            !session.Hydrating && session.Store.saveSlotId >= 0, "actor disk oracle requires a completed native checkpoint load");
        Require(actorGameplayItems.Count == 0 && !actorGameplaySeeded, "actor disk oracle must not replace existing QA fixtures");
        ActorGameplayBindLoadedCarry(session.Carry);
        actorGameplaySeeded = actorGameplayHostDone = actorGameplayCheckpointPrepared = true;
        CheckActorGameplayCheckpointHost();
        Record("PASS actor-nonempty-disk-load", ActorGameplayEvidence("loaded-native-checkpoint", session.Carry).ToString(Formatting.None));
    }

    private bool RestoreActorGameplayCheckpointGuest()
    {
        Require(Enabled && IsGuestProduct && !host, "actor disk guest oracle outside disposable QA");
        var world = NativeWorld.Guest;
        if (world?.Ready != true || !world.HasActorProjection || world.ActorCarry == null || world.HasPendingWork) return false;
        Require(PlayerStore.Instance.saveSlotId == -1, "disk-load Guest entered a native slot");
        if (actorGameplayGuestStage != 6)
        {
            Require(actorGameplayItems.Count == 0, "actor disk guest oracle must not replace existing QA fixtures");
            ActorGameplayBindLoadedCarry(world.ActorCarry);
            actorGameplayGuestStage = 6;
        }
        CheckActorGameplayCheckpointGuest();
        return true;
    }

    private void ActorGameplayBindLoadedCarry(NativeActorCarry carry)
    {
        var seenIds = new HashSet<int>();
        foreach (var actor in new[] { NativeActorRun.Slot.Host, NativeActorRun.Slot.Partner })
        {
            string prefix = actor == NativeActorRun.Slot.Host ? "HOST" : "PARTNER";
            var bag = carry.Get(actor, NativeActorInventory.CarrySlot.Backpack).childItem;
            Require(bag != null && bag.IsTag(ActorGameplayPrefix + prefix + "_BAG"), "loaded native carry has wrong actor bag: " + actor);
            var marker = ActorGameplayBag(bag).childItems.SingleOrDefault(item => item.IsTag(ActorGameplayPrefix + prefix + "_MARKER"));
            Require(marker != null && marker.unitCount == 1, "loaded native carry lost unique content: " + actor);
            foreach (var row in new[] { (Role: prefix + "_BAG", Item: bag), (Role: prefix + "_MARKER", Item: marker) })
            {
                int id = row.Item.GetTagReadonly(ActorGameplaySourceId).GetInt();
                Require(id > 0 && row.Item.uniqueId == id && seenIds.Add(id), "loaded native identity changed or duplicated: " + row.Role);
                actorGameplayItems.Add(row.Role, row.Item);
                actorGameplayIds.Add(row.Role, id);
            }
        }
    }

    private void ActorGameplayCheckRetained(NativeActorCarry carry)
    {
        foreach (var actor in new[] { NativeActorRun.Slot.Host, NativeActorRun.Slot.Partner })
        {
            string prefix = actor == NativeActorRun.Slot.Host ? "HOST" : "PARTNER";
            var bag = carry.Get(actor, NativeActorInventory.CarrySlot.Backpack).childItem;
            Require(bag != null && bag.IsTag(ActorGameplayPrefix + prefix + "_BAG"), "restored actor bag belongs to wrong actor: " + actor);
            var marker = ActorGameplayBag(bag).childItems.SingleOrDefault(item => item.IsTag(ActorGameplayPrefix + prefix + "_MARKER"));
            Require(marker != null && marker.unitCount == 1, "restored personal bag lost its unique content: " + actor);
            foreach (var row in new[] { (Role: prefix + "_BAG", Item: bag), (Role: prefix + "_MARKER", Item: marker) })
            {
                Require(row.Item.GetTagReadonly(ActorGameplaySourceId).GetInt() == actorGameplayIds[row.Role], "fixture persistent identity tag changed: " + row.Role);
                // Native mirrors carry the original uniqueId through their
                // field contract; reloaded source items must retain it too.
                Require(row.Item.uniqueId == actorGameplayIds[row.Role], "native persistent uniqueId changed: " + row.Role);
                actorGameplayItems[row.Role] = row.Item;
            }
            ActorGameplayCheckBag(carry, actor, bag, marker);
        }
    }

    private void ActorGameplayCreate(string role, string id)
    {
        var item = DirectoryMaster.Item(id);
        Require(item != null && item.uniqueId > 0 && item.unitCount == 1, "invalid actor fixture " + id);
        GeneralHelper.SetItemOwned(item, true);
        item.EnableTag(ActorGameplayPrefix + role);
        item.ModifyTag(ActorGameplaySourceId, state => state.Enable().SetInt(item.uniqueId));
        actorGameplayItems.Add(role, item);
        actorGameplayIds.Add(role, item.uniqueId);
    }

    private static void ActorGameplayPlace(GameInventory target, GameItem item)
    {
        var slot = target.TryFindOneValidInventorySlot(item);
        Require(slot?.IsValid() == true && slot.TryAcceptOnce() == item.unitCount && ReferenceEquals(item.parentInventory, target),
            "native actor fixture placement failed: " + item.identifier);
    }

    private static GameInventory ActorGameplayBag(GameItem bag)
    {
        var inventory = bag.contentWindow?.childElement as GameInventory;
        Require(inventory != null, "actor fixture bag lost original content inventory");
        return inventory;
    }

    private static GameItem ActorGameplayFind(NativeWorld world, string role) => world.Objects.Values.OfType<GameItem>()
        .SingleOrDefault(item => item.parentInventory != null && item.IsTag(ActorGameplayPrefix + role));

    private static void ActorGameplayCheckBag(NativeActorCarry carry, NativeActorRun.Slot actor, GameItem bag, GameItem marker)
    {
        var slot = carry.Get(actor, NativeActorInventory.CarrySlot.Backpack);
        Require(ReferenceEquals(slot.childItem, bag) && ReferenceEquals(bag.parentInventory, slot) &&
            ReferenceEquals(marker.parentInventory, ActorGameplayBag(bag)) && marker.unitCount == 1,
            "actor carry bag/content ownership drift: " + actor);
    }

    private void ActorGameplayObserve(GameInventory inventory, int bit)
    {
        Action<GameItem, GameInventory, SlotMarker> observe = (item, _, __) =>
        {
            if (!ReferenceEquals(item, actorGameplayItems["PARTNER_CONSUME"])) return;
            Require(NativeActorSession.Current?.Run.CurrentActor == NativeActorRun.Slot.Partner, "native carry command lost Partner scope");
            Require((actorGameplayMoveMask & bit) == 0, "original actor move executed more than once");
            actorGameplayMoveMask |= bit;
            Record("TRACE actor-source-slot-commit", "leg=" + bit + " nativeId=" + item.uniqueId + " actor=Partner");
        };
        inventory.onSlotAddItemEndFunc += observe;
        actorGameplayObservers.Add((inventory, observe));
    }

    private void ActorGameplayMoveGuest(ReplicaLink link, GameInventory target, double now, int nextStage)
    {
        var slot = target.TryFindOneValidInventorySlot(actorGameplayItems["PARTNER_CONSUME"]);
        Require(slot?.IsValid() == true, "guest native actor destination invalid stage=" + nextStage);
        slot.TryAcceptOnce();
        ActorGameplayIssued(link, now, nextStage);
    }

    private void ActorGameplayIssued(ReplicaLink link, double now, int nextStage)
    {
        Require(QaWaiting(link), "original actor entry point emitted no authenticated request stage=" + nextStage);
        ulong request = QaIssued(link);
        Require(request > actorGameplayRequest, "actor operation did not allocate a new request");
        actorGameplayRequest = request; actorGameplayGuestStage = nextStage; actorGameplayUntil = now + 30;
    }

    private static void ActorGameplayQueryProbe(GameItem toolbox, GameItem coordinator)
    {
        var map = MapUIManager.Instance;
        Require(map != null, "actor reach query requires original map");
        var field = AccessTools.Field(typeof(MapUIManager), "<Current>k__BackingField");
        Require(field != null, "original outside query field missing");
        var before = map.Current;
        var queryScene = AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND);
        Require(queryScene != null, "original query location unregistered");
        try
        {
            // Read-only synchronous branch probe: no Travel, scene callbacks,
            // frame yield, UI open/close, or network capture can occur here.
            field.SetValue(map, queryScene);
            Require(ReferenceEquals(ToolboxHelper.FindToolboxInReach(), toolbox), "native toolbox query crossed actor carry ownership");
            Require(ReferenceEquals(MachineStorageCoordinator.FindCoordinatorInReach(), coordinator), "native coordinator query crossed actor carry ownership");
        }
        finally { field.SetValue(map, before); }
    }

    private JObject ActorGameplayEvidence(string phase, NativeActorCarry carry)
    {
        var items = new JArray();
        foreach (var pair in actorGameplayItems.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            items.Add(new JObject { ["role"] = pair.Key, ["sourceUniqueId"] = actorGameplayIds[pair.Key],
                ["nativeUniqueId"] = pair.Value.uniqueId, ["attached"] = pair.Value.parentInventory != null,
                ["networkEntity"] = host ? 0 : NativeWorld.Guest?.PointerIdentity(pair.Value) ?? 0 });
        var source = NativeActorSession.Current;
        return new JObject { ["phase"] = phase, ["items"] = items, ["moveMask"] = actorGameplayMoveMask,
            ["hostNicotine"] = host ? source.Store.healthData.daysSinceNicotineConsumption : NativeWorld.Guest.RemoteHostActor.healthData.daysSinceNicotineConsumption,
            ["partnerNicotine"] = host ? source.Run.Partner.healthData.daysSinceNicotineConsumption : PlayerStore.Instance.healthData.daysSinceNicotineConsumption,
            ["hostBag"] = carry.Get(NativeActorRun.Slot.Host, NativeActorInventory.CarrySlot.Backpack).childItem?.uniqueId,
            ["partnerBag"] = carry.Get(NativeActorRun.Slot.Partner, NativeActorInventory.CarrySlot.Backpack).childItem?.uniqueId };
    }
}
