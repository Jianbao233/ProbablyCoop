using HarmonyLib;

namespace ProbablyCoop;

[HarmonyPatch(typeof(StartingPerk), nameof(StartingPerk.GetLocalizedDescription))]
internal static class NativeActorPerkDescription
{
    private static void Postfix(StartingPerk __instance, ref string __result)
    {
        bool partner = NativeActorPartnerSetup.SelectingPartnerPerks || NativeGuest.Active ||
            NativeInventoryPilot.HasCampaign && !NativeInventoryPilot.AuthoritativeCampaign;
        if (!partner && NativeActorSession.Current == null && !CoopNewGameFlow.SelectingPerks) return;

        var scope = NativeActorPartnerSetup.Classify(__instance.id);
        if (scope != NativeActorPartnerSetup.PerkScope.Mixed) return;

        if (__instance.id == "gambling_addiction")
            __result += "\n\n" + ModHelper.GetLocalized("probably_coop", "perk_gambling_coop",
                LocHelper.GetLocalizedItem("item_black_injector_name"), LocHelper.GetLocalizedItem("item_mind_reprogrammer_name"));
        else if (partner)
            __result += "\n\n" + ModHelper.GetLocalized("probably_coop", "perk_mixed_coop");
    }
}
