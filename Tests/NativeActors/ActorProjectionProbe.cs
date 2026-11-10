using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

static class ActorProjectionProbe
{
    private static int checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL actor projection: " + label);
        checks++;
    }
    private static void Reject<T>(Action action, string label) where T : Exception
    {
        try { action(); } catch (T) { checks++; return; }
        throw new Exception("Not rejected actor projection: " + label);
    }
    private static PlayerStore Shell()
    {
        var shell = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        shell.healthData = new HealthData();
        shell.startingPerks = new List<StartingPerk>();
        shell.appraisalBlacklist = new List<string>();
        shell.appraisalPromotionDay = -1;
        shell.saveSlotId = -1;
        shell.playerCash = 900;
        shell.runID = "guest-local-descriptor";
        return shell;
    }
    private static void Bad(JObject source, Guid run, Action<JObject> mutate, string label)
    {
        var copy = (JObject)source.DeepClone(); mutate(copy);
        Reject<InvalidDataException>(() => NativeActorProjection.Read(copy, run), label);
    }

    internal static int Run()
    {
        var run = Guid.NewGuid();
        var host = NativeActorState.CreateDefault();
        host.playerName = "host"; host.appraiserLevel = 1; host.healthData.luck = 3;
        host.startingPerks.Add(new StartingPerk { id = "lucky", cost = 1 });
        var partner = NativeActorState.CreateDefault();
        partner.playerName = "partner"; partner.appraiserLevel = 2; partner.appraisalProgress = 25.5;
        partner.appraisalPromotionDeclined = true; partner.appraisalPromotionDay = 14;
        partner.scavengingAttempts = 4; partner.dumpingGroundMajorWoundImmunitySearchesLeft = 2;
        partner.dumpingGroundMinorWoundImmunitySearchesLeft = 7;
        partner.healthData.daysSinceNicotineConsumption = -4; partner.healthData.woundState = 6;
        partner.healthData.luck = -5; partner.healthData.liquidLuckDaysLeft = 3;
        partner.appraisalBlacklist.Add("guns");
        partner.startingPerks.Add(new StartingPerk { id = "sticky", cost = -3, maxSlot = 2,
            type = StartingPerk.StartingPerkType.NEGATIVE, mindEditable = true,
            rawName = "retained", rawDescription = "new facts", incompatiblePerks = new List<string> { "other" } });
        partner.startingPerks.Add(new StartingPerk { id = "coop_projection_probe", cost = 1 });
        var wire = NativeActorProjection.Capture(run, host, partner);
        Check((int)wire["actors"][0]["slot"] == 0 && (int)wire["actors"][1]["slot"] == 1, "explicit fixed slots");
        Check(!NativeActorProjection.OwnsPlayerField("playerCash") && !NativeActorProjection.OwnsPlayerField("visitLeftTonight") && NativeActorProjection.OwnsPlayerField("appraisalBlacklist") && NativeActorProjection.OwnsPlayerField("healthData"), "personal world field exclusion");
        Bad(wire, run, j => j["run"] = Guid.NewGuid().ToString("D"), "wrong run");
        Bad(wire, run, j => j["schema"] = 2, "unknown schema");
        Bad(wire, run, j => j["schema"] = "1", "schema numeric string");
        Bad(wire, run, j => ((JArray)j["actors"]).RemoveAt(1), "missing partner");
        Bad(wire, run, j => j["actors"][1]["slot"] = 0, "duplicate slot");
        Bad(wire, run, j => j["actors"][1]["slot"] = 2, "unknown slot");
        Bad(wire, run, j => j["actors"][1]["slot"] = 1.0, "fractional-typed slot");
        Bad(wire, run, j => j["actors"][1]["slot"] = long.MaxValue, "overflow slot");
        Bad(wire, run, j => j["actors"][1]["unexpected"] = true, "extra slot property");
        Bad(wire, run, j => j["actors"][1]["state"]["healthData"]["woundState"] = -1, "invalid partner health");
        Bad(wire, run, j => j["actors"][0]["state"]["startingPerks"][0]["nameProvider"] = "evil", "network callback rejected");
        var cleanWorld = new JObject { ["world"] = new JObject { ["playerCash"] = 900 }, ["business"] = new JObject(), ["scene"] = new JObject { ["visits"] = 1 } };
        NativeActorProjection.RejectLegacyPersonalMirrors(cleanWorld);
        foreach (var field in typeof(NativeActorState).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (!NativeActorProjection.OwnsPlayerField(field.Name)) continue;
            var legacy = (JObject)cleanWorld.DeepClone(); legacy["world"][field.Name] = 0;
            Reject<InvalidDataException>(() => NativeActorProjection.RejectLegacyPersonalMirrors(legacy), "world mirror " + field.Name);
        }
        foreach (string key in new[] { "health", "startingPerks" })
        {
            var legacy = (JObject)cleanWorld.DeepClone(); legacy["business"][key] = JValue.CreateNull();
            Reject<InvalidDataException>(() => NativeActorProjection.RejectLegacyPersonalMirrors(legacy), "business mirror " + key);
        }
        foreach (string key in new[] { "wounded", "searches", "minor", "major" })
        {
            var legacy = (JObject)cleanWorld.DeepClone(); legacy["scene"][key] = 0;
            Reject<InvalidDataException>(() => NativeActorProjection.RejectLegacyPersonalMirrors(legacy), "scene mirror " + key);
        }

        var store = Shell(); var remote = NativeActorState.CreateDefault();
        Func<string> existingProvider = () => throw new Exception("existing provider invoked");
        Func<string> registeredProvider = () => throw new Exception("registered provider invoked");
        var sticky = new StartingPerk { id = "sticky", source = "local-source", iconKey = "local-icon", nameProvider = existingProvider };
        store.startingPerks.Add(sticky); store.startingPerks.Add(new StartingPerk { id = "removed" });
        var registered = new StartingPerk { id = "coop_projection_probe", source = "registered-source", iconKey = "registered-icon", nameProvider = registeredProvider };
        var registry = (List<StartingPerk>)typeof(StartingPerkList).GetField("modPerks", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        registry.Add(registered);
        try
        {
            var projection = NativeActorProjection.Read(wire, run);
            Check(projection.Run == run, "authenticated run identity");
            var initialLocal = NativeActorState.Capture(store).Serialize(); var initialRemote = remote.Serialize();
            var localHealth = store.healthData; var localPerks = store.startingPerks; var localBlacklist = store.appraisalBlacklist;
            var localIncompatible = sticky.incompatiblePerks; var remoteHealth = remote.healthData; var remotePerks = remote.startingPerks;
            var prepared = projection.PrepareGuest(store, remote);
            Check(JToken.DeepEquals(initialLocal, NativeActorState.Capture(store).Serialize()) && JToken.DeepEquals(initialRemote, remote.Serialize()), "prepare has no semantic writes");
            prepared.Commit();
            Check(JToken.DeepEquals(partner.Serialize(), NativeActorState.Capture(store).Serialize()) && JToken.DeepEquals(host.Serialize(), remote.Serialize()), "Partner raw store and Host replica facts correct");
            Check(ReferenceEquals(store.healthData, localHealth) && ReferenceEquals(store.startingPerks, localPerks) && ReferenceEquals(store.appraisalBlacklist, localBlacklist), "local health/list identities stable");
            Check(ReferenceEquals(remote.healthData, remoteHealth) && ReferenceEquals(remote.startingPerks, remotePerks), "remote health/list identities stable");
            Check(ReferenceEquals(store.startingPerks[0], sticky) && ReferenceEquals(sticky.incompatiblePerks, localIncompatible), "existing perk and metadata list identity stable");
            Check(ReferenceEquals(sticky.nameProvider, existingProvider) && sticky.source == "local-source" && sticky.iconKey == "local-icon", "existing local runtime binding retained");
            var added = store.startingPerks[1];
            Check(!ReferenceEquals(added, registered) && ReferenceEquals(added.nameProvider, registeredProvider) && added.source == "registered-source" && added.iconKey == "registered-icon", "new perk binds registered metadata without aliasing definition");
            Check(registered.cost == 0 && registered.incompatiblePerks.Count == 0, "registry definition never mutated");
            Check(store.playerCash == 900 && store.saveSlotId == -1 && store.runID == "guest-local-descriptor", "shared fields and native descriptor unchanged");
            Reject<InvalidOperationException>(prepared.Commit, "duplicate commit");
            var perkEnumerator = store.startingPerks.GetEnumerator(); var incompatibleEnumerator = sticky.incompatiblePerks.GetEnumerator(); var blacklistEnumerator = store.appraisalBlacklist.GetEnumerator();
            projection.PrepareGuest(store, remote).Commit();
            Check(perkEnumerator.MoveNext() && incompatibleEnumerator.MoveNext() && blacklistEnumerator.MoveNext(), "unchanged snapshot does not invalidate UI list enumeration");
            var localBefore = NativeActorState.Capture(store).Serialize();
            var invalidated = projection.PrepareGuest(store, remote);
            remote.healthData = new HealthData();
            Reject<InvalidDataException>(invalidated.Commit, "stale remote binding");
            Check(JToken.DeepEquals(localBefore, NativeActorState.Capture(store).Serialize()), "bad remote target causes no local writes");
            remote.healthData = remoteHealth;
            invalidated = projection.PrepareGuest(store, remote); store.runID = "replacement-guest";
            Reject<InvalidDataException>(invalidated.Commit, "stale guest identity"); store.runID = "guest-local-descriptor";
            var crossThread = projection.PrepareGuest(store, remote);
            Exception threadError = null; var thread = new Thread(() => { try { crossThread.Commit(); } catch (Exception error) { threadError = error; } });
            thread.Start(); Check(thread.Join(TimeSpan.FromSeconds(5)) && threadError is InvalidOperationException, "commit belongs to preparing thread");
            Check(JToken.DeepEquals(localBefore, NativeActorState.Capture(store).Serialize()), "cross-thread rejection has no writes");
            remote.healthData = store.healthData;
            Reject<InvalidDataException>(() => projection.PrepareGuest(store, remote), "cross-actor health alias"); remote.healthData = remoteHealth;
            var oldBlacklist = store.appraisalBlacklist; store.appraisalBlacklist = sticky.incompatiblePerks;
            Reject<InvalidDataException>(() => projection.PrepareGuest(store, remote), "intra-actor mutable list alias"); store.appraisalBlacklist = oldBlacklist;
            var reversed = (JObject)wire.DeepClone(); var rows = (JArray)reversed["actors"]; var first = rows[0]; rows.RemoveAt(0); rows.Add(first);
            NativeActorProjection.Read(reversed, run).PrepareGuest(store, remote).Commit();
            Check(JToken.DeepEquals(partner.Serialize(), NativeActorState.Capture(store).Serialize()), "explicit slots independent of row order");
        }
        finally { registry.Remove(registered); }
        Console.WriteLine($"PASS {checks} actor projection wire, stable identity, local runtime binding, no-effect preflight and stale-target checks; raw guest field shell only, no gameplay or singleton.");
        return 0;
    }
}
