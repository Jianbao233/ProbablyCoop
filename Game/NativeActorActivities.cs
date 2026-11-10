using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using HarmonyLib;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Activities bind continuation execution, never a whole Unity Update or a
// scope spanning a yield. The native owner supplies its actual lifetime guard.
internal sealed class NativeActorActivities : IDisposable
{
    private readonly NativeActorRun run;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<Key, Lease> active = new Dictionary<Key, Lease>();
    private uint nextId;
    private int executing;
    private bool disposed;
    private Lease current;
    internal bool HasExecuting => executing != 0;
    internal bool HasActiveBusiness { get { CheckThread(); Prune(); return active.Values.Any(value => value.BlocksCommit); } }
    internal Lease Current => current;

    internal NativeActorActivities(NativeActorRun run) => this.run = run ?? throw new ArgumentNullException(nameof(run));

    internal Lease Begin(object source, string channel, Func<bool> alive, bool blocksCommit = false) =>
        Begin(source, channel, run.CurrentActor, alive, blocksCommit);

    internal Lease Begin(object source, string channel, NativeActorRun.Slot actor, Func<bool> alive, bool blocksCommit = false)
    {
        CheckThread();
        if (disposed) throw new ObjectDisposedException(nameof(NativeActorActivities));
        if (source == null || string.IsNullOrEmpty(channel) || channel.Length > 128 || alive == null)
            throw new ArgumentException("Native activity needs an owner, channel and lifetime guard.");
        Prune();
        var key = new Key(source, channel);
        if (active.TryGetValue(key, out var previous)) previous.Dispose();
        if (active.Count >= 256) throw new InvalidOperationException("Too many live native actor activities.");
        var token = run.RegisterActivity(checked(++nextId), actor);
        var lease = new Lease(this, key, token, actor, alive, blocksCommit);
        active.Add(key, lease);
        return lease;
    }

    internal Lease Find(object source, string channel)
    {
        CheckThread();
        if (source == null || channel == null || disposed) return null;
        if (!active.TryGetValue(new Key(source, channel), out var lease)) return null;
        return lease.IsAlive ? lease : null;
    }

