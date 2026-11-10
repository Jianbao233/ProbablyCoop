using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    // Observers only: no invocation of sleep, lottery, morning or save, and no
    // fixture mutations. Counters refer to original native methods completing,
    // not coordinator promises, report text, or successful network receipts.
    private enum ActorNightCall { WorldNight, HostSleep, PartnerSleep, Lottery, DayEnd, BeginDay, StartDay, Save }
    private sealed class ActorNightEvidence
    {
        internal NativeActorSession Session;
        internal int Day, ExpectedLottery;
        internal readonly int[] Entered = new int[8];
        internal readonly int[] CompletedOriginal = new int[8];
        internal readonly List<string> Errors = new List<string>();
    }
    private sealed class ActorNightObservation
    {
        internal ActorNightEvidence Night;
        internal ActorNightCall Kind;
        internal bool ReachedPostfix;
    }
    private static NativeActorSession actorNightSourceSession;
    private static readonly Dictionary<int, ActorNightEvidence> actorNightSourceDays = new Dictionary<int, ActorNightEvidence>();
    private static ActorNightEvidence actorNightSourcePending;

    private static bool ActorNightSourceEnabled => Enabled && IsHostProduct && !NativeGuest.Active;

    private static NativeActorSession ActorNightSourceSession()
    {
        if (!ActorNightSourceEnabled) return null;
        var session = NativeActorSession.Current;
        if (session == null) return null;
        if (!ReferenceEquals(actorNightSourceSession, session))
        {
            actorNightSourceSession = session;
            actorNightSourceDays.Clear();
            actorNightSourcePending = null;
        }
        Require(session.Store.saveSlotId == session.NativeSaveSlot &&
            Guid.TryParse(session.Store.runID, out var run) && run == session.Run.Run,
            "actor night observer source run/slot changed");
        return session;
    }

    private static ActorNightObservation ObserveActorNightCall(ActorNightCall kind, object instance)
    {
        var session = ActorNightSourceSession();
        if (session == null) return null;
        bool personalOrNight = kind == ActorNightCall.WorldNight || kind == ActorNightCall.HostSleep || kind == ActorNightCall.Lottery;
        // NightActive ends with EndNight; the original closure subsequently
        // calls OnDayEnd, BeginDay/StartDay and SaveGame in TransitionActive.
        if (personalOrNight ? !session.NightActive : !session.TransitionActive) return null;
        int day = StoreStation.Instance.dayCounter;
        if (kind == ActorNightCall.WorldNight)
        {
            Require(ReferenceEquals(instance, session.Store) && session.TransitionActive,
                "actor night observer did not enter the native day-transition source");
            if (!actorNightSourceDays.TryGetValue(day, out var created))
            {
                Require(actorNightSourceDays.Count < 64, "actor night observer exceeded bounded history");
                created = new ActorNightEvidence { Session = session, Day = day };
                if (session.Run.CurrentActor != NativeActorRun.Slot.Host)
                    created.Errors.Add("EndNight entered outside Host world context");
                // Native HealthData.OnSleep has this exact date predicate.
                // In particular days 6, 7 and 8 do not draw a lottery at all.
                created.ExpectedLottery = day % 7 == 5 && LotteryHelper.IsLotteryUnlocked() ? 1 : 0;
                actorNightSourceDays.Add(day, created);
            }
            actorNightSourcePending = created;
        }
        var night = actorNightSourcePending;
        Require(night != null && ReferenceEquals(night.Session, session),
            "actor night observer saw a phase without its native EndNight");
        if (kind == ActorNightCall.HostSleep)
        {
            var actor = session.Run.CurrentActor;
            var expected = actor == NativeActorRun.Slot.Host ? session.Store.healthData : session.Run.Partner.healthData;
            if (!session.InPersonalNight || !ReferenceEquals(instance, expected) ||
                ReferenceEquals(session.Store.healthData, session.Run.Partner.healthData))
                night.Errors.Add("OnSleep actor/health object identity mismatch");
            kind = actor == NativeActorRun.Slot.Host ? ActorNightCall.HostSleep : ActorNightCall.PartnerSleep;
        }
        else if (kind == ActorNightCall.DayEnd && !ReferenceEquals(instance, StoreStation.Instance.storeEventManager))
            night.Errors.Add("OnDayEnd used another event manager");
        else if (kind == ActorNightCall.StartDay && !ReferenceEquals(instance, StoreStation.Instance))
            night.Errors.Add("StartDay used another StoreStation");
        else if ((kind == ActorNightCall.WorldNight || kind == ActorNightCall.BeginDay || kind == ActorNightCall.Save) &&
            !ReferenceEquals(instance, session.Store))
            night.Errors.Add(kind + " used another PlayerStore");
        if (kind != ActorNightCall.HostSleep && kind != ActorNightCall.PartnerSleep &&
            session.Run.CurrentActor != NativeActorRun.Slot.Host)
            night.Errors.Add(kind + " ran outside Host world context");
        int expectedDay = kind == ActorNightCall.Save ? night.Day + 1 : night.Day;
        if (day != expectedDay) night.Errors.Add(kind + " started on day " + day + " instead of " + expectedDay);
        night.Entered[(int)kind]++;
        return new ActorNightObservation { Night = night, Kind = kind };
    }

    private static void CompletedActorNightCall(ActorNightObservation state, bool runOriginal)
    {
        if (state == null) return;
        state.ReachedPostfix = true;
        if (!runOriginal) { state.Night.Errors.Add(state.Kind + " original method was suppressed"); return; }
        if (!ReferenceEquals(NativeActorSession.Current, state.Night.Session))
            state.Night.Errors.Add(state.Kind + " completed in another native actor session");
        int expectedDay = state.Kind == ActorNightCall.Save || state.Kind == ActorNightCall.BeginDay ||
            state.Kind == ActorNightCall.StartDay ? state.Night.Day + 1 : state.Night.Day;
        if (StoreStation.Instance.dayCounter != expectedDay)
            state.Night.Errors.Add(state.Kind + " completed on an unexpected day");
        state.Night.CompletedOriginal[(int)state.Kind]++;
    }

    private static Exception FinalizedActorNightCall(ActorNightObservation state, Exception exception)
    {
        if (state == null) return exception;
        if (exception != null)
            state.Night.Errors.Add(state.Kind + " threw " + exception.GetType().Name + ": " + exception.Message);
        else if (!state.ReachedPostfix)
            state.Night.Errors.Add(state.Kind + " never reached its original-completion observer");
        return exception; // Never swallow or replace a native failure.
    }

    [HarmonyPatch]
    private static class ActorNightNativeSourceObserver
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.EndNight));
            yield return AccessTools.Method(typeof(HealthData), nameof(HealthData.OnSleep));
            yield return AccessTools.Method(typeof(StoreEventManager), nameof(StoreEventManager.OnDayEnd));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.BeginDay));
            yield return AccessTools.Method(typeof(StoreStation), nameof(StoreStation.StartDay));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.SaveGame));
        }
        // Run after production admission has established NightActive. This
        // prefix changes no argument, result, or decision to execute original.
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(MethodBase __originalMethod, object __instance, out ActorNightObservation __state)
        {
            var kind = __originalMethod.Name switch
            {
                nameof(PlayerStore.EndNight) => ActorNightCall.WorldNight,
                nameof(HealthData.OnSleep) => ActorNightCall.HostSleep,
                nameof(StoreEventManager.OnDayEnd) => ActorNightCall.DayEnd,
                nameof(PlayerStore.BeginDay) => ActorNightCall.BeginDay,
                nameof(StoreStation.StartDay) => ActorNightCall.StartDay,
                nameof(PlayerStore.SaveGame) => ActorNightCall.Save,
                _ => throw new InvalidOperationException("Unexpected actor night observation method")
            };
            __state = ObserveActorNightCall(kind, __instance);
        }
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ActorNightObservation __state, bool __runOriginal) => CompletedActorNightCall(__state, __runOriginal);
        [HarmonyPriority(Priority.Last)]
        private static Exception Finalizer(ActorNightObservation __state, Exception __exception) => FinalizedActorNightCall(__state, __exception);
    }

    [HarmonyPatch(typeof(LotteryHelper), nameof(LotteryHelper.OnDrawWiningNumber))]
    private static class ActorNightLotterySourceObserver
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(out ActorNightObservation __state) => __state = ObserveActorNightCall(ActorNightCall.Lottery, null);
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ActorNightObservation __state, bool __runOriginal) => CompletedActorNightCall(__state, __runOriginal);
        [HarmonyPriority(Priority.Last)]
        private static Exception Finalizer(ActorNightObservation __state, Exception __exception) => FinalizedActorNightCall(__state, __exception);
    }

    private void CheckActorNightQa(int day)
    {
        Require(host && ActorNightSourceEnabled, "actor night checkpoint check outside isolated Host QA");
        var session = ActorNightSourceSession();
        Require(session != null && actorNightSourceDays.TryGetValue(day, out var night) &&
            ReferenceEquals(night.Session, session), "actor night checkpoint has no native source observations");
        // Fetch separately because Require is intentionally not a flow-analysis
        // assertion and must not hide a missing dictionary entry.
        night = actorNightSourceDays[day];
        Require(!session.NightActive && !session.TransitionActive && !session.Faulted && !session.Terminal &&
            session.VerifiedCheckpoint?.Day == day + 1 && session.VerifiedCheckpoint.Run == session.Run.Run &&
            session.VerifiedCheckpoint.Slot == session.NativeSaveSlot && StoreStation.Instance.dayCounter == day + 1,
            "actor night observation is not followed by its verified native checkpoint");
        Require(night.Errors.Count == 0, "actor night source failure: " + string.Join("; ", night.Errors));
        for (int i = 0; i < night.Entered.Length; i++)
        {
            int expected = i == (int)ActorNightCall.Lottery ? night.ExpectedLottery : 1;
            Require(night.Entered[i] == expected && night.CompletedOriginal[i] == expected,
                "actor night " + (ActorNightCall)i + " count mismatch: entered=" + night.Entered[i] +
                " originalCompleted=" + night.CompletedOriginal[i] + " expected=" + expected);
        }
        Record("PASS actor-night-source-counts", "day=" + day + "->" + (day + 1) +
            " hostSleep=" + night.CompletedOriginal[(int)ActorNightCall.HostSleep] +
            " partnerSleep=" + night.CompletedOriginal[(int)ActorNightCall.PartnerSleep] +
            " worldNight=" + night.CompletedOriginal[(int)ActorNightCall.WorldNight] +
            " lottery=" + night.CompletedOriginal[(int)ActorNightCall.Lottery] +
            " save=" + night.CompletedOriginal[(int)ActorNightCall.Save] +
            " dayEnd=" + night.CompletedOriginal[(int)ActorNightCall.DayEnd] +
            " beginDay=" + night.CompletedOriginal[(int)ActorNightCall.BeginDay] +
            " startDay=" + night.CompletedOriginal[(int)ActorNightCall.StartDay] +
            " expectedLottery=" + night.ExpectedLottery + " source=original-method-postfix runOriginal=true");
    }
}
