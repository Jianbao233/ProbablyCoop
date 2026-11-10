using System;
using System.Linq;
using HarmonyLib;

namespace ProbablyCoop;

// The shared scene changes once. Only the two personal carry operations and
// outing records are repeated, never the location's arrive/leave side effects.
[HarmonyPatch(typeof(EmporiumEntry),nameof(EmporiumEntry.TransferBackpackToInv))]
internal static class NativeActorTravelBags
{
    private static bool Prefix(EmporiumEntry __instance)
    {
        var session=NativeActorSession.Current;
        if(session==null || NativeGuest.Active || !ReferenceEquals(__instance,session.Carry.Source)) return true;
        foreach(var actor in new[]{NativeActorRun.Slot.Host,NativeActorRun.Slot.Partner})
        {
            using var scope=session.Enter(actor);
            var result=session.Carry.TransferSelectedBackpack(actor);
            if(!result.Complete) throw new InvalidOperationException("Selected actor backpack could not transfer: "+result.Reason);
        }
        return false;
    }
}

[HarmonyPatch(typeof(EmporiumEntry),nameof(EmporiumEntry.TransferAfterhourToInv))]
internal static class NativeActorTravelReturn
{
    private static bool Prefix(EmporiumEntry __instance,bool countsDumpedWaste)
    {
        var session=NativeActorSession.Current;
        if(session==null || NativeGuest.Active || !ReferenceEquals(__instance,session.Carry.Source)) return true;
        foreach(var actor in new[]{NativeActorRun.Slot.Host,NativeActorRun.Slot.Partner})
        {
            using var scope=session.Enter(actor);
            var result=session.Carry.ReturnCarryToShop(actor);
            if(!result.Complete) StoreUIManager.Instance.Notify("店铺空间不足，未放下的物品仍保留在你的随身栏。");
        }
        // This is the original shared-ground branch, executed once. Keeping
        // items in personal carry must not count them as dumped waste.
        foreach(var item in __instance.afterhourInventory.childItems.ToArray())
        {
            if(countsDumpedWaste && item.identifier=="nuclear_waste") PlayerStore.GetStoreClientData().nuclearWasteDumped++;
            __instance.afterhourInventory.Expel(item);
        }
        return false;
    }
}

[HarmonyPatch(typeof(MapUIManager),"OnGoingOutside")]
internal static class NativeActorTravelPersonalRecords
{
    private static void Postfix()
    {
        var session=NativeActorSession.Current;
        if(session==null || NativeGuest.Active) return;
        // Both characters physically join this outing. The initiator alone
        // was checked for permission before the joint confirmation.
        session.Store.healthData.wentToAfterhoursLastNight=true;
        session.Store.lastAfterhourOutingDay=StoreStation.Instance.dayCounter;
        session.Run.Partner.healthData.wentToAfterhoursLastNight=true;
    }
}
