using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ProbablyCoop;

// Classify the queried effect, not the entire local list: partner UI and
// actions retain personal perks, but shared-only shop configuration is the
// host's one canonical configuration on BOTH peers. This never runs an effect.
internal static class NativeActorPerkQueries
{
    internal static IEnumerable<StartingPerk> MissingStatusPerks(PlayerStore store)
    {
        var guest = NativeWorld.Guest;
        var remote = guest?.HasActorProjection == true ? guest.RemoteHostActor : NativeInventoryPilot.RemoteHostActor;
        var session = NativeActorSession.Current;
        bool authority = ReferenceEquals(session?.Store, store);
        if (!authority && remote == null) yield break;

        var saved = authority ? NativeOpening.SharedPerks(store) : guest?.OpeningSharedPerks;
        var hostPerks = authority ? store.startingPerks : remote?.startingPerks;
        var localPerks = (authority ? session.Run.Resolve(store)?.startingPerks : null) ?? store.startingPerks;
        var shown = new HashSet<string>(localPerks.Select(perk => perk.id), StringComparer.Ordinal);
        var sharedChoices = saved?.Values.AsEnumerable() ?? Enumerable.Empty<StartingPerk>();
        foreach (var perk in sharedChoices.Concat(hostPerks ?? Enumerable.Empty<StartingPerk>()))
            if (NativeActorPartnerSetup.Classify(perk.id) == NativeActorPartnerSetup.PerkScope.SharedShop &&
                shown.Add(perk.id))
                yield return perk;
    }

    internal static bool TryShared(string perkId, out StartingPerk perk)
    {
        perk = null;
        var authority = NativeActorSession.Current?.Store ?? NativeActorPersistence.LoadedOpeningStore;
        var guest = NativeWorld.Guest;
        var remote = guest?.HasActorProjection == true ? guest.RemoteHostActor : NativeInventoryPilot.RemoteHostActor;
        var openingPerk = NativeOpening.SharedStartingPerk(perkId);
        if (authority == null && remote == null && openingPerk == null) return false;
        if (NativeActorPartnerSetup.Classify(perkId) != NativeActorPartnerSetup.PerkScope.SharedShop) return false;
        if (openingPerk != null) { perk = openingPerk; return true; }
        List<StartingPerk> source;
        if (authority != null)
        {
            if (NativeOpening.SharedPerks(authority)?.TryGetValue(perkId, out perk) == true) return true;
            source = authority.startingPerks; // Deliberate raw Host field.
        }
        else
        {
            if (guest?.OpeningSharedPerks?.TryGetValue(perkId, out perk) == true) return true;
            if (remote == null) return false;
            source = remote.startingPerks;
        }
        foreach (var candidate in source)
            if (candidate.id == perkId) { perk = candidate; break; }
        return true;
    }
}

[HarmonyPatch(typeof(AdvCalendarUIManager), nameof(AdvCalendarUIManager.OnStatusButtonClicked))]
internal static class NativeActorSharedStatus
{
    private static void Postfix(AdvCalendarUIManager __instance)
    {
        var store = PlayerStore.Instance;
        if (store == null) return;
        // Give the native panel its missing rows without adding those choices
        // to either actor's saved list of personal perks.
        foreach (var perk in NativeActorPerkQueries.MissingStatusPerks(store))
        {
            var row = UnityEngine.Object.Instantiate(__instance.perkElementPrefab,
                __instance.activePerksContainer.transform).GetComponent<StartingPerkElement>();
            row.perk = perk;
            row.id = perk.id;
            row.background.sprite = __instance.highlightPerkBackground;
        }
    }
}

[HarmonyPatch(typeof(StartingPerk), nameof(StartingPerk.IsPerkActive))]
internal static class NativeActorSharedPerkActive
{
    private static bool Prefix(string perkId, ref bool __result)
    {
        if (NativeOpening.HasMixedBenefit(perkId)) { __result = true; return false; }
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
