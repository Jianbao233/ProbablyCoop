using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;

namespace ProbablyCoop;

// SlotMarker is also used by autonomous production and forced game events.
// Intercept the player transfer only. Native simulation can change an item's
// state, which invalidates its original pickup proof on the next check.
[HarmonyPatch]
internal static class NativeHandTransferInput
{
    internal static bool Active { get; private set; }
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ItemQuickTransferHandler),"OnEventRelease");
        yield return AccessTools.Method(typeof(ItemQuickTransferAltHandler),"OnEventRelease");
        yield return AccessTools.Method(typeof(ItemQuickTransferAltHandler1),"OnEventRelease");
        yield return AccessTools.Method(typeof(ItemMouseDragHandler),"EndDrag");
        yield return AccessTools.Method(typeof(ItemMultiSelectHandler),"EndGroupDrag");
    }
    private static void Prefix(out bool __state) { __state=Active; Active=NativeWorld.Host?.PartnerHasHand==true; }
    private static void Finalizer(bool __state) { Active=__state; }

    internal static bool BlocksHostSlot(SlotMarker slot)
    {
        var world=NativeWorld.Host;
        if(world==null) return false;
        return (Active || NativeDealStash.Active) && world.BlocksLocalSlot(slot) ||
            NativeDealStash.Active && world.BlocksRemoteDealStash(slot);
    }
}

// Rev12 uses this same optional transfer after buying, wholesale and barter.
// A blocked destination must leave the acquired item where vanilla put it.
[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.MoveDealItemToInventory))]
internal static class NativeDealStash
{
    internal static bool Active { get; private set; }
    private static void Prefix(out bool __state)
    { __state=Active; Active=NativeWorld.Host!=null && !NativeGuest.Active; }
    private static void Finalizer(bool __state) { Active=__state; }
}

[HarmonyPatch(typeof(NegociationUIManager),nameof(NegociationUIManager.OnWholesaleClick))]
internal static class NativeWholesaleGuard
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix()=>NativeGuest.Active || NativeWorld.Host?.AllowNativeWholesale()!=false;
}

// Guard the UI operation before the first item, payment, or post-deal
// dialogue. A conflict must not turn a multi-item deal into a partial sale.
[HarmonyPatch]
internal static class NativePartnerBatchTrade
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NegociationUIManager),nameof(NegociationUIManager.BuyMultipleItem));
        yield return AccessTools.Method(typeof(NegociationUIManager),nameof(NegociationUIManager.SellMultiple));
    }
    private static bool Prefix(List<GameItem> items)
    {
        var host=NativeWorld.Host;
        return host?.PartnerHasHand!=true || !items.Any(host.RejectHeldInput);
    }
}
[HarmonyPatch]
internal static class NativePartnerTableTrade
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NegociationUIManager),nameof(NegociationUIManager.BuyItem));
        yield return AccessTools.Method(typeof(NegociationUIManager),nameof(NegociationUIManager.SellItem));
        yield return AccessTools.Method(typeof(PlayerStore),nameof(PlayerStore.BuyItemFromTable));
        yield return AccessTools.Method(typeof(PlayerStore),nameof(PlayerStore.SellItemFromTable));
    }
    private static bool Prefix()
    {
        var host=NativeWorld.Host;
        return host?.PartnerHasHand!=true || !host.RejectHeldInput(PlayerStore.Instance.currentNegociatedItem);
    }
}
[HarmonyPatch]
internal static class NativePartnerAllTableItems
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PlayerStore),nameof(PlayerStore.BuyAllItemFromTable));
        yield return AccessTools.Method(typeof(PlayerStore),nameof(PlayerStore.SellAllItemFromTable));
    }
    private static bool Prefix()
    {
        var host=NativeWorld.Host;
        return host?.PartnerHasHand!=true || !EmporiumEntry.Instance.backInvinvElement.childItems.Any(host.RejectHeldInput);
    }
}
