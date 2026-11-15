using System;
using System.Buffers.Binary;

namespace ProbablyCoop.Sync;

internal readonly struct CursorPresence
{
    internal const byte Channel = 2;
    private const int Size = 9;
    internal readonly uint Epoch;
    internal readonly ushort X, Y;
    internal readonly bool Shown, Clicked;

    internal CursorPresence(uint epoch, ushort x, ushort y, bool shown, bool clicked)
    {
        Epoch = epoch;
        X = x;
        Y = y;
        Shown = shown;
        Clicked = shown && clicked;
    }

    internal byte[] Encode()
    {
        var data = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Epoch);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), X);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), Y);
        data[8] = Shown ? Clicked ? (byte)3 : (byte)1 : (byte)0;
        return data;
    }

    internal static bool TryRead(byte[] data, out CursorPresence cursor)
    {
        cursor = default;
        if (data == null || data.Length != Size || data[8] > 3 || data[8] == 2) return false;
        cursor = new CursorPresence(BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)),
            (data[8] & 1) != 0, (data[8] & 2) != 0);
        return true;
    }
}
