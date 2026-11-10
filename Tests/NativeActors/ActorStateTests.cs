using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

static class ActorStateTests
{
    private static int checks;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL " + name);
        checks++;
    }
    private static void Rejected(Action action, string name)
    {
        try { action(); } catch (InvalidDataException) { checks++; return; }
        throw new Exception("Not rejected: " + name);
    }
    private static void MutateReject(JObject source, Action<JObject> change, string name)
    {
        var test = (JObject)source.DeepClone();
        change(test);
        Rejected(() => NativeActorState.Deserialize(test), name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run()
    {
        var host = NativeActorState.CreateDefault();
        var partner = NativeActorState.CreateDefault();
        var nativeHealth = new HealthData();
        Check(host.playerName == null && host.appraisalPromotionDay == -1 && host.appraiserLevel == 0 && host.appraisalProgress == 0, "native scalar defaults");
        Check(host.healthData.daysSinceNarcoticRelief == nativeHealth.daysSinceNarcoticRelief && host.healthData.daysSinceNarcoticRelief == 999 && host.healthData.isWoundStable, "native health defaults");
        Check(host.startingPerks.Count == 0 && host.appraisalBlacklist.Count == 0 && !host.appraisalPromotionDeclined && host.scavengingAttempts == 0 && host.dumpingGroundMajorWoundImmunitySearchesLeft == 0 && host.dumpingGroundMinorWoundImmunitySearchesLeft == 0, "unselected companion defaults");
        Check(!ReferenceEquals(host.healthData, partner.healthData) && !ReferenceEquals(host.startingPerks, partner.startingPerks) && !ReferenceEquals(host.appraisalBlacklist, partner.appraisalBlacklist), "actor defaults isolated");
        var defaultJson = host.Serialize();
        Check(defaultJson["playerName"].Type == JTokenType.Null, "default null player name uses canonical JSON null");
        Check(JToken.DeepEquals(defaultJson, JObject.Parse(defaultJson.ToString())), "default host survives direct JSON text comparison before typed decoding");
        Check(JToken.DeepEquals(defaultJson, NativeActorState.Deserialize(JObject.Parse(defaultJson.ToString())).Serialize()), "default host nullable name typed text roundtrip");
        var nullablePerks = host.Clone();
        nullablePerks.startingPerks.Add(new StartingPerk { id = "nullable-fixture", incompatiblePerks = new List<string>(), rawName = null, rawDescription = null });
        var nullableJson = nullablePerks.Serialize();
        Check(nullableJson["startingPerks"][0]["rawName"].Type == JTokenType.Null && nullableJson["startingPerks"][0]["rawDescription"].Type == JTokenType.Null,
            "nullable perk definition strings use canonical JSON null");
        Check(JToken.DeepEquals(nullableJson, JObject.Parse(nullableJson.ToString())), "null actor and perk fields survive strict serialized-token comparison");
        Check(JToken.DeepEquals(nullableJson, NativeActorState.Deserialize(JObject.Parse(nullableJson.ToString())).Serialize()), "null actor and perk fields survive typed text roundtrip");
        host.playerName = "角色甲";
        host.appraiserLevel = 2;
        host.appraisalProgress = 12.125;
        host.appraisalPromotionDay = 21;
        host.appraisalPromotionDeclined = true;
        host.scavengingAttempts = 4;
        host.dumpingGroundMajorWoundImmunitySearchesLeft = 20;
        host.dumpingGroundMinorWoundImmunitySearchesLeft = 10;
        host.healthData.daysSinceNicotineConsumption = -5;
        host.healthData.daysSinceAlcoholConsumption = -7;
        host.healthData.daysSinceNarcoticConsumption = -9;
        host.healthData.daysSinceNarcoticRelief = -10;
        host.healthData.daysSinceSentinelInjection = 13;
        host.healthData.sentinelPool = 18;
        host.healthData.woundState = 14;
        host.healthData.isWoundStable = false;
        host.healthData.isWoundedFresh = true;
        host.healthData.wentToAfterhoursLastNight = true;
        host.healthData.luck = -5;
        host.healthData.liquidLuckDaysLeft = 12;
        host.appraisalBlacklist.Add("FOOD");
        host.startingPerks.Add(new StartingPerk
        {
            id = "unlucky", cost = -1, maxSlot = 3, type = StartingPerk.StartingPerkType.NEGATIVE,
            mindEditable = true, incompatiblePerks = new List<string> { "lucky" },
            rawName = null, rawDescription = "测试", source = "runtime", iconKey = "runtime/icon",
            nameProvider = () => throw new Exception("must not invoke provider"),
            descriptionProvider = () => throw new Exception("must not invoke provider")
        });
        Check(partner.healthData.woundState == 0 && partner.startingPerks.Count == 0 && partner.appraisalBlacklist.Count == 0, "host changes do not affect partner");
        var json = host.Serialize();
        Check(json.Count == 12 && ((JObject)json["healthData"]).Count == 12 && ((JObject)json["startingPerks"][0]).Count == 8, "explicit field-only shape");
        Check(!json.ToString().Contains("provider", StringComparison.OrdinalIgnoreCase) && json["playerCash"] == null && json["visitLeftTonight"] == null && json["storeService"] == null && json["selectedBackpack"] == null, "no delegates UI or shared state serialized");
        var restored = NativeActorState.Deserialize(JObject.Parse(json.ToString()));
        Check(JToken.DeepEquals(json, restored.Serialize()), "all personal and native metadata round trip");
        Check(restored.startingPerks[0].nameProvider == null && restored.startingPerks[0].descriptionProvider == null && restored.startingPerks[0].source == null && restored.startingPerks[0].iconKey == null, "deserialize does not hydrate runtime callbacks");
        Check(!ReferenceEquals(restored.healthData, host.healthData) && !ReferenceEquals(restored.startingPerks[0], host.startingPerks[0]) && !ReferenceEquals(restored.startingPerks[0].incompatiblePerks, host.startingPerks[0].incompatiblePerks), "round-trip nested isolation");
        var clone = host.Clone();
        Check(ReferenceEquals(clone.startingPerks[0].nameProvider, host.startingPerks[0].nameProvider), "runtime clone retains binding without executing it");
        clone.healthData.luck = 99;
        clone.startingPerks[0].incompatiblePerks.Add("other");
        clone.startingPerks[0].rawDescription = "changed";
        clone.appraisalBlacklist.Add("TOOL");
        Check(host.healthData.luck == -5 && host.startingPerks[0].incompatiblePerks.Count == 1 && host.startingPerks[0].rawDescription == "测试" && host.appraisalBlacklist.Count == 1, "clone deep mutation isolation");
        restored.RestoreTo(clone);
        Check(JToken.DeepEquals(clone.Serialize(), json) && !ReferenceEquals(clone.healthData, restored.healthData), "restore accessor-owned state");
        var before = clone.Serialize();
        restored.appraiserLevel = -1;
        Rejected(() => clone.CopyFrom(restored), "invalid restore");
        Check(JToken.DeepEquals(clone.Serialize(), before), "invalid restore leaves target unchanged");
        MutateReject(json, j => j.Remove("healthData"), "missing health");
        MutateReject(json, j => j["schema"] = 2, "unknown schema");
        MutateReject(json, j => j["playerCash"] = 600, "shared field injection");
        MutateReject(json, j => j["appraiserLevel"] = 4, "rank range");
        MutateReject(json, j => j["appraiserLevel"] = "2", "numeric string");
        MutateReject(json, j => j["scavengingAttempts"] = 1.5, "fractional counter");
        MutateReject(json, j => j["scavengingAttempts"] = long.MaxValue, "integer overflow");
        MutateReject(json, j => j["appraisalPromotionDeclined"] = 1, "boolean type");
        MutateReject(json, j => j["appraisalProgress"] = double.NaN, "NaN");
        MutateReject(json, j => j["appraisalProgress"] = double.PositiveInfinity, "infinity");
        MutateReject(json, j => j["appraisalProgress"] = -1, "negative progress");
        MutateReject(json, j => j["appraisalPromotionDay"] = -2, "invalid sentinel");
        MutateReject(json, j => j["healthData"]["woundState"] = -1, "negative wound");
        MutateReject(json, j => j["healthData"]["sentinelPool"] = -1, "negative sentinel pool");
        MutateReject(json, j => j["healthData"]["liquidLuckDaysLeft"] = -1, "negative luck duration");
        MutateReject(json, j => j["startingPerks"][0]["type"] = 3, "perk enum");
        MutateReject(json, j => j["startingPerks"][0]["id"] = "", "empty perk id");
        MutateReject(json, j => j["startingPerks"][0]["nameProvider"] = "fake", "provider injection");
        MutateReject(json, j => ((JArray)j["startingPerks"]).Add(j["startingPerks"][0].DeepClone()), "duplicate perk");
        MutateReject(json, j => j["appraisalBlacklist"] = new JArray(new string[NativeActorState.MaximumBlacklist + 1].Select(_ => "x")), "bounded list");
        MutateReject(json, j => j["appraisalBlacklist"] = new JArray(1), "invalid list element");
        MutateReject(json, j => j["playerName"] = new string('x', 16385), "bounded text");
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(NativeActorState.Deserialize(json).appraisalProgress == 12.125, "culture independent floating-point read");
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Console.WriteLine($"PASS {checks} native actor field, isolation, persistence, and malformed-input checks; no PlayerStore constructor, singleton access, engine/UI method, or save I/O.");
        return 0;
    }
}
