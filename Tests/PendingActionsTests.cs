using ProbablyCoop.Core;

internal static class PendingActionsTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    internal static void Run()
    {
        // Release/re-pick the same item three times before the first result.
        // Other items can still progress while those source proofs wait.
        var ledger = new PendingActions();
        var first = ledger.Reserve(1, new uint[] { 7 });
        var second = ledger.Reserve(2, new uint[] { 7 });
        var third = ledger.Reserve(3, new uint[] { 7 });
        var unrelated = ledger.Reserve(4, new uint[] { 8 });
        Require(first.CanSubmit && unrelated.CanSubmit && !second.CanSubmit && !third.CanSubmit,
            "re-pick blocked an unrelated source or lost its predecessor");
        ledger.Sent(first, 10);
        ledger.Sent(unrelated, 11);
        ledger.Result(unrelated, true);
        ledger.Projected(11);
        Require(unrelated.State == PendingActions.Status.Committed && !second.CanSubmit,
            "a later receipt must not confirm a missing earlier result");
        ledger.Result(first, true);
        Require(first.State == PendingActions.Status.Committed && second.CanSubmit && !third.CanSubmit,
            "projection-before-ACK failed to release exactly the next re-pick");
        ledger.Sent(second, 12);
        ledger.Result(second, true);
        Require(!third.CanSubmit, "accepted operation was used before its graph was projected");
        ledger.Projected(11);
        Require(!third.CanSubmit, "stale snapshot released a dependent action");
        ledger.Projected(12);
        Require(third.CanSubmit, "new authoritative geometry did not release the next re-pick");
        ledger.Sent(third, 13);
        ledger.Result(third, true);
        ledger.Projected(13);
        Require(ledger.Count == 0, "completed gestures leaked capacity");

        // A different source occupying a newly freed cell must roll back with
        // the freeing move. A third unrelated move must survive the conflict.
        var freeing = ledger.Reserve(5, new uint[] { 1 });
        var cellReuse = ledger.Reserve(6, new uint[] { 2 });
        cellReuse.AddDependency(freeing);
        var nextReuse = ledger.Reserve(7, new uint[] { 2 });
        var survivor = ledger.Reserve(8, new uint[] { 3 });
        Require(!cellReuse.CanSubmit && !nextReuse.CanSubmit && survivor.CanSubmit, "freed-cell dependency missing");
        ledger.Sent(freeing, 14);
        ledger.Result(freeing, false);
        Require(new[] { freeing, cellReuse, nextReuse }.All(e => e.State == PendingActions.Status.Rejected) &&
            survivor.CanSubmit && ledger.Count == 1, "conflict rollback escaped its dependency closure");
        ledger.Cancel(survivor);

        // Cancelling a local re-pick must not forget the earlier pending move.
        var moving = ledger.Reserve(9, new uint[] { 4 });
        ledger.Sent(moving, 15);
        var cancelled = ledger.Reserve(10, new uint[] { 4 });
        ledger.Cancel(cancelled);
        var retry = ledger.Reserve(11, new uint[] { 4 });
        Require(!retry.CanSubmit, "cancelled re-pick erased the earlier pending source");
        ledger.Result(moving, true);
        ledger.Projected(15);
        Require(retry.CanSubmit, "retry did not retain its live ancestor");
        ledger.Clear();
        Require(retry.State == PendingActions.Status.Cancelled && ledger.Count == 0, "scene reset retained input");

        // Group selection connects all of its sources to the same commit.
        uint[] sources = { 20, 21 };
        var group = ledger.Reserve(1, sources);
        sources[0] = 99;
        var fromGroup = ledger.Reserve(2, new uint[] { 20 });
        Require(group.Sources[0] == 20 && !fromGroup.CanSubmit, "mutable caller array changed action identity");
        ledger.Cancel(group);
        Require(fromGroup.State == PendingActions.Status.Cancelled && ledger.Count == 0,
            "group cancellation failed to retract dependent single gestures");

        for (ulong i = 3; i < 3 + PendingActions.Capacity; i++)
            Require(ledger.Reserve(i, new uint[] { (uint)i }) != null, "action capacity too small");
        Require(ledger.Reserve(100, new uint[] { 100 }) == null, "unbounded local prediction queue");
        ledger.Clear();
        var fresh = ledger.Reserve(1, new uint[] { 1 });
        ledger.Sent(fresh, 1);
        ledger.Result(fresh, true);
        Require(fresh.State == PendingActions.Status.Accepted, "new session inherited a prior projection watermark");
        ledger.Projected(1);
        Require(fresh.State == PendingActions.Status.Committed, "new session did not commit fresh input");
        Console.WriteLine("PASS pending actions: repeated pickup, freed cells, independent conflicts, cancel/retry, group dependencies and bounded reset");
    }
}