    internal void Retire(object source, string channel) { Find(source, channel)?.Dispose(); }
    internal void RetireSource(object source)
    {
        CheckThread();
        foreach (var lease in active.Values.Where(value => ReferenceEquals(value.Source, source)).ToArray()) lease.Dispose();
    }
    internal void RetireScene()
    {
        CheckThread();
        foreach (var lease in active.Values.ToArray()) lease.Dispose();
    }
    public void Dispose()
    {
        CheckThread();
        if (disposed) return;
        disposed = true;
        RetireScene();
    }
    private void Prune() { foreach (var lease in active.Values.ToArray()) _ = lease.IsAlive; }
    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Native activities belong to the game thread.");
    }
    internal readonly struct Key : IEquatable<Key>
    {
        internal readonly object Source;
        internal readonly string Channel;
        internal Key(object source, string channel) { Source = source; Channel = channel; }
        public bool Equals(Key other) => ReferenceEquals(Source, other.Source) && Channel == other.Channel;
        public override bool Equals(object value) => value is Key other && Equals(other);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(Source) ^ Channel.GetHashCode();
    }

    internal sealed class Lease : IDisposable
    {
        private readonly NativeActorActivities owner;
        private readonly Key key;
        private readonly ActivityToken token;
        private readonly Func<bool> alive;
        private int depth;
        private bool retired, released, checkingAlive;
        internal readonly NativeActorRun.Slot Actor;
        internal readonly bool BlocksCommit;
        internal object Source => key.Source;
        internal bool IsDialogueConsequence => key.Channel == "dialogue-end";
        internal bool IsConsequenceContinuation => key.Channel == "consequence-continuation";
        internal bool HasScheduledContinuation { get; private set; }
        internal void MarkScheduled() { owner.CheckThread(); HasScheduledContinuation = true; }
        internal bool IsAlive
        {
            get
            {
                owner.CheckThread();
                if (retired || owner.disposed) return false;
                if (checkingAlive) throw new InvalidOperationException("Native activity lifetime predicates cannot recursively evaluate themselves.");
                bool valid;
                checkingAlive = true;
                try
                {
                    // Lifetimes can query personal carry/permissions. A Host
                    // checkpoint or another actor's Begin must not evaluate a
                    // Partner choice against Host inventory permissions.
                    using (owner.run.Enter(Actor)) valid = alive();
                }
                finally { checkingAlive = false; }
                if (valid && !retired && !owner.disposed) return true;
                Dispose(); return false;
            }
        }
        internal Lease(NativeActorActivities owner, Key key, ActivityToken token, NativeActorRun.Slot actor, Func<bool> alive, bool blocksCommit)
        { this.owner = owner; this.key = key; this.token = token; Actor = actor; this.alive = alive; BlocksCommit = blocksCommit; }

        internal Action Wrap(Action callback)
        {
            if (callback == null) return null;
            var invoke = Capture(callback);
            return () => invoke();
        }
        // A committed consequence can schedule its own later phase after the
        // dialogue's native once flag has been set. Give that phase a new,
        // once-only lifetime instead of extending/replaying the original line.
        internal Action ForkOnce(Action callback)
        {
            if (callback == null) return null;
            owner.CheckThread();
            var child = owner.Begin(new object(), "consequence-continuation", Actor, () => true, true);
            bool used = false;
            return child.Wrap(() =>
            {
                if (used) return;
                used = true;
                try { callback(); }
                catch (Exception error)
                {
                    var session = NativeActorSession.Current;
                    if (session != null && ReferenceEquals(session.Run, owner.run)) session.MarkFault(error);
                    throw;
                }
                finally { child.Dispose(); }
            });
        }
        internal Action<T> Wrap<T>(Action<T> callback)
        {
            if (callback == null) return null;
            return value => Capture(() => callback(value))();
        }
        internal Func<T> Wrap<T>(Func<T> callback, T stale = default)
        {
            if (callback == null) return null;
            return () => { T value = stale; Capture(() => value = callback())(); return value; };
        }
        internal Func<T, TResult> Wrap<T, TResult>(Func<T, TResult> callback, TResult stale = default)
        {
            if (callback == null) return null;
            return arg => { TResult value = stale; Capture(() => value = callback(arg))(); return value; };
        }
        internal Func<bool> Capture(Action callback)
        {
            owner.CheckThread();
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (retired || owner.disposed) return () => { owner.CheckThread(); return false; };
            var call = owner.run.Capture(token, callback);
            return () =>
            {
                if (!IsAlive) return false;
                var previous = owner.current;
                owner.current = this; owner.executing++; depth++;
                try { return call(); }
                finally
                {
                    depth--; owner.executing--; owner.current = previous;
                    if (retired && depth == 0) Release();
                }
            };
        }
        internal IEnumerator WrapEnumerator(IEnumerator iterator) => iterator == null ? null : new Continuation(this, iterator);
        // For native methods patched at their entry/exit. The lease is retired
        // by its owner, not by leaving this individual method invocation.
        internal IDisposable EnterNative()
        {
            if (!IsAlive) return null;
            var scope = owner.run.Enter(Actor);
            var previous = owner.current;
            owner.current = this; owner.executing++; depth++;
            return new NativeBinding(this, scope, previous);
        }
        private sealed class NativeBinding : IDisposable
        {
            private Lease lease;
            private readonly IDisposable scope;
            private readonly Lease previous;
            internal NativeBinding(Lease lease, IDisposable scope, Lease previous) { this.lease = lease; this.scope = scope; this.previous = previous; }
            public void Dispose()
            {
                if (lease == null) return;
                var value = lease;
                scope.Dispose();
                lease = null;
                value.depth--; value.owner.executing--; value.owner.current = previous;
                if (value.retired && value.depth == 0) value.Release();
            }
        }
        public void Dispose()
        {
            owner.CheckThread();
            if (retired) return;
            retired = true;
            if (owner.active.TryGetValue(key, out var existing) && ReferenceEquals(existing, this)) owner.active.Remove(key);
            // Native callbacks routinely close their own window. Invalidate
            // immediately, then retire the ledger only after Execute unwinds.
            if (depth == 0) Release();
        }
        private void Release()
        {
            if (released) return;
            owner.run.RetireActivity(token);
            released = true;
        }
        private sealed class Continuation : IEnumerator, IDisposable
        {
            private readonly Lease lease;
            private readonly IEnumerator iterator;
            private readonly Func<bool> move;
            private bool ended;
            public object Current { get; private set; }
            internal Continuation(Lease lease, IEnumerator iterator)
            {
                this.lease = lease; this.iterator = iterator;
                move = lease.Wrap(() =>
                {
                    bool more = iterator.MoveNext();
                    Current = more ? iterator.Current : null;
                    if (Current is IEnumerator child) Current = lease.WrapEnumerator(child);
                    return more;
                }, false);
            }
            public bool MoveNext()
            {
                if (ended) return false;
                bool more = move();
                if (!more) { ended = true; Current = null; }
                return more;
            }
            public void Reset() => throw new NotSupportedException("Native actor continuations cannot be replayed.");
            public void Dispose()
            {
                if (ended) return;
                ended = true; Current = null;
                // Do not execute native finally/gameplay code after the source
                // lifetime has ended. Live disposal still restores its actor.
                if (iterator is IDisposable disposable) lease.Wrap(disposable.Dispose)();
            }
        }
    }
}

