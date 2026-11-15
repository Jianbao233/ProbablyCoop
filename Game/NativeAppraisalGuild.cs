using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ProbablyCoop;

// The association issues one certificate for the shared store. A partner can
// serve appraisal customers and accept promotions on behalf of that store.
internal static class NativeAppraisalGuild
{
    private const string Intro = "appraisalBoardIntro";
    private const string FollowUp = "appraisalBoardIntro2";
    private const string Promotion = AppraisalRankHelper.PROMOTION_CLIENT_ID;

    private static NativeActorSession Authority
    {
        get
        {
            var session = NativeActorSession.Current;
            return session != null && !session.Hydrating && !NativeGuest.Active ? session : null;
        }
    }

    private static bool IsAssociation(string id) => id is Intro or FollowUp or Promotion;

    private static bool Obsolete(string id, PlayerStore shop)
    {
        if (id is Intro or FollowUp)
            return shop.isAppraisalIntroduced || shop.appraiserLevel > AppraisalRankHelper.RANK_JUNIOR;
        if (id != Promotion) return false;
        return !shop.isAppraisalIntroduced || shop.appraiserLevel >= AppraisalRankHelper.RANK_MASTER ||
            shop.appraisalPromotionDeclined ||
            shop.appraisalProgress < AppraisalRankHelper.GetRankData(shop.appraiserLevel).progressToPromote;
    }

    private static void PruneQueue(PlayerStore shop) =>
        shop.storeClientManager?.clientStack?.RemoveAll(client => Obsolete(client?.identifier, shop));

    [HarmonyPatch]
    private static class AssociationArrival
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(StoreClientManager), nameof(StoreClientManager.AddClient));
            yield return AccessTools.Method(typeof(StoreClientManager), nameof(StoreClientManager.AddNextClient));
        }

        private static bool Prefix(StoreClientManager __instance, StoreClient storeClient)
        {
            string id = storeClient?.identifier;
            if (!IsAssociation(id) || Authority?.Store is not { } shop || shop.storeClientManager != __instance)
                return true;
            PruneQueue(shop);
            return !Obsolete(id, shop) &&
                !IsAssociation(shop.currentClientInstance?.GetClientBlueprint()?.identifier) &&
                !__instance.clientStack.Any(client => IsAssociation(client?.identifier));
        }
    }

    [HarmonyPatch]
    private static class SavedAssociationQueue
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.HandleFutureClientQueue));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.CanNextClient));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.CanEndDay));
        }

        private static void Prefix(PlayerStore __instance)
        {
            if (ReferenceEquals(Authority?.Store, __instance)) PruneQueue(__instance);
        }
    }

    [HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.SelectChoice))]
    private static class AssociationChoice
    {
        private static bool Prefix(DialogUIManager __instance)
        {
            var shop = Authority?.Store;
            if (shop == null || !Obsolete(shop.currentClientInstance?.GetClientBlueprint()?.identifier, shop))
                return true;
            __instance.CurrentClientLeave(0f);
            return false;
        }
    }

    [HarmonyPatch]
    private static class ShopAppraisal
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AppraisalRankHelper), nameof(AppraisalRankHelper.HandlePromotionClient));
            yield return AccessTools.Method(typeof(AppraisalRankHelper), nameof(AppraisalRankHelper.Promote));
            yield return AccessTools.Method(typeof(AppraisalRankHelper), nameof(AppraisalRankHelper.DeclinePromotionForNow));
            yield return AccessTools.Method(typeof(AppraisalRankHelper), nameof(AppraisalRankHelper.DeclinePromotionForGood));
            yield return AccessTools.Method(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.OnEstimate));
        }

        private static bool Prefix(MethodBase __originalMethod, out IDisposable __state)
        {
            __state = null;
            var session = Authority;
            if (session == null) return true;
            if (__originalMethod.Name == nameof(AppraisalRankHelper.Promote) && Obsolete(Promotion, session.Store))
                return false;
            if (session.Run.CurrentActor != NativeActorRun.Slot.Host)
                __state = session.Enter(NativeActorRun.Slot.Host);
            return true;
        }

        private static void Postfix(MethodBase __originalMethod, bool __runOriginal)
        {
            if (__runOriginal && __originalMethod.Name == nameof(AppraisalRankHelper.Promote) &&
                Authority?.Store is { } shop) PruneQueue(shop);
        }

        private static Exception Finalizer(Exception __exception, IDisposable __state)
        {
            __state?.Dispose();
            return __exception;
        }
    }
}
