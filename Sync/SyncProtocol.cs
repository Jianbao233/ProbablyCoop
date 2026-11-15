using System;
using System.Collections.Generic;

namespace ProbablyCoop.Sync;

// Effect replication protocol (REDESIGN_PLAN_2026-11-11 rule one).
// A peer runs the original game logic locally, then reports the resulting state
// differences as one transaction. The host only orders and arbitrates them.
// Payloads are opaque here: the game layer decides how an item or a value is encoded.

internal enum SyncSpace : byte
{
    Item = 0,      // Id = GameItem.uniqueId
    Scalar = 1,    // Id = stable hash of a shop value name
    Container = 2, // Id = root container index or host item id
}

internal readonly struct EntityKey : IEquatable<EntityKey>
{
    internal readonly SyncSpace Space;
    internal readonly long Id;

    internal EntityKey(SyncSpace space, long id)
    {
        Space = space;
        Id = id;
    }

    internal static EntityKey Item(long uniqueId) => new EntityKey(SyncSpace.Item, uniqueId);

    // FNV-1a 64. The registry that owns scalar names must reject two names with one hash.
    internal static EntityKey Scalar(string name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        ulong hash = 14695981039346656037UL;
        foreach (char c in name)
        {
            hash = (hash ^ (byte)c) * 1099511628211UL;
            hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
        }
        return new EntityKey(SyncSpace.Scalar, (long)hash);
    }

    public bool Equals(EntityKey other) => Space == other.Space && Id == other.Id;

    public override bool Equals(object obj) => obj is EntityKey other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode() * 31 + (int)Space;

    public override string ToString() => Space + ":" + Id;
}

internal enum SyncOpKind : byte
{
    Put = 1,    // full state of one entity, placement included; versioned
    Remove = 2, // entity no longer exists; versioned
    Add = 3,    // signed delta on a numeric value; commutative, never conflicts by version
}

internal sealed class SyncOp
{
    internal EntityKey Key;
    internal SyncOpKind Kind;
    internal ulong Base;  // host sequence the author last saw for this entity
    internal long Delta;  // Add only
    internal byte[] Data; // Put only

    internal static SyncOp Put(EntityKey key, byte[] data) =>
        new SyncOp { Key = key, Kind = SyncOpKind.Put, Data = data ?? Array.Empty<byte>() };

    internal static SyncOp Remove(EntityKey key) => new SyncOp { Key = key, Kind = SyncOpKind.Remove };

    internal static SyncOp Add(EntityKey key, long delta) =>
        new SyncOp { Key = key, Kind = SyncOpKind.Add, Delta = delta };
}

internal sealed class SyncTx
{
    internal byte Origin;     // 0 = host
    internal ulong Serial;    // per origin, starts at 1, no gaps
    internal uint Barrier;    // barrier epoch the author was in
    internal uint Rejects;    // how many of its own rejections the author has processed
    internal bool Crossing;   // part of a team barrier, allowed while the barrier is closed
    internal byte[] Context;  // bounded action-specific validation data
    internal List<SyncOp> Ops = new List<SyncOp>();
}

internal enum SyncVerdict : byte
{
    Committed = 0,
    Duplicate = 1, // already processed, ignore
    Gap = 2,       // serial skipped: transport contract broken, resync the peer
    Stale = 3,     // someone else changed a touched entity first, or it follows a rejected change
    Held = 4,      // another peer is holding a touched entity
    Busy = 5,      // a team barrier is closed
    Refused = 6,   // the world rejected it (space, cash floor, ...)
    Malformed = 7,
}

internal enum SyncMessage : byte
{
    Tx = 1,      // any peer -> host
    Commit = 2,  // host -> everyone, in sequence order
    Reject = 3,  // host -> author
    Hold = 4,    // presence: peer started holding an entity
    Release = 5, // presence: peer stopped holding an entity
    Barrier = 6, // host -> everyone: barrier closed, or completed with a new epoch
}

internal sealed class SyncFrame
{
    internal SyncMessage Message;
    internal SyncTx Tx;          // Tx, Commit
    internal ulong Sequence;     // Commit, Barrier (sequence at completion)
    internal ulong Serial;       // Reject
    internal SyncVerdict Verdict; // Reject
    internal byte Peer;          // Hold, Release
    internal EntityKey Key;      // Hold, Release
    internal uint Barrier;       // Barrier
    internal bool Closed;        // Barrier
}

internal static class SyncCodec
{
    internal const int MaxOps = 4096;
    internal const int MaxData = 1024 * 1024;
    internal const int MaxContext = 128;
    internal const int MaxFrameBytes = 8 * 1024 * 1024;

    // Reserve space for the commit header and worst-case varints before stamping
    // a request. Every encodable request must also fit when echoed as a commit.
    internal static bool Fits(List<SyncOp> ops, byte[] context = null)
    {
        int contextLength = context?.Length ?? 0;
        if (ops == null || ops.Count == 0 || ops.Count > MaxOps || contextLength > MaxContext) return false;
        int remaining = MaxFrameBytes - 42 - contextLength - 25 * ops.Count;
        foreach (SyncOp op in ops)
        {
            if (op == null || op.Key.Space > SyncSpace.Container ||
                op.Kind < SyncOpKind.Put || op.Kind > SyncOpKind.Add) return false;
            if (op.Kind != SyncOpKind.Put) continue;
            int length = op.Data?.Length ?? 0;
            if (length > MaxData || length > remaining) return false;
            remaining -= length;
        }
        return true;
    }

    internal static byte[] Tx(SyncTx tx)
    {
        var writer = new SyncWriter();
        writer.Byte((byte)SyncMessage.Tx);
        WriteTx(writer, tx);
        return writer.ToArray();
    }

