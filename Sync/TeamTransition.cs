using System;

namespace ProbablyCoop.Sync;

internal enum TeamTransitionKind : byte { None, Depart, Return, Sleep }

internal enum TeamVoteFailure : byte { None, Busy, Unavailable, OtherProposal, BackpackUnavailable, BackpackTaken, Stale }

internal readonly struct TeamTransition : IEquatable<TeamTransition>
{
    internal readonly TeamTransitionKind Kind;
    internal readonly int Day;
    internal readonly string Destination;

    internal TeamTransition(TeamTransitionKind kind, int day, string destination = "")
    {
        Kind = kind;
        Day = day;
        Destination = destination;
    }

    internal bool Valid => Kind is >= TeamTransitionKind.Depart and <= TeamTransitionKind.Sleep &&
        Day > 0 && (Kind == TeamTransitionKind.Depart ? ValidLocation(Destination) : Destination == "");
    private bool Empty => Kind == TeamTransitionKind.None && Day == 0 && string.IsNullOrEmpty(Destination);

    internal static bool ValidLocation(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
        foreach (char c in name)
            if (c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.'))
                return false;
        return true;
    }

    internal byte[] EncodeVote(uint epoch, int backpack = 0, ulong revision = 0, bool cancel = false)
    {
        var writer = new SyncWriter();
        writer.Byte(cancel ? WorldWire.TeamCancel : WorldWire.TeamVote);
        writer.UInt(epoch);
        Write(writer);
        writer.UInt((uint)backpack);
        writer.ULong(revision);
        return writer.ToArray();
    }

    internal byte[] EncodeReadiness(uint epoch, byte mask, ulong revision, int hostBackpack, int guestBackpack,
        TeamVoteFailure failure)
    {
        var writer = new SyncWriter();
        writer.Byte(WorldWire.TeamReadiness);
        writer.UInt(epoch);
        Write(writer);
        writer.Byte(mask);
        writer.ULong(revision);
        writer.UInt((uint)hostBackpack);
        writer.UInt((uint)guestBackpack);
        writer.Byte((byte)failure);
        return writer.ToArray();
    }

    private void Write(SyncWriter writer)
    {
        writer.Byte((byte)Kind);
        writer.UInt((uint)Day);
        writer.Text(Destination);
    }

    private static TeamTransition Read(SyncReader reader) =>
        new TeamTransition((TeamTransitionKind)reader.Byte(), unchecked((int)reader.UInt()), reader.Text(128));

    internal static bool TryReadVote(byte[] data, out uint epoch, out TeamTransition action, out int backpack, out ulong revision)
    {
        epoch = 0;
        action = default;
        backpack = 0;
        revision = 0;
        if (data == null || data.Length > 160) return false;
        var reader = new SyncReader(data);
        byte message = reader.Byte();
        epoch = reader.UInt();
        action = Read(reader);
        backpack = unchecked((int)reader.UInt());
        revision = reader.ULong();
        if (!reader.Ok || !reader.AtEnd) return false;
        if (message == WorldWire.TeamCancel) return action.Valid && revision != 0 && backpack == 0;
        return message == WorldWire.TeamVote && (action.Valid && backpack >= 0 &&
            (backpack == 0 || action.Kind == TeamTransitionKind.Depart) || action.Empty && revision == 0 && backpack == 0);
    }

    internal static bool TryReadReadiness(byte[] data, out uint epoch, out TeamTransition action, out byte mask,
        out ulong revision, out int hostBackpack, out int guestBackpack, out TeamVoteFailure failure)
    {
        epoch = 0;
        action = default;
        mask = 0;
        revision = 0;
        hostBackpack = guestBackpack = 0;
        failure = TeamVoteFailure.None;
        if (data == null || data.Length > 168) return false;
        var reader = new SyncReader(data);
        if (reader.Byte() != WorldWire.TeamReadiness) return false;
        epoch = reader.UInt();
        action = Read(reader);
        mask = reader.Byte();
        revision = reader.ULong();
        hostBackpack = unchecked((int)reader.UInt());
        guestBackpack = unchecked((int)reader.UInt());
        failure = (TeamVoteFailure)reader.Byte();
        if (!reader.Ok || !reader.AtEnd || failure > TeamVoteFailure.Stale ||
            hostBackpack < 0 || guestBackpack < 0 || hostBackpack != 0 && hostBackpack == guestBackpack) return false;
        if (mask == 0) return action.Empty && revision == 0 && hostBackpack == 0 && guestBackpack == 0;
        return mask <= 3 && action.Valid && revision != 0 &&
            (hostBackpack == 0 || action.Kind == TeamTransitionKind.Depart && (mask & 1) != 0) &&
            (guestBackpack == 0 || action.Kind == TeamTransitionKind.Depart && (mask & 2) != 0);
    }

    public bool Equals(TeamTransition other) => Kind == other.Kind && Day == other.Day && Destination == other.Destination;
    public override bool Equals(object other) => other is TeamTransition action && Equals(action);
    public override int GetHashCode() => HashCode.Combine(Kind, Day, Destination);
}
