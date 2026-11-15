using ProbablyCoop.Core;

internal static class DaySettlementTests
{
    internal static void Run()
    {
        var store = new object();
        var session = new object();
        DaySettlement Start() => new(store, "run-a", 10, session, 125, 20);
        foreach (bool morningFirst in new[] { false, true })
        foreach (int timeoutStage in new[] { -1, 0, 1, 2 })
        {
            var attempt = Start();
            Require(!attempt.Expire(19.999), "deadline must preserve the original wait");
            if (timeoutStage == 0) Require(attempt.Expire(20), "pending night times out at deadline");
            if (morningFirst) attempt.ConfirmMorning(true); else attempt.ConfirmSave(true);
            Require(!attempt.TryComplete(), "one receipt cannot authorize completion");
            if (timeoutStage == 1) Require(attempt.Expire(45), "late second receipt remains recoverable");
            if (morningFirst) attempt.ConfirmSave(true); else attempt.ConfirmMorning(true);
            if (timeoutStage == 2) Require(attempt.Expire(45), "receipts do not publish inside native hooks");
            Require(!attempt.Saved && attempt.Pending, "native hooks defer publication until the next tick");
            Require(attempt.TryComplete() && attempt.Saved && !attempt.Pending, "both receipts complete the same night");
            Require(!attempt.TryComplete() && !attempt.Expire(60), "completion publishes once, including after reconnect");
            attempt.ConfirmSave(true); attempt.ConfirmMorning(true);
            Require(!attempt.TryComplete(), "duplicate callbacks cannot publish again");
        }

        foreach (int failureStage in new[] { 0, 1, 2 })
        foreach (string reason in new[] { "save", "skipped morning", "finalizer", "disposed", "terminal" })
        {
            var attempt = Start();
            attempt.Expire(45);
            if (failureStage >= 1) attempt.ConfirmMorning(true);
            if (failureStage >= 2) attempt.ConfirmSave(true);
            if (reason == "save") attempt.ConfirmSave(false);
            else if (reason == "skipped morning") attempt.ConfirmMorning(false);
            else attempt.Fail();
            attempt.ConfirmSave(true); attempt.ConfirmMorning(true);
            Require(attempt.Failed && !attempt.Pending && !attempt.TryComplete() && !attempt.Saved,
                "late success cannot revive " + reason + " at stage " + failureStage);
        }

        var identity = Start();
        Require(identity.Matches(store, "run-a", 10, session), "original native run identity");
        Require(!identity.Matches(new object(), "run-a", 10, session), "replacement store rejected");
        Require(!identity.Matches(store, "run-b", 10, session), "replacement run rejected");
        Require(!identity.Matches(store, "run-a", 14, session), "replacement slot rejected");
        Require(!identity.Matches(store, "run-a", 10, new object()), "replacement actor session rejected");
        Require(!identity.Matches(store, "run-a", 10, null), "retired actor session rejected");
        Require(identity.AcceptsDay(125) && identity.AcceptsDay(126) &&
            !identity.AcceptsDay(124) && !identity.AcceptsDay(127), "only the original night may advance one day");
        identity.Fail();
        var next = Start();
        identity.ConfirmSave(true); identity.ConfirmMorning(true);
        Require(!identity.TryComplete() && !next.TryComplete(), "retired callbacks cannot supply a new night");
        Console.WriteLine("PASS native day settlement: receipt order, delayed completion, timeout, failures, identity and retirement");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new Exception(message); }
}
