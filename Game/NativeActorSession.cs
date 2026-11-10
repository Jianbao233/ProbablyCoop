using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ProbablyCoop;

// This is a native run lifetime, not a network connection lifetime. Closing a
// room must not discard the partner, their carry or a partially attempted night.
internal sealed class NativeActorSession : IDisposable
{
    internal static NativeActorSession Current { get; private set; }
    internal PlayerStore Store { get; }
    internal int NativeSaveSlot { get; }
    internal NativeActorRun Run { get; }
    internal NativeActorCarry Carry { get; }
    internal NativeActorActivities Activities { get; }
    internal NativeActorPersonalUi PersonalUi { get; }
    internal NativeActorNight Night { get; }
    internal NativeActorCheckpoint.Envelope VerifiedCheckpoint { get; private set; }
    internal bool Terminal { get; private set; }
    internal bool Faulted { get; private set; }
    internal string Failure { get; private set; }
    internal string Ending { get; private set; }
    internal bool CreatedFromArmedRun { get; }
    internal bool Hydrating { get; set; }
    internal bool NightActive { get; set; }
    internal bool TransitionActive { get; set; }
    internal bool InPersonalNight { get; set; }
    internal readonly HashSet<int> AttemptedNights = new HashSet<int>();
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly Harmony harmony;
    private bool fieldsInstalled, carryInstalled, nightInstalled, disposed;

    private NativeActorSession(PlayerStore store, NativeActorRun run, EmporiumEntry entry, bool createdFromArmedRun)
    {
        if (Current != null) throw new InvalidOperationException("A native actor session already exists.");
        Store = store ?? throw new ArgumentNullException(nameof(store));
        NativeSaveSlot = store.saveSlotId;
        Run = run ?? throw new ArgumentNullException(nameof(run));
        CreatedFromArmedRun = createdFromArmedRun;
        var loaded = ModLoader.Mods.Single(mod => mod.Assembly == typeof(NativeActorSession).Assembly &&
            mod.Manifest?.ID == "probably_coop");
        harmony = loaded.HarmonyInstance ?? throw new InvalidOperationException("Missing framework Harmony.");
        try
        {
            Carry = new NativeActorCarry(entry, () => Run.CurrentActor, NativeActorRun.Slot.Host);
            Activities = new NativeActorActivities(run);
            PersonalUi = new NativeActorPersonalUi(run);
            Night = new NativeActorNight(run.Run.ToString("N") + "/" + Guid.NewGuid().ToString("N"));
            Current = this;
            fieldsInstalled = true; NativeActorFieldAccess.Install(harmony, run.Resolve);
            carryInstalled = true;
            NativeActorCarryAccess.Install(harmony, source => ReferenceEquals(source, Carry.Source) ? Carry : null);
            nightInstalled = true; NativeActorNight.Install(harmony);
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            CleanupPatches(failures);
            TryCleanup(() => PersonalUi?.Dispose(), failures);
            TryCleanup(() => Activities?.Dispose(), failures);
            TryCleanup(() => Carry?.DisposeForSceneUnload(), failures);
            TryCleanup(Run.Dispose, failures);
            if (ReferenceEquals(Current, this)) Current = null;
            if (failures.Count > 1) throw new AggregateException("Cooperative session initialization and cleanup failed.", failures);
            throw;
        }
    }

    internal static NativeActorSession EnsureForExisting(PlayerStore store)
    {
        var session = Current;
        if (session == null || !ReferenceEquals(session.Store, store))
            throw new InvalidOperationException("This cooperative run has no restored two-actor checkpoint; create a new cooperative run.");
        session.Check();
        return session;
    }

    internal static NativeActorSession EnsureCreating(PlayerStore store)
    {
        if (!CoopRun.IsCreating(store)) throw new InvalidOperationException("Only an armed native InitialSave can create actors.");
        if (Current != null) return EnsureForExisting(store);
        var partner = NativeActorState.CreateDefault();
        partner.playerName = "Partner";
        return new NativeActorSession(store, new NativeActorRun(store, partner), EmporiumEntry.Instance, true);
    }

