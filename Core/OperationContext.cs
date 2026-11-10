using System;

namespace ProbablyCoop.Core;

// Business proofs may remain valid for an unchanged item across a day or
// location transition. This session-local generation prevents replay in a
// different phase, including a return to the original location on the same day.
internal sealed class OperationContext
{
    private int day, phase;
    private string scene;
    private bool inTransit;
    internal ulong Generation { get; private set; }

    internal ulong Observe(int currentDay, int currentPhase, string currentScene, bool currentTransit)
    {
        if (currentScene == null) throw new ArgumentNullException(nameof(currentScene));
        if (Generation == 0 || day != currentDay || phase != currentPhase ||
            scene != currentScene || inTransit != currentTransit)
        {
            Generation = checked(Generation + 1);
            day = currentDay;
            phase = currentPhase;
            scene = currentScene;
            inTransit = currentTransit;
        }
        return Generation;
    }
}
