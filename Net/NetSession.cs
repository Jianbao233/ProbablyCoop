using System;
using System.Collections.Generic;
using System.Text;
using ProbablyCoop.Sync;

namespace ProbablyCoop.Net;

internal enum SessionState { Idle, Negotiating, Ready }

internal enum SessionDecline : byte { None = 0, Protocol = 1, Game = 2, Mods = 3 }

internal enum ModDiffKind : byte { HostOnly = 1, GuestOnly = 2, Version = 3, Adapter = 4 }

internal readonly struct SessionMod
{
    internal readonly string Id;
    internal readonly string Version;
    internal readonly string AdapterProtocol;
    internal readonly bool PresentationOnly;

    internal SessionMod(string id, string version, string adapterProtocol = "", bool presentationOnly = false)
    {
        Id = id ?? "";
        Version = version ?? "";
        AdapterProtocol = adapterProtocol ?? "";
        PresentationOnly = presentationOnly;
    }

    internal string Compatibility => PresentationOnly ? "presentation" :
        string.IsNullOrEmpty(AdapterProtocol) ? "default" : AdapterProtocol;

    internal static bool IsAdapterProtocol(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 48) return false;
        foreach (char c in value)
            if (!(c >= '0' && c <= '9' || c >= 'A' && c <= 'Z' ||
                c >= 'a' && c <= 'z' || c == '.' || c == '-' || c == '_')) return false;
        return true;
    }
}

internal readonly struct ModDiff
{
    internal readonly ModDiffKind Kind;
    internal readonly string Id;
    internal readonly string Host;
    internal readonly string Guest;

    internal ModDiff(ModDiffKind kind, string id, string host, string guest)
    {
        Kind = kind;
        Id = id;
        Host = host;
        Guest = guest;
    }
}

/// <summary>
/// What one side runs: the game build and every enabled mod, this one included. A different
/// version of this mod is therefore an ordinary mod difference.
/// </summary>
internal sealed class SessionManifest
{
    internal const int MaxMods = 512;
    // UTF-8 bytes per string.
    internal const int MaxText = 128;

    internal string Game = "";
    internal readonly List<SessionMod> Mods = new List<SessionMod>();

    internal void Write(SyncWriter writer)
    {
        writer.Text(Game);
        writer.ULong((ulong)Mods.Count);
        foreach (SessionMod mod in Mods)
        {
            writer.Text(mod.Id);
            writer.Text(mod.Version);
            writer.Byte(mod.PresentationOnly ? (byte)1 : (byte)0);
            writer.Text(mod.AdapterProtocol);
        }
    }