    internal static byte[] Commit(ulong sequence, SyncTx tx)
    {
        var writer = new SyncWriter();
        writer.Byte((byte)SyncMessage.Commit);
        writer.ULong(sequence);
        WriteTx(writer, tx);
        return writer.ToArray();
    }

    internal static byte[] Reject(ulong serial, SyncVerdict verdict)
    {
        var writer = new SyncWriter();
        writer.Byte((byte)SyncMessage.Reject);
        writer.ULong(serial);
        writer.Byte((byte)verdict);
        return writer.ToArray();
    }

    internal static byte[] Hold(byte peer, EntityKey key, bool held)
    {
        var writer = new SyncWriter();
        writer.Byte((byte)(held ? SyncMessage.Hold : SyncMessage.Release));
        writer.Byte(peer);
        WriteKey(writer, key);
        return writer.ToArray();
    }

    internal static byte[] Barrier(uint epoch, bool closed, ulong sequence)
    {
        var writer = new SyncWriter();
        writer.Byte((byte)SyncMessage.Barrier);
        writer.UInt(epoch);
        writer.Byte(closed ? (byte)1 : (byte)0);
        writer.ULong(sequence);
        return writer.ToArray();
    }

    internal static bool TryDecode(byte[] data, out SyncFrame frame)
    {
        frame = null;
        if (data == null || data.Length > MaxFrameBytes) return false;
        var reader = new SyncReader(data);
        var result = new SyncFrame { Message = (SyncMessage)reader.Byte() };
        switch (result.Message)
        {
            case SyncMessage.Tx:
                result.Tx = ReadTx(reader);
                break;
            case SyncMessage.Commit:
                result.Sequence = reader.ULong();
                result.Tx = ReadTx(reader);
                if (result.Sequence == 0) return false;
                break;
            case SyncMessage.Reject:
                result.Serial = reader.ULong();
                result.Verdict = (SyncVerdict)reader.Byte();
                if (result.Verdict < SyncVerdict.Stale || result.Verdict > SyncVerdict.Malformed) return false;
                break;
            case SyncMessage.Hold:
            case SyncMessage.Release:
                result.Peer = reader.Byte();
                if (!ReadKey(reader, out result.Key)) return false;
                break;
            case SyncMessage.Barrier:
                result.Barrier = reader.UInt();
                byte closed = reader.Byte();
                if (closed > 1) return false;
                result.Closed = closed == 1;
                result.Sequence = reader.ULong();
                break;
            default:
                return false;
        }

        if (!reader.Ok || !reader.AtEnd) return false;
        if ((result.Message == SyncMessage.Tx || result.Message == SyncMessage.Commit) && result.Tx == null)
            return false;
        frame = result;
        return true;
    }

    private static void WriteKey(SyncWriter writer, EntityKey key)
    {
        writer.Byte((byte)key.Space);
        writer.Long(key.Id);
    }

    private static bool ReadKey(SyncReader reader, out EntityKey key)
    {
        byte space = reader.Byte();
        long id = reader.Long();
        key = new EntityKey((SyncSpace)space, id);
        return reader.Ok && space <= (byte)SyncSpace.Container;
    }

    private static void WriteTx(SyncWriter writer, SyncTx tx)
    {
        if (tx == null) throw new ArgumentNullException(nameof(tx));
        if (!Fits(tx.Ops, tx.Context)) throw new ArgumentException("transaction exceeds protocol limits", nameof(tx));
        writer.Byte(tx.Origin);
        writer.ULong(tx.Serial);
        writer.UInt(tx.Barrier);
        writer.UInt(tx.Rejects);
        writer.Byte(tx.Crossing ? (byte)1 : (byte)0);
        writer.Bytes(tx.Context);
        writer.UInt((uint)tx.Ops.Count);
        foreach (SyncOp op in tx.Ops)
        {
            WriteKey(writer, op.Key);
            writer.Byte((byte)op.Kind);
            switch (op.Kind)
            {
                case SyncOpKind.Put:
                    if (op.Data != null && op.Data.Length > MaxData) throw new ArgumentException("op data", nameof(tx));
                    writer.ULong(op.Base);
                    writer.Bytes(op.Data);
                    break;
                case SyncOpKind.Remove:
                    writer.ULong(op.Base);
                    break;
                case SyncOpKind.Add:
                    writer.Long(op.Delta);
                    break;
                default:
                    throw new ArgumentException("op kind", nameof(tx));
            }
        }
    }

    private static SyncTx ReadTx(SyncReader reader)
    {
        var tx = new SyncTx
        {
            Origin = reader.Byte(),
            Serial = reader.ULong(),
            Barrier = reader.UInt(),
            Rejects = reader.UInt(),
        };
        byte crossing = reader.Byte();
        tx.Context = reader.Bytes(MaxContext);
        uint count = reader.UInt();
        if (!reader.Ok || tx.Serial == 0 || crossing > 1 || count == 0 || count > MaxOps) return null;
        tx.Crossing = crossing == 1;
        for (uint i = 0; i < count; i++)
        {
            var op = new SyncOp();
            if (!ReadKey(reader, out op.Key)) return null;
            op.Kind = (SyncOpKind)reader.Byte();
            switch (op.Kind)
            {
                case SyncOpKind.Put:
                    op.Base = reader.ULong();
                    op.Data = reader.Bytes(MaxData);
                    break;
                case SyncOpKind.Remove:
                    op.Base = reader.ULong();
                    break;
                case SyncOpKind.Add:
                    op.Delta = reader.Long();
                    break;
                default:
                    return null;
            }
            if (!reader.Ok) return null;
            tx.Ops.Add(op);
        }
        return Fits(tx.Ops, tx.Context) ? tx : null;
    }
}
