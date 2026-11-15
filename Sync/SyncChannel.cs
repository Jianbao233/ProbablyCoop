using System;
using System.Collections.Generic;

namespace ProbablyCoop.Sync;

// What the transaction channel needs from the game. Implemented by the shop adapter;
// the tests use an in-memory world.
internal interface ISyncWorld
{
    // Validate and apply the whole transaction together. A refusal leaves the
    // world unchanged. On the host this runs before the sequence advances.
    bool TryApply(SyncTx tx);

    // Guest only. Record acceptance of a local prediction without running the action again.
    bool TryConfirm(SyncTx tx);

    // Guest only. Undo speculative effects. The host follows rejection with the
    // authoritative state of the touched versioned entities.
    void Revert(SyncTx tx);

    // Host only. Current encoded state of an entity, null when it does not exist.
    byte[] Read(EntityKey key);

    // Host only. A grip may target an existing entity that the actor can move.
    bool CanHold(byte actor, EntityKey key);
}

internal interface ISyncWholeItemMove
{
    bool CanCarryUpdatedState(SyncTx tx, SyncOp op);
}

// Host end of the world channel: orders everyone's changes and keeps guests aligned.
internal sealed class SyncHostChannel
{
    private readonly SyncHost host = new SyncHost();
    private readonly HashSet<byte> peers = new HashSet<byte>();
    private readonly Func<byte, byte[], bool> send;
    private readonly ISyncWorld world;
    private ulong serial;

    internal SyncHostChannel(Func<byte, byte[], bool> send, ISyncWorld world)
    {
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        host.Validate = world.TryApply;
        if (world is ISyncWholeItemMove movement) host.CanCarryUpdatedState = movement.CanCarryUpdatedState;
    }

    internal ulong Sequence => host.Sequence;

    internal uint Barrier => host.Barrier;

    internal bool Closed => host.Closed;

    internal bool HasReservations => host.AnyHold;

    internal bool IsAttached(byte peer) => peers.Contains(peer);

    // A guest lost a commit. Capture the world and call Attach for it again.
    internal Action<byte> Resync;

    // Gives a guest the world. `save` must be captured now, with no Local call in between.
    // Also used to realign a guest that is already attached.
    internal bool Attach(byte peer, byte[] save, int limit)
    {
        if (peer == SyncHost.HostPeer || !ValidSave(save)) return false;
        byte[] message = WorldWire.EncodeCheckpoint(host.Barrier, host.Sequence, save);
        if (message.Length > limit) return false;
        if (peers.Contains(peer))
        {
            // Installing a checkpoint cancels the guest's original mouse drag.
            // Its old holds must not be re-sent with the new state.
            foreach (EntityKey key in host.ReleaseAll(peer))
                Broadcast(SyncCodec.Hold(peer, key, false));
        }
        if (!Align(peer, message)) return false;
        peers.Add(peer);
        return true;
    }

    internal void Detach(byte peer)
    {
        if (!peers.Remove(peer)) return;
        foreach (EntityKey key in host.Forget(peer)) Broadcast(SyncCodec.Hold(peer, key, false));
    }

    // A change the host already made to its own world. Returns its sequence, 0 when empty.
    internal ulong Local(List<SyncOp> ops)
    {
        if (ops == null || ops.Count == 0) return 0;
        if (!SyncCodec.Fits(ops)) throw new ArgumentException("Host change exceeds the protocol limits.", nameof(ops));
        var tx = new SyncTx { Origin = SyncHost.HostPeer, Serial = ++serial, Barrier = host.Barrier, Ops = ops };
        if (host.Submit(tx, out ulong sequence) != SyncVerdict.Committed)
            throw new ArgumentException("Malformed host change.", nameof(ops));
        Broadcast(SyncCodec.Commit(sequence, tx));
        return sequence;
    }

    // False: the peer broke the protocol and must be dropped.
    internal bool Receive(byte peer, byte[] message)
    {
        if (!peers.Contains(peer)) return false;
        if (WorldWire.IsResync(message))
        {
            Resync?.Invoke(peer);
            return true;
        }
        if (!SyncCodec.TryDecode(message, out SyncFrame frame)) return false;
        switch (frame.Message)
        {
            case SyncMessage.Tx:
                return OnTx(peer, frame.Tx);
            case SyncMessage.Hold:
                if (frame.Peer != peer) return false;
                if (host.Closed || !world.CanHold(peer, frame.Key))
                    return send(peer, SyncCodec.Hold(peer, frame.Key, false));
                if (host.Hold(peer, frame.Key)) Broadcast(SyncCodec.Hold(peer, frame.Key, true));
                else if (host.Holder(frame.Key, out byte holder)) send(peer, SyncCodec.Hold(holder, frame.Key, true));
                return true;
            case SyncMessage.Release:
                if (frame.Peer != peer) return false;
                if (host.Release(peer, frame.Key)) Broadcast(SyncCodec.Hold(peer, frame.Key, false));
                return true;
            default:
                return false;
        }
    }

