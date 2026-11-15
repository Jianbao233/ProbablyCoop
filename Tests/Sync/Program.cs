using System;
using System.Collections.Generic;
using ProbablyCoop.Sync;

namespace ProbablyCoop.SyncTests;

internal static class Program
{
    private static int failures;

    private static int Main()
    {
        Codec();
        Ordering();
        Conflict();
        OwnPipeline();
        RejectedChain();
        Deltas();
        Holds();
        BarrierFlow();
        InvalidClaims();
        GuestFlow();
        Console.WriteLine(failures == 0 ? "PASS sync-core" : "FAIL sync-core failures=" + failures);
        int net = NetTests.Run();
        int world = WorldSessionTests.Run();
        int inventory = InventoryLayoutTests.Run();
        return failures == 0 && net == 0 && world == 0 && inventory == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string name)
    {
        if (condition) return;
        failures++;
        Console.WriteLine("FAIL " + name);
    }

    private static SyncTx Tx(byte origin, ulong serial, params SyncOp[] ops) =>
        new SyncTx { Origin = origin, Serial = serial, Ops = new List<SyncOp>(ops) };

    private static SyncOp Put(long id, ulong seen)
    {
        SyncOp op = SyncOp.Put(EntityKey.Item(id), new byte[] { 1, 2, 3 });
        op.Base = seen;
        return op;
    }

    private static void Codec()
    {
        var tx = new SyncTx { Origin = 1, Serial = 7, Barrier = 3, Rejects = 2, Crossing = true };
        tx.Ops.Add(Put(-101, 300));
        SyncOp remove = SyncOp.Remove(EntityKey.Item(long.MaxValue));
        remove.Base = ulong.MaxValue;
        tx.Ops.Add(remove);
        tx.Ops.Add(SyncOp.Add(EntityKey.Scalar("playerCash"), long.MinValue));

        byte[] bytes = SyncCodec.Commit(42, tx);
        Check(SyncCodec.TryDecode(bytes, out SyncFrame frame), "codec-commit-decodes");
        Check(frame != null && frame.Message == SyncMessage.Commit && frame.Sequence == 42, "codec-commit-header");
        SyncTx back = frame?.Tx;
        Check(back != null && back.Origin == 1 && back.Serial == 7 && back.Barrier == 3 && back.Rejects == 2 &&
              back.Crossing && back.Ops.Count == 3, "codec-tx-fields");
        if (back != null && back.Ops.Count == 3)
        {
            Check(back.Ops[0].Key.Equals(EntityKey.Item(-101)) && back.Ops[0].Base == 300 &&
                  back.Ops[0].Data.Length == 3 && back.Ops[0].Data[2] == 3, "codec-put");
            Check(back.Ops[1].Kind == SyncOpKind.Remove && back.Ops[1].Key.Id == long.MaxValue &&
                  back.Ops[1].Base == ulong.MaxValue, "codec-remove");
            Check(back.Ops[2].Kind == SyncOpKind.Add && back.Ops[2].Delta == long.MinValue &&
                  back.Ops[2].Key.Equals(EntityKey.Scalar("playerCash")), "codec-add");
        }

        for (int length = 0; length < bytes.Length; length++)
        {
            var cut = new byte[length];
            Array.Copy(bytes, cut, length);
            Check(!SyncCodec.TryDecode(cut, out _), "codec-truncated-" + length);
        }
        var extra = new byte[bytes.Length + 1];
        Array.Copy(bytes, extra, bytes.Length);
        Check(!SyncCodec.TryDecode(extra, out _), "codec-trailing-byte");
        Check(!SyncCodec.TryDecode(null, out _), "codec-null");
        Check(!SyncCodec.TryDecode(new byte[] { 99 }, out _), "codec-unknown-message");

        Check(SyncCodec.TryDecode(SyncCodec.Reject(9, SyncVerdict.Held), out frame) && frame.Serial == 9 &&
              frame.Verdict == SyncVerdict.Held, "codec-reject");
        Check(SyncCodec.TryDecode(SyncCodec.Hold(2, EntityKey.Item(5), true), out frame) &&
              frame.Message == SyncMessage.Hold && frame.Peer == 2 && frame.Key.Id == 5, "codec-hold");
        Check(SyncCodec.TryDecode(SyncCodec.Hold(2, EntityKey.Item(5), false), out frame) &&
              frame.Message == SyncMessage.Release, "codec-release");
        Check(SyncCodec.TryDecode(SyncCodec.Barrier(4, true, 77), out frame) && frame.Barrier == 4 &&
              frame.Closed && frame.Sequence == 77, "codec-barrier");
        Check(!EntityKey.Scalar("playerCash").Equals(EntityKey.Scalar("loanValue")), "scalar-keys-differ");
    }

