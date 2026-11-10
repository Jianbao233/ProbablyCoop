using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProbablyCoop;

// Installed explicitly by NativeActorSession with the framework-owned Harmony.
// NativeActorNightIntegration invokes this coordinator inside the original
// EndNight boundary; the rest of the native world transition runs once.
internal sealed class NativeActorNight
{
    internal enum Result { Completed, AlreadyCompleted, Terminal }
    internal enum AttemptState { Running, Completed, Terminal, Faulted }

    internal sealed class Attempt
    {
        internal readonly int Day;
        internal AttemptState State { get; private set; }
        internal int CompletedActorMask { get; private set; }
        internal bool LotteryRequested { get; private set; }
        internal bool LotteryAttempted { get; private set; }
        internal bool LotteryCompleted { get; private set; }
        internal Exception Failure { get; private set; }

        internal Attempt(int day) { Day = day; State = AttemptState.Running; }
        internal void MarkActor(int slot) => CompletedActorMask |= 1 << slot;
        internal void RequestLottery() => LotteryRequested = true;
        internal void StartLottery() => LotteryAttempted = true;
        internal void FinishLottery() => LotteryCompleted = true;
        internal void Finish(AttemptState state) => State = state;
        internal void Fail(Exception failure) { Failure = failure; State = AttemptState.Faulted; }
    }

    private sealed class Scope
    {
        internal Attempt Attempt;
        internal Func<bool> IsTerminal;
        internal bool InPersonalPhase;
    }

    private static readonly MethodInfo Sleep = AccessTools.Method(typeof(HealthData), nameof(HealthData.OnSleep));
    private static readonly MethodInfo Draw = AccessTools.Method(typeof(LotteryHelper), nameof(LotteryHelper.OnDrawWiningNumber));
    private static readonly MethodInfo Replacement = AccessTools.Method(typeof(NativeActorNight), nameof(RequestLottery));
    private static readonly MethodInfo Transform = AccessTools.Method(typeof(NativeActorNight), nameof(ReplaceLotteryCall));
    [ThreadStatic] private static Scope current;

    private readonly Dictionary<int, Attempt> attempts = new Dictionary<int, Attempt>();
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    internal string RunEpoch { get; }

    // One coordinator is retained for the entire run epoch, not recreated for each
    // button click. A failed attempt is never retried against partly-mutated state.
    // After checkpoint restoration the caller creates a NEW epoch/coordinator.
    internal NativeActorNight(string runEpoch)
    {
        if (string.IsNullOrWhiteSpace(runEpoch)) throw new ArgumentException("A run epoch is required.", nameof(runEpoch));
        RunEpoch = runEpoch;
    }

    internal Attempt GetAttempt(int day) => attempts.TryGetValue(day, out var value) ? value : null;

    internal static bool IsInstalled => Harmony.GetPatchInfo(Sleep)?.Transpilers.Any(p => p.PatchMethod == Transform) == true;

    internal static void Install(Harmony harmony)
    {
        if (harmony == null) throw new ArgumentNullException(nameof(harmony));
        if (current != null) throw new InvalidOperationException("Cannot patch during an actor night phase.");
        if (IsInstalled) throw new InvalidOperationException("Actor night patch is already installed.");
        harmony.Patch(Sleep, transpiler: new HarmonyMethod(Transform));
        if (!IsInstalled) throw new InvalidOperationException("Actor night transpiler was not installed.");
    }

    internal static void Uninstall(Harmony harmony)
    {
        if (harmony == null) throw new ArgumentNullException(nameof(harmony));
        if (current != null) throw new InvalidOperationException("Cannot unpatch during an actor night phase.");
        harmony.Unpatch(Sleep, Transform); // Remove only this patch, not another mod's patches.
    }

