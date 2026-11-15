using System;
using System.Globalization;

namespace ProbablyCoop.Sync;

// Native allocation stays below HostStart. The two replication ranges are
// independently monotonic, including items already sold or destroyed.
internal readonly struct EffectItemIds
{
    internal const int HostStart = 0x20000000, GuestStart = 0x40000000, End = 0x60000000;
    internal const string RunKey = "probably_coop.effect_run", RunSchema = "1";
    internal const string SaveKey = "probably_coop.effect_item_ids.v1";
    internal readonly int Host, Guest;

    internal EffectItemIds(int host, int guest)
    {
        Host = host;
        Guest = guest;
    }

    internal static bool ValidGuest(int value) => value >= GuestStart - 1 && value < End;
    internal bool Valid => Host >= HostStart - 1 && Host < GuestStart && ValidGuest(Guest);
    internal bool Covers(int id, int native) => id > 0 &&
        (id < HostStart ? id <= native : id < GuestStart ? id <= Host : id <= Guest);

    internal string Encode() => Host.ToString(CultureInfo.InvariantCulture) + "," + Guest.ToString(CultureInfo.InvariantCulture);

    internal static bool TryRead(string text, out EffectItemIds value)
    {
        value = default;
        if (text == null || text.Length > 21) return false;
        int split = text.IndexOf(',');
        if (split <= 0 || !int.TryParse(text.AsSpan(0, split), NumberStyles.None, CultureInfo.InvariantCulture, out int host) ||
            !int.TryParse(text.AsSpan(split + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int guest)) return false;
        value = new EffectItemIds(host, guest);
        return value.Valid;
    }
}