    private static void Ordering()
    {
        var host = new SyncHost();
        Check(host.Submit(Tx(1, 1, Put(10, 0)), out ulong first) == SyncVerdict.Committed && first == 1, "first-commit");
        Check(host.Submit(Tx(1, 1, Put(10, 0)), out _) == SyncVerdict.Duplicate, "duplicate-serial");
        Check(host.Submit(Tx(1, 3, Put(10, 0)), out _) == SyncVerdict.Gap, "serial-gap");
        Check(host.Submit(Tx(1, 2, Put(11, 0)), out ulong second) == SyncVerdict.Committed && second == 2, "next-serial");
        Check(host.Submit(Tx(1, 3), out _) == SyncVerdict.Malformed, "empty-tx");
        Check(host.Submit(null, out _) == SyncVerdict.Malformed, "null-tx");
        Check(host.Sequence == 2, "sequence-only-counts-commits");
    }

    private static void Conflict()
    {
        var host = new SyncHost();
        // Both guests move the same item from the same starting state: only the first wins.
        Check(host.Submit(Tx(1, 1, Put(10, 0)), out _) == SyncVerdict.Committed, "conflict-first-wins");
        Check(host.Submit(Tx(2, 1, Put(10, 0)), out _) == SyncVerdict.Stale, "conflict-second-stale");
        // An unrelated item is not blocked (no global lock).
        Check(host.Submit(Tx(2, 2, Put(11, 0)), out _) == SyncVerdict.Committed, "conflict-other-item-free");
        // The host's own change always commits and invalidates what guests built before it.
        Check(host.Submit(Tx(0, 1, Put(10, 0)), out ulong hostSequence) == SyncVerdict.Committed, "host-always-commits");
        var stale = Tx(1, 2, Put(10, 1));
        Check(host.Submit(stale, out _) == SyncVerdict.Stale, "guest-stale-after-host");
        var fresh = Tx(1, 3, Put(10, hostSequence));
        fresh.Rejects = 1;
        Check(host.Submit(fresh, out _) == SyncVerdict.Committed, "guest-fresh-after-host");
        // Refusal by the world.
        host.Validate = _ => false;
        var refused = Tx(1, 4, Put(12, 0));
        refused.Rejects = 1;
        Check(host.Submit(refused, out _) == SyncVerdict.Refused, "world-refuses");
        Check(host.Submit(Tx(0, 2, Put(12, 0)), out _) == SyncVerdict.Committed, "host-skips-validate");
    }

    private static void OwnPipeline()
    {
        var host = new SyncHost();
        // Three quick moves of one item before any commit came back: all based on version 0.
        Check(host.Submit(Tx(1, 1, Put(10, 0)), out _) == SyncVerdict.Committed, "pipeline-1");
        Check(host.Submit(Tx(1, 2, Put(10, 0)), out _) == SyncVerdict.Committed, "pipeline-2");
        Check(host.Submit(Tx(1, 3, Put(10, 0)), out ulong third) == SyncVerdict.Committed, "pipeline-3");
        // The partner saw all three, then moves it; the first guest's next blind move must lose.
        Check(host.Submit(Tx(2, 1, Put(10, third)), out _) == SyncVerdict.Committed, "pipeline-partner");
        Check(host.Submit(Tx(1, 4, Put(10, third)), out _) == SyncVerdict.Stale, "pipeline-then-stale");
        // Three peers: C wrote after B; A, still on B's version, must lose.
        var three = new SyncHost();
        three.Submit(Tx(1, 1, Put(20, 0)), out ulong a);
        three.Submit(Tx(2, 1, Put(20, a)), out ulong b);
        three.Submit(Tx(3, 1, Put(20, b)), out _);
        Check(three.Submit(Tx(1, 2, Put(20, b)), out _) == SyncVerdict.Stale, "three-peers-stale");
    }

