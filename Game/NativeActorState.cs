using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// One fixed coop actor, not another PlayerStore. Native field accessors own the
// live instance; checkpoints and Capture are detached copies of personal data.
// Inventory roots and actor identity belong to the containing coop run.
internal sealed class NativeActorState
{
    internal const int Schema = 1;
    internal const int MaximumPerks = 256;
    internal const int MaximumBlacklist = 1024;
    private const int MaximumIdLength = 256;
    private const int MaximumTextLength = 16384;

    internal string playerName;
    internal int appraiserLevel;
    internal double appraisalProgress;
    internal bool appraisalPromotionDeclined;
    internal int appraisalPromotionDay = -1;
    internal int scavengingAttempts;
    internal int dumpingGroundMajorWoundImmunitySearchesLeft;
    internal int dumpingGroundMinorWoundImmunitySearchesLeft;
    internal HealthData healthData = new HealthData();
    internal List<StartingPerk> startingPerks = new List<StartingPerk>();
    internal List<string> appraisalBlacklist = new List<string>();

    // These are field/constructor defaults, before new-game effects. In
    // particular, this does not select perks, apply withdrawal, or award stock.
    internal static NativeActorState CreateDefault() => new NativeActorState();

    internal static NativeActorState Capture(PlayerStore store)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        return new NativeActorState
        {
            playerName = store.playerName,
            appraiserLevel = store.appraiserLevel,
            appraisalProgress = store.appraisalProgress,
            appraisalPromotionDeclined = store.appraisalPromotionDeclined,
            appraisalPromotionDay = store.appraisalPromotionDay,
            scavengingAttempts = store.scavengingAttempts,
            dumpingGroundMajorWoundImmunitySearchesLeft = store.dumpingGroundMajorWoundImmunitySearchesLeft,
            dumpingGroundMinorWoundImmunitySearchesLeft = store.dumpingGroundMinorWoundImmunitySearchesLeft,
            healthData = store.healthData,
            startingPerks = store.startingPerks,
            appraisalBlacklist = store.appraisalBlacklist
        }.Clone();
    }

    internal NativeActorState Clone()
    {
        Validate();
        var copy = new NativeActorState
        {
            playerName = playerName,
            appraiserLevel = appraiserLevel,
            appraisalProgress = appraisalProgress,
            appraisalPromotionDeclined = appraisalPromotionDeclined,
            appraisalPromotionDay = appraisalPromotionDay,
            scavengingAttempts = scavengingAttempts,
            dumpingGroundMajorWoundImmunitySearchesLeft = dumpingGroundMajorWoundImmunitySearchesLeft,
            dumpingGroundMinorWoundImmunitySearchesLeft = dumpingGroundMinorWoundImmunitySearchesLeft,
            healthData = CloneHealth(healthData),
            appraisalBlacklist = new List<string>(appraisalBlacklist)
        };
        foreach (var perk in startingPerks) copy.startingPerks.Add(ClonePerk(perk));
        return copy;
    }

    // Restore into the state held by the field accessor, never into a swapped
    // singleton. Validate and allocate every nested value before publishing.
    internal void RestoreTo(NativeActorState target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        target.CopyFrom(this);
    }

    internal void CopyFrom(NativeActorState source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var copy = source.Clone();
        playerName = copy.playerName;
        appraiserLevel = copy.appraiserLevel;
        appraisalProgress = copy.appraisalProgress;
        appraisalPromotionDeclined = copy.appraisalPromotionDeclined;
        appraisalPromotionDay = copy.appraisalPromotionDay;
        scavengingAttempts = copy.scavengingAttempts;
        dumpingGroundMajorWoundImmunitySearchesLeft = copy.dumpingGroundMajorWoundImmunitySearchesLeft;
        dumpingGroundMinorWoundImmunitySearchesLeft = copy.dumpingGroundMinorWoundImmunitySearchesLeft;
        healthData = copy.healthData;
        startingPerks = copy.startingPerks;
        appraisalBlacklist = copy.appraisalBlacklist;
    }

    internal void Validate()
    {
        CheckText(playerName, MaximumTextLength, true, "playerName");
        CheckRange(appraiserLevel, 0, 3, "appraiserLevel");
        if (double.IsNaN(appraisalProgress) || double.IsInfinity(appraisalProgress) || appraisalProgress < 0)
            throw Invalid("appraisalProgress");
        CheckRange(appraisalPromotionDay, -1, int.MaxValue, "appraisalPromotionDay");
        CheckRange(scavengingAttempts, 0, int.MaxValue, "scavengingAttempts");
        CheckRange(dumpingGroundMajorWoundImmunitySearchesLeft, 0, int.MaxValue, "major wound immunity");
        CheckRange(dumpingGroundMinorWoundImmunitySearchesLeft, 0, int.MaxValue, "minor wound immunity");
        if (healthData == null) throw Invalid("healthData");
        // Consumption/relief counters are signed: native consumables grant
        // future coverage using negative days. Wounds may exceed ten until sleep.
        CheckRange(healthData.daysSinceSentinelInjection, 0, int.MaxValue, "sentinel injection days");
        CheckRange(healthData.sentinelPool, 0, int.MaxValue, "sentinelPool");
        CheckRange(healthData.woundState, 0, int.MaxValue, "woundState");
        CheckRange(healthData.liquidLuckDaysLeft, 0, int.MaxValue, "liquidLuckDaysLeft");
        CheckStrings(appraisalBlacklist, MaximumBlacklist, "appraisalBlacklist");
        if (startingPerks == null || startingPerks.Count > MaximumPerks) throw Invalid("startingPerks");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var perk in startingPerks)
        {
            if (perk == null) throw Invalid("null perk");
            CheckText(perk.id, MaximumIdLength, false, "perk id");
            if (!ids.Add(perk.id)) throw Invalid("duplicate perk id");
            CheckRange((int)perk.type, 0, 2, "perk type");
            CheckStrings(perk.incompatiblePerks, MaximumPerks, "incompatiblePerks");
            CheckText(perk.rawName, MaximumTextLength, true, "perk rawName");
            CheckText(perk.rawDescription, MaximumTextLength, true, "perk rawDescription");
            // cost and maxSlot are signed definition metadata, not balances.
        }
    }

    internal JObject Serialize()
    {
        Validate();
        var perks = new JArray();
        foreach (var perk in startingPerks)
            perks.Add(new JObject
            {
                ["id"] = perk.id, ["cost"] = perk.cost, ["maxSlot"] = perk.maxSlot,
                ["incompatiblePerks"] = new JArray(perk.incompatiblePerks),
                ["type"] = (int)perk.type, ["mindEditable"] = perk.mindEditable,
                ["rawName"] = NullableText(perk.rawName), ["rawDescription"] = NullableText(perk.rawDescription)
            });
        var health = healthData;
        return new JObject
        {
            ["schema"] = Schema,
            ["playerName"] = NullableText(playerName),
            ["appraiserLevel"] = appraiserLevel,
            ["appraisalProgress"] = appraisalProgress,
            ["appraisalPromotionDeclined"] = appraisalPromotionDeclined,
            ["appraisalPromotionDay"] = appraisalPromotionDay,
            ["scavengingAttempts"] = scavengingAttempts,
            ["dumpingGroundMajorWoundImmunitySearchesLeft"] = dumpingGroundMajorWoundImmunitySearchesLeft,
            ["dumpingGroundMinorWoundImmunitySearchesLeft"] = dumpingGroundMinorWoundImmunitySearchesLeft,
            ["appraisalBlacklist"] = new JArray(appraisalBlacklist),
            ["startingPerks"] = perks,
            ["healthData"] = new JObject
            {
                ["daysSinceNicotineConsumption"] = health.daysSinceNicotineConsumption,
                ["daysSinceAlcoholConsumption"] = health.daysSinceAlcoholConsumption,
                ["daysSinceNarcoticConsumption"] = health.daysSinceNarcoticConsumption,
                ["daysSinceNarcoticRelief"] = health.daysSinceNarcoticRelief,
                ["daysSinceSentinelInjection"] = health.daysSinceSentinelInjection,
                ["sentinelPool"] = health.sentinelPool,
                ["woundState"] = health.woundState,
                ["isWoundStable"] = health.isWoundStable,
                ["isWoundedFresh"] = health.isWoundedFresh,
                ["wentToAfterhoursLastNight"] = health.wentToAfterhoursLastNight,
                ["luck"] = health.luck,
                ["liquidLuckDaysLeft"] = health.liquidLuckDaysLeft
            }
        };
    }

    // JValue(string null) has Type=String in memory, but JSON parsing returns
    // Type=Null. Emit canonical nulls so an unchanged actor survives a real
    // wire/checkpoint text roundtrip under strict full-field comparison.
    private static JToken NullableText(string value) => value == null ? JValue.CreateNull() : new JValue(value);

    internal static NativeActorState Deserialize(JToken value)
    {
        var data = Object(value, "schema playerName appraiserLevel appraisalProgress appraisalPromotionDeclined appraisalPromotionDay scavengingAttempts dumpingGroundMajorWoundImmunitySearchesLeft dumpingGroundMinorWoundImmunitySearchesLeft appraisalBlacklist startingPerks healthData");
        if (Integer(data, "schema") != Schema) throw Invalid("schema");
        var health = Object(data["healthData"], "daysSinceNicotineConsumption daysSinceAlcoholConsumption daysSinceNarcoticConsumption daysSinceNarcoticRelief daysSinceSentinelInjection sentinelPool woundState isWoundStable isWoundedFresh wentToAfterhoursLastNight luck liquidLuckDaysLeft");
        var result = new NativeActorState
        {
            playerName = Text(data, "playerName", true),
            appraiserLevel = Integer(data, "appraiserLevel"),
            appraisalProgress = Number(data, "appraisalProgress"),
            appraisalPromotionDeclined = Boolean(data, "appraisalPromotionDeclined"),
            appraisalPromotionDay = Integer(data, "appraisalPromotionDay"),
            scavengingAttempts = Integer(data, "scavengingAttempts"),
            dumpingGroundMajorWoundImmunitySearchesLeft = Integer(data, "dumpingGroundMajorWoundImmunitySearchesLeft"),
            dumpingGroundMinorWoundImmunitySearchesLeft = Integer(data, "dumpingGroundMinorWoundImmunitySearchesLeft"),
            appraisalBlacklist = Strings(data["appraisalBlacklist"], MaximumBlacklist, "appraisalBlacklist"),
            healthData = new HealthData
            {
                daysSinceNicotineConsumption = Integer(health, "daysSinceNicotineConsumption"),
                daysSinceAlcoholConsumption = Integer(health, "daysSinceAlcoholConsumption"),
                daysSinceNarcoticConsumption = Integer(health, "daysSinceNarcoticConsumption"),
                daysSinceNarcoticRelief = Integer(health, "daysSinceNarcoticRelief"),
                daysSinceSentinelInjection = Integer(health, "daysSinceSentinelInjection"),
                sentinelPool = Integer(health, "sentinelPool"),
                woundState = Integer(health, "woundState"),
                isWoundStable = Boolean(health, "isWoundStable"),
                isWoundedFresh = Boolean(health, "isWoundedFresh"),
                wentToAfterhoursLastNight = Boolean(health, "wentToAfterhoursLastNight"),
                luck = Integer(health, "luck"),
                liquidLuckDaysLeft = Integer(health, "liquidLuckDaysLeft")
            }
        };
        if (data["startingPerks"] is not JArray perks || perks.Count > MaximumPerks) throw Invalid("startingPerks");
        foreach (var valuePerk in perks)
        {
            var perk = Object(valuePerk, "id cost maxSlot incompatiblePerks type mindEditable rawName rawDescription");
            result.startingPerks.Add(new StartingPerk
            {
                id = Text(perk, "id", false),
                cost = Integer(perk, "cost"),
                maxSlot = Integer(perk, "maxSlot"),
                incompatiblePerks = Strings(perk["incompatiblePerks"], MaximumPerks, "incompatiblePerks"),
                type = (StartingPerk.StartingPerkType)Integer(perk, "type"),
                mindEditable = Boolean(perk, "mindEditable"),
                rawName = Text(perk, "rawName", true),
                rawDescription = Text(perk, "rawDescription", true)
            });
        }
        result.Validate();
        return result;
    }

    private static HealthData CloneHealth(HealthData value) => new HealthData
    {
        daysSinceNicotineConsumption = value.daysSinceNicotineConsumption,
        daysSinceAlcoholConsumption = value.daysSinceAlcoholConsumption,
        daysSinceNarcoticConsumption = value.daysSinceNarcoticConsumption,
        daysSinceNarcoticRelief = value.daysSinceNarcoticRelief,
        daysSinceSentinelInjection = value.daysSinceSentinelInjection,
        sentinelPool = value.sentinelPool,
        woundState = value.woundState,
        isWoundStable = value.isWoundStable,
        isWoundedFresh = value.isWoundedFresh,
        wentToAfterhoursLastNight = value.wentToAfterhoursLastNight,
        luck = value.luck,
        liquidLuckDaysLeft = value.liquidLuckDaysLeft
    };

    private static StartingPerk ClonePerk(StartingPerk value) => new StartingPerk
    {
        id = value.id, cost = value.cost, maxSlot = value.maxSlot,
        incompatiblePerks = new List<string>(value.incompatiblePerks),
        type = value.type, mindEditable = value.mindEditable,
        rawName = value.rawName, rawDescription = value.rawDescription,
        // Runtime-only bindings are retained on an in-memory copy. They never
        // enter JSON and Deserialize never runs a provider or a perk factory.
        source = value.source, iconKey = value.iconKey,
        nameProvider = value.nameProvider, descriptionProvider = value.descriptionProvider
    };

    private static JObject Object(JToken value, string keys)
    {
        if (value is not JObject data) throw Invalid("object");
        var required = new HashSet<string>(keys.Split(' '), StringComparer.Ordinal);
        foreach (var property in data.Properties())
            if (!required.Remove(property.Name)) throw Invalid("unexpected field " + property.Name);
        if (required.Count != 0) throw Invalid("missing field");
        return data;
    }

    private static int Integer(JObject data, string key)
    {
        var value = data[key];
        if (value?.Type != JTokenType.Integer ||
            !int.TryParse(value.ToString(Newtonsoft.Json.Formatting.None), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int result))
            throw Invalid(key);
        return result;
    }

    private static double Number(JObject data, string key)
    {
        var value = data[key];
        if (value == null || value.Type is not (JTokenType.Integer or JTokenType.Float) ||
            !double.TryParse(value.ToString(Newtonsoft.Json.Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ||
            double.IsNaN(result) || double.IsInfinity(result)) throw Invalid(key);
        return result;
    }

    private static bool Boolean(JObject data, string key)
    {
        if (data[key]?.Type != JTokenType.Boolean) throw Invalid(key);
        return (bool)data[key];
    }

    private static string Text(JObject data, string key, bool nullable)
    {
        var value = data[key];
        if (nullable && value?.Type == JTokenType.Null) return null;
        if (value?.Type != JTokenType.String) throw Invalid(key);
        return (string)value;
    }

    private static List<string> Strings(JToken value, int maximum, string name)
    {
        if (value is not JArray array || array.Count > maximum) throw Invalid(name);
        var result = new List<string>(array.Count);
        foreach (var entry in array)
        {
            if (entry.Type != JTokenType.String) throw Invalid(name);
            result.Add((string)entry);
        }
        CheckStrings(result, maximum, name);
        return result;
    }

    private static void CheckStrings(List<string> values, int maximum, string name)
    {
        if (values == null || values.Count > maximum) throw Invalid(name);
        foreach (string value in values) CheckText(value, MaximumIdLength, false, name);
    }

    private static void CheckText(string value, int maximum, bool nullable, string name)
    {
        if (value == null ? !nullable : (value.Length > maximum || (!nullable && value.Length == 0)))
            throw Invalid(name);
    }

    private static void CheckRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum) throw Invalid(name);
    }

    private static InvalidDataException Invalid(string name) => new InvalidDataException("Invalid native actor " + name);
}
