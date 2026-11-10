using System.Collections.Generic;
using HarmonyLib;

namespace ProbablyCoop;

// Classify the queried effect, not the entire local list: partner UI and
// actions retain personal perks, but shared-only shop configuration is the
// host's one canonical configuration on BOTH peers. This never runs an effect.
internal static class NativeActorPerkQueries
{
    internal static bool TryShared(string perkId, out StartingPerk perk)
    {
        perk = null;
        if (NativeActorPartnerSetup.Classify(perkId) != NativeActorPartnerSetup.PerkScope.SharedShop) return false;
        List<StartingPerk> source;
        var authority = NativeActorSession.Current;
        if (authority != null) source = authority.Store.startingPerks; // Deliberate raw Host field.
        else
        {
            var guest = NativeWorld.Guest;
            if (guest?.HasActorProjection != true || guest.RemoteHostActor == null) return false;
            source = guest.RemoteHostActor.startingPerks;
        }
        foreach (var candidate in source)
            if (candidate.id == perkId) { perk = candidate; break; }
        return true;
    }
}

[HarmonyPatch(typeof(StartingPerk), nameof(StartingPerk.IsPerkActive))]
internal static class NativeActorSharedPerkActive
{
    private static bool Prefix(string perkId, ref bool __result)
    {
        if (!NativeActorPerkQueries.TryShared(perkId, out var perk)) return true;
        __result = perk != null; return false;
    }
}
[HarmonyPatch(typeof(StartingPerk), nameof(StartingPerk.GetPerkById))]
internal static class NativeActorSharedPerkDefinition
{
    private static bool Prefix(string perkId, ref StartingPerk __result)
    {
        if (!NativeActorPerkQueries.TryShared(perkId, out var perk)) return true;
        __result = perk; return false;
    }
}