    // Null when the bytes are not a manifest: over a limit, an empty id, or an id listed twice.
    internal static SessionManifest Read(SyncReader reader)
    {
        var manifest = new SessionManifest { Game = reader.Text(MaxText) };
        ulong count = reader.ULong();
        if (!reader.Ok || count > MaxMods) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            string id = reader.Text(MaxText);
            string version = reader.Text(MaxText);
            byte presentation = reader.Byte();
            string adapter = reader.Text(48);
            if (!reader.Ok || id.Length == 0 || !seen.Add(id) || presentation > 1 ||
                adapter.Length != 0 && (presentation != 0 || !SessionMod.IsAdapterProtocol(adapter))) return null;
            manifest.Mods.Add(new SessionMod(id, version, adapter, presentation == 1));
        }
        return manifest;
    }

    internal bool Valid()
    {
        var writer = new SyncWriter();
        Write(writer);
        var reader = new SyncReader(writer.ToArray());
        return Read(reader) != null && reader.AtEnd;
    }

    // Builds a manifest that is always valid from whatever the mod loader reports:
    // text is cut to the limit, entries without an id and repeated ids are dropped, and
    // the list is ordered by id and capped.
    internal static SessionManifest Of(string game, IEnumerable<SessionMod> mods)
    {
        var manifest = new SessionManifest { Game = Clip(game) };
        if (mods == null) return manifest;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SessionMod mod in mods)
        {
            string id = Clip(mod.Id);
            if (id.Length > 0 && seen.Add(id))
                manifest.Mods.Add(new SessionMod(id, Clip(mod.Version), mod.AdapterProtocol, mod.PresentationOnly));
        }
        manifest.Mods.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        if (manifest.Mods.Count > MaxMods) manifest.Mods.RemoveRange(MaxMods, manifest.Mods.Count - MaxMods);
        return manifest;
    }

    // The text as the other side will read it: unpaired surrogates become U+FFFD, and
    // only the longest prefix that fits MaxText as UTF-8 without splitting a character
    // is kept.
    private static string Clip(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));
        if (Encoding.UTF8.GetByteCount(text) <= MaxText) return text;
        int bytes = 0;
        int end = 0;
        while (end < text.Length)
        {
            int step = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(text.Substring(end, step));
            if (bytes + size > MaxText) break;
            bytes += size;
            end += step;
        }
        return text.Substring(0, end);
    }

    // Ordered by id so both sides list the same differences in the same order.
    internal static List<ModDiff> Compare(SessionManifest host, SessionManifest guest)
    {
        var theirs = new Dictionary<string, SessionMod>(StringComparer.Ordinal);
        foreach (SessionMod mod in guest.Mods) theirs[mod.Id] = mod;
        var result = new List<ModDiff>();
        foreach (SessionMod mod in host.Mods)
        {
            if (!theirs.TryGetValue(mod.Id, out SessionMod other))
            {
                if (!mod.PresentationOnly) result.Add(new ModDiff(ModDiffKind.HostOnly, mod.Id, mod.Version, ""));
            }
            else
            {
                theirs.Remove(mod.Id);
                if (mod.PresentationOnly && other.PresentationOnly) continue;
                if (mod.PresentationOnly != other.PresentationOnly || mod.AdapterProtocol != other.AdapterProtocol)
                    result.Add(new ModDiff(ModDiffKind.Adapter, mod.Id, mod.Compatibility, other.Compatibility));
                else if (!string.Equals(other.Version, mod.Version, StringComparison.Ordinal))
                    result.Add(new ModDiff(ModDiffKind.Version, mod.Id, mod.Version, other.Version));
            }
        }
        foreach (SessionMod left in theirs.Values)
            if (!left.PresentationOnly) result.Add(new ModDiff(ModDiffKind.GuestOnly, left.Id, "", left.Version));
        result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return result;
    }
}

/// <summary>
/// Decides whether two open link ends may play together, then carries the game channels.
/// The guest reports what it runs; the host alone accepts or declines. Channel 0 of the
/// reliable stream is reserved for this exchange.
/// </summary>
internal sealed class NetSession
{
    // Version of everything carried above the link: sync messages and their meaning.
    internal const uint Content = 4;
    internal const uint Oldest = 4;
    internal const byte Control = 0;
    internal const double NegotiateTimeout = 15;
    // How long the host waits for a declined guest to hang up before dropping it.
    internal const double DeclineLinger = 5;
    internal const int MaxDiffs = 32;
    internal const byte GuestPeer = 1;

    private const byte Offer = 1, Accept = 2, Decline = 3;

    private readonly NetLink link;
    private readonly SessionManifest local;
    private double deadline;
    private bool armed;
    private bool dismissed;

    internal NetSession(NetLink link, SessionManifest local)
    {
        if (link == null) throw new ArgumentNullException(nameof(link));
        if (local == null || !local.Valid()) throw new ArgumentException("Invalid session manifest.", nameof(local));
        this.link = link;
        this.local = local;
        link.Opened = OnOpened;
        link.Ended = OnEnded;
        link.Message = OnMessage;
        link.Datagram = OnDatagram;
    }

