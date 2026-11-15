using System;
using System.Collections.Generic;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;

namespace ProbablyCoop.SyncTests;

// Session layer tests: two NetSession ends (or one end against a hand-driven raw link)
// over the simulated network of NetTests.
internal static partial class NetTests
{
    private const string Game = "VERSION 100-Rev13";

    private static SessionManifest Manifest(string game, params string[] mods)
    {
        var manifest = new SessionManifest { Game = game };
        foreach (string mod in mods)
        {
            int cut = mod.IndexOf('=');
            manifest.Mods.Add(cut < 0 ? new SessionMod(mod, "") : new SessionMod(mod.Substring(0, cut), mod.Substring(cut + 1)));
        }
        return manifest;
    }

    private static SessionManifest Plain() => Manifest(Game, "probably_coop=0.2.0", "item_details=1.4", "better_shelves=2.0");

    private static bool SameManifest(SessionManifest a, SessionManifest b)
    {
        if (a == null || b == null || a.Game != b.Game || a.Mods.Count != b.Mods.Count) return false;
        for (int i = 0; i < a.Mods.Count; i++)
            if (a.Mods[i].Id != b.Mods[i].Id || a.Mods[i].Version != b.Mods[i].Version ||
                a.Mods[i].AdapterProtocol != b.Mods[i].AdapterProtocol ||
                a.Mods[i].PresentationOnly != b.Mods[i].PresentationOnly) return false;
        return true;
    }

    private static bool SameDiffs(List<ModDiff> a, List<ModDiff> b, int count)
    {
        if (a.Count < count || b.Count < count) return false;
        for (int i = 0; i < count; i++)
            if (a[i].Kind != b[i].Kind || a[i].Id != b[i].Id || a[i].Host != b[i].Host || a[i].Guest != b[i].Guest) return false;
        return true;
    }

    private static byte[] OfferBytes(uint oldest, uint newest, SessionManifest manifest)
    {
        var writer = new SyncWriter();
        writer.Byte(1);
        writer.UInt(oldest);
        writer.UInt(newest);
        manifest.Write(writer);
        return writer.ToArray();
    }

    private static byte[] AcceptBytes(uint protocol, byte peer, SessionManifest manifest)
    {
        var writer = new SyncWriter();
        writer.Byte(2);
        writer.UInt(protocol);
        writer.Byte(peer);
        manifest.Write(writer);
        return writer.ToArray();
    }

    private static byte[] DeclineBytes(byte reason, ulong total, ulong count, params (byte Kind, string Id, string Host, string Guest)[] diffs)
    {
        var writer = new SyncWriter();
        writer.Byte(3);
        writer.Byte(reason);
        writer.ULong(total);
        writer.ULong(count);
        foreach (var diff in diffs)
        {
            writer.Byte(diff.Kind);
            writer.Text(diff.Id);
            writer.Text(diff.Host);
            writer.Text(diff.Guest);
        }
        return writer.ToArray();
    }

    private static byte[] Plus(byte[] bytes, int delta)
    {
        var result = new byte[bytes.Length + delta];
        Array.Copy(bytes, result, Math.Min(bytes.Length, result.Length));
        return result;
    }

    // A null manifest leaves that end as a raw link driven by the test; what it receives
    // lands in the Sim lists.
    private sealed class Pair
    {
        internal readonly Sim Sim;
        internal NetSession Host, Guest;
        internal int HostReady, GuestReady;
        internal readonly List<SessionDecline> HostDeclined = new List<SessionDecline>();
        internal readonly List<SessionDecline> GuestDeclined = new List<SessionDecline>();
        internal readonly List<NetClose> HostEnded = new List<NetClose>();
        internal readonly List<NetClose> GuestEnded = new List<NetClose>();
        internal readonly List<(byte Channel, byte[] Data)> HostGot = new List<(byte, byte[])>();
        internal readonly List<(byte Channel, byte[] Data)> GuestGot = new List<(byte, byte[])>();
        internal readonly List<(byte Channel, byte[] Data)> HostLoose = new List<(byte, byte[])>();
        internal readonly List<(byte Channel, byte[] Data)> GuestLoose = new List<(byte, byte[])>();
        internal readonly List<string> GuestOrder = new List<string>();

        internal Pair(int seed, SessionManifest host, SessionManifest guest)
        {
            Sim = new Sim(seed);
            if (host != null)
            {
                Host = new NetSession(Sim.Host, host);
                Host.Ready = () => HostReady++;
                Host.Declined = reason => HostDeclined.Add(reason);
                Host.Ended = reason => HostEnded.Add(reason);
                Host.Message = (channel, data) => HostGot.Add((channel, data));
                Host.Datagram = (channel, data) => HostLoose.Add((channel, data));
            }
            if (guest != null) SetGuest(guest);
        }

        internal void SetGuest(SessionManifest manifest)
        {
            Guest = new NetSession(Sim.Guest, manifest);
            Guest.Ready = () => { GuestReady++; GuestOrder.Add("ready"); };
            Guest.Declined = reason => GuestDeclined.Add(reason);
            Guest.Ended = reason => GuestEnded.Add(reason);
            Guest.Message = (channel, data) => { GuestGot.Add((channel, data)); GuestOrder.Add("message"); };
            Guest.Datagram = (channel, data) => GuestLoose.Add((channel, data));
        }

        internal Pair Open()
        {
            Sim.Host.Listen();
            Sim.Guest.Connect(Sim.Now);
            return this;
        }

        internal bool LinksOpen() => Sim.Host.State == NetState.Open && Sim.Guest.State == NetState.Open;

        internal bool BothReady() =>
            Host != null && Guest != null && Host.State == SessionState.Ready && Guest.State == SessionState.Ready;

        internal bool Run(Func<bool> done, double limit = 10)
        {
            double end = Sim.Now + limit;
            while (Sim.Now < end)
            {
                if (done()) return true;
                Sim.Step();
                Host?.Tick(Sim.Now);
                Guest?.Tick(Sim.Now);
            }
            return done();
        }
    }

