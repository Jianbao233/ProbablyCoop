using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

// A separate process loads an archived, previously verified native checkpoint.
// This observer never creates fixtures, calls SaveGame, or changes actor fields.
internal sealed partial class NativeBackgroundQa
{
    private readonly bool actorRestoreOnly = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-actor-restore-only") >= 0;
    private bool actorRunIdentityWritten, actorReloadHostChecked, actorReloadGuestChecked, actorReloadConnected;
    private JObject actorReloadExpected;
    private static int actorReloadNativeLoads;
    private static Guid actorReloadLoadedRun, actorReloadLoadedCheckpoint;

    private static string ActorQaLaunchId()
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "--probably-coop-qa-launch-id");
        if (index < 0 || index + 1 >= args.Length || !Guid.TryParseExact(args[index + 1], "D", out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Actor QA requires its runner-recorded independent process launch identity.");
        return id.ToString("D");
    }

    private void WriteActorRunIdentity()
    {
        if (actorRunIdentityWritten) return;
        Require(Enabled && IsHostProduct && host, "actor identity outside isolated Host");
        var session = NativeActorSession.Current;
        if (session == null || session.Hydrating) return;
        Require(ReferenceEquals(session.Store, PlayerStore.Instance) && CoopRun.IsRegistered(session.Store), "actor identity lacks registered native source");
        var value = new JObject { ["schema"] = 1, ["role"] = "Host", ["nativeSlot"] = session.NativeSaveSlot,
            ["run"] = session.Run.Run.ToString("D"), ["day"] = StoreStation.Instance.dayCounter,
            ["launchId"] = ActorQaLaunchId(), ["createdFromArmedRun"] = session.CreatedFromArmedRun,
            ["checkpointAtEntry"] = session.VerifiedCheckpoint?.Checkpoint.ToString("D") };
        ModHelper.WriteModDataFileText("probably_coop", "qa-actor-run.json", value.ToString(Formatting.Indented));
        actorRunIdentityWritten = true;
    }

    private JObject ActorReloadExpected()
    {
        Require(Enabled && actorRestoreOnly && (IsHostProduct || IsGuestProduct), "actor reload outside isolated restore-only QA");
        if (actorReloadExpected != null) return actorReloadExpected;
        string text = ModHelper.ReadModDataFileText("probably_coop", "qa-actor-reload-expected.json");
        Require(!string.IsNullOrEmpty(text) && text.Length <= NativeActorCheckpoint.MaximumEnvelopeCharacters, "missing bounded archived reload expectation");
        var value = JObject.Parse(text);
        Require((int?)value["schema"] == 1 && (int?)value["checkpointSchema"] == 2 && (bool?)value["requireCarry"] == true &&
            (int?)value["nativeSlot"] >= 0 && (int?)value["day"] >= 0 && Guid.TryParse((string)value["run"], out var run) && run != Guid.Empty &&
            Guid.TryParse((string)value["checkpoint"], out var checkpoint) && checkpoint != Guid.Empty &&
            !string.IsNullOrEmpty((string)value["sourceLaunchId"]) && (string)value["sourceLaunchId"] != ActorQaLaunchId(),
            "invalid archived reload identity or reused process");
        return actorReloadExpected = value;
    }

    private CoopRun.Checkpoint SelectActorReloadCheckpoint(IEnumerable<CoopRun.Checkpoint> checkpoints)
    {
        var expected = ActorReloadExpected();
        var matches = checkpoints.Where(row => row.Run.ToString("D") == (string)expected["run"] &&
            row.Slot == (int)expected["nativeSlot"] && row.Day == (int)expected["day"] && !row.Historical && CoopRun.CanResume(row)).ToArray();
        Require(matches.Length == 1, "archive's exact native actor checkpoint is not the sole selected canonical load");
        return matches[0];
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.LoadGame))]
    private static class ActorReloadNativeLoadObserver
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(PlayerStore __instance, bool __runOriginal)
        {
            if (!Enabled || !IsHostProduct || Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-actor-restore-only") < 0) return;
            var session = NativeActorSession.Current;
            Require(__runOriginal && session != null && ReferenceEquals(session.Store, __instance) && !session.CreatedFromArmedRun &&
                !session.Hydrating && !session.Faulted && session.VerifiedCheckpoint != null, "reload observer did not complete original native LoadGame");
            actorReloadNativeLoads++;
            actorReloadLoadedRun = session.Run.Run;
            actorReloadLoadedCheckpoint = session.VerifiedCheckpoint.Checkpoint;
        }
    }

    private void TickActorReloadHost(RoomSelector room)
    {
        Require(host && IsHostProduct && actorRestoreOnly, "reload Host hook misrouted");
        var expected = ActorReloadExpected();
        var session = NativeActorSession.Current;
        Require(session != null && !session.Faulted && !session.Terminal && !session.CreatedFromArmedRun && !session.Hydrating,
            "restore-only Host does not own a loaded live actor run");
        if (!actorReloadHostChecked)
        {
            Require(actorReloadNativeLoads == 1 && actorReloadLoadedRun == session.Run.Run &&
                actorReloadLoadedCheckpoint == session.VerifiedCheckpoint?.Checkpoint, "a fresh original LoadGame must complete exactly once");
            RestoreActorGameplayCheckpointHost();
            var settings = new ES3Settings(SaveFiles.Slot(session.NativeSaveSlot));
            byte[] bytes = NativeActorPersistence.ReadFileImage(settings.FullPath);
            NativeActorCheckpoint.Receipt receipt;
            using (var reader = ES3Reader.Create(bytes, settings)) receipt = NativeActorCheckpoint.ReadReceipt(reader);
            var checkpoint = NativeActorCheckpoint.DecodeReceipt(receipt, Guid.Parse((string)expected["run"]),
                (int)expected["nativeSlot"], (int)expected["day"], Guid.Parse((string)expected["checkpoint"]));
            Require(session.VerifiedCheckpoint.Encoded == checkpoint.Encoded &&
                ActorReloadHash(bytes) == (string)expected["slotSha256"] &&
                ActorReloadHash(Encoding.UTF8.GetBytes(checkpoint.Encoded)) == (string)expected["checkpointSha256"] &&
                checkpoint.PartnerSetup == (string)expected["partnerSetup"] && NativeActorPartnerSetup.IsComplete(checkpoint.PartnerSetup, checkpoint.Run),
                "loaded source receipt/nonce/setup differs from the archived native save");
            var personal = session.Run.CapturePersonalCheckpoint();
            Require(JToken.DeepEquals(personal, checkpoint.Personal) && JToken.DeepEquals(personal, expected["personal"]),
                "native load changed a personal actor field: " + ActorReloadDifferences((JObject)expected["personal"], personal));
            var carry = ActorReloadCarry(session.Carry);
            Require(JToken.DeepEquals(carry, expected["carry"]), "loaded actor carry roots/native IDs/quantities differ from saved nodes");
            var facts = ActorReloadFacts("Host", session.Run.Run, personal, carry);
            facts["nativeSlot"] = session.NativeSaveSlot; facts["verifiedOnDisk"] = true;
            facts["slotSha256"] = ActorReloadHash(bytes); facts["checkpoint"] = checkpoint.Checkpoint.ToString("D");
            facts["checkpointSha256"] = ActorReloadHash(Encoding.UTF8.GetBytes(checkpoint.Encoded));
            facts["partnerSetup"] = checkpoint.PartnerSetup; facts["nativeLoadCount"] = actorReloadNativeLoads;
            facts["allowedPersonalDifferences"] = new JArray();
            ModHelper.WriteModDataFileText("probably_coop", "qa-actor-reload.json", facts.ToString(Formatting.Indented));
            WriteActorRunIdentity();
            actorReloadHostChecked = true;
            ModHelper.WriteModDataFileText("probably_coop", "qa-room.txt", room.Code);
            Record("PASS actor-reload-host", "original native LoadGame in fresh Host process; exact two personal states, six carry roots, nested IDs and same-file schema2/setup/nonce receipt; no seed/no save");
        }
        if (room.Session?.State == LinkState.Connected) actorReloadConnected = true;
        if (actorReloadConnected && room.Session?.State != LinkState.Connected)
        {
            Require(ActorReloadHash(NativeActorPersistence.ReadFileImage(new ES3Settings(SaveFiles.Slot(session.NativeSaveSlot)).FullPath)) ==
                (string)expected["slotSha256"], "restore-only Host unexpectedly rewrote its canonical save");
            // Exercise the same Stop used by ModEntry.OnDisable, with no QA
            // Application.Quit or QaStopping escape hatch. The runner requires
            // actual process exit and an unchanged canonical file afterwards.
            Require(!QaStopping,"reload shutdown must use the real cooperative runtime boundary");
            StoreUIManager.Instance.CloseAllUI();
            Record("PASS actor-host-stop-preconditions","both reload assertions complete; invoke runtime Stop while registered Host scene remains live; no save requested");
            Record("PASS host runtime","native load and new Guest projection verified; process exit and unchanged file are independently required by runner");
            done=true;
            UnityEngine.Object.FindObjectOfType<CoopRuntime>().Stop();
        }
    }

    private bool RunActorReloadGuest(CoopSession connection)
    {
        Require(!host && IsGuestProduct && actorRestoreOnly, "reload Guest hook misrouted");
        if (actorReloadGuestChecked) return true;
        var world = NativeWorld.Guest;
        if (world?.Ready != true || !world.HasActorProjection || world.HasPendingWork || connection?.Native?.State == null ||
            connection.Native.Waiting || !RestoreActorGameplayCheckpointGuest()) return false;
        var expected = ActorReloadExpected();
        var metadata = NativeJoinSnapshot.Read(connection.Native.State, connection.Native.FullState, connection.Native.FullRevision);
        Require(metadata.Run.ToString("D") == (string)expected["run"] && !metadata.Required && PlayerStore.Instance.saveSlotId == -1,
            "restored Guest did not join the configured Partner in the saved run");
        var personal = new JObject { ["schema"] = 1, ["run"] = metadata.Run.ToString("D"),
            ["actors"] = new JArray(world.RemoteHostActor.Serialize(), NativeActorState.Capture(PlayerStore.Instance).Serialize()) };
        var carry = ActorReloadCarry(world.ActorCarry);
        Require(JToken.DeepEquals(personal, expected["personal"]), "fresh Guest personal projection differs from saved actors");
        Require(JToken.DeepEquals(carry, expected["carry"]), "fresh Guest original carry roots/native IDs/quantities differ from saved nodes");
        var facts = ActorReloadFacts("Guest", metadata.Run, personal, carry);
        facts["nativeSlot"] = PlayerStore.Instance.saveSlotId; facts["verifiedOnDisk"] = false;
        facts["observedSetupRequired"] = metadata.Required;
        facts["expectedSourceCheckpoint"] = expected["checkpoint"].DeepClone();
        facts["checkpointEvidence"] = "Guest observes authenticated actor projection; disk receipt is independently verified only by Host";
        ModHelper.WriteModDataFileText("probably_coop", "qa-actor-reload.json", facts.ToString(Formatting.Indented));
        Record("PASS actor-reload-guest", "new Guest process observes saved Host/Partner personal fields and both original nested carried bags/IDs; no native Guest slot or fixture reseed");
        actorReloadGuestChecked = true;
        return true;
    }

    private JObject ActorReloadFacts(string role, Guid run, JObject personal, JArray carry)
    {
        Require(StoreStation.Instance.dayCounter == (int)ActorReloadExpected()["day"], "reload did not retain saved native day");
        return new JObject { ["schema"] = 1, ["role"] = role, ["run"] = run.ToString("D"),
            ["day"] = StoreStation.Instance.dayCounter, ["launchId"] = ActorQaLaunchId(), ["personal"] = personal, ["carry"] = carry };
    }

    private static JArray ActorReloadCarry(NativeActorCarry carry)
    {
        var rows = new JArray();
        var seen = new HashSet<int>();
        var places = new[] { NativeActorInventory.CarrySlot.LeftPocket, NativeActorInventory.CarrySlot.RightPocket, NativeActorInventory.CarrySlot.Backpack };
        var names = new[] { "pocket-left", "pocket-right", "backpack" };
        for (int actor = 0; actor < 2; actor++)
            for (int place = 0; place < places.Length; place++)
            {
                var root = carry.Get((NativeActorRun.Slot)actor, places[place]).childItem;
                var items = root == null ? Array.Empty<GameItem>() : GraphUtils.FindAllChildrenType<GameItem>(root).Prepend(root).Distinct().OrderBy(item => item.uniqueId).ToArray();
                var values = new JArray();
                foreach (var item in items)
                {
                    Require(item.uniqueId > 0 && seen.Add(item.uniqueId), "reload duplicate/unassigned carried identity");
                    values.Add(new JObject { ["id"] = item.uniqueId, ["identifier"] = item.identifier == null ? JValue.CreateNull() : new JValue(item.identifier), ["count"] = item.unitCount });
                }
                rows.Add(new JObject { ["key"] = "actor/" + actor + "/" + names[place],
                    ["rootId"] = root == null ? JValue.CreateNull() : new JValue(root.uniqueId), ["items"] = values });
            }
        return rows;
    }

    private static string ActorReloadHash(byte[] bytes)
    { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }

    private static string ActorReloadDifferences(JObject expected, JObject actual) => string.Join(",", new[] { 0, 1 }.SelectMany(actor =>
        ((JObject)expected["actors"][actor]).Properties().Where(p => !JToken.DeepEquals(p.Value, actual["actors"][actor][p.Name])).Select(p => actor + ":" + p.Name)));
}