// These are source categories, not item-specific callbacks. Native dialogue
// epochs guard flow continuations; native once-only end actions keep their
// original actor until the native consequence has actually been flushed.
internal static class NativeActorContinuations
{
    private static readonly FieldInfo CustomerEpoch = AccessTools.Field(typeof(DialogUIManager), "conversationEpoch");
    private static readonly FieldInfo PhoneEpoch = AccessTools.Field(typeof(DialoguePhoneUIManager), "conversationEpoch");
    private static readonly FieldInfo PhoneNumber = AccessTools.Field(typeof(PhoneUIManager), "currentNumber");
    private static readonly FieldInfo PhoneCalling = AccessTools.Field(typeof(PhoneUIManager), "isCalling");
    private static readonly FieldInfo PhoneTalking = AccessTools.Field(typeof(PhoneUIManager), "isTalking");
    internal static bool PhoneAlive(PhoneUIManager ui) => ui != null &&
        ((long)PhoneNumber.GetValue(ui) != 0 || (bool)PhoneCalling.GetValue(ui) || (bool)PhoneTalking.GetValue(ui));
    internal static Dialogue Line(object manager) => manager is DialogUIManager customer ? customer.currentDialog : ((DialoguePhoneUIManager)manager).currentDialog;
    private static int Epoch(object manager) => (int)(manager is DialogUIManager ? CustomerEpoch : PhoneEpoch).GetValue(manager);
    internal static NativeActorActivities.Lease BindLine(object manager, bool adoptCaller)
    {
        var session = NativeActorSession.Current;
        var line = Line(manager);
        if (session == null || line == null) return null;
        var activities = session.Activities;
        var existing = activities.Find(line, "dialogue-flow");
        if (!adoptCaller && existing != null) return existing;
        var actor = session.Run.CurrentActor;
        if (existing != null && existing.Actor == actor) return existing;
        int epoch = Epoch(manager);
        var lease = activities.Begin(line, "dialogue-flow", actor,
            () => manager is UnityEngine.Object unity && unity != null && Epoch(manager) == epoch);
        if (line.endAction != null && !line.isEndActionExecuted && activities.Find(line, "dialogue-end") == null)
            activities.Begin(line, "dialogue-end", actor, () => !line.isEndActionExecuted, true);
        return lease;
    }
    internal static NativeActorActivities.Lease Phone(PhoneUIManager ui, bool adoptCaller)
    {
        var session = NativeActorSession.Current;
        if (session == null) return null;
        var lease = session.Activities.Find(ui, "phone-call");
        if (lease != null && (!adoptCaller || lease.Actor == session.Run.CurrentActor)) return lease;
        return session.Activities.Begin(ui, "phone-call", () => PhoneAlive(ui), true);
    }
    internal static void RetireUnscheduledConsequence(Dialogue line)
    {
        if (line == null) return;
        var lease = NativeActorSession.Current?.Activities.Find(line, "dialogue-end");
        if (lease != null && !lease.HasScheduledContinuation) lease.Dispose();
    }
}

[HarmonyPatch]
internal static class NativeActorDialogueLineBinding
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in new[] { typeof(DialogUIManager), typeof(DialoguePhoneUIManager) })
            yield return AccessTools.Method(type, "ContinueConversation");
        yield return AccessTools.Method(typeof(DialoguePhoneUIManager), "StartDialogue");
    }
    private static void Prefix(object __instance, out Dialogue __state) => __state = NativeActorContinuations.Line(__instance);
    private static void Postfix(object __instance, Dialogue __state)
    {
        if (!ReferenceEquals(__state, NativeActorContinuations.Line(__instance))) NativeActorContinuations.RetireUnscheduledConsequence(__state);
        NativeActorContinuations.BindLine(__instance, true);
    }
}

[HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.StartDialogue))]
internal static class NativeActorAcceptedDialogueBinding
{
    private static void Prefix(DialogUIManager __instance, out Dialogue __state) => __state = __instance.currentDialog;
    private static void Postfix(DialogUIManager __instance, bool __result, Dialogue __state)
    {
        if (!__result) return;
        if (!ReferenceEquals(__state, __instance.currentDialog)) NativeActorContinuations.RetireUnscheduledConsequence(__state);
        NativeActorContinuations.BindLine(__instance, true);
    }
}

