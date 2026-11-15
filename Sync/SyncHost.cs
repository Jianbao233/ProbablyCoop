using System;
using System.Collections.Generic;

namespace ProbablyCoop.Sync;

// Host side of rule one: gives every accepted transaction one global sequence number
// and rejects the ones that were built on a state someone else already changed.
// It never re-runs game rules; Validate is the only hook into the world (space, cash floor).
internal sealed class SyncHost
{
    private struct Entry
    {
        internal ulong Version; // sequence of the last versioned write
        internal byte Writer;   // who made it
        internal ulong Other;   // last write by anyone except Writer
    }

    private readonly Dictionary<EntityKey, Entry> entries = new Dictionary<EntityKey, Entry>();
    private readonly Dictionary<byte, ulong> serials = new Dictionary<byte, ulong>();
    private readonly Dictionary<byte, uint> rejects = new Dictionary<byte, uint>();
    private readonly Dictionary<(byte, EntityKey), uint> poison = new Dictionary<(byte, EntityKey), uint>();
    private readonly Dictionary<EntityKey, byte> holds = new Dictionary<EntityKey, byte>();
    private ulong floor;

    internal const byte HostPeer = 0;

    internal ulong Sequence { get; private set; }

    internal uint Barrier { get; private set; }

    internal bool Closed { get; private set; }

    // Accepts a guest transaction that passed conflict checks. The world may
    // atomically apply it here, before Submit advances the sequence.
    internal Func<SyncTx, bool> Validate;
    internal Func<SyncTx, SyncOp, bool> CanCarryUpdatedState;

    // Crossing is a request flag. Only an authority-approved transition
    // operation may use it while ordinary transactions are frozen.
    internal Func<SyncTx, bool> MayCrossBarrier { get; set; }

    // Committed: broadcast SyncCodec.Commit(sequence, tx) to everyone, the author included.
    // Stale/Held/Busy/Refused/Malformed: send SyncCodec.Reject to the author, followed by
    // the authoritative state of the touched entities as an ordinary host transaction.
    internal SyncVerdict Submit(SyncTx tx, out ulong sequence)
    {
        sequence = 0;
        if (tx == null || tx.Serial == 0 || tx.Ops == null) return SyncVerdict.Malformed;
        serials.TryGetValue(tx.Origin, out ulong last);
        if (tx.Serial <= last) return SyncVerdict.Duplicate;
        if (tx.Serial != last + 1) return SyncVerdict.Gap;
        serials[tx.Origin] = tx.Serial;

        SyncVerdict verdict = Judge(tx);
        if (verdict != SyncVerdict.Committed)
        {
            rejects.TryGetValue(tx.Origin, out uint count);
            rejects[tx.Origin] = ++count;
            foreach (SyncOp op in tx.Ops)
                if (op != null && op.Kind != SyncOpKind.Add) poison[(tx.Origin, op.Key)] = count;
            return verdict;
        }

        sequence = ++Sequence;
        foreach (SyncOp op in tx.Ops)
        {
            if (op.Kind == SyncOpKind.Add) continue;
            if (!entries.TryGetValue(op.Key, out Entry entry))
                entry = new Entry { Version = floor, Writer = tx.Origin, Other = floor };
            if (entry.Writer != tx.Origin)
            {
                entry.Other = entry.Version;
                entry.Writer = tx.Origin;
            }
            entry.Version = sequence;
            entries[op.Key] = entry;
        }
        return SyncVerdict.Committed;
    }

    private SyncVerdict Judge(SyncTx tx)
    {
        if (tx.Ops.Count == 0 || tx.Ops.Count > SyncCodec.MaxOps) return SyncVerdict.Malformed;
        foreach (SyncOp op in tx.Ops)
            if (op == null || op.Kind < SyncOpKind.Put || op.Kind > SyncOpKind.Add) return SyncVerdict.Malformed;

        // The host already applied its own change locally; it is authoritative by definition.
        if (tx.Origin == HostPeer) return SyncVerdict.Committed;

        if (tx.Barrier != Barrier) return SyncVerdict.Stale;
        if (Closed && (!tx.Crossing || MayCrossBarrier?.Invoke(tx) != true)) return SyncVerdict.Busy;
        rejects.TryGetValue(tx.Origin, out uint acknowledged);
        if (tx.Rejects > acknowledged) return SyncVerdict.Malformed;

        foreach (SyncOp op in tx.Ops)
        {
            if (holds.TryGetValue(op.Key, out byte holder) && holder != tx.Origin) return SyncVerdict.Held;
            if (op.Kind == SyncOpKind.Add) continue;
            if (op.Base > Sequence) return SyncVerdict.Malformed;

            // Built before the author rolled back an earlier rejected change to this entity.
            if (poison.TryGetValue((tx.Origin, op.Key), out uint needed))
            {
                if (tx.Rejects < needed) return SyncVerdict.Stale;
                poison.Remove((tx.Origin, op.Key));
            }

            if (entries.TryGetValue(op.Key, out Entry entry))
            {
                // The author's own unacknowledged writes do not conflict with each other.
                ulong foreign = entry.Writer == tx.Origin ? entry.Other : entry.Version;
                if (op.Base < foreign && CanCarryUpdatedState?.Invoke(tx, op) != true) return SyncVerdict.Stale;
            }
            else if (op.Base < floor)
            {
                return SyncVerdict.Stale;
            }
        }

        return Validate == null || Validate(tx) ? SyncVerdict.Committed : SyncVerdict.Refused;
    }

    // Presence lock for an entity a peer is dragging. False when someone else holds it.
    internal bool Hold(byte peer, EntityKey key)
    {
        if (holds.TryGetValue(key, out byte holder)) return holder == peer;
        holds[key] = peer;
        return true;
    }

    internal bool Release(byte peer, EntityKey key)
    {
        if (!holds.TryGetValue(key, out byte holder) || holder != peer) return false;
        holds.Remove(key);
        return true;
    }

    internal bool IsHeldByOther(byte peer, EntityKey key) =>
        holds.TryGetValue(key, out byte holder) && holder != peer;

    internal bool AnyHold => holds.Count != 0;

    internal bool Holder(EntityKey key, out byte peer) => holds.TryGetValue(key, out peer);

    internal IEnumerable<KeyValuePair<EntityKey, byte>> Holds => holds;

    // Disconnect: frees the peer's holds and forgets its numbering so a rejoin starts at serial 1.
    internal List<EntityKey> Forget(byte peer)
    {
        var freed = ReleaseAll(peer);
        var stale = new List<(byte, EntityKey)>();
        foreach ((byte, EntityKey) key in poison.Keys)
            if (key.Item1 == peer) stale.Add(key);
        foreach ((byte, EntityKey) key in stale) poison.Remove(key);

        serials.Remove(peer);
        rejects.Remove(peer);
        return freed;
    }

    internal List<EntityKey> ReleaseAll(byte peer)
    {
        var freed = new List<EntityKey>();
        foreach (KeyValuePair<EntityKey, byte> pair in holds)
            if (pair.Value == peer) freed.Add(pair.Key);
        foreach (EntityKey key in freed) holds.Remove(key);

        return freed;
    }

    // Team barrier (travel, night): closing stops ordinary guest transactions.
    internal void Close() => Closed = true;

    // Everyone realigns to the host checkpoint taken at the returned sequence.
    internal ulong Complete()
    {
        Barrier++;
        Closed = false;
        entries.Clear();
        poison.Clear();
        holds.Clear();
        floor = Sequence;
        return Sequence;
    }
}
