using ProbablyCoop.Core;

internal static class ActivityScopeTests
{
    private sealed class State { internal int Value; }
    private sealed class Restore : IDisposable { private readonly Action action; internal Restore(Action action) { this.action = action; } public void Dispose() => action(); }
    internal static void Run()
    {
        State ambient = new State { Value = 11 }; var original = ambient;
        var scope = new ActivityScope<State>(state => { var old = ambient; ambient = state; return new Restore(() => ambient = old); });
        var a = new State { Value = 100 }; var b = new State { Value = 200 };
        var ta = scope.Register(1, a); var tb = scope.Register(2, b);
        var deferredA = scope.Capture(ta, state => { Require(ReferenceEquals(ambient, a), "captured A binding"); state.Value += 3; });
        Require(scope.Execute(tb, state => {
            Require(ReferenceEquals(ambient, b), "B binding");
            Require(deferredA(), "deferred A executes");
            Require(ReferenceEquals(ambient, b) && scope.Current.Value.Equals(tb), "nested scope restores B");
            state.Value -= 7;
        }), "B executes");
        Require(a.Value == 103 && b.Value == 193 && ReferenceEquals(ambient, original) && !scope.Current.HasValue, "independent state");
        try { scope.Execute(ta, _ => throw new ApplicationException("intentional")); throw new Exception("exception swallowed"); }
        catch (ApplicationException) { Require(ReferenceEquals(ambient, original) && !scope.Current.HasValue, "exception restoration"); }
        scope.Close(ta); var ta2 = scope.Register(1, new State());
        Require(!deferredA() && !ta.Equals(ta2) && a.Value == 103, "closed activity generation must not be reused");
        var deferredB = scope.Capture(tb, state => state.Value++);
        scope.NewEpoch(); scope.Register(2, new State());
        Require(!deferredB() && b.Value == 193, "new epoch rejects delayed work");
        bool rejected = Task.Run(() => {
            try { scope.Execute(ta2, _ => {}); return false; } catch (InvalidOperationException) { return true; }
        }).GetAwaiter().GetResult();
        Require(rejected, "wrong thread rejected");
        var parent = scope.Register(10, a);
        Func<bool> childCallback = null!;
        scope.Execute(parent, _ => {
            var child = scope.Register(11, b);
            childCallback = scope.Capture(child, value => value.Value++);
            scope.Execute(child, _ => {
                Reject(() => scope.Register(10, new State()), "cannot replace executing outer activity");
                Reject(() => scope.Close(parent), "cannot close executing outer activity");
                Reject(scope.NewEpoch, "cannot retire epoch from nested callback");
                Reject(() => scope.Register(11, new State()), "cannot replace executing child activity");
                // Unrelated activities can be replaced/retired during normal
                // native callbacks that open a second panel or dialogue.
                var unrelated = scope.Register(12, new State());
                scope.Close(unrelated);
            });
        });
        Require(childCallback(), "child activity retains its actor after parent invocation returns");
        scope.Close(parent);
        Require(childCallback(), "closing parent does not implicitly cancel a distinct activity");
        scope.NewEpoch();
        Require(!childCallback(), "scene retirement cancels child callback");
        bool bindingFailure = true;
        var failing = new ActivityScope<State>(_ => {
            if(bindingFailure) throw new ApplicationException("binding failed");
            return new Restore(() => {});
        });
        var failToken = failing.Register(1, a);
        try { failing.Execute(failToken, _ => {}); } catch(ApplicationException) { }
        bindingFailure = false;
        failToken = failing.Register(1, b);
        Require(failing.Execute(failToken, _ => {}), "failed binding does not leak executing reservation");
        Console.WriteLine("PASS activity scopes: independent state, delayed ownership, nesting, exception restoration, stale generation/epoch and thread gate (fixture)");
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch(InvalidOperationException) { return; }
        throw new Exception("Activity scope: " + message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("Activity scope: " + message); }
}
