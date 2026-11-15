using System;
using System.Text;

namespace ProbablyCoop.Sync;

// Engine-free binary helpers shared by the link layer and the replication protocol.
// Decoding never throws: a short or oversized input flips Ok to false and yields defaults.
internal sealed class SyncWriter
{
    private byte[] data = new byte[256];

    internal int Length { get; private set; }

    internal void Byte(byte value)
    {
        Need(1);
        data[Length++] = value;
    }

    internal void Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

    internal void ULong(ulong value)
    {
        Need(10);
        while (value >= 0x80)
        {
            data[Length++] = (byte)(value | 0x80);
            value >>= 7;
        }
        data[Length++] = (byte)value;
    }

    internal void UInt(uint value) => ULong(value);

    internal void Long(long value) => ULong((ulong)((value << 1) ^ (value >> 63)));

    internal void Bytes(byte[] value) => Bytes(value, 0, value == null ? 0 : value.Length);

    internal void Bytes(byte[] value, int offset, int count)
    {
        if (value == null) count = 0;
        ULong((ulong)count);
        if (count == 0) return;
        Need(count);
        Buffer.BlockCopy(value, offset, data, Length, count);
        Length += count;
    }

    internal void Text(string value) =>
        Bytes(string.IsNullOrEmpty(value) ? null : Encoding.UTF8.GetBytes(value));

    internal byte[] ToArray()
    {
        var result = new byte[Length];
        Buffer.BlockCopy(data, 0, result, 0, Length);
        return result;
    }

    private void Need(int count)
    {
        if (Length + count <= data.Length) return;
        Array.Resize(ref data, Math.Max(data.Length * 2, Length + count));
    }
}

internal sealed class SyncReader
{
    private static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);
    private readonly byte[] data;
    private readonly int end;
    private int position;

    internal SyncReader(byte[] data) : this(data, data == null ? 0 : data.Length) { }

    // Reads only the first count bytes, so a receive buffer can be decoded in place.
    internal SyncReader(byte[] data, int count)
    {
        Ok = data != null && count >= 0 && count <= data.Length;
        this.data = data ?? Array.Empty<byte>();
        end = Ok ? count : 0;
    }

    internal bool Ok { get; private set; }

    internal bool AtEnd => position == end;

    internal byte Byte()
    {
        if (!Ok || position >= end)
        {
            Ok = false;
            return 0;
        }
        return data[position++];
    }

    internal bool Bool()
    {
        byte value = Byte();
        if (value > 1) Ok = false;
        return Ok && value == 1;
    }

    internal ulong ULong()
    {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            byte part = Byte();
            if (!Ok) return 0;
            if (shift == 63 && part > 1) break;
            result |= (ulong)(part & 0x7F) << shift;
            if ((part & 0x80) == 0) return result;
        }
        Ok = false;
        return 0;
    }

    internal uint UInt()
    {
        ulong value = ULong();
        if (value > uint.MaxValue)
        {
            Ok = false;
            return 0;
        }
        return (uint)value;
    }

    internal long Long()
    {
        ulong value = ULong();
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    internal byte[] Bytes(int limit)
    {
        ulong count = ULong();
        if (!Ok || count > (ulong)limit || count > (ulong)(end - position))
        {
            Ok = false;
            return Array.Empty<byte>();
        }
        var result = new byte[(int)count];
        Buffer.BlockCopy(data, position, result, 0, result.Length);
        position += result.Length;
        return result;
    }

    // limit is in UTF-8 bytes. Malformed UTF-8 fails the read instead of producing U+FFFD.
    internal string Text(int limit)
    {
        byte[] raw = Bytes(limit);
        if (!Ok || raw.Length == 0) return "";
        try { return Strict.GetString(raw); }
        catch (ArgumentException)
        {
            Ok = false;
            return "";
        }
    }
}