    private bool OnTx(byte peer, SyncTx tx)
    {
        if (tx.Origin != peer) return false;
        SyncVerdict verdict = host.Submit(tx, out ulong sequence);
        switch (verdict)
        {
            case SyncVerdict.Committed:
                Broadcast(SyncCodec.Commit(sequence, tx));
                return true;
            case SyncVerdict.Duplicate:
                return true;
            case SyncVerdict.Gap:
            case SyncVerdict.Malformed:
                return false;
            default:
                return send(peer, SyncCodec.Reject(tx.Serial, verdict)) && Correct(peer, tx);
        }
    }

    // After a rejection: the true state of every versioned entity the change touched.
    private bool Correct(byte peer, SyncTx tx)
    {
        var seen = new HashSet<EntityKey>();
        var ops = new List<SyncOp>();
        foreach (SyncOp op in tx.Ops)
        {
            if (op.Kind == SyncOpKind.Add || !seen.Add(op.Key)) continue;
            byte[] state = world.Read(op.Key);
            ops.Add(state == null ? SyncOp.Remove(op.Key) : SyncOp.Put(op.Key, state));
        }
        if (ops.Count > 0 && !SyncCodec.Fits(ops))
        {
            if (Resync == null) return false;
            Resync(peer);
            return true;
        }
        Local(ops);
        return true;
    }

    // The host itself picks an entity up. False when a guest holds it.
    internal bool Hold(EntityKey key)
    {
        if (host.Closed || !world.CanHold(SyncHost.HostPeer, key) || !host.Hold(SyncHost.HostPeer, key)) return false;
        Broadcast(SyncCodec.Hold(SyncHost.HostPeer, key, true));
        return true;
    }

    internal void Release(EntityKey key)
    {
        if (host.Release(SyncHost.HostPeer, key)) Broadcast(SyncCodec.Hold(SyncHost.HostPeer, key, false));
    }

    internal bool IsHeldByOther(EntityKey key) => IsHeldByOther(SyncHost.HostPeer, key);
    internal bool IsHeldByOther(byte actor, EntityKey key) => host.IsHeldByOther(actor, key);
    internal bool IsHeld(EntityKey key) => host.Holder(key, out _);

    // Team barrier (travel, night): guests stop ordinary changes until Complete.
    internal void Close()
    {
        if (host.Closed) return;
        host.Close();
        Broadcast(SyncCodec.Barrier(host.Barrier, true, host.Sequence));
    }

    // The barrier is over: everyone reloads the host world captured now.
    // If the checkpoint cannot be encoded, leave the barrier closed so the
    // caller can try again without advancing the authoritative epoch.
    internal bool Complete(byte[] save, int limit)
    {
        if (!host.Closed || !ValidSave(save)) return false;
        byte[] message = WorldWire.EncodeCheckpoint(host.Barrier + 1, host.Sequence, save);
        if (message.Length > limit) return false;
        host.Complete();
        bool sent = true;
        foreach (byte peer in peers) sent &= Align(peer, message);
        return sent;
    }

    private static bool ValidSave(byte[] save) => save != null && save.Length > 0 && save.Length <= WorldWire.MaxSave;

    private bool Align(byte peer, byte[] checkpoint)
    {
        if (!send(peer, checkpoint)) return false;
        foreach (KeyValuePair<EntityKey, byte> hold in host.Holds)
            if (!send(peer, SyncCodec.Hold(hold.Value, hold.Key, true))) return false;
        return !host.Closed || send(peer, SyncCodec.Barrier(host.Barrier, true, host.Sequence));
    }

    private void Broadcast(byte[] message)
    {
        foreach (byte peer in peers) send(peer, message);
    }
}

// Guest end of the world channel.
internal sealed class SyncGuestChannel
{
    internal const int MaxQueued = 256;
    private const int MaxQueuedBytes = SyncCodec.MaxFrameBytes;
    private const int MaxPending = 256;

    private readonly SyncGuest guest;
    private readonly Dictionary<EntityKey, byte> holds = new Dictionary<EntityKey, byte>();
    private readonly List<byte[]> queued = new List<byte[]>();
    private readonly Func<byte[], bool> send;
    private readonly ISyncWorld world;
    private bool loading;
    private bool asked;
    private int queuedBytes;

    internal SyncGuestChannel(byte peer, Func<byte[], bool> send, ISyncWorld world)
    {
        guest = new SyncGuest(peer);
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.world = world ?? throw new ArgumentNullException(nameof(world));
    }

    internal byte Peer => guest.Peer;

    internal ulong Sequence => guest.Sequence;

    internal uint Barrier => guest.Barrier;

    internal int PendingCount => guest.PendingCount;

    // The local world is the host world: local changes may be submitted.
    internal bool Aligned { get; private set; }

    // A team barrier is closed: only crossing changes will be accepted.
    internal bool Closed { get; private set; }

    // A checkpoint arrived. Load this save, then call Loaded. May fire again at any time.
    internal event Action<byte[]> Load;

