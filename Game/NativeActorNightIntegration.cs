using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProbablyCoop;

// Retain the complete native EndNight and its UI transition. The only replaced
// business call is the two personal OnSleep phases; world services, machines,
// morning generation and persistence remain the single original sequence.
internal static class NativeActorNightIntegration
{
    private sealed class NightStopped : Exception
    { internal NightStopped() : base("Cooperative night stopped at a native ending.") { } }
    private sealed class NightScope
    {
        internal NativeActorSession Session;
        internal IDisposable Actor;
        internal bool PreviousConverter;
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.EndNight))]
    private static class NightBoundary
    {
        private static void Prefix(PlayerStore __instance, out NightScope __state)
        {
            __state = null;
            var session = NativeActorSession.Current;
            if (NativeGuest.Active || session == null || !ReferenceEquals(session.Store, __instance)) return;
            if (session.Terminal) throw new NightStopped();
            if (!session.CanCommit || session.NightActive)
                throw new InvalidOperationException("Cooperative night requires settled actors and inventory.");
            int day = StoreStation.Instance.dayCounter;
            if (!session.AttemptedNights.Add(day))
                throw new InvalidOperationException("This native night was already attempted; restore a verified checkpoint before retrying.");
            var scope = new NightScope { Session = session, PreviousConverter = CurrentConverterHelper.isNightCycle };
            __state = scope;
            session.NightActive = true;
            try { scope.Actor = session.Enter(NativeActorRun.Slot.Host); }
            catch (Exception error) { session.MarkFault(error); session.NightActive = false; throw; }
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
        {
            var code = NativeActorPersistence.ReplaceOne(source,
                AccessTools.Method(typeof(HealthData), nameof(HealthData.OnSleep)),
                AccessTools.Method(typeof(NativeActorNightIntegration), nameof(SleepBoth)), "native personal night");
            code = NativeActorPersistence.ReplaceOne(code,
                AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.HandleAfterhourDebuff)),
                AccessTools.Method(typeof(NativeActorNightIntegration), nameof(AfterhourLogs)), "native personal afterhours log");
            code = NativeActorPersistence.ReplaceOne(code,
                AccessTools.Method(typeof(StartingPerkContent), nameof(StartingPerkContent.HandleAgoraphobiaReady)),
                AccessTools.Method(typeof(NativeActorNightIntegration), nameof(AgoraphobiaLogs)), "native personal agoraphobia log");
            return ReplaceOwnedEnumeration(code, 3);
        }
        private static Exception Finalizer(NightScope __state, Exception __exception)
        {
            if (__state == null) return __exception;
            try
            {
                if (__exception != null && __exception is not NightStopped) __state.Session.MarkFault(__exception);
                CurrentConverterHelper.isNightCycle = __state.PreviousConverter;
                __state.Session.NightActive = false;
            }
            finally { __state.Actor?.Dispose(); }
            return __exception;
        }
    }

    private static void SleepBoth(HealthData nativeHealth)
    {
        var session = NativeActorSession.Current;
        if (session == null || !session.NightActive) { nativeHealth.OnSleep(); return; }
        int day = StoreStation.Instance.dayCounter;
        NativeActorNight.Result result;
        session.InPersonalNight = true;
        try
        {
            result = session.Night.ExecuteForDay(day, session.Store.healthData, session.Run.Partner.healthData,
                actor => session.Enter((NativeActorRun.Slot)actor),
                () => ReferenceEquals(NativeActorSession.Current, session) && session.NightActive &&
                    StoreStation.Instance.dayCounter == day && !session.Faulted,
                () => session.Terminal);
        }
        finally { session.InPersonalNight = false; }
        if (result == NativeActorNight.Result.Terminal) throw new NightStopped();
        if (result != NativeActorNight.Result.Completed)
            throw new InvalidOperationException("Personal night completion cannot authorize a second world night.");
    }

    private static void AfterhourLogs(PlayerStore store)
    {
        var session = NativeActorSession.Current;
        if (session == null || !session.NightActive || !ReferenceEquals(session.Store, store)) { store.HandleAfterhourDebuff(); return; }
        for (int actor = 0; actor < 2; actor++)
            using (session.Enter((NativeActorRun.Slot)actor))
            using (NativeActorNightReport.EnterPersonal(session, (NativeActorRun.Slot)actor)) store.HandleAfterhourDebuff();
    }
    private static void AgoraphobiaLogs()
    {
        var session = NativeActorSession.Current;
        if (session == null || !session.NightActive) { StartingPerkContent.HandleAgoraphobiaReady(); return; }
        for (int actor = 0; actor < 2; actor++)
            using (session.Enter((NativeActorRun.Slot)actor))
            using (NativeActorNightReport.EnterPersonal(session, (NativeActorRun.Slot)actor)) StartingPerkContent.HandleAgoraphobiaReady();
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.ExecuteGameOver))]
    private static class EndingBoundary
    {
        private static bool Prefix(PlayerStore __instance, out NativeActorSession __state)
        {
            __state = NativeActorSession.Current;
            if (NativeGuest.Active || __state == null || !ReferenceEquals(__state.Store, __instance)) { __state = null; return true; }
            if (__state.Terminal) { __state = null; return false; }
            return true;
        }
        private static void Postfix(string ending, NativeActorSession __state)
        {
            if (__state == null) return;
            __state.MarkTerminal(ending);
            // Personal OnSleep must return to its coordinator so it can stop the
            // second actor and lottery. All other night/end-day endings unwind
            // immediately, before another service, morning or SaveGame can run.
            if ((__state.TransitionActive || __state.NightActive) && !__state.InPersonalNight) throw new NightStopped();
        }
    }

    // This is the native actionDuringBlack closure, not a replacement sequence.
    // An ending unwinds only this synchronous action. The original blackscreen
    // coroutine still fades out, and the original ending UI remains available.
    [HarmonyPatch]
    private static class TransitionBoundary
    {
        private static MethodBase TargetMethod() => typeof(EndOfDayUIManager)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Single(method => method.Name == "<OnEndDayButtonClick>b__0");
        private static void Prefix(out NativeActorSession __state)
        {
            __state = NativeGuest.Active ? null : NativeActorSession.Current;
            if (__state == null) return;
            if (__state.TransitionActive || __state.Faulted) throw new InvalidOperationException("Cooperative day transition is not available.");
            if (__state.Terminal) throw new NightStopped();
            __state.TransitionActive = true;
        }
        private static Exception Finalizer(NativeActorSession __state, Exception __exception)
        {
            if (__state == null) return __exception;
            __state.TransitionActive = false;
            if (__exception != null)
            {
                if (StoreUIManager.Instance?.savingTMP != null) StoreUIManager.Instance.savingTMP.gameObject.SetActive(false);
                if (__exception is NightStopped) return null;
                __state.MarkFault(__exception);
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(StartOfDayUIManager), nameof(StartOfDayUIManager.OpenUI))]
    private static class MorningBoundary
    {
        private static bool Prefix() => NativeGuest.Active || NativeActorSession.Current == null ||
            !(NativeActorSession.Current.Terminal || NativeActorSession.Current.Faulted);
    }
    [HarmonyPatch(typeof(GameOverUIManager), nameof(GameOverUIManager.ContinuePlayClick))]
    private static class ContinueBoundary
    {
        private static void Postfix(bool __runOriginal)
        {
            if (__runOriginal && GameOverUIManager.Instance?.IsShowing == false)
                NativeActorSession.Current?.ContinueNativeEnding();
        }
    }

    // Target machine maintenance callers, not the global query. Even during
    // EndNight an inspection/event query must retain its original coverage.
    private static List<GameItem> NightOwnedItems(PlayerStore store, bool isOwn)
    {
        var result = store.FindAllItem(isOwn);
        var session = NativeActorSession.Current;
        if (session == null || !session.NightActive || !ReferenceEquals(session.Store, store)) return result;
        return result.Concat(session.Carry.GetAllCarriedItems().Where(item => !isOwn || item.IsTag("IS_OWNED_TAG"))).Distinct().ToList();
    }
    private static List<GameItem> NightAllItems(EmporiumEntry entry)
    {
        var result = entry.GetAllItems();
        var session = NativeActorSession.Current;
        if (session == null || !session.NightActive || !ReferenceEquals(session.Carry.Source, entry)) return result;
        return result.Concat(session.Carry.GetAllCarriedItems()).Distinct().ToList();
    }
    private static IEnumerable<CodeInstruction> ReplaceOwnedEnumeration(IEnumerable<CodeInstruction> source, int expected)
    {
        var code = source.ToList();
        var target = AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.FindAllItem));
        int count = 0;
        for (int i = 0; i < code.Count; i++)
            if ((code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) && Equals(code[i].operand, target))
            {
                code[i] = new CodeInstruction(code[i]) { opcode = OpCodes.Call,
                    operand = AccessTools.Method(typeof(NativeActorNightIntegration), nameof(NightOwnedItems)) };
                count++;
            }
        if (count != expected) throw new InvalidOperationException("Native machine enumeration boundary changed.");
        return code;
    }
    [HarmonyPatch]
    private static class NightOwnedEnumeration
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MachineFridge), nameof(MachineFridge.ClearColdMarkers));
            yield return AccessTools.Method(typeof(MachineFridge), nameof(MachineFridge.HandleColdFeatureExpiry));
            yield return AccessTools.Method(typeof(HopperHelper), nameof(HopperHelper.RunWindow));
            yield return AccessTools.Method(typeof(HopperHelper), nameof(HopperHelper.FindOutlet));
            yield return AccessTools.Method(typeof(HopperHelper), nameof(HopperHelper.FindIntakesOf));
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source) => ReplaceOwnedEnumeration(source, 1);
    }
    [HarmonyPatch]
    private static class NightAllEnumeration
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UseCountHelper), nameof(UseCountHelper.OnNight));
            yield return AccessTools.Method(typeof(ChemicalHelper), nameof(ChemicalHelper.RunNightlyChemicalSimulation));
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source) => NativeActorPersistence.ReplaceOne(source,
            AccessTools.Method(typeof(EmporiumEntry), nameof(EmporiumEntry.GetAllItems)),
            AccessTools.Method(typeof(NativeActorNightIntegration), nameof(NightAllItems)), "native timed item maintenance");
    }
}
