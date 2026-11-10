using System;
using System.Buffers.Binary;

namespace ProbablyCoop.Core;

/// <summary>Accommodates an interleaved four-chunk send plus repair and control traffic.</summary>
internal sealed class DirectFragments
{
    internal const byte Marker = 3;
    internal const int BodyOffset = 28;
    internal const int ChunkSize = 1000;
    private readonly Assembly[] pending = new Assembly[8];

    internal static int Count(int length) => (length + ChunkSize - 1) / ChunkSize;

    internal static byte[] Encode(byte[] payload, ulong sequence, int index)
    {
        var chunk = payload.AsSpan(index * ChunkSize, Math.Min(ChunkSize, payload.Length - index * ChunkSize));
        var datagram = new byte[BodyOffset + chunk.Length];
        datagram[16] = Marker;
        BinaryPrimitives.WriteUInt64LittleEndian(datagram.AsSpan(17, 8), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(25, 2), (ushort)payload.Length);
        datagram[27] = (byte)index;
        chunk.CopyTo(datagram.AsSpan(BodyOffset));
        return datagram;
    }

    internal byte[] Receive(ReadOnlySpan<byte> datagram, double now)
    {
        if (datagram.Length <= BodyOffset || datagram.Length > BodyOffset + ChunkSize) return null;
        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(17, 8));
        int length = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(25, 2));
        int index = datagram[27];
        if (sequence == 0 || length <= ChunkSize || length > Wire.MaxSize || index >= Count(length) ||
            datagram.Length - BodyOffset != Math.Min(ChunkSize, length - index * ChunkSize))
            return null;

        int available = -1;
        int oldest = 0;
        int completed = -1;
        Assembly entry = null;
        for (int i = 0; i < pending.Length; i++)
        {
            var current = pending[i];
            if (current == null || now >= current.Expires)
            {
                pending[i] = null;
                available = i;
            }
            else if (current.Sequence == sequence)
            {
                entry = current;
            }
            else if (current.Complete && (completed<0 || current.Expires<pending[completed].Expires))
            {
                completed = i;
            }
            else if (pending[oldest] != null && current.Expires < pending[oldest].Expires)
            {
                oldest = i;
            }
        }
        if (entry == null)
        {
            entry = new Assembly(sequence, length, now + 2);
            pending[available >= 0 ? available : completed >= 0 ? completed : oldest] = entry;
        }
        int bit = 1 << index;
        if (entry.Data.Length != length || (entry.Mask & bit) != 0) return null;
        datagram.Slice(BodyOffset).CopyTo(entry.Data.AsSpan(index * ChunkSize));
        entry.Mask |= bit;
        return entry.Complete ? entry.Data : null;
    }

    internal void Reset() => Array.Clear(pending, 0, pending.Length);

    private sealed class Assembly
    {
        internal readonly ulong Sequence;
        internal readonly byte[] Data;
        internal readonly double Expires;
        internal int Mask;
        internal bool Complete => Mask == (1 << Count(Data.Length)) - 1;

        internal Assembly(ulong sequence, int length, double expires)
        {
            Sequence = sequence;
            Data = new byte[length];
            Expires = expires;
        }
    }
}