[HarmonyPatch]
internal static class NativeActorDialogueContinuation
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in new[] { typeof(DialogUIManager), typeof(DialoguePhoneUIManager) })
            foreach (string method in new[] { "ScheduleFlow", "OnCurrentTextDisplayed" }) yield return AccessTools.Method(type, method);
    }
    private static void Prefix(object __instance, MethodBase __originalMethod, out IDisposable __state)
    {
        var lease = NativeActorContinuations.BindLine(__instance, false);
        // Native untracked end-action timers intentionally survive flow epoch
        // changes. ScheduleFlow itself always enters the cancellable flow lease.
        if (__originalMethod.Name == "OnCurrentTextDisplayed")
            lease = NativeActorSession.Current?.Activities.Find(NativeActorContinuations.Line(__instance), "dialogue-end") ?? lease;
        __state = lease?.EnterNative();
    }
    private static Exception Finalizer(Exception __exception, IDisposable __state) { __state?.Dispose(); return __exception; }
}

[HarmonyPatch(typeof(MetaHelper), nameof(MetaHelper.WaitAndDo))]
internal static class NativeActorDelayedAction
{
    private static void Prefix(ref Action action)
    {
        var lease = NativeActorSession.Current?.Activities.Current;
        if (lease == null) return;
        if (lease.IsDialogueConsequence && lease.Source is Dialogue line && !line.isEndActionExecuted)
        { lease.MarkScheduled(); action = lease.Wrap(action); }
        else if (lease.IsDialogueConsequence || lease.IsConsequenceContinuation) action = lease.ForkOnce(action);
        else action = lease.Wrap(action);
    }
}

[HarmonyPatch(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.InterruptDialog))]
internal static class NativeActorPhoneDialogueCancellation
{
    private static void Prefix(DialoguePhoneUIManager __instance, out Dialogue __state) => __state = __instance.currentDialog;
    private static void Postfix(Dialogue __state) => NativeActorContinuations.RetireUnscheduledConsequence(__state);
}

[HarmonyPatch(typeof(StoreUIManager), nameof(StoreUIManager.StartBlackscreenSequence))]
internal static class NativeActorBlackscreenActions
{
    private static void Prefix(bool playFullSequence, ref Action actionDuringBlack, ref Action actionAfterBlack)
    {
        var lease = NativeActorSession.Current?.Activities.Current;
        if (lease == null) return;
        bool fork = lease.IsConsequenceContinuation || lease.IsDialogueConsequence && lease.Source is Dialogue line && line.isEndActionExecuted;
        actionDuringBlack = fork ? lease.ForkOnce(actionDuringBlack) : lease.Wrap(actionDuringBlack);
        // Native BlackScreenSequence exits before this callback in the partial
        // sequence, so do not create an unfinishable pending child for it.
        if (playFullSequence) actionAfterBlack = fork ? lease.ForkOnce(actionAfterBlack) : lease.Wrap(actionAfterBlack);
    }
}

[HarmonyPatch(typeof(Dialogue), nameof(Dialogue.ExecuteEndAction))]
internal static class NativeActorDialogueConsequence
{
    internal sealed class Invocation { internal NativeActorActivities.Lease Lease; internal IDisposable Scope; }
    private static void Prefix(Dialogue __instance, out Invocation __state)
    {
        __state = null;
        if (__instance.isEndActionExecuted) return;
        var activities = NativeActorSession.Current?.Activities;
        var lease = activities?.Find(__instance, "dialogue-end") ?? activities?.Find(__instance, "dialogue-flow");
        if (lease != null) __state = new Invocation { Lease = lease, Scope = lease.EnterNative() };
    }
    private static Exception Finalizer(Exception __exception, Dialogue __instance, Invocation __state)
    {
        __state?.Scope?.Dispose();
        if (__instance.isEndActionExecuted) __state?.Lease?.Dispose();
        return __exception;
    }
}

[HarmonyPatch]
internal static class NativeActorPhoneOrigin
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.AutoCall));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.PressNumber));
    }
    private static void Postfix(PhoneUIManager __instance)
    { if (NativeActorContinuations.PhoneAlive(__instance)) NativeActorContinuations.Phone(__instance, true); }
}

[HarmonyPatch]
internal static class NativeActorPhoneContinuation
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string method in new[] { "StartCalling", "HandleCall", "StartPhoneDialog" }) yield return AccessTools.Method(typeof(PhoneUIManager), method);
    }
    private static void Prefix(PhoneUIManager __instance, out IDisposable __state) => __state = NativeActorContinuations.Phone(__instance, false)?.EnterNative();
    private static Exception Finalizer(Exception __exception, IDisposable __state) { __state?.Dispose(); return __exception; }
}

[HarmonyPatch]
internal static class NativeActorPhoneRetirement
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string method in new[] { "StopCall", "OnPhoneDialogEnd", "NoNumber" }) yield return AccessTools.Method(typeof(PhoneUIManager), method);
    }
    private static void Postfix(PhoneUIManager __instance) => NativeActorSession.Current?.Activities.Retire(__instance, "phone-call");
}