    private static void Sessions()
    {
        SessionManifests();
        SessionBuild();
        SessionCompare();
        SessionAdapters();
        SessionHappy();
        SessionDeclines();
        SessionProtocol();
        SessionLinger();
        SessionBadGuest();
        SessionBadHost();
        SessionTimeouts();
        SessionLossy();
        SessionReentry();
        SessionFuzz();
    }

    private static SessionMod Mod(string id, string version) => new SessionMod(id, version);

    private static void SessionBuild()
    {
        SessionManifest built = SessionManifest.Of(Game, new[]
        {
            Mod("zeta", "2"), Mod("alpha", "1"), Mod("", "9"), Mod(null, "9"), Mod("alpha", "7"), Mod("Alpha", null)
        });
        Check(built.Valid() && built.Game == Game && built.Mods.Count == 3, "build-drops-empty-and-repeated");
        Check(built.Mods[0].Id == "Alpha" && built.Mods[0].Version == "" && built.Mods[1].Id == "alpha" &&
              built.Mods[1].Version == "1" && built.Mods[2].Id == "zeta", "build-ordered-first-wins");
        // The order the loader reports mods in does not matter.
        SessionManifest other = SessionManifest.Of(Game, new[] { Mod("alpha", "1"), Mod("Alpha", ""), Mod("zeta", "2") });
        Check(SessionManifest.Compare(built, other).Count == 0, "build-order-independent");

        SessionManifest empty = SessionManifest.Of(null, null);
        Check(empty.Valid() && empty.Game == "" && empty.Mods.Count == 0, "build-nothing");

        // Text is cut to 128 UTF-8 bytes on a character boundary.
        string ascii = new string('a', 200);
        string han = new string('\u5E97', 60);                // 3 bytes each
        string astral = string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 40));   // 4 bytes, 2 chars each
        string mixed = "x" + han;                              // 1 + 42 * 3 = 127, the next would be 130
        SessionManifest clipped = SessionManifest.Of(ascii, new[] { Mod(han, astral), Mod(mixed, "ok") });
        Check(clipped.Valid() && clipped.Game.Length == 128, "build-clips-ascii");
        SessionMod first = clipped.Mods[0], second = clipped.Mods[1];
        Check(first.Id == "x" + new string('\u5E97', 42) && second.Id == new string('\u5E97', 42), "build-clips-three-byte");
        Check(second.Version.Length == 64 && System.Text.Encoding.UTF8.GetByteCount(second.Version) == 128 &&
              char.IsLowSurrogate(second.Version[63]), "build-clips-surrogate-pairs-whole");
        Check(SessionManifest.Of(new string('a', 128), null).Game.Length == 128 &&
              SessionManifest.Of(new string('a', 129), null).Game.Length == 128, "build-clip-boundary");
        // Ids that only differ past the limit count once.
        SessionManifest merged = SessionManifest.Of(Game, new[] { Mod(ascii + "1", "1"), Mod(ascii + "2", "2") });
        Check(merged.Valid() && merged.Mods.Count == 1 && merged.Mods[0].Version == "1", "build-clipped-ids-merge");

        // An unpaired surrogate cannot be sent; both sides must hold the same replacement.
        SessionManifest broken = SessionManifest.Of("g\uD83D", new[] { Mod("a\uD800b", "1\uDC00"), Mod("a\uDFFFb", "2") });
        Check(broken.Valid() && broken.Game == "g\uFFFD" && broken.Mods.Count == 1 && broken.Mods[0].Id == "a\uFFFDb" &&
              broken.Mods[0].Version == "1\uFFFD", "build-unpaired-surrogates-replaced");

        var many = new List<SessionMod>();
        for (int i = 999; i >= 0; i--) many.Add(Mod("mod" + i.ToString("D4"), "1"));
        SessionManifest capped = SessionManifest.Of(Game, many);
        Check(capped.Valid() && capped.Mods.Count == SessionManifest.MaxMods && capped.Mods[0].Id == "mod0000" &&
              capped.Mods[SessionManifest.MaxMods - 1].Id == "mod0511", "build-capped-after-ordering");

        // Whatever the loader hands over, the result can be sent.
        var random = new Random(4117);
        bool allValid = true;
        bool allSame = true;
        string firstBad = null;
        for (int round = 0; round < 300; round++)
        {
            var list = new List<SessionMod>();
            int count = random.Next(0, 40);
            for (int i = 0; i < count; i++) list.Add(Mod(Junk(random), Junk(random)));
            SessionManifest junk = SessionManifest.Of(Junk(random), list);
            var writer = new SyncWriter();
            junk.Write(writer);
            SessionManifest back = SessionManifest.Read(new SyncReader(writer.ToArray()));
            bool valid = junk.Valid() && back != null;
            bool same = valid && back.Mods.Count == junk.Mods.Count &&
                        SessionManifest.Compare(junk, back).Count == 0 && back.Game == junk.Game;
            if (!same && firstBad == null)
                firstBad = $"round={round} valid={valid} mods={junk.Mods.Count}/{(back == null ? -1 : back.Mods.Count)} " +
                           $"game-same={(back != null && back.Game == junk.Game)}";
            allValid &= valid;
            allSame &= same;
        }
        Check(allValid, "build-always-valid");
        Check(allSame, "build-round-trips " + firstBad);
    }

    private static string Junk(Random random)
    {
        if (random.Next(12) == 0) return null;
        var chars = new char[random.Next(0, 90)];
        for (int i = 0; i < chars.Length; i++)
        {
            int pick = random.Next(6);
            chars[i] = pick == 0 ? (char)random.Next(0xD800, 0xE000) : pick == 1 ? (char)random.Next(0x4E00, 0x9FFF) :
                pick == 2 ? (char)random.Next(0, 32) : (char)random.Next(32, 127);
        }
        return new string(chars);
    }

    private static void SessionManifests()
    {
        SessionManifest manifest = Manifest(Game, "probably_coop=0.2.0", "物品详情=1.4β", "no_version");
        var writer = new SyncWriter();
        manifest.Write(writer);
        byte[] bytes = writer.ToArray();
        var reader = new SyncReader(bytes);
        SessionManifest back = SessionManifest.Read(reader);
        Check(back != null && reader.AtEnd && SameManifest(manifest, back), "manifest-round-trip");
        for (int length = 0; length < bytes.Length; length++)
        {
            var cut = new SyncReader(bytes, length);
            SessionManifest partial = SessionManifest.Read(cut);
            Check(partial == null || !cut.Ok, "manifest-truncated-" + length);
        }
        Check(manifest.Valid() && new SessionManifest().Valid(), "manifest-valid");
        Check(!Manifest(Game, "a=1", "a=2").Valid(), "manifest-duplicate-id");
        Check(!Manifest(Game, "=1").Valid(), "manifest-empty-id");
        Check(Manifest(Game, "a=1", "A=1").Valid(), "manifest-ids-case-sensitive");
        Check(Manifest(new string('g', 128), new string('i', 128) + "=" + new string('v', 128)).Valid(), "manifest-text-at-limit");
        Check(!Manifest(new string('g', 129)).Valid(), "manifest-game-too-long");
        Check(!Manifest(Game, new string('i', 129) + "=1").Valid(), "manifest-id-too-long");
        Check(!Manifest(Game, "a=" + new string('v', 129)).Valid(), "manifest-version-too-long");
        Check(Manifest(Game, new string('é', 64) + "=1").Valid() && !Manifest(Game, new string('é', 65) + "=1").Valid(),
            "manifest-limit-counts-bytes");
        var many = new SessionManifest { Game = Game };
        for (int i = 0; i < SessionManifest.MaxMods; i++) many.Mods.Add(new SessionMod("m" + i, "1"));
        Check(many.Valid(), "manifest-mods-at-limit");
        many.Mods.Add(new SessionMod("extra", "1"));
        Check(!many.Valid(), "manifest-too-many-mods");
    }

    private static void SessionCompare()
    {
        SessionManifest host = Manifest(Game, "c=1", "a=1", "b=1");
        Check(SessionManifest.Compare(host, Manifest(Game, "b=1", "c=1", "a=1")).Count == 0, "compare-order-ignored");
        List<ModDiff> diffs = SessionManifest.Compare(host, Manifest(Game, "d=7", "b=2", "a=1"));
        Check(diffs.Count == 3 &&
              diffs[0].Kind == ModDiffKind.Version && diffs[0].Id == "b" && diffs[0].Host == "1" && diffs[0].Guest == "2" &&
              diffs[1].Kind == ModDiffKind.HostOnly && diffs[1].Id == "c" && diffs[1].Host == "1" && diffs[1].Guest == "" &&
              diffs[2].Kind == ModDiffKind.GuestOnly && diffs[2].Id == "d" && diffs[2].Host == "" && diffs[2].Guest == "7",
            "compare-three-kinds-sorted");
        Check(SessionManifest.Compare(Manifest("x"), Manifest("y")).Count == 0, "compare-ignores-game");
    }

    private static void SessionAdapters()
    {
        var host = SessionManifest.Of(Game, new[]
        {
            new SessionMod("camera", "2", presentationOnly: true),
            new SessionMod("machine", "5", "rules-1"),
        });
        var compatible = SessionManifest.Of(Game, new[]
        {
            new SessionMod("overlay", "1", presentationOnly: true),
            new SessionMod("machine", "5", "rules-1"),
        });
        var pair = new Pair(305, host, compatible).Open();
        Check(pair.Run(pair.BothReady, 5) && SameManifest(pair.Host.Remote, compatible),
            "one-sided presentation mods are compatible");

        var wrong = SessionManifest.Of(Game, new[] { new SessionMod("machine", "5", "rules-2") });
        pair = new Pair(306, host, wrong).Open();
        Check(pair.Run(() => pair.GuestDeclined.Count == 1, 5) && pair.Guest.Reason == SessionDecline.Mods &&
            pair.Guest.Differences.Count == 1 && pair.Guest.Differences[0].Kind == ModDiffKind.Adapter &&
            pair.Guest.Differences[0].Host == "rules-1" && pair.Guest.Differences[0].Guest == "rules-2",
            "gameplay adapter version mismatch reaches guest");

        var pretend = SessionManifest.Of(Game, new[] { new SessionMod("machine", "5", presentationOnly: true) });
        Check(SessionManifest.Compare(host, pretend).Count == 1, "a peer cannot mark a required mod cosmetic");
        Check(SessionManifest.Compare(host, SessionManifest.Of(Game, null)).Count == 1, "missing gameplay mod is reported");
        Check(!SessionManifest.Of(Game, new[] { new SessionMod("machine", "5", "rules 2") }).Valid(),
            "invalid adapter protocol text rejected");
    }

    private static void SessionHappy()
    {
        SessionManifest hostManifest = Plain();
        SessionManifest guestManifest = Manifest(Game, "better_shelves=2.0", "probably_coop=0.2.0", "item_details=1.4");
        var pair = new Pair(21, hostManifest, guestManifest);
        Check(pair.Host.State == SessionState.Idle && !pair.Host.Send(1, new byte[] { 1 }) && !pair.Host.SendLoose(1, new byte[] { 1 }),
            "session-idle-refuses-send");
        pair.Open();
        Check(pair.Run(pair.BothReady, 5), "session-ready");
        Check(pair.HostReady == 1 && pair.GuestReady == 1 && pair.HostDeclined.Count == 0 && pair.GuestDeclined.Count == 0,
            "session-ready-once");
        Check(pair.Host.IsHost && !pair.Guest.IsHost && pair.Host.Peer == 0 && pair.Guest.Peer == NetSession.GuestPeer &&
              pair.Host.Protocol == NetSession.Content && pair.Guest.Protocol == NetSession.Content, "session-ids");
        Check(SameManifest(pair.Host.Remote, guestManifest) && SameManifest(pair.Guest.Remote, hostManifest), "session-remote-manifests");
        Check(pair.Host.Differences.Count == 0 && pair.Guest.Differences.Count == 0 && pair.Host.Reason == SessionDecline.None,
            "session-no-differences");
        Check(pair.Sim.Now < 1.0, "session-ready-prompt");

        Check(!pair.Host.Send(NetSession.Control, new byte[] { 1 }) && !pair.Guest.Send(NetSession.Control, new byte[] { 1 }),
            "session-control-channel-reserved");
        for (int i = 0; i < 30; i++)
        {
            pair.Host.Send((byte)(1 + i % 3), new byte[] { (byte)i });
            pair.Guest.Send((byte)(1 + i % 3), new byte[] { (byte)(100 + i) });
        }
        pair.Host.SendLoose(7, new byte[] { 5 });
        pair.Guest.SendLoose(7, new byte[] { 6 });
        pair.Run(() => pair.HostGot.Count == 30 && pair.GuestGot.Count == 30 && pair.HostLoose.Count == 1 && pair.GuestLoose.Count == 1, 5);
        bool ordered = pair.HostGot.Count == 30 && pair.GuestGot.Count == 30;
        for (int i = 0; ordered && i < 30; i++)
            ordered = pair.GuestGot[i].Channel == 1 + i % 3 && pair.GuestGot[i].Data[0] == i &&
                      pair.HostGot[i].Channel == 1 + i % 3 && pair.HostGot[i].Data[0] == 100 + i;
        Check(ordered, "session-messages-ordered");
        Check(pair.HostLoose.Count == 1 && pair.HostLoose[0].Channel == 7 && pair.HostLoose[0].Data[0] == 6 &&
              pair.GuestLoose.Count == 1 && pair.GuestLoose[0].Data[0] == 5, "session-datagrams");

        // Keep-alives, not the negotiation timer, govern an established session.
        pair.Run(() => false, 30);
        Check(pair.BothReady() && pair.LinksOpen() && pair.HostEnded.Count == 0, "session-stays-ready");

        pair.Sim.Guest.Close();
        pair.Run(() => pair.HostEnded.Count > 0, 5);
        Check(pair.HostEnded.Count == 1 && pair.HostEnded[0] == NetClose.Remote && pair.GuestEnded.Count == 1 &&
              pair.GuestEnded[0] == NetClose.Local && pair.Host.State == SessionState.Idle && pair.Guest.State == SessionState.Idle &&
              !pair.Host.Send(1, new byte[] { 1 }), "session-ends-with-link");

        // The largest manifest the format allows crosses in both directions.
        var big = new SessionManifest { Game = new string('g', 128) };
        for (int i = 0; i < SessionManifest.MaxMods; i++)
            big.Mods.Add(new SessionMod(i.ToString("D4") + new string('i', 124), new string('v', 128)));
        var large = new Pair(22, big, big).Open();
        Check(large.Run(large.BothReady, 10) && large.Guest.Remote.Mods.Count == SessionManifest.MaxMods &&
              SameManifest(large.Host.Remote, big), "session-largest-manifest");
    }

    private static void SessionDeclines()
    {
        // Different game build: never admitted, whatever the host policy says.
        var pair = new Pair(31, Plain(), Manifest("VERSION 100-Rev12", "probably_coop=0.2.0", "item_details=1.4", "better_shelves=2.0"));
        bool asked = false;
        pair.Host.Admit = (remote, diffs) => { asked = true; return true; };
        pair.Open();
        Check(pair.Run(() => pair.HostEnded.Count > 0 && pair.GuestEnded.Count > 0, 10), "decline-game-ends-both");
        Check(pair.HostDeclined.Count == 1 && pair.HostDeclined[0] == SessionDecline.Game && pair.GuestDeclined.Count == 1 &&
              pair.GuestDeclined[0] == SessionDecline.Game && pair.HostReady == 0 && pair.GuestReady == 0 && !asked, "decline-game");
        Check(pair.GuestEnded.Count == 1 && pair.GuestEnded[0] == NetClose.Local && pair.HostEnded.Count == 1 &&
              pair.HostEnded[0] == NetClose.Remote && pair.Sim.Now < 2.0,
            "decline-guest-hangs-up-promptly");
        Check(pair.Guest.State == SessionState.Idle && pair.Guest.Reason == SessionDecline.Game &&
              pair.Host.Reason == SessionDecline.Game && pair.Guest.DifferenceCount == 0, "decline-reason-kept-after-end");

        // The room stays usable: the same host admits the next, matching guest.
        pair.Sim.Host.Listen();
        pair.SetGuest(Plain());
        pair.Sim.Guest.Connect(pair.Sim.Now);
        Check(pair.Run(pair.BothReady, 5) && pair.Host.Reason == SessionDecline.None && pair.HostReady == 1 && pair.GuestReady == 1,
            "decline-then-next-guest-admitted");

        // Mod differences with the default policy.
        SessionManifest hostMods = Manifest(Game, "a=1", "b=1", "c=1");
        SessionManifest guestMods = Manifest(Game, "a=1", "b=2", "d=1");
        pair = new Pair(32, hostMods, guestMods).Open();
        pair.Run(() => pair.HostEnded.Count > 0 && pair.GuestEnded.Count > 0, 10);
        Check(pair.HostDeclined.Count == 1 && pair.HostDeclined[0] == SessionDecline.Mods && pair.GuestDeclined.Count == 1 &&
              pair.GuestDeclined[0] == SessionDecline.Mods, "decline-mods");
        Check(pair.Host.DifferenceCount == 3 && pair.Guest.DifferenceCount == 3 && pair.Guest.Differences.Count == 3 &&
              SameDiffs(pair.Host.Differences, pair.Guest.Differences, 3) &&
              pair.Guest.Differences[0].Kind == ModDiffKind.Version && pair.Guest.Differences[0].Id == "b" &&
              pair.Guest.Differences[0].Host == "1" && pair.Guest.Differences[0].Guest == "2" &&
              pair.Guest.Differences[1].Kind == ModDiffKind.HostOnly && pair.Guest.Differences[2].Kind == ModDiffKind.GuestOnly,
            "decline-mods-lists-differences");
        Check(SameManifest(pair.Host.Remote, guestMods), "decline-host-knows-guest-manifest");

        // A different version of this mod is declined by default like any other difference.
        pair = new Pair(33, Plain(), Manifest(Game, "probably_coop=0.2.1", "item_details=1.4", "better_shelves=2.0")).Open();
        pair.Run(() => pair.GuestEnded.Count > 0, 10);
        Check(pair.GuestDeclined.Count == 1 && pair.GuestDeclined[0] == SessionDecline.Mods && pair.Guest.Differences.Count == 1 &&
              pair.Guest.Differences[0].Id == "probably_coop", "decline-own-version-difference");

        // The host policy may admit differences; both ends still know what differs.
        pair = new Pair(34, hostMods, guestMods);
        int seen = -1;
        SessionManifest seenRemote = null;
        pair.Host.Admit = (remote, diffs) => { seen = diffs.Count; seenRemote = remote; return true; };
        pair.Open();
        Check(pair.Run(pair.BothReady, 5) && seen == 3 && SameManifest(seenRemote, guestMods), "admit-hook-admits");
        Check(pair.Host.Differences.Count == 3 && pair.Guest.Differences.Count == 3 &&
              SameDiffs(pair.Host.Differences, pair.Guest.Differences, 3) && pair.Guest.Reason == SessionDecline.None,
            "admit-both-know-differences");

        // ... and may refuse identical lists.
        pair = new Pair(35, Plain(), Plain());
        pair.Host.Admit = (remote, diffs) => false;
        pair.Open();
        pair.Run(() => pair.GuestEnded.Count > 0, 10);
        Check(pair.GuestDeclined.Count == 1 && pair.GuestDeclined[0] == SessionDecline.Mods && pair.Guest.DifferenceCount == 0 &&
              pair.GuestReady == 0, "admit-hook-refuses");

        // A long difference list is capped on the wire; the guest learns the real count.
        var forty = new SessionManifest { Game = Game };
        for (int i = 0; i < 40; i++) forty.Mods.Add(new SessionMod("m" + i.ToString("D2"), "1"));
        pair = new Pair(36, forty, Manifest(Game)).Open();
        pair.Run(() => pair.GuestEnded.Count > 0, 10);
        Check(pair.Host.Differences.Count == 40 && pair.Guest.DifferenceCount == 40 &&
              pair.Guest.Differences.Count == NetSession.MaxDiffs && pair.Guest.Differences[0].Id == "m00" &&
              pair.Guest.Differences[NetSession.MaxDiffs - 1].Id == "m31" &&
              SameDiffs(pair.Host.Differences, pair.Guest.Differences, NetSession.MaxDiffs), "decline-difference-list-capped");
    }

    // The host's answer to one hand-written offer, as the raw guest receives it.
    private static byte[] Answer(int seed, byte[] offer, out Pair pair)
    {
        pair = new Pair(seed, Plain(), null).Open();
        Pair opened = pair;
        pair.Run(opened.LinksOpen, 5);
        pair.Sim.Guest.Send(NetSession.Control, offer);
        pair.Run(() => opened.Sim.GuestGot.Count > 0 || opened.HostEnded.Count > 0, 5);
        return pair.Sim.GuestGot.Count == 1 && pair.Sim.GuestGot[0].Channel == NetSession.Control ? pair.Sim.GuestGot[0].Data : new byte[0];
    }

    private static void SessionProtocol()
    {
        byte[] answer = Answer(41, OfferBytes(NetSession.Content + 1, NetSession.Content + 2, Plain()), out Pair pair);
        Check(Same(answer, DeclineBytes((byte)SessionDecline.Protocol, 0, 0)) && pair.HostDeclined.Count == 1 &&
              pair.HostDeclined[0] == SessionDecline.Protocol && pair.Host.State == SessionState.Negotiating, "protocol-newer-guest-declined");
        answer = Answer(42, OfferBytes(0, 1, Plain()), out pair);
        Check(Same(answer, DeclineBytes((byte)SessionDecline.Protocol, 0, 0)), "protocol-zero-oldest-declined");
        answer = Answer(43, OfferBytes(3, 1, Plain()), out pair);
        Check(Same(answer, DeclineBytes((byte)SessionDecline.Protocol, 0, 0)), "protocol-inverted-range-declined");
        // Protocol is judged before the game build and the mods.
        answer = Answer(44, OfferBytes(NetSession.Oldest - 1, NetSession.Oldest - 1, Manifest("other", "x=1")), out pair);
        Check(Same(answer, DeclineBytes((byte)SessionDecline.Protocol, 0, 0)), "protocol-judged-first");
        answer = Answer(45, OfferBytes(1, 5, Plain()), out pair);
        Check(Same(answer, AcceptBytes(NetSession.Content, NetSession.GuestPeer, Plain())) && pair.Host.State == SessionState.Ready &&
              pair.Host.Protocol == NetSession.Content && pair.HostReady == 1, "protocol-newer-guest-range-accepted");
    }

    private static void SessionLinger()
    {
        Answer(51, OfferBytes(NetSession.Content, NetSession.Content, Manifest("other")), out Pair pair);
        double declinedAt = pair.Sim.Now;
        Check(pair.HostDeclined.Count == 1 && pair.HostDeclined[0] == SessionDecline.Game, "linger-declined");
        // A guest that does not hang up cannot do anything with the link.
        pair.Sim.Guest.Send(1, new byte[] { 1 });
        pair.Sim.Guest.Send(NetSession.Control, new byte[] { 9, 9 });
        pair.Sim.Guest.Send(NetSession.Control, OfferBytes(NetSession.Content, NetSession.Content, Plain()));
        pair.Sim.Guest.SendLoose(2, new byte[] { 1 });
        pair.Run(() => false, 2);
        Check(pair.HostEnded.Count == 0 && pair.Sim.Host.State == NetState.Open && pair.HostGot.Count == 0 && pair.HostLoose.Count == 0 &&
              pair.HostReady == 0 && pair.HostDeclined.Count == 1 && !pair.Host.Send(1, new byte[] { 1 }), "linger-ignores-declined-guest");
        pair.Run(() => pair.HostEnded.Count > 0, 10);
        double waited = pair.Sim.Now - declinedAt;
        Check(pair.HostEnded.Count == 1 && pair.HostEnded[0] == NetClose.Timeout && waited > NetSession.DeclineLinger - 0.1 &&
              waited < NetSession.DeclineLinger + 0.2, "linger-drops-guest waited=" + waited.ToString("F2"));
    }

    private static void BadGuest(int seed, string name, Action<Pair> act)
    {
        var pair = new Pair(seed, Plain(), null).Open();
        pair.Run(pair.LinksOpen, 5);
        act(pair);
        pair.Run(() => pair.HostEnded.Count > 0, 5);
        Check(pair.HostEnded.Count == 1 && pair.HostEnded[0] == NetClose.Corrupt && pair.HostGot.Count == 0 &&
              pair.Host.State == SessionState.Idle, name);
    }

    private static void SessionBadGuest()
    {
        byte[] offer = OfferBytes(NetSession.Content, NetSession.Content, Plain());
        BadGuest(61, "bad-guest-data-before-offer", pair => pair.Sim.Guest.Send(1, new byte[] { 1 }));
        BadGuest(62, "bad-guest-garbage", pair => pair.Sim.Guest.Send(NetSession.Control, new byte[] { 9, 9, 9 }));
        BadGuest(63, "bad-guest-empty-control", pair => pair.Sim.Host.Message(NetSession.Control, new byte[0]));
        BadGuest(64, "bad-guest-offer-trailing-byte", pair => pair.Sim.Guest.Send(NetSession.Control, Plus(offer, 1)));
        BadGuest(65, "bad-guest-offer-truncated", pair => pair.Sim.Guest.Send(NetSession.Control, Plus(offer, -1)));
        BadGuest(66, "bad-guest-offer-duplicate-mod",
            pair => pair.Sim.Guest.Send(NetSession.Control, OfferBytes(NetSession.Content, NetSession.Content, Manifest(Game, "a=1", "a=1"))));
        BadGuest(67, "bad-guest-sends-accept",
            pair => pair.Sim.Guest.Send(NetSession.Control, AcceptBytes(NetSession.Content, 1, Plain())));
        BadGuest(68, "bad-guest-sends-decline", pair => pair.Sim.Guest.Send(NetSession.Control, DeclineBytes(2, 0, 0)));
        // Once admitted, the control channel carries nothing further.
        var pair2 = new Pair(69, Plain(), null).Open();
        pair2.Run(pair2.LinksOpen, 5);
        pair2.Sim.Guest.Send(NetSession.Control, offer);
        pair2.Sim.Guest.Send(NetSession.Control, offer);
        pair2.Run(() => pair2.HostEnded.Count > 0, 5);
        Check(pair2.HostReady == 1 && pair2.HostEnded.Count == 1 && pair2.HostEnded[0] == NetClose.Corrupt, "bad-guest-second-offer");
    }

    private static void BadHost(int seed, string name, byte channel, byte[] message)
    {
        var pair = new Pair(seed, null, Plain()).Open();
        pair.Run(() => pair.Sim.HostGot.Count == 1, 5);
        pair.Sim.Host.Send(channel, message);
        pair.Run(() => pair.GuestEnded.Count > 0, 5);
        Check(pair.GuestEnded.Count == 1 && pair.GuestEnded[0] == NetClose.Corrupt && pair.GuestReady == 0 &&
              pair.GuestDeclined.Count == 0 && pair.GuestGot.Count == 0, name);
    }

    private static void SessionBadHost()
    {
        var pair = new Pair(70, null, Plain()).Open();
        pair.Run(() => pair.Sim.HostGot.Count == 1, 5);
        Check(pair.Sim.HostGot.Count == 1 && pair.Sim.HostGot[0].Channel == NetSession.Control &&
              Same(pair.Sim.HostGot[0].Data, OfferBytes(NetSession.Oldest, NetSession.Content, Plain())), "offer-wire-format");

        // Datagrams can overtake the accept; they are dropped, not treated as a violation.
        pair.Sim.Host.SendLoose(2, new byte[] { 1 });
        pair.Run(() => false, 0.5);
        Check(pair.GuestLoose.Count == 0 && pair.GuestEnded.Count == 0 && !pair.Guest.SendLoose(2, new byte[] { 1 }),
            "datagram-before-ready-dropped");
        pair.Sim.Host.Send(NetSession.Control, AcceptBytes(NetSession.Content, 1, Plain()));
        pair.Sim.Host.Send(3, new byte[] { 7 });
        pair.Run(() => pair.GuestGot.Count == 1, 5);
        pair.Sim.Host.SendLoose(2, new byte[] { 2 });
        pair.Run(() => pair.GuestLoose.Count == 1, 5);
        Check(pair.GuestReady == 1 && pair.GuestGot.Count == 1 && pair.GuestGot[0].Channel == 3 && pair.GuestLoose.Count == 1 &&
              pair.GuestLoose[0].Data[0] == 2 && pair.GuestOrder.Count == 2 && pair.GuestOrder[0] == "ready", "raw-accept-then-data");
        pair.Sim.Host.Send(NetSession.Control, AcceptBytes(NetSession.Content, 1, Plain()));
        pair.Run(() => pair.GuestEnded.Count > 0, 5);
        Check(pair.GuestEnded.Count == 1 && pair.GuestEnded[0] == NetClose.Corrupt, "bad-host-second-accept");

        byte[] accept = AcceptBytes(NetSession.Content, 1, Plain());
        BadHost(71, "bad-host-data-before-accept", 1, new byte[] { 1 });
        BadHost(72, "bad-host-garbage", NetSession.Control, new byte[] { 9 });
        BadHost(73, "bad-host-sends-offer", NetSession.Control, OfferBytes(NetSession.Content, NetSession.Content, Plain()));
        BadHost(74, "bad-host-accept-peer-zero", NetSession.Control, AcceptBytes(NetSession.Content, 0, Plain()));
        BadHost(75, "bad-host-accept-protocol-zero", NetSession.Control, AcceptBytes(0, 1, Plain()));
        BadHost(76, "bad-host-accept-protocol-unoffered", NetSession.Control, AcceptBytes(NetSession.Content + 1, 1, Plain()));
        BadHost(77, "bad-host-accept-trailing-byte", NetSession.Control, Plus(accept, 1));
        BadHost(78, "bad-host-accept-truncated", NetSession.Control, Plus(accept, -1));
        BadHost(79, "bad-host-decline-reason-zero", NetSession.Control, DeclineBytes(0, 0, 0));
        BadHost(80, "bad-host-decline-reason-unknown", NetSession.Control, DeclineBytes(4, 0, 0));
        BadHost(81, "bad-host-decline-count-over-total", NetSession.Control, DeclineBytes(3, 0, 1, (1, "a", "1", "")));
        BadHost(82, "bad-host-decline-count-missing-rows", NetSession.Control, DeclineBytes(3, 2, 2, (1, "a", "1", "")));
        BadHost(83, "bad-host-decline-kind-zero", NetSession.Control, DeclineBytes(3, 1, 1, (0, "a", "1", "")));
        BadHost(84, "bad-host-decline-kind-unknown", NetSession.Control, DeclineBytes(3, 1, 1, (5, "a", "1", "")));
        BadHost(87, "bad-host-accept-invalid-peer", NetSession.Control, AcceptBytes(NetSession.Content, 2, Plain()));
        BadHost(88, "bad-host-accept-other-game", NetSession.Control, AcceptBytes(NetSession.Content, 1, Manifest("different")));
        BadHost(85, "bad-host-decline-trailing-byte", NetSession.Control, Plus(DeclineBytes(2, 0, 0), 1));
        var rows = new (byte, string, string, string)[NetSession.MaxDiffs + 1];
        for (int i = 0; i < rows.Length; i++) rows[i] = (1, "m" + i, "1", "");
        BadHost(86, "bad-host-decline-too-many-rows", NetSession.Control, DeclineBytes(3, (ulong)rows.Length, (ulong)rows.Length, rows));

        // A well-formed raw decline: the guest reports it and hangs up.
        pair = new Pair(87, null, Plain()).Open();
        pair.Run(() => pair.Sim.HostGot.Count == 1, 5);
        pair.Sim.Host.Send(NetSession.Control, DeclineBytes(3, 40, 2, (3, "a", "1", "2"), (2, "b", "", "5")));
        pair.Run(() => pair.Sim.HostEnded.Count > 0, 5);
        Check(pair.GuestDeclined.Count == 1 && pair.GuestDeclined[0] == SessionDecline.Mods && pair.Guest.DifferenceCount == 40 &&
              pair.Guest.Differences.Count == 2 && pair.Guest.Differences[1].Kind == ModDiffKind.GuestOnly &&
              pair.Guest.Differences[1].Guest == "5" && pair.GuestEnded.Count == 1 && pair.GuestEnded[0] == NetClose.Local &&
              pair.Sim.HostEnded.Count == 1 && pair.Sim.HostEnded[0] == NetClose.Remote, "raw-decline-reported");
    }

    private static void SessionTimeouts()
    {
        // The host never answers the offer.
        var pair = new Pair(91, null, Plain()).Open();
        pair.Run(pair.LinksOpen, 5);
        double opened = pair.Sim.Now;
        pair.Run(() => pair.GuestEnded.Count > 0, 30);
        double waited = pair.Sim.Now - opened;
        Check(pair.GuestEnded.Count == 1 && pair.GuestEnded[0] == NetClose.Timeout && waited > NetSession.NegotiateTimeout - 0.2 &&
              waited < NetSession.NegotiateTimeout + 0.2 && pair.GuestReady == 0, "timeout-silent-host waited=" + waited.ToString("F2"));

        // The guest never sends an offer.
        pair = new Pair(92, Plain(), null).Open();
        pair.Run(pair.LinksOpen, 5);
        opened = pair.Sim.Now;
        pair.Run(() => pair.HostEnded.Count > 0, 30);
        waited = pair.Sim.Now - opened;
        Check(pair.HostEnded.Count == 1 && pair.HostEnded[0] == NetClose.Timeout && waited > NetSession.NegotiateTimeout - 0.2 &&
              waited < NetSession.NegotiateTimeout + 0.2 && pair.HostReady == 0, "timeout-silent-guest waited=" + waited.ToString("F2"));

        // A connection attempt nobody answers ends the session too.
        pair = new Pair(93, null, Plain());
        pair.Sim.CutToHost = true;
        pair.Sim.Guest.Connect(pair.Sim.Now);
        pair.Run(() => pair.GuestEnded.Count > 0, 30);
        Check(pair.GuestEnded.Count == 1 && pair.GuestEnded[0] == NetClose.Timeout && pair.Guest.State == SessionState.Idle,
            "timeout-connect-reported");
    }

    private static void SessionLossy()
    {
        var manifest = new SessionManifest { Game = Game };
        for (int i = 0; i < 60; i++) manifest.Mods.Add(new SessionMod("workshop_mod_number_" + i, "1." + i));
        int ready = 0, exact = 0;
        double slowest = 0;
        for (int seed = 1; seed <= 8; seed++)
        {
            var pair = new Pair(100 + seed, manifest, manifest);
            pair.Sim.Loss = 0.3;
            pair.Sim.Duplicate = 0.1;
            pair.Sim.Jitter = 0.05;
            pair.Open();
            if (!pair.Run(pair.BothReady, 40)) continue;
            ready++;
            slowest = Math.Max(slowest, pair.Sim.Now);
            for (int i = 0; i < 40; i++)
            {
                pair.Host.Send((byte)(1 + i % 2), new byte[] { (byte)i });
                pair.Guest.Send((byte)(1 + i % 2), new byte[] { (byte)i });
            }
            pair.Run(() => pair.HostGot.Count == 40 && pair.GuestGot.Count == 40, 60);
            bool ok = pair.HostGot.Count == 40 && pair.GuestGot.Count == 40 && pair.HostReady == 1 && pair.GuestReady == 1 &&
                      SameManifest(pair.Host.Remote, manifest) && SameManifest(pair.Guest.Remote, manifest);
            for (int i = 0; ok && i < 40; i++) ok = pair.HostGot[i].Data[0] == i && pair.GuestGot[i].Data[0] == i;
            if (ok) exact++;
        }
        Console.WriteLine("info session-lossy ready=" + ready + "/8 slowest-ready=" + slowest.ToString("F1") + "s");
        Check(ready == 8 && exact == 8, "session-lossy ready=" + ready + " exact=" + exact);
    }

    private static void SessionReentry()
    {
        // The owner leaves the room from inside the Declined callback.
        var pair = new Pair(111, Manifest("other"), Plain());
        pair.Guest.Declined = reason => { pair.GuestDeclined.Add(reason); pair.Sim.Guest.Close(); };
        pair.Open();
        pair.Run(() => pair.HostEnded.Count > 0, 10);
        Check(pair.GuestDeclined.Count == 1 && pair.GuestEnded.Count == 1 && pair.GuestEnded[0] == NetClose.Local &&
              pair.HostEnded.Count == 1 && pair.HostEnded[0] == NetClose.Remote, "reentry-guest-closes-in-declined");

        // The host closes the room from inside Declined: the guest just sees the host leave.
        pair = new Pair(112, Manifest("other"), Plain());
        pair.Host.Declined = reason => { pair.HostDeclined.Add(reason); pair.Sim.Host.Close(); };
        pair.Open();
        pair.Run(() => pair.GuestEnded.Count > 0, 10);
        Check(pair.HostEnded.Count == 1 && pair.HostEnded[0] == NetClose.Local && pair.GuestEnded.Count == 1 &&
              pair.GuestEnded[0] == NetClose.Remote && pair.GuestReady == 0, "reentry-host-closes-in-declined");

        // Sending from inside Ready: the guest still sees Ready before the first message.
        pair = new Pair(113, Plain(), Plain());
        pair.Host.Ready = () => { pair.HostReady++; pair.Host.Send(4, new byte[] { 1 }); };
        pair.Guest.Ready = () => { pair.GuestReady++; pair.GuestOrder.Add("ready"); pair.Guest.Send(4, new byte[] { 2 }); };
        pair.Open();
        pair.Run(() => pair.HostGot.Count == 1 && pair.GuestGot.Count == 1, 5);
        Check(pair.HostGot.Count == 1 && pair.HostGot[0].Data[0] == 2 && pair.GuestGot.Count == 1 && pair.GuestGot[0].Data[0] == 1 &&
              pair.GuestOrder.Count == 2 && pair.GuestOrder[0] == "ready" && pair.GuestOrder[1] == "message", "reentry-send-in-ready");

        // Closing from inside Ready.
        pair = new Pair(114, Plain(), Plain());
        pair.Guest.Ready = () => { pair.GuestReady++; pair.Sim.Guest.Close(); };
        pair.Open();
        pair.Run(() => pair.HostEnded.Count > 0, 5);
        Check(pair.GuestReady == 1 && pair.GuestEnded.Count == 1 && pair.Guest.State == SessionState.Idle && pair.HostEnded.Count == 1 &&
              pair.HostEnded[0] == NetClose.Remote, "reentry-close-in-ready");

        bool threw = false;
        try { new NetSession(null, Plain()); } catch (ArgumentNullException) { threw = true; }
        Check(threw, "ctor-null-link");
        threw = false;
        try { new NetSession(new Sim(1).Host, null); } catch (ArgumentException) { threw = true; }
        Check(threw, "ctor-null-manifest");
        threw = false;
        try { new NetSession(new Sim(1).Host, Manifest(Game, "a=1", "a=2")); } catch (ArgumentException) { threw = true; }
        Check(threw, "ctor-invalid-manifest");
    }

    private static void SessionFuzz()
    {
        var random = new Random(4242);
        byte[][] seeds =
        {
            OfferBytes(NetSession.Content, NetSession.Content, Plain()), AcceptBytes(NetSession.Content, 1, Plain()), DeclineBytes(3, 2, 2, (1, "a", "1", ""), (3, "b", "1", "2")),
            DeclineBytes(2, 0, 0), OfferBytes(NetSession.Content, NetSession.Content, Manifest(Game))
        };
        int thrown = 0, reached = 0;
        for (int round = 0; round < 400; round++)
        {
            bool hostSide = round % 2 == 0;
            bool ready = round % 4 >= 2;
            var pair = ready ? new Pair(round, Plain(), Plain()) : new Pair(round, hostSide ? Plain() : null, hostSide ? null : Plain());
            pair.Open();
            pair.Run(ready ? pair.BothReady : pair.LinksOpen, 5);
            NetLink target = hostSide ? pair.Sim.Host : pair.Sim.Guest;
            NetSession session = hostSide ? pair.Host : pair.Guest;
            for (int i = 0; i < 20; i++)
            {
                byte[] bytes;
                if (random.Next(4) == 0) bytes = Blob(random, random.Next(0, 80));
                else
                {
                    bytes = (byte[])seeds[random.Next(seeds.Length)].Clone();
                    int edits = random.Next(1, 4);
                    for (int e = 0; e < edits && bytes.Length > 0; e++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                    if (random.Next(3) == 0) bytes = Plus(bytes, random.Next(-Math.Min(bytes.Length, 6), 7));
                }
                try
                {
                    target.Message((byte)(random.Next(5) == 0 ? 1 : 0), bytes);
                    target.Datagram(1, bytes);
                    session.Tick(pair.Sim.Now);
                    if (session.State == SessionState.Ready && (session.Remote == null || session.Protocol != NetSession.Content))
                        thrown++;
                    if (session.State == SessionState.Ready) reached++;
                }
                catch (Exception)
                {
                    thrown++;
                }
            }
        }
        Check(thrown == 0 && reached > 0, "session-fuzz thrown=" + thrown + " reached=" + reached);
    }
}
