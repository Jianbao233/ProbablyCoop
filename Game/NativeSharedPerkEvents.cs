using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProbablyCoop;

internal static class NativeSharedPerkEvents
{
    private const string CommissaryBan = "banned_commissary";

    private static bool PrepareAuthority() => NativePlatform.SupportsAuthorityContract &&
        NativePlatform.MatchesActorModule(typeof(PlayerStore).Module, NativeActorCarryAccess.ExpectedMvid);

    private static bool HasPerk(IEnumerable<StartingPerk> perks, string id)
    {
        foreach (var perk in perks)
            if (perk.id == id) return true;
        return false;
    }

    private static bool NeedsSentinelSupplies()
    {
        const string sentinel = "sentinel";
        if (NativeOpening.Current is { } opening)
            return HasPerk(opening.HostPerks, sentinel) || HasPerk(opening.PartnerPerks, sentinel);
        var session = NativeActorSession.Current;
        if (session == null || NativeGuest.Active) return StartingPerk.IsSentinel();
        return HasPerk(session.Store.startingPerks, sentinel) ||
            HasPerk(session.Run.Partner.startingPerks, sentinel);
    }

    private static bool ShopBannedFromCommissary()
    {
        if (NativeOpening.Current is { } opening)
            return HasPerk(opening.HostPerks, CommissaryBan) && HasPerk(opening.PartnerPerks, CommissaryBan);
        var session = NativeActorSession.Current;
        if (session == null || NativeGuest.Active) return StartingPerk.IsBannedCommissary();
        return HasPerk(session.Store.startingPerks, CommissaryBan) &&
            HasPerk(session.Run.Partner.startingPerks, CommissaryBan);
    }

    internal static void RestoreMissedCommissaryIntroduction(NativeActorSession session)
    {
        var store = session.Store;
        if (store.isStampIntroduced || (!store.skipIntro && StoreStation.Instance.dayCounter <= 11) ||
            !HasPerk(store.startingPerks, CommissaryBan) ||
            HasPerk(session.Run.Partner.startingPerks, CommissaryBan)) return;
        if (store.modData.TryGetValue(NativeActorPartnerSetup.SaveKey, out string receipt) &&
            NativeActorPartnerSetup.IsComplete(receipt, session.Run.Run))
        {
            // The earlier Host-only condition suppressed the common
            // introduction. Restore access without replaying starter gifts.
            store.isStampIntroduced = true;
        }
    }

    // Suppliers and gifts are shared. Health and the effect of an injection
    // continue to use the active actor's original rule.
    [HarmonyPatch]
    private static class SentinelSupply
    {
        [HarmonyPrepare] private static bool Prepare() => PrepareAuthority();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(StoreClientManager), nameof(StoreClientManager.HandleScalperClient));
            yield return AccessTools.Method(typeof(StartingPerkContent), nameof(StartingPerkContent.HandleSecretAdmirerOnOpenShutter));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source) =>
            NativeActorPersistence.ReplaceOne(source,
                AccessTools.Method(typeof(StartingPerk), nameof(StartingPerk.IsSentinel)),
                AccessTools.Method(typeof(NativeSharedPerkEvents), nameof(NeedsSentinelSupplies)), "shared sentinel supplies");
    }

    // Teach and stock the shared exchange if either player can use it.
    // CommissaryLocation.IsDiscovered still checks the visiting actor.
    [HarmonyPatch]
    private static class CommissaryOnboarding
    {
        [HarmonyPrepare] private static bool Prepare() => PrepareAuthority();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.HandleSkipIntro));
            yield return AccessTools.Method(typeof(StoreShutterButton), nameof(StoreShutterButton.OnShutterDoorOpenMorning));
            yield return AccessTools.Method(typeof(StoreClientManager), nameof(StoreClientManager.IsClientBlockedByBannedCommissary));
            yield return AccessTools.Method(typeof(StoreClientList), nameof(StoreClientList.PlaceInventorInventory));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source, MethodBase __originalMethod)
        {
            var original = AccessTools.Method(typeof(StartingPerk), nameof(StartingPerk.IsBannedCommissary));
            var replacement = AccessTools.Method(typeof(NativeSharedPerkEvents), nameof(ShopBannedFromCommissary));
            int expected = __originalMethod.DeclaringType == typeof(PlayerStore) ? 3 : 1;
            var code = source.ToList();
            int matches = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (!code[i].Calls(original)) continue;
                code[i] = new CodeInstruction(code[i]) { opcode = OpCodes.Call, operand = replacement };
                matches++;
            }
            if (matches != expected)
                throw new InvalidOperationException("Native commissary onboarding changed: " + __originalMethod.Name);
            return code;
        }
    }
}