    // Replaces exactly one call, preserving branches/exception block metadata.
    // All original health logic and the original day-of-week condition remain.
    internal static IEnumerable<CodeInstruction> ReplaceLotteryCall(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var matches = code.Where(i => i.opcode == OpCodes.Call && Equals(i.operand, Draw)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("HealthData.OnSleep lottery boundary changed; expected exactly one call.");
        int index = code.IndexOf(matches[0]);
        code[index] = new CodeInstruction(code[index]) { opcode = OpCodes.Call, operand = Replacement };
        return code;
    }

    // This scope is strictly synchronous: nothing here may yield or await.
    // enterActor must bind ALL personal field/static queries for that slot and
    // restore them in IDisposable.Dispose; merely swapping healthData is unsafe.
    // The scope factory must itself restore any partial entry if it throws.
    // isCurrentNight verifies the caller's run epoch/day/phase identity.
    // isTerminal is authoritative run state, never just whether an ending UI is visible.
    // Caller MUST stop the rest of EndNight/BeginDay/Save on Terminal or exception.
    // AlreadyCompleted is NOT permission to replay later world stages: the outer
    // day coordinator still owns the world phase/effect journal and retry policy.
    internal Result ExecuteForDay(int day, HealthData hostHealth, HealthData partnerHealth,
        Func<int, IDisposable> enterActor, Func<bool> isCurrentNight, Func<bool> isTerminal)
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Actor night must run on its owning game thread.");
        if (current != null) throw new InvalidOperationException("Nested actor night execution is not supported.");
        if (!IsInstalled) throw new InvalidOperationException("Actor night patch is not installed; refusing unguarded OnSleep.");
        if (day < 0) throw new ArgumentOutOfRangeException(nameof(day));
        if (hostHealth == null || partnerHealth == null || ReferenceEquals(hostHealth, partnerHealth))
            throw new ArgumentException("Two distinct, existing actor health states are required.");
        if (enterActor == null) throw new ArgumentNullException(nameof(enterActor));
        if (isCurrentNight == null) throw new ArgumentNullException(nameof(isCurrentNight));
        if (isTerminal == null) throw new ArgumentNullException(nameof(isTerminal));

        RequireCurrent(isCurrentNight); // A stale request must not consume this epoch's valid day attempt.
        if (attempts.TryGetValue(day, out var prior))
        {
            if (prior.State == AttemptState.Completed) return Result.AlreadyCompleted;
            if (prior.State == AttemptState.Terminal) return Result.Terminal;
            throw new InvalidOperationException("This night was already attempted; restore a verified checkpoint before retrying.", prior.Failure);
        }

        var attempt = new Attempt(day);
        attempts.Add(day, attempt); // Record before any mutating action, including actor entry.
        var scope = new Scope { Attempt = attempt, IsTerminal = isTerminal };
        current = scope;
        try
        {
            RequireCurrent(isCurrentNight);
            if (Stop(attempt, isTerminal)) return Result.Terminal;
            var actors = new[] { hostHealth, partnerHealth };
            for (int slot = 0; slot < actors.Length; slot++)
            {
                RequireCurrent(isCurrentNight);
                if (Stop(attempt, isTerminal)) return Result.Terminal;
                using (var actorScope = enterActor(slot) ?? throw new InvalidOperationException("Actor scope must restore its context."))
                {
                    RequireCurrent(isCurrentNight);
                    if (Stop(attempt, isTerminal)) return Result.Terminal;
                    scope.InPersonalPhase = true;
                    try { actors[slot].OnSleep(); }
                    finally { scope.InPersonalPhase = false; }
                    attempt.MarkActor(slot);
                    if (Stop(attempt, isTerminal)) return Result.Terminal;
                }
            }

            RequireCurrent(isCurrentNight);
            if (Stop(attempt, isTerminal)) return Result.Terminal;
            if (attempt.LotteryRequested)
            {
                // IsLotteryUnlocked itself depends on a personal gambling perk.
                // The native conditional may have requested a draw while partner
                // context was active, so confirm eligibility under the host context.
                using (var actorScope = enterActor(0) ?? throw new InvalidOperationException("Host scope must restore its context."))
                {
                    RequireCurrent(isCurrentNight);
                    if (Stop(attempt, isTerminal)) return Result.Terminal;
                    if (LotteryHelper.IsLotteryUnlocked())
                    {
                        attempt.StartLottery(); // An exception may occur after partial native mutation.
                        LotteryHelper.OnDrawWiningNumber();
                        attempt.FinishLottery();
                    }
                    if (Stop(attempt, isTerminal)) return Result.Terminal;
                }
            }
            RequireCurrent(isCurrentNight);
            if (Stop(attempt, isTerminal)) return Result.Terminal;
            attempt.Finish(AttemptState.Completed);
            return Result.Completed;
        }
        catch (Exception exception)
        {
            attempt.Fail(exception);
            throw; // Never advertise success or repeat a partially-executed night.
        }
        finally { current = null; }
    }

    private static void RequireCurrent(Func<bool> isCurrentNight)
    {
        if (!isCurrentNight()) throw new InvalidOperationException("Actor night run/day/phase is no longer current.");
    }

    private static bool Stop(Attempt attempt, Func<bool> isTerminal)
    {
        if (attempt.State == AttemptState.Terminal) return true;
        if (!isTerminal()) return false;
        attempt.Finish(AttemptState.Terminal);
        return true;
    }

    private static void RequestLottery()
    {
        var scope = current;
        if (scope == null)
        {
            LotteryHelper.OnDrawWiningNumber(); // Unchanged original behavior outside this explicit cooperative scope.
            return;
        }
        if (!scope.InPersonalPhase) throw new InvalidOperationException("Unexpected recursive OnSleep during world lottery execution.");
        if (!Stop(scope.Attempt, scope.IsTerminal)) scope.Attempt.RequestLottery();
    }
}
