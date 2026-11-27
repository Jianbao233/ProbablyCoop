using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
        ClearGround(__instance.afterhourInventory, countsDumpedWaste);
        return false;
    }

    internal static void ClearGround(GameGridInventory ground, bool countsDumpedWaste)
    {
        // The original ground branch counts each waste item once.
        foreach (var item in ground.childItems.ToArray())
        {
            if (countsDumpedWaste && item.identifier == "nuclear_waste") PlayerStore.GetStoreClientData().nuclearWasteDumped++;
            ground.Expel(item);
        }
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
        int day = StoreStation.Instance.dayCounter;
        session.Run.RecordOuting(NativeActorRun.Slot.Host, day);
        session.Run.RecordOuting(NativeActorRun.Slot.Partner, day);
    }
}

internal static class NativeOutingDayRules
{
    private static readonly FieldInfo OutingDayField = AccessTools.Field(typeof(PlayerStore), nameof(PlayerStore.lastAfterhourOutingDay));

    private static int ReadPersonalDay(PlayerStore store)
    {
        var session = NativeActorSession.Current;
        return session != null && ReferenceEquals(session.Store, store)
            ? session.Run.OutingDay(session.Run.CurrentActor)
            : store.lastAfterhourOutingDay;
    }

    private static int ReadSharedDay(PlayerStore store)
    {
        var session = NativeActorSession.Current;
        return session != null && ReferenceEquals(session.Store, store)
            ? Math.Max(store.lastAfterhourOutingDay, session.Run.OutingDay(NativeActorRun.Slot.Partner))
            : store.lastAfterhourOutingDay;
    }

    internal static bool HasLateOpeningPenalty(HealthData nativeHealth)
    {
        if (NativeActorSession.Current is { } authority &&
            (ReferenceEquals(nativeHealth, authority.Store.healthData) ||
                ReferenceEquals(nativeHealth, authority.Run.Partner.healthData)))
            return authority.Run.HasLateOpeningPenalty();
        bool local = nativeHealth.wentToAfterhoursLastNight && !StartingPerk.IsShortSleeper();
        return local || (NativeGuest.Active && NativeWorld.Guest?.RemoteHostActor is { } host &&
            NativeActorRun.CausesLateOpening(host.healthData, host.startingPerks));
    }

    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> source, string reader)
    {
        var code = source.ToArray();
        var operand = code.Single(instruction => instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, OutingDayField));
        int index = Array.IndexOf(code, operand);
        code[index] = new CodeInstruction(operand) { opcode = OpCodes.Call, operand = AccessTools.Method(typeof(NativeOutingDayRules), reader) };
        return code;
    }

    [HarmonyPatch]
    private static class PersonalCooldown
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(StartingPerkContent), nameof(StartingPerkContent.GetAgoraphobiaReadyDay));
            yield return AccessTools.Method(typeof(StartingPerkContent), nameof(StartingPerkContent.HandleAgoraphobiaReady));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, nameof(ReadPersonalDay));
    }

    [HarmonyPatch(typeof(DebrisHelper), nameof(DebrisHelper.OnNight))]
    private static class SharedDebris
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, nameof(ReadSharedDay));
    }

    // The native report and customer estimate must see the same single shop
    // penalty. Keep the rest of both methods, including the native count clamp.
    [HarmonyPatch]
    private static class SharedLateOpening
    {
        private static readonly FieldInfo InstructionLabels = AccessTools.Field(typeof(CodeInstruction), "labels");
        [HarmonyPrepare] private static bool Prepare() =>
            NativePlatform.MatchesActorModule(typeof(PlayerStore).Module, NativeActorCarryAccess.ExpectedMvid);

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.HandleAfterhourDebuff));
            yield return AccessTools.Method(typeof(StoreClientManager), nameof(StoreClientManager.GetNormalCustomerCount));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
        {
            var code = source.ToList();
            var flag = AccessTools.Field(typeof(HealthData), nameof(HealthData.wentToAfterhoursLastNight));
            var shortSleeper = AccessTools.Method(typeof(StartingPerk), nameof(StartingPerk.IsShortSleeper));
            int index = Enumerable.Range(0, code.Count).Single(i => code[i].LoadsField(flag));
            if (index + 3 >= code.Count || !code[index + 2].Calls(shortSleeper) ||
                (code[index + 1].opcode != OpCodes.Brfalse && code[index + 1].opcode != OpCodes.Brfalse_S) ||
                (code[index + 3].opcode != OpCodes.Brtrue && code[index + 3].opcode != OpCodes.Brtrue_S) ||
                !code.Skip(index + 4).Any(row => InstructionLabels.GetValue(row) is System.Collections.IList labels &&
                    labels.Contains(code[index + 1].operand) && labels.Contains(code[index + 3].operand)))
                throw new InvalidOperationException("Native late opening condition changed.");
            code[index] = new CodeInstruction(code[index]) { opcode = OpCodes.Call,
                operand = AccessTools.Method(typeof(NativeOutingDayRules), nameof(HasLateOpeningPenalty)) };
            code[index + 2] = new CodeInstruction(code[index + 2]) { opcode = OpCodes.Nop, operand = null };
            code[index + 3] = new CodeInstruction(code[index + 3]) { opcode = OpCodes.Nop, operand = null };
            return code;
        }
    }
}

[HarmonyPatch(typeof(StoreShutterButton),nameof(StoreShutterButton.EndAndClose))]
internal static class NativeActorAfterhoursReset
{
    private static void Postfix(bool __runOriginal)
    {
        var session=NativeActorSession.Current;
        if(!__runOriginal || NativeGuest.Active || session==null ||
            session.Store.storeState!=PlayerStore.StoreState.CLOSED) return;

        // The original close resets the current actor. Start the new evening
        // with clean outing records for both actors before either travels.
        session.Store.healthData.wentToAfterhoursLastNight=false;
        session.Run.Partner.healthData.wentToAfterhoursLastNight=false;
    }
}
