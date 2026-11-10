using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// Personal facts are independent of the world graph. Parsing never reads a
// singleton; publication keeps the guest's existing PlayerStore and UI objects.
internal sealed class NativeActorProjection
{
    internal const int Schema = 1;
    internal const int HostSlot = 0, PartnerSlot = 1;
    private static readonly HashSet<string> PersonalFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "playerName", "appraiserLevel", "appraisalProgress", "appraisalPromotionDeclined", "appraisalPromotionDay",
        "scavengingAttempts", "dumpingGroundMajorWoundImmunitySearchesLeft", "dumpingGroundMinorWoundImmunitySearchesLeft",
        "healthData", "startingPerks", "appraisalBlacklist"
    };
    private readonly NativeActorState host, partner;
    internal Guid Run { get; }
    private NativeActorProjection(Guid run, NativeActorState host, NativeActorState partner)
    { Run = run; this.host = host; this.partner = partner; }

    internal static bool OwnsPlayerField(string name) => name != null && PersonalFields.Contains(name);

    internal static JObject Capture(Guid run, NativeActorState host, NativeActorState partner)
    {
        if (run == Guid.Empty || host == null || partner == null) throw Invalid("Missing actor authority");
        return new JObject
        {
            ["schema"] = Schema, ["run"] = run.ToString("D"),
            ["actors"] = new JArray(
                new JObject { ["slot"] = HostSlot, ["state"] = host.Serialize() },
                new JObject { ["slot"] = PartnerSlot, ["state"] = partner.Serialize() })
        };
    }

    internal static NativeActorProjection Read(JToken value, Guid expectedRun)
    {
        if (expectedRun == Guid.Empty || value is not JObject envelope || envelope.Count != 3 ||
            Integer(envelope["schema"]) != Schema || envelope["run"]?.Type != JTokenType.String ||
            !Guid.TryParse((string)envelope["run"], out var run) || run != expectedRun ||
            envelope["actors"] is not JArray actors || actors.Count != 2)
            throw Invalid("Invalid actor envelope");
        var slots = new NativeActorState[2];
        foreach (var entry in actors)
        {
            if (entry is not JObject row || row.Count != 2 || row["state"] == null) throw Invalid("Invalid actor row");
            int slot = Integer(row["slot"]);
            if (slot < HostSlot || slot > PartnerSlot || slots[slot] != null) throw Invalid("Duplicate or unknown actor slot");
            slots[slot] = NativeActorState.Deserialize(row["state"]);
        }
        return new NativeActorProjection(run, slots[HostSlot], slots[PartnerSlot]);
    }

    // Call with the complete new-schema snapshot during preflight. Accepting
    // both buses would let a later reflection pass overwrite the local actor.
    internal static void RejectLegacyPersonalMirrors(JObject snapshot)
    {
        if (snapshot == null) throw Invalid("Missing world snapshot");
        if (snapshot["world"] is JObject world)
            foreach (var property in world.Properties())
                if (OwnsPlayerField(property.Name)) throw Invalid("Legacy world actor field: " + property.Name);
        if (snapshot["business"] is JObject business && (business["health"] != null || business["startingPerks"] != null))
            throw Invalid("Legacy business actor mirror");
        if (snapshot["scene"] is JObject scene)
            foreach (string key in new[] { "wounded", "searches", "minor", "major" })
                if (scene[key] != null) throw Invalid("Legacy personal scene query: " + key);
    }

    // Guest local raw fields represent Partner. Host is retained separately for
    // participant/status views; it never replaces the singleton or runs rules.
    internal Prepared PrepareGuest(PlayerStore localGuestStore, NativeActorState remoteHostReplica)
    {
        return new Prepared(this, localGuestStore, remoteHostReplica);
    }

    internal sealed class Prepared
    {
        private readonly Target local, remote;
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        private bool committed;
        internal Prepared(NativeActorProjection projection, PlayerStore localGuestStore, NativeActorState remoteHostReplica)
        {
            if (projection == null || localGuestStore == null || remoteHostReplica == null) throw new ArgumentNullException("actor target");
            NativeActorState.Capture(localGuestStore); // Validate existing fields, no native methods.
            remoteHostReplica.Validate();
            local = new Target(localGuestStore, null, projection.partner);
            remote = new Target(null, remoteHostReplica, projection.host);
            var owned = new HashSet<object>();
            foreach (var value in local.MutableObjects())
                if (!owned.Add(value)) throw Invalid("Local actor fields alias mutable state");
            foreach (var value in remote.MutableObjects())
                if (!owned.Add(value)) throw Invalid("Actor targets share mutable state");
        }

        internal void Commit()
        {
            if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Actor projection belongs to its preparing game thread.");
            if (committed) throw new InvalidOperationException("Actor projection was already committed.");
            // Check BOTH destinations before touching either. Reserving capacity
            // changes no semantic contents and invokes no gameplay/UI callback.
            local.CheckBindings(); remote.CheckBindings();
            local.Reserve(); remote.Reserve();
            local.Apply(); remote.Apply();
            committed = true;
        }
    }

    private sealed class Target
    {
        private readonly PlayerStore store;
        private readonly NativeActorState state, facts;
        private readonly string storeRun;
        private readonly int storeSlot;
        private readonly HealthData health;
        private readonly List<StartingPerk> perks;
        private readonly List<string> blacklist;
        private readonly StartingPerk[] originalPerks, desiredPerks;
        private readonly List<string>[] originalIncompatibles;
        private readonly string[] originalIds;
        private readonly PerkWrite[] writes;

        internal Target(PlayerStore store, NativeActorState state, NativeActorState facts)
        {
            this.store = store; this.state = state; this.facts = facts;
            if (store != null) { storeRun = store.runID; storeSlot = store.saveSlotId; }
            health = store != null ? store.healthData : state.healthData;
            perks = store != null ? store.startingPerks : state.startingPerks;
            blacklist = store != null ? store.appraisalBlacklist : state.appraisalBlacklist;
            originalPerks = perks.ToArray();
            originalIncompatibles = new List<string>[originalPerks.Length];
            originalIds = new string[originalPerks.Length];
            var byId = new Dictionary<string, StartingPerk>(StringComparer.Ordinal);
            for (int i = 0; i < originalPerks.Length; i++)
            {
                var perk = originalPerks[i];
                originalIncompatibles[i] = perk.incompatiblePerks;
                originalIds[i] = perk.id;
                byId.Add(perk.id, perk);
            }
            desiredPerks = new StartingPerk[facts.startingPerks.Count];
            writes = new PerkWrite[desiredPerks.Length];
            for (int i = 0; i < writes.Length; i++)
            {
                var source = facts.startingPerks[i];
                if (!byId.TryGetValue(source.id, out var target))
                {
                    target = new StartingPerk { id = source.id };
                    // Registry lookup only. No perk factory, provider, UI or
                    // new-game effect runs while preparing a replica.
                    var definition = StartingPerkList.GetModPerk(source.id);
                    if (definition != null)
                    {
                        target.source = definition.source; target.iconKey = definition.iconKey;
                        target.nameProvider = definition.nameProvider; target.descriptionProvider = definition.descriptionProvider;
                    }
                }
                desiredPerks[i] = target;
                writes[i] = new PerkWrite(target, source);
            }
        }

        internal IEnumerable<object> MutableObjects()
        {
            yield return health; yield return perks; yield return blacklist;
            foreach (var perk in originalPerks) { yield return perk; yield return perk.incompatiblePerks; }
        }

        internal void CheckBindings()
        {
            if (store != null && (store.runID != storeRun || store.saveSlotId != storeSlot))
                throw Invalid("Guest store changed during actor preflight");
            if (!ReferenceEquals(health, store != null ? store.healthData : state.healthData) ||
                !ReferenceEquals(perks, store != null ? store.startingPerks : state.startingPerks) ||
                !ReferenceEquals(blacklist, store != null ? store.appraisalBlacklist : state.appraisalBlacklist) ||
                perks.Count != originalPerks.Length) throw Invalid("Actor binding changed during preflight");
            for (int i = 0; i < originalPerks.Length; i++)
                if (!ReferenceEquals(perks[i], originalPerks[i]) || perks[i].id != originalIds[i] ||
                    !ReferenceEquals(perks[i].incompatiblePerks, originalIncompatibles[i]))
                    throw Invalid("Actor perk binding changed during preflight");
        }

        internal void Reserve()
        {
            if (perks.Capacity < desiredPerks.Length) perks.Capacity = desiredPerks.Length;
            if (blacklist.Capacity < facts.appraisalBlacklist.Count) blacklist.Capacity = facts.appraisalBlacklist.Count;
            foreach (var write in writes) write.Reserve();
        }

        internal void Apply()
        {
            if (store != null)
            {
                store.playerName = facts.playerName; store.appraiserLevel = facts.appraiserLevel;
                store.appraisalProgress = facts.appraisalProgress; store.appraisalPromotionDeclined = facts.appraisalPromotionDeclined;
                store.appraisalPromotionDay = facts.appraisalPromotionDay; store.scavengingAttempts = facts.scavengingAttempts;
                store.dumpingGroundMajorWoundImmunitySearchesLeft = facts.dumpingGroundMajorWoundImmunitySearchesLeft;
                store.dumpingGroundMinorWoundImmunitySearchesLeft = facts.dumpingGroundMinorWoundImmunitySearchesLeft;
            }
            else
            {
                state.playerName = facts.playerName; state.appraiserLevel = facts.appraiserLevel;
                state.appraisalProgress = facts.appraisalProgress; state.appraisalPromotionDeclined = facts.appraisalPromotionDeclined;
                state.appraisalPromotionDay = facts.appraisalPromotionDay; state.scavengingAttempts = facts.scavengingAttempts;
                state.dumpingGroundMajorWoundImmunitySearchesLeft = facts.dumpingGroundMajorWoundImmunitySearchesLeft;
                state.dumpingGroundMinorWoundImmunitySearchesLeft = facts.dumpingGroundMinorWoundImmunitySearchesLeft;
            }
            var source = facts.healthData;
            health.daysSinceNicotineConsumption = source.daysSinceNicotineConsumption;
            health.daysSinceAlcoholConsumption = source.daysSinceAlcoholConsumption;
            health.daysSinceNarcoticConsumption = source.daysSinceNarcoticConsumption;
            health.daysSinceNarcoticRelief = source.daysSinceNarcoticRelief;
            health.daysSinceSentinelInjection = source.daysSinceSentinelInjection;
            health.sentinelPool = source.sentinelPool; health.woundState = source.woundState;
            health.isWoundStable = source.isWoundStable; health.isWoundedFresh = source.isWoundedFresh;
            health.wentToAfterhoursLastNight = source.wentToAfterhoursLastNight;
            health.luck = source.luck; health.liquidLuckDaysLeft = source.liquidLuckDaysLeft;
            foreach (var write in writes) write.Apply();
            Replace(perks, desiredPerks); Replace(blacklist, facts.appraisalBlacklist);
        }
    }

    private sealed class PerkWrite
    {
        private readonly StartingPerk target, facts;
        internal PerkWrite(StartingPerk target, StartingPerk facts) { this.target = target; this.facts = facts; }
        internal void Reserve()
        { if (target.incompatiblePerks.Capacity < facts.incompatiblePerks.Count) target.incompatiblePerks.Capacity = facts.incompatiblePerks.Count; }
        internal void Apply()
        {
            target.cost = facts.cost; target.maxSlot = facts.maxSlot; target.type = facts.type;
            target.mindEditable = facts.mindEditable; target.rawName = facts.rawName; target.rawDescription = facts.rawDescription;
            Replace(target.incompatiblePerks, facts.incompatiblePerks);
            // Existing local runtime bindings and object identity are retained.
        }
    }

    private static void Replace<T>(List<T> target, IList<T> source)
    {
        bool same = target.Count == source.Count;
        for (int i = 0; same && i < source.Count; i++) same = EqualityComparer<T>.Default.Equals(target[i], source[i]);
        if (same) return; // Avoid invalidating native UI enumerators on identical snapshots.
        target.Clear();
        for (int i = 0; i < source.Count; i++) target.Add(source[i]);
    }

    private static int Integer(JToken value)
    {
        if (value?.Type != JTokenType.Integer || !int.TryParse(value.ToString(Newtonsoft.Json.Formatting.None),
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int result)) throw Invalid("Invalid actor envelope integer");
        return result;
    }
    private static InvalidDataException Invalid(string message) => new InvalidDataException(message);
}