    internal SessionState State { get; private set; }
    internal bool IsHost => link.IsHost;
    // This side's id in sync messages: 0 on the host, assigned by the host on a guest.
    internal byte Peer { get; private set; }
    internal uint Protocol { get; private set; }
    internal SessionManifest Remote { get; private set; }
    // Why the last negotiation was declined; kept after the link ends, cleared when it reopens.
    internal SessionDecline Reason { get; private set; }
    // Mod differences of the last negotiation, whether it was declined or admitted.
    internal List<ModDiff> Differences { get; private set; } = new List<ModDiff>();
    // May exceed Differences.Count on the guest: the list sent with a decline is capped.
    internal int DifferenceCount { get; private set; }

    // Host only: may this guest join despite the listed differences? Null admits identical lists only.
    internal Func<SessionManifest, List<ModDiff>, bool> Admit;
    internal Action Ready;
    internal Action<SessionDecline> Declined;
    internal Action<NetClose> Ended;
    internal Action<byte, byte[]> Message;
    internal Action<byte, byte[]> Datagram;

    internal bool Send(byte channel, byte[] message) =>
        channel != Control && State == SessionState.Ready && link.Send(channel, message);

    internal bool SendLoose(byte channel, byte[] payload) =>
        State == SessionState.Ready && link.SendLoose(channel, payload);

    internal void Tick(double now)
    {
        if (State != SessionState.Negotiating) return;
        if (!armed)
        {
            armed = true;
            deadline = now + (dismissed ? DeclineLinger : NegotiateTimeout);
        }
        else if (now >= deadline) link.Abort(NetClose.Timeout);
    }

    private void OnOpened()
    {
        State = SessionState.Negotiating;
        armed = dismissed = false;
        Peer = 0;
        Protocol = 0;
        Remote = null;
        Reason = SessionDecline.None;
        Differences = new List<ModDiff>();
        DifferenceCount = 0;
        if (link.IsHost) return;
        var writer = new SyncWriter();
        writer.Byte(Offer);
        writer.UInt(Oldest);
        writer.UInt(Content);
        local.Write(writer);
        link.Send(Control, writer.ToArray());
    }

    private void OnEnded(NetClose reason)
    {
        State = SessionState.Idle;
        dismissed = false;
        Ended?.Invoke(reason);
    }

    private void OnDatagram(byte channel, byte[] data)
    {
        if (State == SessionState.Ready) Datagram?.Invoke(channel, data);
    }

    private void OnMessage(byte channel, byte[] data)
    {
        if (State == SessionState.Idle || dismissed) return;
        if (channel != Control)
        {
            if (State == SessionState.Ready) Message?.Invoke(channel, data);
            else link.Abort(NetClose.Corrupt);
            return;
        }
        var reader = new SyncReader(data);
        byte type = reader.Byte();
        if (State == SessionState.Negotiating)
        {
            if (link.IsHost && type == Offer) { OnOffer(reader); return; }
            if (!link.IsHost && type == Accept) { OnAccept(reader); return; }
            if (!link.IsHost && type == Decline) { OnDecline(reader); return; }
        }
        link.Abort(NetClose.Corrupt);
    }

    private void OnOffer(SyncReader reader)
    {
        uint oldest = reader.UInt();
        uint newest = reader.UInt();
        if (!reader.Ok)
        {
            link.Abort(NetClose.Corrupt);
            return;
        }
        uint protocol = Agree(oldest, newest);
        if (protocol == 0)
        {
            Dismiss(SessionDecline.Protocol, null);
            return;
        }
        SessionManifest remote = SessionManifest.Read(reader);
        if (remote == null || !reader.Ok || !reader.AtEnd)
        {
            link.Abort(NetClose.Corrupt);
            return;
        }
        Remote = remote;
        List<ModDiff> diffs = SessionManifest.Compare(local, remote);
        if (!string.Equals(local.Game, remote.Game, StringComparison.Ordinal)) Dismiss(SessionDecline.Game, null);
        else if (!(Admit != null ? Admit(remote, diffs) : diffs.Count == 0)) Dismiss(SessionDecline.Mods, diffs);
        else
        {
            Protocol = protocol;
            Differences = diffs;
            DifferenceCount = diffs.Count;
            var writer = new SyncWriter();
            writer.Byte(Accept);
            writer.UInt(protocol);
            writer.Byte(GuestPeer);
            local.Write(writer);
            link.Send(Control, writer.ToArray());
            State = SessionState.Ready;
            Ready?.Invoke();
        }
    }