    internal static NativeActorSession Restore(PlayerStore store, NativeActorCheckpoint.Envelope checkpoint)
    {
        if (!CoopRun.IsRegistered(store)) throw new InvalidOperationException("Actor restoration requires a registered cooperative run.");
        if (Current != null) throw new InvalidOperationException("Retire the previous native scene before restoring actors.");
        var result = new NativeActorSession(store, NativeActorRun.RestorePersonalCheckpoint(store, checkpoint.Personal), EmporiumEntry.Instance, false);
        result.Hydrating = true;
        return result;
    }

    internal IDisposable Enter(NativeActorRun.Slot actor)
    {
        Check();
        // Read-only ending presentation may still require the actor context.
        // Mutation admission belongs to the world command/transition gate.
        return Run.Enter(actor);
    }

    internal bool CanCommit => !disposed && !Terminal && !Faulted && !Hydrating && !NightActive &&
        !Activities.HasExecuting && !Activities.HasActiveBusiness && Run.CurrentActor == NativeActorRun.Slot.Host &&
        !(NativeWorld.Host?.PartnerHasHand ?? false) &&
        !(ItemMouseDragHandler.current?.IsDraggingItem ?? false) &&
        !(ItemMultiSelectHandler.current?.IsGroupDragging ?? false);

    internal void SetVerified(NativeActorCheckpoint.Envelope value)
    {
        Check();
        if (value == null || value.Run != Run.Run || value.Slot != Store.saveSlotId ||
            value.Day != StoreStation.Instance.dayCounter) throw new InvalidOperationException("Verified checkpoint identity changed.");
        VerifiedCheckpoint = value;
    }
    internal void MarkTerminal(string ending)
    { Check(); Terminal = true; Ending = ending ?? ""; }
    internal void ContinueNativeEnding()
    {
        Check();
        if (Ending == "buyout" || Ending == "buyout_mortgage") { Terminal = false; Ending = null; }
    }
    internal void MarkFault(Exception error)
    { CheckThread(); Faulted = true; Failure = error?.Message ?? "Native cooperative lifecycle failed."; }

    internal void RetireScene()
    {
        Check();
        if (Activities.HasExecuting) throw new InvalidOperationException("Retire scene after its synchronous activity returns.");
        Activities.RetireScene();
        Run.RetireScene();
    }

    public void Dispose()
    {
        CheckThread();
        if (disposed) return;
        if (Activities.HasExecuting || NightActive) throw new InvalidOperationException("Cannot dispose an executing native run.");
        // Run first checks that no caller still owns a synchronous actor scope.
        // A fault may itself be malformed personal state. Cleanup must not
        // serialize/validate it; RetireScene checks scope ownership directly.
        Run.RetireScene();
        Activities.RetireScene(); Run.Dispose();
        var failures = new List<Exception>();
        TryCleanup(PersonalUi.Dispose, failures); TryCleanup(Activities.Dispose, failures);
        CleanupPatches(failures);
        TryCleanup(Carry.DisposeForSceneUnload, failures);
        disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        if (failures.Count > 0) throw new AggregateException("Cooperative session cleanup failed; actor routing is disabled.", failures);
    }

    private void CleanupPatches(List<Exception> failures)
    {
        if (nightInstalled) TryCleanup(() => { NativeActorNight.Uninstall(harmony); nightInstalled = false; }, failures);
        if (carryInstalled) TryCleanup(() => { NativeActorCarryAccess.Uninstall(harmony); carryInstalled = false; }, failures);
        if (fieldsInstalled) TryCleanup(() => { NativeActorFieldAccess.Uninstall(harmony); fieldsInstalled = false; }, failures);
    }
    private static void TryCleanup(Action action, List<Exception> failures)
    { try { action(); } catch (Exception error) { failures.Add(error); } }

    private void Check()
    {
        CheckThread();
        if (disposed || !ReferenceEquals(Current, this)) throw new ObjectDisposedException(nameof(NativeActorSession));
        if (!Guid.TryParse(Store.runID, out var id) || id != Run.Run)
            throw new InvalidOperationException("Native actor run identity changed.");
    }
    private void CheckThread()
    { if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Native session belongs to the game thread."); }
}
