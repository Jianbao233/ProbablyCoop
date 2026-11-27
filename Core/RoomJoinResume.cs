using System;
using System.Globalization;

namespace ProbablyCoop.Core;

internal sealed class RoomJoinResume
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private readonly long savedAtTicks;
    internal string Code { get; }
    internal string Mapping { get; }

    private RoomJoinResume(long savedAtTicks, string code, string mapping)
    {
        this.savedAtTicks = savedAtTicks;
        Code = code;
        Mapping = mapping;
    }

    internal bool IsCurrent(DateTime now) => now.Ticks >= savedAtTicks && now.Ticks - savedAtTicks < Lifetime.Ticks;

    internal string Write() => savedAtTicks.ToString(CultureInfo.InvariantCulture) + "\n" + Code +
        (Mapping == null ? "" : "\n" + Mapping);

    internal static RoomJoinResume Create(string joinInfo, DateTime now)
    {
        if (DirectRoomEndpoint.TryReadMappedJoin(joinInfo, out var code, out var endpoint))
            return new RoomJoinResume(now.Ticks, code, endpoint);
        if (RoomCode.TryDecode(joinInfo, out ulong lobby))
            code = RoomCode.Encode(lobby);
        else if (DirectRoomEndpoint.TryParseCode(joinInfo, out _, out _, out _))
            code = joinInfo;
        else return null;
        return new RoomJoinResume(now.Ticks, code, null);
    }

    internal static RoomJoinResume Read(string text, DateTime now)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 128) return null;
        int newline = text.IndexOf('\n');
        if (newline <= 0 || !long.TryParse(text.AsSpan(0, newline), NumberStyles.None, CultureInfo.InvariantCulture,
            out long ticks) || ticks <= 0 || ticks > now.Ticks || now.Ticks - ticks >= Lifetime.Ticks) return null;
        return Create(text.Substring(newline + 1), new DateTime(ticks, DateTimeKind.Utc));
    }
}
