using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using ProbablyCoop;
using ProbablyCoop.Core;

// A raw field shell only. The game's PlayerStore constructor and singleton
// access are intentionally never called by this executable.
static class ActorRunProbe
{
    private static int checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL actor run: " + label);
        checks++;
    }
    private static void Reject<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new Exception("Not rejected actor run: " + label);
    }
    private static void RejectOnWorker(Action action, string label)
    {
        Exception error = null;
        var worker = new Thread(() => { try { action(); } catch (Exception found) { error = found; } });
        worker.Start();
        Check(worker.Join(TimeSpan.FromSeconds(5)), label + " worker completed");
        Check(error is InvalidOperationException, label + " cross-thread rejected");
    }
    private static void FallsThroughOnWorker(Func<NativeActorState> read, string label)
    {
        Exception error = null;
        NativeActorState value = null;
        var worker = new Thread(() => { try { value = read(); } catch (Exception found) { error = found; } });
        worker.Start();
        Check(worker.Join(TimeSpan.FromSeconds(5)), label + " worker completed");
        Check(error == null && value == null, label + " preserves native fallback across threads");
    }
    private static PlayerStore Shell(NativeActorState state, string run, int slot = 7)
    {
        var shell = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        var fields = state.Clone();
        shell.playerName = fields.playerName;
        shell.appraiserLevel = fields.appraiserLevel;
        shell.appraisalProgress = fields.appraisalProgress;
        shell.appraisalPromotionDeclined = fields.appraisalPromotionDeclined;
        shell.appraisalPromotionDay = fields.appraisalPromotionDay;
        shell.scavengingAttempts = fields.scavengingAttempts;
        shell.dumpingGroundMajorWoundImmunitySearchesLeft = fields.dumpingGroundMajorWoundImmunitySearchesLeft;
        shell.dumpingGroundMinorWoundImmunitySearchesLeft = fields.dumpingGroundMinorWoundImmunitySearchesLeft;
        shell.healthData = fields.healthData;
        shell.startingPerks = fields.startingPerks;
        shell.appraisalBlacklist = fields.appraisalBlacklist;
        shell.runID = run;
        shell.saveSlotId = slot;
        shell.playerCash = 12345;
        shell.storeName = "unchanged-world";
        return shell;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run()
    {
        var host = NativeActorState.CreateDefault();
        host.playerName = "host";
        host.healthData.luck = 5;
        host.appraiserLevel = 1;
        host.startingPerks.Add(new StartingPerk { id = "lucky", cost = 1 });
        var partner = NativeActorState.CreateDefault();
        partner.playerName = "partner";
        partner.healthData.luck = -5;
        partner.appraiserLevel = 2;
        partner.appraisalBlacklist.Add("guns");
        var runId = Guid.NewGuid().ToString("D");
        var store = Shell(host, runId);
        var historical = Shell(host, runId);
        Check(JToken.DeepEquals(NativeActorState.Capture(store).Serialize(), host.Serialize()), "capture explicit raw owner fields");
        using var run = new NativeActorRun(store, partner);
        Check(run.Run == Guid.Parse(runId) && run.CurrentActor == NativeActorRun.Slot.Host, "owner run and default host");
        Check(run.Resolve(store) == null, "host falls through to canonical raw store");
        Check(!ReferenceEquals(run.Partner, partner) && !ReferenceEquals(run.Partner.healthData, partner.healthData), "partner clone detached");
        partner.healthData.luck = 99;
        Check(run.Partner.healthData.luck == -5, "input clone cannot mutate run partner");
        Func<bool> openedInActorCommand = null;
        using (run.Enter(NativeActorRun.Slot.Partner))
        {
            Check(ReferenceEquals(run.Resolve(store), run.Partner), "partner resolves current owner");
            Check(run.Resolve(historical) == null && run.Resolve(null) == null, "historical instance with same identity is not redirected");
            using (run.Enter(NativeActorRun.Slot.Host)) Check(run.Resolve(store) == null, "nested host resolves canonical");
            Check(run.CurrentActor == NativeActorRun.Slot.Partner && run.Resolve(store).healthData.luck == -5, "nested host restores partner");
            run.Resolve(store).healthData.luck = -4;
            Check(store.healthData.luck == 5, "partner write cannot change owner raw health");
            Reject<InvalidOperationException>(() => run.CapturePersonalCheckpoint(), "checkpoint inside scope");
            var child = run.RegisterActivity(10, run.CurrentActor);
            openedInActorCommand = run.Capture(child, () => Check(run.CurrentActor == NativeActorRun.Slot.Partner, "command-created callback retains origin partner"));
            Reject<InvalidOperationException>(() => run.Dispose(), "dispose active scope");
        }
        Check(run.CurrentActor == NativeActorRun.Slot.Host && run.Resolve(store) == null, "outer scope restores host");
        Check(openedInActorCommand(), "new activity registered inside actor command can run later");
        Func<bool> openedViaExecute = null;
        run.Execute(NativeActorRun.Slot.Partner, () =>
        {
            var commandActivity = run.RegisterActivity(12, run.CurrentActor);
            openedViaExecute = run.Capture(commandActivity, () => Check(run.CurrentActor == NativeActorRun.Slot.Partner, "Execute-created deferred callback retains partner"));
        });
        Check(openedViaExecute(), "Partner Execute may register a deferred activity");
        Reject<ApplicationException>(() => run.Execute(NativeActorRun.Slot.Partner, () => throw new ApplicationException("expected")), "exception unwinds scope");
        Check(run.CurrentActor == NativeActorRun.Slot.Host, "exception leaves host binding");
        var outer = run.Enter(NativeActorRun.Slot.Partner);
        var inner = run.Enter(NativeActorRun.Slot.Host);
        Reject<InvalidOperationException>(() => outer.Dispose(), "out-of-order scope disposal");
        Check(run.CurrentActor == NativeActorRun.Slot.Host, "bad disposal does not pop active scope");
        inner.Dispose();
        Check(run.CurrentActor == NativeActorRun.Slot.Partner, "valid inner disposal recovers outer");
        outer.Dispose();
        outer.Dispose();
        Check(run.CurrentActor == NativeActorRun.Slot.Host, "double disposal idempotent");
        Reject<ArgumentOutOfRangeException>(() => run.Enter((NativeActorRun.Slot)2), "invalid actor");
        RejectOnWorker(() => run.Enter(NativeActorRun.Slot.Partner), "Enter");
        RejectOnWorker(() => run.Resolve(store), "Resolve");
        RejectOnWorker(() => run.Dispose(), "Dispose");
        FallsThroughOnWorker(() => run.Resolve(historical), "historical non-owner resolver");
        FallsThroughOnWorker(() => run.Resolve(null), "null non-owner resolver");

        store.saveSlotId++;
        Reject<InvalidOperationException>(() => run.Enter(NativeActorRun.Slot.Partner), "changed owner slot");
        Reject<InvalidOperationException>(() => run.CapturePersonalCheckpoint(), "changed slot checkpoint");
        store.saveSlotId--;
        using (run.Enter(NativeActorRun.Slot.Partner))
        {
            store.runID = Guid.NewGuid().ToString("D");
            Reject<InvalidOperationException>(() => run.Resolve(store), "changed owner identity while bound");
            store.runID = runId;
        }
        store.runID = Guid.NewGuid().ToString("D");
        Reject<InvalidOperationException>(() => run.Enter(NativeActorRun.Slot.Host), "changed owner run");
        store.runID = runId;

        int calls = 0;
        var token = run.RegisterActivity(1, NativeActorRun.Slot.Partner);
        var callback = run.Capture(token, () =>
        {
            Check(run.CurrentActor == NativeActorRun.Slot.Partner && ReferenceEquals(run.Resolve(store), run.Partner), "callback explicit origin actor");
            calls++;
        });
        Check(callback() && calls == 1 && run.CurrentActor == NativeActorRun.Slot.Host, "callback binds and unwinds");
        RejectOnWorker(() => callback(), "callback");
        Check(calls == 1, "cross-thread callback has no effect");
        store.runID = Guid.NewGuid().ToString("D");
        Reject<InvalidOperationException>(() => callback(), "stale native owner callback");
        Check(calls == 1, "stale owner callback has no effect");
        store.runID = runId;
        var badCallback = run.Capture(token, () => throw new ApplicationException("expected callback"));
        Reject<ApplicationException>(() => badCallback(), "callback exception");
        Check(run.CurrentActor == NativeActorRun.Slot.Host, "callback exception restores actor and activity context");
        var hostToken = run.RegisterActivity(2, NativeActorRun.Slot.Host);
        var hostCallback = run.Capture(hostToken, () =>
        {
            Check(run.Resolve(store) == null, "nested callback host canonical");
            Reject<InvalidOperationException>(() => run.RegisterActivity(1, NativeActorRun.Slot.Host), "nested callback cannot replace executing outer activity ID");
            Reject<InvalidOperationException>(() => run.RetireActivity(token), "nested callback cannot close executing outer activity ID");
            Reject<InvalidOperationException>(() => run.RegisterActivity(2, NativeActorRun.Slot.Host), "callback cannot replace own executing activity ID");
            var unrelated = run.RegisterActivity(11, NativeActorRun.Slot.Partner);
            var unrelatedCallback = run.Capture(unrelated, () => calls++);
            run.RetireActivity(unrelated);
            Check(!unrelatedCallback(), "nested callback can register and retire unrelated activity");
        });
        var nested = run.Capture(token, () =>
        {
            Check(hostCallback(), "nested host callback executes");
            Check(run.CurrentActor == NativeActorRun.Slot.Partner, "nested callback restores outer actor");
            Reject<InvalidOperationException>(() => run.RetireScene(), "retire active scene");
            Reject<InvalidOperationException>(() => run.RetireActivity(token), "retire active activity");
        });
        Check(nested(), "nested partner callback executes");
        run.RetireActivity(token);
        Check(!callback() && calls == 1, "retired callback cannot run");
        token = run.RegisterActivity(1, NativeActorRun.Slot.Partner);
        callback = run.Capture(token, () => calls++);
        var replacement = run.RegisterActivity(1, NativeActorRun.Slot.Partner);
        Check(!callback() && calls == 1, "replaced activity generation cannot run");
        callback = run.Capture(replacement, () => calls++);
        run.RetireScene();
        Check(!callback() && !hostCallback() && calls == 1, "old scene callbacks cannot run");
        Check(!run.Capture(default, () => calls++)(), "unknown activity cannot run");

        var checkpoint = run.CapturePersonalCheckpoint();
        Check(checkpoint["actors"] is JArray actors && actors.Count == 2, "checkpoint contains exact two actors");
        Check(JToken.DeepEquals(checkpoint["actors"][0], NativeActorState.Capture(store).Serialize()) && JToken.DeepEquals(checkpoint["actors"][1], run.Partner.Serialize()), "settled two-actor checkpoint");
        var originalHost = NativeActorState.Capture(store).Serialize();
        var originalHealth = store.healthData;
        using (var restored = NativeActorRun.RestorePersonalCheckpoint(store, JObject.Parse(checkpoint.ToString())))
        {
            Check(JToken.DeepEquals(restored.CapturePersonalCheckpoint(), checkpoint), "checkpoint round-trip");
            Check(restored.Resolve(store) == null, "restored host remains canonical");
            restored.Execute(NativeActorRun.Slot.Partner, () => Check(restored.Resolve(store).appraiserLevel == 2, "restored partner resolver"));
            restored.Partner.healthData.luck = -22;
            Check(run.Partner.healthData.luck == -4 && store.healthData.luck == 5, "restored actor mutable state detached");
        }
        var mismatch = (JObject)checkpoint.DeepClone();
        mismatch["actors"][0]["appraiserLevel"] = 3;
        Reject<InvalidDataException>(() => NativeActorRun.RestorePersonalCheckpoint(store, mismatch), "mismatched host checkpoint");
        mismatch = (JObject)checkpoint.DeepClone();
        mismatch["actors"][1]["healthData"]["woundState"] = -1;
        Reject<InvalidDataException>(() => NativeActorRun.RestorePersonalCheckpoint(store, mismatch), "invalid partner after valid host");
        mismatch = (JObject)checkpoint.DeepClone();
        mismatch["run"] = Guid.NewGuid().ToString("D");
        Reject<InvalidDataException>(() => NativeActorRun.RestorePersonalCheckpoint(store, mismatch), "wrong run checkpoint");
        mismatch = (JObject)checkpoint.DeepClone();
        mismatch["schema"] = 2;
        Reject<InvalidDataException>(() => NativeActorRun.RestorePersonalCheckpoint(store, mismatch), "unknown checkpoint schema");
        mismatch = (JObject)checkpoint.DeepClone();
        ((JArray)mismatch["actors"]).RemoveAt(1);
        Reject<InvalidDataException>(() => NativeActorRun.RestorePersonalCheckpoint(store, mismatch), "missing partner checkpoint");
        store.appraisalProgress = 1;
        Reject<InvalidDataException>(() => NativeActorRun.RestorePersonalCheckpoint(store, checkpoint), "world host diverged since checkpoint");
        Check(store.appraisalProgress == 1, "checkpoint rejection does not silently repair host");
        store.appraisalProgress = 0;
        Check(JToken.DeepEquals(originalHost, NativeActorState.Capture(store).Serialize()) && ReferenceEquals(store.healthData, originalHealth) && store.playerCash == 12345 && store.storeName == "unchanged-world" && store.runID == runId && store.saveSlotId == 7, "all restore attempts leave original native fields and world unchanged");

        token = run.RegisterActivity(3, NativeActorRun.Slot.Partner);
        callback = run.Capture(token, () => calls++);
        run.Dispose();
        Check(!callback() && calls == 1 && run.Resolve(store) == null, "disposed callback cannot execute and resolver releases ownership");
        FallsThroughOnWorker(() => run.Resolve(store), "disposed owner resolver");
        Reject<ObjectDisposedException>(() => run.Enter(NativeActorRun.Slot.Partner), "disposed run binding");
        Reject<ObjectDisposedException>(() => run.CapturePersonalCheckpoint(), "disposed run checkpoint");
        RejectOnWorker(() => callback(), "disposed callback");
        Console.WriteLine($"PASS {checks} native actor run ownership, scope, activity lifetime, thread, and checkpoint checks; PlayerStore field shells only (constructors/singletons/engine/save I/O never invoked).");
        return 0;
    }
}