    private static void RejectedChain()
    {
        var host = new SyncHost();
        host.Submit(Tx(2, 1, Put(10, 0)), out ulong partner);
        // Guest 1 split a stack: tx1 touches the source (stale), tx2 depends on the new piece.
        Check(host.Submit(Tx(1, 1, Put(10, 0), Put(1000, 0)), out _) == SyncVerdict.Stale, "chain-root-rejected");
        Check(host.Submit(Tx(1, 2, Put(1000, 0)), out _) == SyncVerdict.Stale, "chain-dependent-rejected");
        // An action on something unrelated still goes through.
        Check(host.Submit(Tx(1, 3, Put(30, 0)), out _) == SyncVerdict.Committed, "chain-unrelated-ok");
        // After the guest processed one rejection only, the second poison still stands.
        var early = Tx(1, 4, Put(1000, partner));
        early.Rejects = 1;
        Check(host.Submit(early, out _) == SyncVerdict.Stale, "chain-needs-all-rollbacks");
        var late = Tx(1, 5, Put(1000, partner), Put(10, partner));
        late.Rejects = 3;
        Check(host.Submit(late, out _) == SyncVerdict.Committed, "chain-clean-after-rollback");
    }

    private static void Deltas()
    {
        var host = new SyncHost();
        EntityKey cash = EntityKey.Scalar("playerCash");
        // Both players earn/spend at once: deltas never conflict by version.
        Check(host.Submit(Tx(1, 1, SyncOp.Add(cash, -50)), out _) == SyncVerdict.Committed, "delta-guest-1");
        Check(host.Submit(Tx(2, 1, SyncOp.Add(cash, 120)), out _) == SyncVerdict.Committed, "delta-guest-2");
        long balance = 40;
        host.Validate = tx =>
        {
            long next = balance;
            foreach (SyncOp op in tx.Ops)
                if (op.Kind == SyncOpKind.Add) next += op.Delta;
            if (next < 0) return false;
            balance = next;
            return true;
        };
        Check(host.Submit(Tx(1, 2, SyncOp.Add(cash, -30)), out _) == SyncVerdict.Committed, "delta-within-floor");
        Check(host.Submit(Tx(2, 2, SyncOp.Add(cash, -30)), out _) == SyncVerdict.Refused, "delta-below-floor");
        Check(balance == 10, "delta-balance");
        // A refused delta does not poison later deltas.
        Check(host.Submit(Tx(2, 3, SyncOp.Add(cash, -10)), out _) == SyncVerdict.Committed, "delta-no-poison");
    }

    private static void Holds()
    {
        var host = new SyncHost();
        EntityKey item = EntityKey.Item(10);
        Check(host.Hold(1, item), "hold-first");
        Check(host.Hold(1, item), "hold-repeat-same-peer");
        Check(!host.Hold(2, item), "hold-second-denied");
        Check(host.IsHeldByOther(2, item) && !host.IsHeldByOther(1, item), "hold-query");
        Check(host.Submit(Tx(2, 1, Put(10, 0)), out _) == SyncVerdict.Held, "held-blocks-other");
        Check(host.Submit(Tx(1, 1, Put(10, 0)), out _) == SyncVerdict.Committed, "holder-can-commit");
        Check(!host.Release(2, item), "release-wrong-peer");
        // Disconnect frees the hold.
        List<EntityKey> freed = host.Forget(1);
        Check(freed.Count == 1 && freed[0].Equals(item) && !host.AnyHold, "disconnect-releases");
        Check(host.Hold(2, item), "hold-after-disconnect");
        // The rejoined peer starts numbering again.
        Check(host.Submit(Tx(1, 1, Put(11, 0)), out _) == SyncVerdict.Committed, "rejoin-serial-restarts");
    }

    private static void BarrierFlow()
    {
        var host = new SyncHost();
        host.Submit(Tx(1, 1, Put(10, 0)), out _);
        host.Close();
        Check(host.Submit(Tx(1, 2, Put(11, 0)), out _) == SyncVerdict.Busy, "barrier-blocks");
        var crossing = Tx(1, 3, Put(12, 0));
        crossing.Crossing = true;
        Check(host.Submit(crossing, out _) == SyncVerdict.Busy, "untrusted crossing flag cannot reopen the barrier");
        host.MayCrossBarrier = tx => tx.Ops.Count == 1 && tx.Ops[0].Key.Equals(EntityKey.Scalar("ready"));
        var ready = Tx(1, 4, SyncOp.Put(EntityKey.Scalar("ready"), new byte[] { 1 }));
        ready.Crossing = true;
        Check(host.Submit(ready, out _) == SyncVerdict.Committed, "host can authorize a specific barrier operation");
        Check(host.Submit(Tx(0, 1, Put(13, 0)), out _) == SyncVerdict.Committed, "barrier-host-works");
        ulong checkpoint = host.Complete();
        Check(!host.Closed && host.Barrier == 1 && checkpoint == host.Sequence, "barrier-complete");
        // Built before the checkpoint: wrong epoch.
        Check(host.Submit(Tx(1, 5, Put(10, 0)), out _) == SyncVerdict.Stale, "barrier-old-epoch");
        // Realigned to the checkpoint.
        var aligned = Tx(1, 6, Put(10, checkpoint));
        aligned.Barrier = 1;
        aligned.Rejects = 3;
        Check(host.Submit(aligned, out _) == SyncVerdict.Committed, "barrier-realigned");
        var behind = Tx(1, 7, Put(14, checkpoint - 1));
        behind.Barrier = 1;
        behind.Rejects = 3;
        Check(host.Submit(behind, out _) == SyncVerdict.Stale, "barrier-below-floor");
    }

