using System;
using System.Globalization;

namespace ProbablyCoop.Net;

// Steam room code: PS1-<lobby id in base 36>-<CRC-16, 4 hex digits>.
// The checksum catches typing mistakes. It is not authentication: Steam authenticates
// the lobby members and the host approves the partner by name.
internal static class SteamCode
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    internal static string Encode(ulong lobby)
    {
        if (lobby == 0) throw new ArgumentOutOfRangeException(nameof(lobby));
        var buffer = new char[13];
        int start = buffer.Length;
        ulong value = lobby;
        do
        {
            buffer[--start] = Digits[(int)(value % 36)];
            value /= 36;
        } while (value != 0);
        return "PS1-" + new string(buffer, start, buffer.Length - start) + "-" +
               Checksum(lobby).ToString("X4", CultureInfo.InvariantCulture);
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
                CultureInfo.InvariantCulture, out ushort checksum) || checksum != Checksum(value)) return false;
        lobby = value;
        return true;
    }

    // CRC-16/CCITT-FALSE over the little-endian lobby id.
    private static ushort Checksum(ulong value)
    {
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
