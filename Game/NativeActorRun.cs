using System;
using System.IO;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// R2 ownership adapter. Activation belongs to the cooperative run lifecycle;
// constructing this adapter neither loads a slot nor installs native patches.
// The host's original fields stay canonical. Only an explicit partner scope
// redirects accesses to Partner, including scopes captured by an activity.
internal sealed class NativeActorRun : IDisposable
{
    internal enum Slot : byte { Host, Partner }
    private sealed class Invocation
    {
        internal readonly Slot Actor;
        internal Invocation(Slot actor) { Actor = actor; }
    }

    private sealed class Binding : IDisposable
    {
        private NativeActorRun owner;
        internal readonly Slot Actor;
        internal readonly Binding Previous;
        internal Binding(NativeActorRun owner, Slot actor, Binding previous)
        { this.owner = owner; Actor = actor; Previous = previous; }
        public void Dispose()
        {
            if (owner == null) return;
            owner.CheckThread();
            if (!ReferenceEquals(owner.current, this))
                throw new InvalidOperationException("Actor scopes must close in reverse order.");
            owner.current = Previous;
            owner = null;
        }
    }

    private readonly PlayerStore store;
    private readonly int saveSlot;
    private readonly string nativeRunIdentity;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly ActivityScope<Invocation> activities;
    private Binding current;
    private volatile bool disposed;
    internal Guid Run { get; }
    internal NativeActorState Partner { get; }
    internal Slot CurrentActor => current?.Actor ?? Slot.Host;

    internal NativeActorRun(PlayerStore existingStore, NativeActorState partner)
    {
        if (existingStore == null) throw new ArgumentNullException(nameof(existingStore));
        if (existingStore.saveSlotId < 0 || !Guid.TryParse(existingStore.runID, out var run) || run == Guid.Empty)
            throw new InvalidDataException("Actor ownership requires an identified cooperative run.");
        store = existingStore;
        saveSlot = existingStore.saveSlotId;
        nativeRunIdentity = existingStore.runID;
        Run = run;
        Partner = (partner ?? throw new ArgumentNullException(nameof(partner))).Clone();
        activities = new ActivityScope<Invocation>(invocation => Enter(invocation.Actor));
    }

    // NativeActorFieldAccess uses this resolver. Historical PlayerStore values,
    // a menu descriptor and other run instances must retain their own fields.
    internal NativeActorState Resolve(PlayerStore source)
    {
        if (disposed || !ReferenceEquals(source, store)) return null;
        CheckThread();
        if (CurrentActor != Slot.Partner) return null;
        CheckIdentity();
        return Partner;
    }

    internal IDisposable Enter(Slot actor)
    {
        CheckActive();
        if (actor is not (Slot.Host or Slot.Partner)) throw new ArgumentOutOfRangeException(nameof(actor));
        var binding = new Binding(this, actor, current);
        current = binding;
        return binding;
    }

    internal void Execute(Slot actor, Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        using (Enter(actor)) action();
    }

    internal ActivityToken RegisterActivity(uint activity, Slot actor)
    {
        CheckActive();
        // An actor command can open a new dialogue/device activity. The shared
        // scope ledger rejects replacing an executing identity, not registering
        // an unrelated child activity while its originating actor is known.
        if (actor is not (Slot.Host or Slot.Partner)) throw new ArgumentOutOfRangeException(nameof(actor));
        return activities.Register(activity, new Invocation(actor));
    }

    internal Func<bool> Capture(ActivityToken activity, Action callback)
    {
        CheckActive();
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        // Each invocation enters and exits its own actor context. A coroutine
        // adapter must use this around EACH MoveNext, not across a yield.
        var invoke = activities.Capture(activity, _ => callback());
        return () => { CheckThread(); return !disposed && invoke(); };
    }

    internal void RetireActivity(ActivityToken activity)
    {
        CheckActive();
        activities.Close(activity);
    }

    internal void RetireScene()
    {
        CheckActive();
        if (current != null) throw new InvalidOperationException("Cannot retire an executing actor scene.");
        activities.NewEpoch();
    }

    // Encoding is side-effect-free. The persistence adapter must include this
    // envelope in the SAME verified native world checkpoint, plus carried roots
    // and activity effects. This personal-state envelope alone is not a save.
    internal JObject CapturePersonalCheckpoint()
    {
        CheckActive();
        if (current != null) throw new InvalidOperationException("Capture actors only at a settled world boundary.");
        return new JObject { ["schema"] = 1, ["run"] = Run.ToString("D"),
            ["actors"] = new JArray(NativeActorState.Capture(store).Serialize(), Partner.Serialize()) };
    }

    internal static NativeActorRun RestorePersonalCheckpoint(PlayerStore existingStore, JToken value)
    {
        if (existingStore == null) throw new ArgumentNullException(nameof(existingStore));
        if (value is not JObject envelope || envelope.Count != 3 || envelope["schema"]?.Type != JTokenType.Integer ||
            envelope["schema"].ToString() != "1" || envelope["run"]?.Type != JTokenType.String ||
            !Guid.TryParse((string)envelope["run"], out var run) || run == Guid.Empty ||
            !Guid.TryParse(existingStore.runID, out var nativeRun) || run != nativeRun ||
            envelope["actors"] is not JArray actors || actors.Count != 2)
            throw new InvalidDataException("Invalid cooperative personal checkpoint.");
        var host = NativeActorState.Deserialize(actors[0]);
        var partner = NativeActorState.Deserialize(actors[1]);
        // Never silently repair a mismatched host/world pair or overwrite it
        // with data from another checkpoint; the native slot remains untouched.
        if (!JToken.DeepEquals(host.Serialize(), NativeActorState.Capture(existingStore).Serialize()))
            throw new InvalidDataException("Personal checkpoint does not match the native host state.");
        return new NativeActorRun(existingStore, partner);
    }

    public void Dispose()
    {
        CheckThread();
        if (disposed) return;
        if (current != null) throw new InvalidOperationException("Cannot dispose an executing actor run.");
        activities.NewEpoch();
        disposed = true;
    }

    private void CheckActive()
    {
        CheckThread();
        if (disposed) throw new ObjectDisposedException(nameof(NativeActorRun));
        CheckIdentity();
    }
    private void CheckIdentity()
    {
        if (store.saveSlotId != saveSlot || !string.Equals(store.runID, nativeRunIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException("The native store no longer belongs to this actor run.");
    }
    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Actor state belongs to its owning game thread.");
    }
}