    // The decline travels on the reliable stream, so the link has to stay up until it
    // arrives. The guest hangs up on receipt; Tick drops a guest that does not.
    private void Dismiss(SessionDecline reason, List<ModDiff> diffs)
    {
        Reason = reason;
        Differences = diffs ?? new List<ModDiff>();
        DifferenceCount = Differences.Count;
        int sent = Math.Min(Differences.Count, MaxDiffs);
        var writer = new SyncWriter();
        writer.Byte(Decline);
        writer.Byte((byte)reason);
        writer.ULong((ulong)Differences.Count);
        writer.ULong((ulong)sent);
        for (int i = 0; i < sent; i++)
        {
            ModDiff diff = Differences[i];
            writer.Byte((byte)diff.Kind);
            writer.Text(diff.Id);
            writer.Text(diff.Host);
            writer.Text(diff.Guest);
        }
        link.Send(Control, writer.ToArray());
        dismissed = true;
        armed = false;
        Declined?.Invoke(reason);
    }

    private void OnAccept(SyncReader reader)
    {
        uint protocol = reader.UInt();
        byte peer = reader.Byte();
        SessionManifest remote = SessionManifest.Read(reader);
        if (remote == null || !reader.Ok || !reader.AtEnd || protocol < Oldest || protocol > Content ||
            peer != GuestPeer || !string.Equals(remote.Game, local.Game, StringComparison.Ordinal))
        {
            link.Abort(NetClose.Corrupt);
            return;
        }
        Protocol = protocol;
        Peer = peer;
        Remote = remote;
        Differences = SessionManifest.Compare(remote, local);
        DifferenceCount = Differences.Count;
        State = SessionState.Ready;
        Ready?.Invoke();
    }

    private void OnDecline(SyncReader reader)
    {
        byte reason = reader.Byte();
        ulong total = reader.ULong();
        ulong count = reader.ULong();
        var diffs = new List<ModDiff>();
        bool valid = reader.Ok && reason >= (byte)SessionDecline.Protocol && reason <= (byte)SessionDecline.Mods &&
                     count <= MaxDiffs && total >= count;
        for (ulong i = 0; valid && i < count; i++)
        {
            byte kind = reader.Byte();
            string id = reader.Text(SessionManifest.MaxText);
            string host = reader.Text(SessionManifest.MaxText);
            string guest = reader.Text(SessionManifest.MaxText);
            valid = reader.Ok && kind >= (byte)ModDiffKind.HostOnly && kind <= (byte)ModDiffKind.Adapter;
            diffs.Add(new ModDiff((ModDiffKind)kind, id, host, guest));
        }
        if (!valid || !reader.AtEnd)
        {
            link.Abort(NetClose.Corrupt);
            return;
        }
        Reason = (SessionDecline)reason;
        Differences = diffs;
        DifferenceCount = (int)Math.Min(total, int.MaxValue);
        Declined?.Invoke(Reason);
        // The handler may already have left the room.
        if (State == SessionState.Negotiating) link.Close();
    }

    private static uint Agree(uint oldest, uint newest)
    {
        if (oldest == 0 || newest < oldest) return 0;
        uint chosen = newest < Content ? newest : Content;
        return chosen >= oldest && chosen >= Oldest ? chosen : 0;
    }
}
