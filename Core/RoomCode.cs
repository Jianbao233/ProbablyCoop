using System;
using System.Globalization;

namespace ProbablyCoop.Core;

internal static class RoomCode
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    internal static string Encode(ulong lobby)
    {
        if (lobby == 0) throw new ArgumentOutOfRangeException(nameof(lobby));
        Span<char> buffer = stackalloc char[13];
        var value = lobby;
        int start = buffer.Length;
        do
        {
            buffer[--start] = Digits[(int)(value % 36)];
            value /= 36;
        } while (value != 0);

        return $"PS1-{new string(buffer.Slice(start))}-{Checksum(lobby):X4}";
    }

    internal static bool TryDecode(string code, out ulong lobby)
    {
        lobby = 0;
        if (code == null || code.Length > 64) return false;
        string[] parts = code.Trim().ToUpperInvariant().Split('-');
        if (parts.Length != 3 || parts[0] != "PS1" || parts[1].Length is < 1 or > 13 ||
            parts[2].Length != 4) return false;

        ulong value = 0;
        foreach (char c in parts[1])
        {
            int digit = Digits.IndexOf(c);
            if (digit < 0 || value > (ulong.MaxValue - (ulong)digit) / 36) return false;
            value = value * 36 + (ulong)digit;
        }

        if (value == 0 || !ushort.TryParse(parts[2], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var checksum) || checksum != Checksum(value)) return false;
        lobby = value;
        return true;
    }

    private static ushort Checksum(ulong value)
    {
        // CRC-16/CCITT-FALSE over the little-endian lobby ID. Detects typos, not authentication.
        ushort crc = 0xffff;
        for (int i = 0; i < 8; i++, value >>= 8)
        {
            crc ^= (ushort)((value & 255) << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        return crc;
    }
}
