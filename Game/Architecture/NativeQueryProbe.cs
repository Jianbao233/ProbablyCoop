using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json;

namespace ProbablyCoop;

/// <summary>Proof of a native read-model adapter for delegate-backed queries.
/// Transfers values only. No business delegate is reconstructed on the replica.
/// Actual networking must key this by entity/activity/revision and refresh atomically.
/// </summary>
internal static class NativeQueryProbe
{
    internal sealed class BarterView
    {
        public bool Acceptable;
        public int OfferCount;
        public string Response;
    }
    private static readonly ConditionalWeakTable<BarterOffer, BarterView> replicas = new ConditionalWeakTable<BarterOffer, BarterView>();
    internal static void Bind(BarterOffer source, BarterOffer replica)
    {
        if (!NativeArchitectureQa.Active) throw new InvalidOperationException("QA only");
        var view = new BarterView { Acceptable = source.IsBarterAcceptable(),
            OfferCount = source.OfferSetCount(), Response = source.GetItemPlacedResponse() };
        // Independent data roundtrip, not a source closure attached to the replica.
        replicas.Add(replica, JsonConvert.DeserializeObject<BarterView>(JsonConvert.SerializeObject(view)));
    }
    internal static bool TryGet(BarterOffer offer, out BarterView view)
    {
        view = null;
        return (NativeArchitectureQa.Active || NativeGuest.Active) && offer != null && replicas.TryGetValue(offer, out view);
    }
    internal static void BindReplica(BarterOffer offer, BarterView view)
    {
        if (!NativeGuest.Active) throw new InvalidOperationException("Replica only");
        replicas.Remove(offer); replicas.Add(offer, view);
    }
}

[HarmonyPatch(typeof(BarterOffer), nameof(BarterOffer.IsBarterAcceptable))]
internal static class ArchitectureBarterAcceptQuery
{
    private static bool Prefix(BarterOffer __instance, ref bool __result)
    {
        if (!NativeQueryProbe.TryGet(__instance, out var view)) return true;
        __result = view.Acceptable; return false;
    }
}
[HarmonyPatch(typeof(BarterOffer), nameof(BarterOffer.OfferSetCount))]
internal static class ArchitectureBarterCountQuery
{
    private static bool Prefix(BarterOffer __instance, ref int __result)
    {
        if (!NativeQueryProbe.TryGet(__instance, out var view)) return true;
        __result = view.OfferCount; return false;
    }
}
[HarmonyPatch(typeof(BarterOffer), nameof(BarterOffer.GetItemPlacedResponse))]
internal static class ArchitectureBarterResponseQuery
{
    private static bool Prefix(BarterOffer __instance, ref string __result)
    {
        if (!NativeQueryProbe.TryGet(__instance, out var view)) return true;
        __result = view.Response; return false;
    }
}
