// Test-only lifecycle boundary. Production NativeActorSession is intentionally
// not linked: constructing it would install Harmony and access the game world.
namespace ProbablyCoop;
internal sealed class NativeActorSession
{
    internal static NativeActorSession Current { get; set; }
    internal NativeActorRun Run { get; }
    internal PlayerStore Store { get; set; }
    internal NativeActorActivities Activities { get; }
    internal NativeActorPersonalUi PersonalUi { get; }
    internal bool Faulted { get; private set; }
    internal void MarkFault(System.Exception error) => Faulted = true;
    internal NativeActorSession(NativeActorRun run)
    { Run = run; Activities = new NativeActorActivities(run); PersonalUi = new NativeActorPersonalUi(run); }
    internal System.IDisposable Enter(NativeActorRun.Slot actor) => Run.Enter(actor);
}
internal sealed class NativeWorld
{
    internal static NativeWorld Guest { get; set; }
    internal bool HasActorProjection { get; set; }
    internal NativeActorState RemoteHostActor { get; set; }
}