    private static void InvalidClaims()
    {
        var host = new SyncHost();
        host.Submit(Tx(0, 1, Put(10, 0)), out _);
        Check(host.Submit(Tx(1, 1, Put(10, 2)), out _) == SyncVerdict.Malformed &&
            host.Sequence == 1, "cannot claim a future version");
        var fabricated = Tx(2, 1, Put(10, 1));
        fabricated.Rejects = 1;
        Check(host.Submit(fabricated, out _) == SyncVerdict.Malformed &&
            host.Sequence == 1, "cannot acknowledge an unsent rejection");
    }

    private static void GuestFlow()
    {
        var host = new SyncHost();
        var guest = new SyncGuest(1);
        EntityKey item = EntityKey.Item(10);

        // Own action: stamped, pending, confirmed without re-applying.
        SyncTx own = guest.Submit(new List<SyncOp> { SyncOp.Put(item, new byte[] { 9 }) });
        Check(own.Serial == 1 && own.Origin == 1 && guest.IsPending(item) && guest.PendingCount == 1, "guest-submit");
        Check(host.Submit(own, out ulong sequence) == SyncVerdict.Committed, "guest-own-committed");
        Check(guest.OnCommit(sequence, own) == SyncApply.Own && !guest.IsPending(item), "guest-own-not-reapplied");
        Check(guest.OnCommit(sequence, own) == SyncApply.Ignore, "guest-duplicate-commit");

        // Partner changes the item; our blind change loses and we count the rejection.
        SyncTx partner = Tx(2, 1, Put(10, sequence));
        host.Submit(partner, out ulong partnerSequence);
        SyncTx blind = guest.Submit(new List<SyncOp> { SyncOp.Put(item, new byte[] { 8 }) });
        Check(blind.Ops[0].Base == sequence, "guest-base-is-last-seen");
        Check(host.Submit(blind, out _) == SyncVerdict.Stale, "guest-blind-stale");
        Check(guest.OnCommit(partnerSequence, partner) == SyncApply.Remote, "guest-remote-applies");
        SyncTx bounced = guest.OnReject(blind.Serial);
        Check(ReferenceEquals(bounced, blind) && guest.Rejects == 1 && guest.PendingCount == 0, "guest-reject-pops");
        Check(guest.OnReject(blind.Serial) == null && guest.OnReject(blind.Serial + 1) == null &&
            guest.Rejects == 1, "duplicate and unsent rejections are ignored");

        // After the rollback the next change carries the new base and reject count.
        SyncTx retry = guest.Submit(new List<SyncOp> { SyncOp.Put(item, new byte[] { 7 }) });
        Check(retry.Ops[0].Base == partnerSequence && retry.Rejects == 1, "guest-retry-stamps");
        Check(host.Submit(retry, out ulong retrySequence) == SyncVerdict.Committed, "guest-retry-committed");
        Check(guest.OnCommit(retrySequence + 1, retry) == SyncApply.Resync, "guest-missing-commit-resyncs");
        Check(guest.OnCommit(retrySequence, retry) == SyncApply.Own, "guest-retry-own");

        // Barrier: pending work is dropped and the new epoch is stamped.
        guest.Submit(new List<SyncOp> { SyncOp.Add(EntityKey.Scalar("playerCash"), 5) });
        host.Close();
        ulong checkpoint = host.Complete();
        guest.Realign(host.Barrier, checkpoint);
        Check(guest.PendingCount == 0 && guest.Sequence == checkpoint && guest.Barrier == 1, "guest-realign");
        Check(guest.OnReject(4) == null && guest.Rejects == 2, "guest-late-reject-counted");
        SyncTx after = guest.Submit(new List<SyncOp> { SyncOp.Put(EntityKey.Item(99), null) });
        Check(after.Barrier == 1 && after.Ops[0].Base == checkpoint && after.Serial == 5, "guest-after-barrier-stamps");
    }
}