    // One of our changes was rejected (already reverted); for telling the player.
    internal event Action<SyncTx, SyncVerdict> Bounced;

    // False: the host broke the protocol; leave the room.
    internal bool Receive(byte[] message)
    {
        if (message == null || message.Length == 0 || message.Length > SyncCodec.MaxFrameBytes) return false;
        if (message[0] == WorldWire.Checkpoint)
        {
            if (!WorldWire.TryDecodeCheckpoint(message, out uint barrier, out ulong sequence, out byte[] save) ||
                barrier < guest.Barrier || sequence < guest.Sequence)
                return false;
            guest.Realign(barrier, sequence);
            holds.Clear();
            queued.Clear();
            queuedBytes = 0;
            Closed = false;
            Aligned = false;
            asked = false;
            loading = true;
            Load?.Invoke(save);
            return true;
        }
        if (message[0] < (byte)SyncMessage.Commit || message[0] > (byte)SyncMessage.Barrier) return false;
        if (!loading) return (Aligned || asked) && Process(message);
        if (queued.Count >= MaxQueued || message.Length > MaxQueuedBytes - queuedBytes) return false;
        queued.Add(message);
        queuedBytes += message.Length;
        return true;
    }

    // The save handed to Load is now the local world. False: leave the room.
    internal bool Loaded()
    {
        if (!loading) return true;
        loading = false;
        Aligned = true;
        bool valid = true;
        foreach (byte[] message in queued)
            if (!(valid = Process(message))) break;
        queued.Clear();
        queuedBytes = 0;
        return valid;
    }

    private bool Process(byte[] message)
    {
        if (!SyncCodec.TryDecode(message, out SyncFrame frame)) return false;
        if (asked)
        {
            // A reliable reject may follow the gap but precede the requested
            // checkpoint. Keep the counter, without touching the old world.
            if (frame.Message == SyncMessage.Reject) guest.OnReject(frame.Serial);
            return true;
        }
        switch (frame.Message)
        {
            case SyncMessage.Commit:
                if (frame.Tx.Origin != SyncHost.HostPeer && frame.Tx.Origin != guest.Peer) return false;
                if (frame.Tx.Barrier != guest.Barrier) return RequestCheckpoint();
                SyncApply apply = guest.OnCommit(frame.Sequence, frame.Tx);
                if (apply == SyncApply.Resync || apply == SyncApply.Remote && !world.TryApply(frame.Tx) ||
                    apply == SyncApply.Own && !world.TryConfirm(frame.Tx))
                    return RequestCheckpoint();
                return true;
            case SyncMessage.Reject:
                SyncTx bounced = guest.OnReject(frame.Serial);
                if (bounced != null)
                {
                    world.Revert(bounced);
                    Bounced?.Invoke(bounced, frame.Verdict);
                }
                return true;
            case SyncMessage.Hold:
                holds[frame.Key] = frame.Peer;
                return true;
            case SyncMessage.Release:
                if (holds.TryGetValue(frame.Key, out byte holder) && holder == frame.Peer) holds.Remove(frame.Key);
                return true;
            case SyncMessage.Barrier:
                if (frame.Barrier != guest.Barrier || frame.Sequence != guest.Sequence) return RequestCheckpoint();
                Closed = frame.Closed;
                return true;
            default:
                return false;
        }
    }

    private bool RequestCheckpoint()
    {
        asked = true;
        Aligned = false;
        return send(WorldWire.EncodeResync());
    }

    // A change already made to the local world. False: not aligned, nothing was sent
    // (the checkpoint being loaded replaces the local world anyway).
    internal bool Local(List<SyncOp> ops, bool crossing = false) => Local(ops, out _, crossing);

    internal bool Local(List<SyncOp> ops, out ulong serial, bool crossing = false, byte[] context = null)
    {
        serial = 0;
        if (!Aligned || Closed && !crossing || ops == null || ops.Count == 0) return false;
        if (guest.PendingCount < MaxPending && SyncCodec.Fits(ops, context))
        {
            var transaction = guest.Submit(ops, crossing, context);
            if (!send(SyncCodec.Tx(transaction))) return false;
            serial = transaction.Serial;
            return true;
        }
        RequestCheckpoint();
        return false;
    }

    // Ask to pick an entity up; the answer arrives as a hold naming whoever has it.
    internal bool Hold(EntityKey key)
    {
        return Aligned && !Closed && !IsHeldByOther(key) && send(SyncCodec.Hold(guest.Peer, key, true));
    }

    internal void Release(EntityKey key)
    {
        if (Aligned) send(SyncCodec.Hold(guest.Peer, key, false));
    }

    internal bool Holder(EntityKey key, out byte peer) => holds.TryGetValue(key, out peer);

    internal bool IsHeldByOther(EntityKey key) => holds.TryGetValue(key, out byte holder) && holder != guest.Peer;

    internal bool IsPending(EntityKey key) => guest.IsPending(key);
}
