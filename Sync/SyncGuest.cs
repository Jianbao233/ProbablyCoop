using System;
using System.Collections.Generic;

namespace ProbablyCoop.Sync;

internal enum SyncApply : byte
{
    Remote = 0, // apply the ops to the local world
    Own = 1,    // our own change, already applied locally
    Ignore = 2, // already seen
    Resync = 3, // a commit is missing: ask the host for a checkpoint
}

// Guest side of rule one: stamps local changes, tracks which are still unconfirmed,
// and tells the game layer what to do with every commit and rejection from the host.
internal sealed class SyncGuest
{
    private readonly Dictionary<EntityKey, ulong> known = new Dictionary<EntityKey, ulong>();
    private readonly Queue<SyncTx> pending = new Queue<SyncTx>();
    private ulong serial;
    private ulong lastRejection;
    private ulong floor;

    internal SyncGuest(byte peer)
    {
        if (peer == SyncHost.HostPeer) throw new ArgumentOutOfRangeException(nameof(peer));
        Peer = peer;
    }

    internal byte Peer { get; }

    internal ulong Sequence { get; private set; }

    internal uint Barrier { get; private set; }

    internal uint Rejects { get; private set; }

    internal int PendingCount => pending.Count;

    // Wraps the state differences of one local action. Send SyncCodec.Tx(result) reliably and in order.
    internal SyncTx Submit(List<SyncOp> ops, bool crossing = false, byte[] context = null)
    {
        if (ops == null || ops.Count == 0) throw new ArgumentException("ops", nameof(ops));
        var tx = new SyncTx
        {
            Origin = Peer,
            Serial = ++serial,
            Barrier = Barrier,
            Rejects = Rejects,
            Crossing = crossing,
            Context = context,
            Ops = ops,
        };
        foreach (SyncOp op in ops)
        {
            if (op.Kind == SyncOpKind.Add) continue;
            op.Base = known.TryGetValue(op.Key, out ulong version) ? version : floor;
        }
        pending.Enqueue(tx);
        return tx;
    }

    internal SyncApply OnCommit(ulong sequence, SyncTx tx)
    {
        if (tx == null || sequence <= Sequence) return SyncApply.Ignore;
        if (sequence != Sequence + 1) return SyncApply.Resync;
        Sequence = sequence;
        foreach (SyncOp op in tx.Ops)
            if (op.Kind != SyncOpKind.Add) known[op.Key] = sequence;

        if (tx.Origin != Peer) return SyncApply.Remote;
        if (pending.Count != 0 && pending.Peek().Serial == tx.Serial)
        {
            pending.Dequeue();
            return SyncApply.Own;
        }
        // Ours, but dropped locally by a realign in between: treat it like anyone else's.
        return SyncApply.Remote;
    }

    // Returns the rejected transaction so the UI can tell the player which action bounced,
    // or null when it was already dropped. The host follows up with the authoritative state.
    internal SyncTx OnReject(ulong rejected)
    {
        // The ordered stream may deliver a response for an action discarded by
        // realignment. Count it once so the next request can follow that rejection.
        if (rejected <= lastRejection || rejected > serial) return null;
        lastRejection = rejected;
        Rejects++;
        if (pending.Count == 0 || pending.Peek().Serial != rejected) return null;
        return pending.Dequeue();
    }

    // True while a local change to this entity is unconfirmed.
    internal bool IsPending(EntityKey key)
    {
        foreach (SyncTx tx in pending)
            foreach (SyncOp op in tx.Ops)
                if (op.Key.Equals(key)) return true;
        return false;
    }

    // After loading a host checkpoint taken at this sequence (join, reconnect, barrier, resync).
    internal void Realign(uint barrier, ulong sequence)
    {
        known.Clear();
        pending.Clear();
        Barrier = barrier;
        Sequence = sequence;
        floor = sequence;
    }
}
