using System;
using System.Reflection;
using System.Runtime.Loader;

// Loads the freshly built mod and exercises the exact production completion gate
// without constructing Unity objects, running game methods, or touching saves.
internal static class NativeEffectTransitionTests
{
    internal static void Run(string assemblyPath)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(System.IO.Path.GetFullPath(assemblyPath));
        var type = assembly.GetType("ProbablyCoop.NativeEffectTransitionAttempt", throwOnError: true);
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(Guid), typeof(double) }, null);
        var observe = type.GetMethod("TryComplete", BindingFlags.Instance | BindingFlags.NonPublic);
        if (constructor == null || observe == null) throw new Exception("Production transition gate was not found.");
        object Begin(Guid run, double now) => constructor.Invoke(new object[] { run, now });
        bool Complete(object attempt, Guid run, bool settled, double now, Func<bool> publish) =>
            (bool)observe.Invoke(attempt, new object[] { run, settled, now, publish });

        Guid run = Guid.NewGuid();
        int receipts = 0;
        var late = Begin(run, 100);
        Check(!Complete(late, run, false, 119, () => { receipts++; return true; }) && receipts == 0,
            "an unfinished attempt waits before its existing deadline");
        Check(Complete(late, run, true, 145, () => { receipts++; return true; }) && receipts == 1,
            "a transition settled during disconnection is confirmed after its deadline");
        Check(!Complete(late, run, true, 146, () => { receipts++; return true; }) && receipts == 1,
            "the same native transition publishes only one completion");

        var stalled = Begin(run, 100);
        Check(!Complete(stalled, run, false, 120, () => { receipts++; return true; }),
            "the exact twenty-second boundary retains its previous behavior");
        ExpectFailure(() => Complete(stalled, run, false, 120.001, () => { receipts++; return true; }),
            "did not settle");
        Check(receipts == 1, "an unfinished timed-out attempt never publishes");

        var stale = Begin(run, 100);
        ExpectFailure(() => Complete(stale, Guid.NewGuid(), true, 145, () => { receipts++; return true; }),
            "identity changed");
        Check(receipts == 1, "settled state from a different run cannot confirm the old attempt");

        var failedPublication = Begin(run, 100);
        ExpectFailure(() => Complete(failedPublication, run, true, 145, () => false), "could not publish");
        Console.WriteLine("PASS effect transition production gate: late settled reconnect, unfinished timeout, run identity, once-only completion and failed publication");
    }

    private static void ExpectFailure(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException error) when
            (error.InnerException is InvalidOperationException failure && failure.Message.Contains(message)) { return; }
        throw new Exception("Expected production transition failure: " + message);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
