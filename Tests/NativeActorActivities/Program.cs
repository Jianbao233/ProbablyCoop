using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

var managed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "native-managed-path.txt")).Trim();
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(managed, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
Probe.Run();
return PartnerSetupTests.Run();

internal static class Probe
{
    private static int checks;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); checks++; }
    private static void Reject<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { checks++; return; } throw new Exception("Expected rejection: " + name); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run()
    {
        var store = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        store.saveSlotId = 7; store.runID = Guid.NewGuid().ToString("D");
        var raw = NativeActorState.CreateDefault();
        foreach (var field in typeof(NativeActorState).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            typeof(PlayerStore).GetField(field.Name)?.SetValue(store, field.GetValue(raw));
        using var run = new NativeActorRun(store, NativeActorState.CreateDefault());
        var session = new NativeActorSession(run) { Store = store };
        NativeActorSession.Current = session;
        var activities = session.Activities;
        bool personalAvailable = true;
        var personalLifetime = activities.Begin(new object(), "personal-lifetime", NativeActorRun.Slot.Partner,
            () => personalAvailable && ReferenceEquals(run.Resolve(store), run.Partner), true);
        Check(activities.HasActiveBusiness && personalLifetime.IsAlive && run.CurrentActor == NativeActorRun.Slot.Host,
            "host checkpoint prunes partner lifetime under its own actor and restores host");
        var automaticLifetime = activities.Begin(new object(), "automatic-lifetime", NativeActorRun.Slot.Host,
            () => run.Resolve(store) == null);
        Check(personalLifetime.IsAlive, "host Begin does not retire partner personal source");
        using (run.Enter(NativeActorRun.Slot.Partner))
        {
            Check(automaticLifetime.IsAlive && run.CurrentActor == NativeActorRun.Slot.Partner,
                "partner lifetime checks restore caller after host automatic guard");
            using var newPartner = activities.Begin(new object(), "second-personal", () => true);
            Check(personalLifetime.IsAlive && automaticLifetime.IsAlive, "cross actor Prune preserves both valid owners");
        }
        personalAvailable = false;
        Check(!personalLifetime.IsAlive && !activities.HasActiveBusiness, "expired personal source still retires instead of being kept alive");
        automaticLifetime.Dispose();
        bool guardThrows = true;
        var throwingLifetime = activities.Begin(new object(), "throwing-lifetime", NativeActorRun.Slot.Partner,
            () => guardThrows ? throw new InvalidOperationException("guard failure") : false);
        Reject<InvalidOperationException>(() => _ = throwingLifetime.IsAlive, "lifetime predicate exception propagates");
        Check(run.CurrentActor == NativeActorRun.Slot.Host && !activities.HasExecuting && activities.Current == null,
            "failed lifetime check restores actor without entering business execution");
        guardThrows = false;
        Check(!throwingLifetime.IsAlive, "failed predicate check does not leave its recursion guard stuck");
        NativeActorActivities.Lease recursiveLifetime = null;
        bool recurse = true;
        recursiveLifetime = activities.Begin(new object(), "recursive-lifetime", NativeActorRun.Slot.Partner,
            () => recurse && recursiveLifetime.IsAlive);
        Reject<InvalidOperationException>(() => _ = recursiveLifetime.IsAlive, "recursive predicate rejected without unbounded recursion");
        Check(run.CurrentActor == NativeActorRun.Slot.Host, "recursive lifetime rejection restores actor");
        recurse = false;
        Check(!recursiveLifetime.IsAlive, "recursive lifetime can subsequently retire normally");
        var source = new object(); bool alive = true; int calls = 0;
        var lease = activities.Begin(source, "tool-choice", NativeActorRun.Slot.Partner, () => alive, true);
        Check(activities.HasActiveBusiness && !activities.HasExecuting, "pending business has no executing scope");
        var callback = lease.Wrap(() => { Check(run.CurrentActor == NativeActorRun.Slot.Partner && activities.HasExecuting && activities.Current == lease, "callback origin scope"); calls++; });
        callback(); Check(calls == 1 && run.CurrentActor == NativeActorRun.Slot.Host && !activities.HasExecuting, "scope restores outside callback");
        var failing = lease.Wrap(() => { throw new InvalidOperationException("callback failure"); });
        Reject<InvalidOperationException>(failing, "callback exception propagated");
        Check(run.CurrentActor == NativeActorRun.Slot.Host && !activities.HasExecuting, "exception restores actor");
        var host = activities.Begin(new object(), "automatic", NativeActorRun.Slot.Host, () => true);
        lease.Wrap(() => { host.Wrap(() => Check(run.CurrentActor == NativeActorRun.Slot.Host, "explicit automatic event host"))(); Check(run.CurrentActor == NativeActorRun.Slot.Partner, "nested owner restored"); })();
        var selfClosing = activities.Begin(new object(), "window", NativeActorRun.Slot.Partner, () => true, true);
        var selfAction = selfClosing.Wrap(() => { selfClosing.Dispose(); Check(run.CurrentActor == NativeActorRun.Slot.Partner, "closing own source waits for unwind"); });
        selfAction(); selfAction(); Check(!selfClosing.IsAlive && run.CurrentActor == NativeActorRun.Slot.Host, "closed callback cannot replay");
        var replacement = activities.Begin(source, "tool-choice", NativeActorRun.Slot.Host, () => alive);
        callback(); Check(calls == 1 && !lease.IsAlive && replacement.IsAlive, "replacing source generation invalidates old callbacks");
        alive = false; replacement.Wrap(() => calls++)(); Check(!replacement.IsAlive && calls == 1, "source lifetime invalidates callback");

        var coroutineLease = activities.Begin(new object(), "machine-progress", NativeActorRun.Slot.Partner, () => true);
        IEnumerator Child()
        { Check(run.CurrentActor == NativeActorRun.Slot.Partner, "nested iterator move actor"); yield return "child"; }
        IEnumerator Steps()
        {
            Check(run.CurrentActor == NativeActorRun.Slot.Partner, "first iterator step actor"); yield return Child();
            Check(run.CurrentActor == NativeActorRun.Slot.Partner, "second iterator step actor"); yield return "second";
        }
        var iterator = coroutineLease.WrapEnumerator(Steps());
        Check(iterator.MoveNext() && run.CurrentActor == NativeActorRun.Slot.Host, "yield does not retain scope");
        var child = (IEnumerator)iterator.Current;
        Check(child.MoveNext() && (string)child.Current == "child" && run.CurrentActor == NativeActorRun.Slot.Host, "nested yield does not retain scope");
        Check(iterator.MoveNext() && (string)iterator.Current == "second", "next iterator step works");
        coroutineLease.Dispose(); Check(!iterator.MoveNext(), "retired iterator cannot continue");
        Reject<NotSupportedException>(iterator.Reset, "continuation cannot reset");

        var native = activities.Begin(new object(), "native-method", NativeActorRun.Slot.Partner, () => true);
        using (native.EnterNative())
        {
            Check(run.CurrentActor == NativeActorRun.Slot.Partner && activities.Current == native, "native method scope");
            Action during = () => Check(run.CurrentActor == NativeActorRun.Slot.Partner, "black during captures source actor");
            Action after = () => Check(run.CurrentActor == NativeActorRun.Slot.Partner, "black after captures source actor");
            var args = new object[] { true, during, after };
            typeof(NativeActorBlackscreenActions).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
            during = (Action)args[1]; after = (Action)args[2];
            using (run.Enter(NativeActorRun.Slot.Host)) { during(); after(); Check(run.CurrentActor == NativeActorRun.Slot.Host, "black callbacks restore caller"); }
        }
        Check(run.CurrentActor == NativeActorRun.Slot.Host && !activities.HasExecuting, "native method restores context");
        var pending = native.Wrap(() => calls++); activities.RetireScene(); run.RetireScene(); pending();
        Check(calls == 1 && !activities.HasActiveBusiness, "scene retirement invalidates pending callback");
        bool consequenceExecuted = false;
        var consequence = activities.Begin(new object(), "dialogue-end", NativeActorRun.Slot.Partner, () => !consequenceExecuted, true);
        using (consequence.EnterNative()) { consequenceExecuted = true; Check(run.CurrentActor == NativeActorRun.Slot.Partner, "native once flag changes after origin entry"); }
        Check(!activities.HasActiveBusiness && !consequence.IsAlive, "completed native consequence does not block day commit");
        int dialogueEpoch = 1; var line = new Dialogue { endAction = () => { } };
        var flow = activities.Begin(line, "dialogue-flow", NativeActorRun.Slot.Partner, () => dialogueEpoch == 1);
        var end = activities.Begin(line, "dialogue-end", NativeActorRun.Slot.Partner, () => !line.isEndActionExecuted, true);
        Action delayedEnd = () => { Check(run.CurrentActor == NativeActorRun.Slot.Partner, "untracked end timer retains actor"); line.isEndActionExecuted = true; };
        Action delayedFlow = () => calls++;
        var delayPrefix = typeof(NativeActorDelayedAction).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
        using (end.EnterNative())
        {
            var args = new object[] { delayedEnd }; delayPrefix.Invoke(null, args); delayedEnd = (Action)args[0];
            using (flow.EnterNative()) { args = new object[] { delayedFlow }; delayPrefix.Invoke(null, args); delayedFlow = (Action)args[0]; }
        }
        Check(end.HasScheduledContinuation, "end timer records scheduling separately from typing");
        dialogueEpoch++; delayedFlow(); delayedEnd();
        Check(calls == 1 && line.isEndActionExecuted && !activities.HasActiveBusiness, "flow cancellation preserves scheduled end consequence");
        line = new Dialogue { endAction = () => { } };
        end = activities.Begin(line, "dialogue-end", NativeActorRun.Slot.Partner, () => !line.isEndActionExecuted, true);
        NativeActorContinuations.RetireUnscheduledConsequence(line);
        Check(!end.IsAlive && !activities.HasActiveBusiness, "phone interrupt retires typing line with no scheduled consequence");
        line = new Dialogue { endAction = () => { } };
        end = activities.Begin(line, "dialogue-end", NativeActorRun.Slot.Partner, () => !line.isEndActionExecuted, true); end.MarkScheduled();
        NativeActorContinuations.RetireUnscheduledConsequence(line);
        Check(end.IsAlive && activities.HasActiveBusiness, "phone interrupt retains native scheduled consequence");
        end.Wrap(() => line.isEndActionExecuted = true)();
        Check(!activities.HasActiveBusiness, "scheduled consequence finishes before night commit");
        line = new Dialogue { endAction = () => { } };
        end = activities.Begin(line, "dialogue-end", NativeActorRun.Slot.Partner, () => !line.isEndActionExecuted, true);
        Action phase2 = null;
        Action phase1 = () =>
        {
            Check(run.CurrentActor == NativeActorRun.Slot.Partner, "committed consequence child retains actor");
            Action next = () => Check(run.CurrentActor == NativeActorRun.Slot.Partner, "nested consequence child retains actor");
            var args = new object[] { next }; delayPrefix.Invoke(null, args); phase2 = (Action)args[0];
        };
        using (end.EnterNative())
        {
            line.isEndActionExecuted = true;
            var args = new object[] { phase1 }; delayPrefix.Invoke(null, args); phase1 = (Action)args[0];
        }
        end.Dispose();
        Check(activities.HasActiveBusiness, "committed child has independent pending lifetime");
        phase1(); phase1();
        Check(phase2 != null && activities.HasActiveBusiness, "child can schedule another phase without replaying parent");
        phase2(); phase2();
        Check(!activities.HasActiveBusiness && run.CurrentActor == NativeActorRun.Slot.Host, "consequence chain closes all pending lifetimes");
        line = new Dialogue { endAction = () => { } };
        end = activities.Begin(line, "dialogue-end", NativeActorRun.Slot.Partner, () => !line.isEndActionExecuted, true);
        Action skipped = () => throw new Exception("native partial sequence must not invoke after");
        using (end.EnterNative())
        {
            line.isEndActionExecuted = true;
            var args = new object[] { false, null, skipped };
            typeof(NativeActorBlackscreenActions).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
            Check(ReferenceEquals(args[2], skipped), "partial black sequence creates no after continuation");
        }
        end.Dispose();
        Check(!activities.HasActiveBusiness, "partial black sequence leaves no unfinishable business lease");
        var failureOwner = activities.Begin(new object(), "consequence-continuation", NativeActorRun.Slot.Partner, () => true);
        var failureAction = failureOwner.ForkOnce(() => throw new InvalidOperationException("partial consequence"));
        failureOwner.Dispose();
        Reject<InvalidOperationException>(failureAction, "failed delayed consequence propagates");
        Check(session.Faulted && !activities.HasActiveBusiness && run.CurrentActor == NativeActorRun.Slot.Host, "failed consequence faults session before allowing checkpoint");
        Exception threadError = null;
        var thread = new Thread(() => { try { activities.Begin(new object(), "thread", () => true); } catch (Exception e) { threadError = e; } });
        thread.Start(); Check(thread.Join(5000) && threadError is InvalidOperationException, "cross thread activity rejected");

        var ui = session.PersonalUi;
        var receiver = new NativeActorPersonalUi.Receiver();
        var empty = NativeActorPersonalUi.Notifications.Read(ui.CaptureNotifications(), run.Run);
        int displayed = 0; receiver.Apply(empty, (_, _) => displayed++); Check(displayed == 0, "empty notification stream");
        ui.NotifyPartner("one", "sec"); ui.NotifyPartner("two", "default");
        var wire = ui.CaptureNotifications(); var batch = NativeActorPersonalUi.Notifications.Read(wire, run.Run);
        receiver.Apply(batch, (_, _) => displayed++); receiver.Apply(batch, (_, _) => displayed++);
        Check(displayed == 2 && receiver.Through == 2 && receiver.Missed == 0, "duplicate snapshots do not replay notifications");
        receiver.Apply(empty, (_, _) => displayed++); Check(displayed == 2, "old snapshot does not replay notification");
        var wrong = (JObject)wire.DeepClone(); wrong["run"] = Guid.NewGuid().ToString();
        Reject<InvalidDataException>(() => NativeActorPersonalUi.Notifications.Read(wrong, run.Run), "wrong run");
        wrong = (JObject)wire.DeepClone(); wrong["messages"][1]["seq"] = 7;
        Reject<InvalidDataException>(() => NativeActorPersonalUi.Notifications.Read(wrong, run.Run), "late malformed row rejects entire batch");
        Check(displayed == 2, "prevalidation causes no UI effects");
        wrong = (JObject)wire.DeepClone(); wrong["stream"] = Guid.NewGuid().ToString();
        Reject<InvalidDataException>(() => receiver.Apply(NativeActorPersonalUi.Notifications.Read(wrong, run.Run), (_, _) => displayed++), "stream switch requires reset");
        for (int i = 0; i < 256; i++) ui.NotifyPartner("bounded", "default");
        var bounded = NativeActorPersonalUi.Notifications.Read(ui.CaptureNotifications(), run.Run);
        Check(bounded.Rows.Length == 256 && bounded.First == 3 && bounded.Through == 258, "bounded notification journal");
        var fresh = new NativeActorPersonalUi.Receiver(); fresh.Apply(bounded, (_, _) => { });
        Check(fresh.Missed == 2 && fresh.Through == 258, "lost journal window explicitly counted");
        ui.NotifyPartner("throwing-display", "sec");
        var last = NativeActorPersonalUi.Notifications.Read(ui.CaptureNotifications(), run.Run);
        Reject<InvalidOperationException>(() => fresh.Apply(last, (_, _) => throw new InvalidOperationException()), "presentation exception propagated");
        fresh.Apply(last, (_, _) => displayed++); Check(fresh.Through == 259 && displayed == 2, "failed UI callback not replayed");
        string proof = NativeActorPersonalUi.StatusProof(store, "shared"); store.healthData.luck++;
        Check(proof != NativeActorPersonalUi.StatusProof(store, "shared"), "health changes invalidate status proof");
        proof = NativeActorPersonalUi.StatusProof(store, "shared"); store.appraiserLevel = 1;
        Check(proof != NativeActorPersonalUi.StatusProof(store, "shared"), "skill changes invalidate status proof");
        Check(NativeActorPersonalUi.ReadForPartner(() => run.CurrentActor) == NativeActorRun.Slot.Partner && run.CurrentActor == NativeActorRun.Slot.Host, "personal query restores actor");
        var notify = typeof(NativeActorPersonalNotification).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
        Check((bool)notify.Invoke(null, new object[] { "host", "default" }), "Host notification stays native");
        using (run.Enter(NativeActorRun.Slot.Partner))
            Check(!(bool)notify.Invoke(null, new object[] { "partner", "sec" }), "Partner notification routes to personal journal");
        Check(NativeActorPersonalUi.Notifications.Read(ui.CaptureNotifications(), run.Run).Through == 260, "notification routed exactly once");
        foreach (var type in new[] { typeof(NativeActorDialogueLineBinding), typeof(NativeActorDialogueContinuation), typeof(NativeActorPhoneOrigin), typeof(NativeActorPhoneContinuation), typeof(NativeActorPhoneRetirement) })
        {
            var targets = (IEnumerable<MethodBase>)type.GetMethod("TargetMethods", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Check(targets.All(method => method != null), "native source hook metadata resolves: " + type.Name);
        }
        activities.Dispose(); ui.Dispose(); NativeActorSession.Current = null;
        pending(); Check(calls == 1, "disposed session callback stays retired");
        Check(NativeActorPersonalUi.ReadForPartner(() => 3) == 3, "single player query stays native");
        Console.WriteLine($"PASS {checks} native actor activity and personal presentation checks; field-shell owner, no engine/singleton/save I/O, no Harmony patch installation.");
        return 0;
    }
}
