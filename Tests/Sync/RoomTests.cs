using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;

namespace ProbablyCoop.SyncTests;

internal static partial class NetTests
{
    private static int roomEarly;

    private sealed class FakeHub
    {
        internal readonly Dictionary<ulong, FakeLobby> Hosts = new Dictionary<ulong, FakeLobby>();
        internal ulong Next = 0x0186000000000001UL;
    }

    // Stands in for the Steam carrier: same statuses and the same approval gate, with
    // frames handed over in memory.
    private sealed class FakeLobby : INetLobby
    {
        private readonly FakeHub hub;
        private readonly Queue<byte[]> inbox = new Queue<byte[]>();
        private FakeLobby target;
        private FakeLobby candidate;
        private FakeLobby home;
        private ulong lobby;
        private bool host;
        private bool opened;

        internal FakeLobby(FakeHub hub)
        {
            this.hub = hub;
            Link = new NetLink(Send, 1100);
        }

        public NetLink Link { get; }
        public bool HasLobby => lobby != 0;
        public bool Busy => false;
        public bool IsHost => HasLobby && host;
        public bool IsGuest => HasLobby && !host;
        public bool CanApprove => IsHost && candidate != null && target == null;
        public bool FriendsCanJoin => IsHost;
        public string Code => HasLobby ? SteamCode.Encode(lobby) : "";
        public string Status { get; private set; } = "idle";
        public string Detail => "";
        public string PartnerName => target == null ? "" : "Partner";
        public int Sent { get; private set; }
        public int Received { get; private set; }

        public void Create(double now)
        {
            if (HasLobby) return;
            lobby = hub.Next++;
            hub.Hosts[lobby] = this;
            host = true;
            Link.Listen();
            Status = "waiting";
        }

        public void Join(double now, ulong id)
        {
            if (HasLobby) return;
            if (!hub.Hosts.TryGetValue(id, out FakeLobby owner))
            {
                Status = "join_failed";
                return;
            }
            lobby = id;
            host = false;
            home = owner;
            target = owner;
            owner.candidate = this;
            if (owner.target == null) owner.Status = "approve";
            Link.Connect(now);
            Status = "await_approval";
        }

        public void Approve()
        {
            if (!CanApprove) return;
            target = candidate;
            Status = "handshake";
        }

        public void Leave(string status = "idle")
        {
            if (HasLobby)
            {
                Link.Close();
                if (host) hub.Hosts.Remove(lobby);
                else if (home.candidate == this) home.candidate = null;
            }
            lobby = 0;
            target = null;
            candidate = null;
            home = null;
            opened = false;
            inbox.Clear();
            Status = status;
        }

        public void Poll(double now)
        {
            while (HasLobby && inbox.Count > 0)
            {
                byte[] frame = inbox.Dequeue();
                if (host && target == null) continue;
                if (Link.Receive(frame, frame.Length, now)) Received++;
            }
            if (!HasLobby) return;
            if (host && target != null && target.home != this) Link.Abort(NetClose.Remote);
            if (host && target == null && (Status == "approve" || Status == "waiting"))
                Status = candidate != null ? "approve" : "waiting";
            Settle(now);
        }

        public void Flush(double now)
        {
            if (!HasLobby) return;
            Link.Tick(now);
            Settle(now);
        }

        public void Dispose() => Leave();

        private bool Send(byte[] bytes, bool reliable)
        {
            if (target == null) return false;
            target.inbox.Enqueue((byte[])bytes.Clone());
            Sent++;
            return true;
        }

        private void Settle(double now)
        {
            if (!HasLobby) return;
            if (Link.State == NetState.Open)
            {
                if (opened) return;
                opened = true;
                Status = "connected";
                return;
            }
            if (Link.State != NetState.Closed || Link.Reason == NetClose.Local) return;
            NetClose reason = Link.Reason;
            if (host)
            {
                target = null;
                opened = false;
                Link.Listen();
                Status = reason != NetClose.Remote ? "peer_timeout" : candidate != null ? "approve" : "waiting";
            }
            else if (reason == NetClose.Refused) Leave("incompatible");
            else if (!opened && reason == NetClose.Timeout) Link.Connect(now);
            else if (reason == NetClose.Remote) Leave("host_left");
            else Leave("peer_timeout");
        }
    }

    private sealed class RoomEnd
    {
        internal readonly NetRoom Room;
        internal int Readies;
        internal readonly List<NetClose> Ends = new List<NetClose>();
        internal readonly List<string> Got = new List<string>();
        internal readonly List<string> Loose = new List<string>();

        internal RoomEnd(SessionManifest manifest, bool localTest = true, FakeHub hub = null)
        {
            Room = new NetRoom(() => manifest, hub == null ? null : () => new FakeLobby(hub), localTest);
            Room.Ready = () => Readies++;
            Room.Ended = reason => Ends.Add(reason);
            Room.Message = (channel, message) => Got.Add(channel + ":" + message.Length);
            Room.Datagram += (channel, payload) => Loose.Add(channel + ":" + payload.Length);
        }
    }

    private static void Watch(NetRoom room)
    {
        // A room may only call itself connected once both sides agreed on content.
        if (room.Status.EndsWith("connected", StringComparison.Ordinal) && !room.IsReady) roomEarly++;
    }

    private static bool SpinRooms(Func<bool> done, double limit, params RoomEnd[] ends)
    {
        double end = Now + limit;
        while (true)
        {
            foreach (RoomEnd each in ends)
            {
                each.Room.Poll(Now);
                Watch(each.Room);
                each.Room.Flush(Now);
                Watch(each.Room);
            }
            if (done()) return true;
            if (Now >= end) return false;
            Thread.Sleep(1);
        }
    }

    private static void Rooms()
    {
        roomEarly = 0;
        RoomIdle();
        RoomLoopback();
        RoomHostLeaves();
        RoomRefused();
        RoomGame();
        RoomMods();
        RoomLanAddress();
        RoomLobby();
        RoomLobbyDecline();
        RoomMuteGuest();
        Check(roomEarly == 0, "room-never-connected-before-ready");
    }

    private static void RoomIdle()
    {
        var local = new RoomEnd(Plain());
        Check(local.Room.Status == "local_idle" && !local.Room.HasLobby && !local.Room.IsReady && local.Room.Code == "",
            "room-idle-local");
        Check(!local.Room.Send(1, new byte[1]) && !local.Room.SendLoose(1, new byte[1]), "room-idle-send-refused");
        local.Room.Poll(Now);
        local.Room.Flush(Now);
        local.Room.Approve();
        local.Room.Leave();
        Check(local.Room.Status == "local_idle", "room-idle-calls-harmless");

        local.Room.Join(Now, "not a code");
        Check(local.Room.Status == "bad_code" && !local.Room.HasLobby, "room-bad-code");
        local.Room.Join(Now, "PSL-1-zz");
        Check(local.Room.Status == "bad_code" && !local.Room.HasLobby, "room-bad-direct-code");
        local.Room.Join(Now, null);
        Check(local.Room.Status == "bad_code", "room-null-code");
        local.Room.CreateLan(Now, "nonsense");
        Check(local.Room.Status == "bad_address" && !local.Room.HasLobby, "room-bad-address");
        local.Room.CreateLan(Now, "127.0.0.1");
        Check(local.Room.Status == "bad_address" && !local.Room.HasLobby, "room-loopback-address-refused");
        local.Room.CreateLan(Now, null);
        Check(local.Room.Status == "bad_address" && !local.Room.HasLobby, "room-null-address");
        // Addresses that parse but cannot host: not IPv4, multicast, unspecified.
        foreach (string unusable in new[] { "fe80::1", "239.1.2.3", "0.0.0.0" })
        {
            local.Room.Join(Now, "not a code");
            local.Room.CreateLan(Now, unusable);
            Check(local.Room.Status == "bad_address" && !local.Room.HasLobby, "room-unusable-address " + unusable);
        }
        // TEST-NET-1 is never assigned to an interface, so the bind fails.
        local.Room.CreateLan(Now, "192.0.2.1");
        Check(local.Room.Status == "create_failed" && local.Room.Detail.Length > 0 && !local.Room.HasLobby,
            "room-create-failed-detail");
        local.Room.Leave();
        Check(local.Room.Status == "local_idle", "room-leave-clears-carrier-error");
        local.Room.Join(Now, "not a code");
        Check(local.Room.Status == "bad_code", "room-bad-code-again");
        local.Room.Leave();
        Check(local.Room.Status == "local_idle", "room-leave-clears-notice");

        var steam = new RoomEnd(Plain(), false);
        Check(steam.Room.Status == "idle", "room-idle-steam");
        steam.Room.Create(Now);
        Check(steam.Room.Status == "steam_unavailable" && !steam.Room.HasLobby, "room-create-without-lobby-service");
        steam.Room.Join(Now, SteamCode.Encode(0x0186000000000001UL));
        Check(steam.Room.Status == "steam_unavailable", "room-join-without-lobby-service");
        steam.Room.JoinLobby(Now, 0x0186000000000001UL);
        Check(steam.Room.Status == "steam_unavailable", "room-join-lobby-without-service");
        // A direct room still works when the lobby service is missing.
        steam.Room.Join(Now, "PSL-1-zz");
        Check(steam.Room.Status == "bad_code", "room-notice-replaced");
        local.Room.Dispose();
        steam.Room.Dispose();
    }

    private static void RoomLoopback()
    {
        var host = new RoomEnd(Plain());
        var guest = new RoomEnd(Plain());
        host.Room.Create(Now);
        string code = host.Room.Code;
        Check(host.Room.HasLobby && host.Room.IsHost && !host.Room.IsGuest && host.Room.IsLoopback &&
              host.Room.Status == "local_waiting" && LanTransport.IsCode(code), "room-loopback-hosted");
        host.Room.Create(Now);
        host.Room.CreateLan(Now, "10.0.0.1");
        host.Room.Join(Now, "not a code");
        Check(host.Room.Code == code && host.Room.Status == "local_waiting", "room-open-ignores-new-requests");

        guest.Room.Join(Now, "  " + code + " ");
        Check(guest.Room.HasLobby && guest.Room.IsGuest && guest.Room.Status == "handshake", "room-loopback-joining");
        bool ready = SpinRooms(() => host.Room.IsReady && guest.Room.IsReady, 5, host, guest);
        Check(ready && host.Readies == 1 && guest.Readies == 1, "room-loopback-ready");
        Check(host.Room.Status == "local_connected" && guest.Room.Status == "local_connected", "room-loopback-status");
        Check(host.Room.Peer == 0 && guest.Room.Peer == NetSession.GuestPeer && host.Room.Remote != null &&
              guest.Room.Remote != null && host.Room.Reason == SessionDecline.None, "room-loopback-session");
        Check(host.Room.PartnerName == "Guest" && guest.Room.PartnerName == "Host", "room-partner-names");

        Check(!host.Room.Send(NetSession.Control, new byte[4]), "room-control-channel-reserved");
        Check(host.Room.Send(1, new byte[5000]) && guest.Room.Send(2, new byte[7]) &&
              host.Room.SendLoose(3, new byte[40]), "room-send-accepted");
        bool moved = SpinRooms(() => guest.Got.Count == 1 && host.Got.Count == 1 && guest.Loose.Count == 1, 5, host, guest);
        Check(moved && guest.Got[0] == "1:5000" && host.Got[0] == "2:7" && guest.Loose[0] == "3:40", "room-messages");
        Check(host.Room.Sent > 0 && host.Room.Received > 0, "room-counters");

        // The guest leaves; the same room takes it back.
        guest.Room.Leave();
        Check(guest.Room.Status == "local_idle" && !guest.Room.HasLobby && !guest.Room.IsReady, "room-guest-left");
        Check(guest.Room.Remote == null && guest.Room.Peer == 0 && !guest.Room.Send(1, new byte[1]),
            "room-left-forgets-session");
        bool waiting = SpinRooms(() => host.Room.Status == "local_waiting" && !host.Room.IsReady, 5, host, guest);
        Check(waiting && host.Ends.Count == 1 && host.Ends[0] == NetClose.Remote && host.Room.Code == code,
            "room-host-waits-again");
        guest.Room.Join(Now, code);
        bool again = SpinRooms(() => host.Room.IsReady && guest.Room.IsReady, 5, host, guest);
        Check(again && host.Readies == 2 && guest.Readies == 2 && host.Room.Status == "local_connected", "room-rejoin");
        Check(host.Room.Send(1, new byte[3]) && SpinRooms(() => guest.Got.Count == 2, 5, host, guest) &&
              guest.Got[1] == "1:3", "room-rejoin-messages");
        host.Room.Dispose();
        guest.Room.Dispose();
    }

    private static void RoomHostLeaves()
    {
        var host = new RoomEnd(Plain());
        var guest = new RoomEnd(Plain());
        host.Room.Create(Now);
        guest.Room.Join(Now, host.Room.Code);
        Check(SpinRooms(() => host.Room.IsReady && guest.Room.IsReady, 5, host, guest), "room-leave-setup");
        host.Room.Leave();
        Check(host.Room.Status == "local_idle" && !host.Room.HasLobby && host.Ends.Count == 1 &&
              host.Ends[0] == NetClose.Local, "room-host-left");
        bool told = SpinRooms(() => !guest.Room.HasLobby, 5, host, guest);
        Check(told && guest.Room.Status == "host_left" && !guest.Room.IsReady && guest.Ends.Count == 1 &&
              guest.Ends[0] == NetClose.Remote, "room-guest-sees-host-leave");
        // Either side can start over.
        guest.Room.Create(Now);
        host.Room.Join(Now, guest.Room.Code);
        bool swapped = SpinRooms(() => host.Room.IsReady && guest.Room.IsReady, 5, host, guest);
        Check(swapped && guest.Room.IsHost && host.Room.IsGuest, "room-roles-swap");
        host.Room.Dispose();
        guest.Room.Dispose();
    }

    private static void RoomRefused()
    {
        // A host that speaks no link protocol this build knows answers Hello with a refusal.
        Guid id = Guid.NewGuid();
        byte[] token = id.ToByteArray();
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint).Port;
        var guest = new RoomEnd(Plain());
        guest.Room.Join(Now, $"PSL-{port}-{id:N}");
        Check(guest.Room.IsGuest && guest.Room.Status == "handshake", "room-refused-setup");
        bool answered = false;
        bool left = SpinRooms(() =>
        {
            while (socket.Available > 0)
            {
                IPEndPoint from = null;
                byte[] datagram = socket.Receive(ref from);
                if (datagram.Length <= 16) continue;
                var frame = new byte[datagram.Length - 16];
                Buffer.BlockCopy(datagram, 16, frame, 0, frame.Length);
                if (!NetWire.TryOpen(frame, frame.Length, out NetKind kind, out _, out SyncReader body) ||
                    kind != NetKind.Hello) continue;
                SyncWriter refuse = NetWire.Open(NetKind.Refuse, 0);
                refuse.ULong(body.ULong());
                refuse.Byte((byte)NetRefusal.Protocol);
                byte[] reply = refuse.ToArray();
                var answer = new byte[16 + reply.Length];
                Buffer.BlockCopy(token, 0, answer, 0, 16);
                Buffer.BlockCopy(reply, 0, answer, 16, reply.Length);
                socket.Send(answer, answer.Length, from);
                answered = true;
            }
            return !guest.Room.HasLobby;
        }, 5, guest);
        Check(answered && left && guest.Room.Status == "incompatible" && guest.Readies == 0, "room-refused-by-link");
        Check(guest.Ends.Count == 1 && guest.Ends[0] == NetClose.Refused && guest.Room.Reason == SessionDecline.None,
            "room-refused-reported");
        guest.Room.Dispose();
    }

    private static void RoomGame()
    {
        var host = new RoomEnd(Plain());
        var other = new RoomEnd(Manifest("VERSION 101-Rev14", "probably_coop=0.2.0", "item_details=1.4", "better_shelves=2.0"));
        int asked = 0;
        host.Room.Admit = (_, _) => { asked++; return true; };
        host.Room.Create(Now);
        string code = host.Room.Code;
        other.Room.Join(Now, code);
        double start = Now;
        bool refused = SpinRooms(() => !other.Room.HasLobby && host.Room.Status == "local_waiting", 5, host, other);
        Check(refused && Now - start < 3, "room-game-mismatch-prompt");
        Check(other.Room.Status == "incompatible" && other.Room.Reason == SessionDecline.Game && other.Readies == 0,
            "room-game-mismatch-guest");
        Check(host.Room.Reason == SessionDecline.Game && host.Readies == 0 && asked == 0 && host.Room.Code == code,
            "room-game-mismatch-host");

        var good = new RoomEnd(Plain());
        good.Room.Join(Now, code);
        bool ready = SpinRooms(() => host.Room.IsReady && good.Room.IsReady, 5, host, good);
        Check(ready, "room-next-guest-admitted");
        Check(host.Room.Reason == SessionDecline.None, "room-next-guest-clears-reason");
        Check(asked == 0, "room-identical-guest-not-asked");
        // Trying again forgets the old refusal.
        other.Room.Join(Now, "nope");
        Check(other.Room.Status == "bad_code" && other.Room.Reason == SessionDecline.None, "room-retry-clears-reason");
        host.Room.Dispose();
        other.Room.Dispose();
        good.Room.Dispose();
    }

    private static void RoomMods()
    {
        var host = new RoomEnd(Plain());
        var guest = new RoomEnd(Manifest(Game, "probably_coop=0.2.0", "item_details=1.5", "extra_mod=1.0"));
        host.Room.Create(Now);
        string code = host.Room.Code;
        guest.Room.Join(Now, code);
        bool refused = SpinRooms(() => !guest.Room.HasLobby && host.Room.Status == "local_waiting", 5, host, guest);
        Check(refused && guest.Room.Status == "incompatible" && guest.Room.Reason == SessionDecline.Mods,
            "room-mods-refused-by-default");
        Check(guest.Room.DifferenceCount == 3 && guest.Room.Differences.Count == 3 && host.Room.Differences.Count == 3 &&
              host.Room.Reason == SessionDecline.Mods, "room-mods-differences-both-sides");
        Check(guest.Room.Differences[0].Id == "better_shelves" && guest.Room.Differences[0].Kind == ModDiffKind.HostOnly &&
              guest.Room.Differences[1].Id == "extra_mod" && guest.Room.Differences[1].Kind == ModDiffKind.GuestOnly &&
              guest.Room.Differences[2].Id == "item_details" && guest.Room.Differences[2].Kind == ModDiffKind.Version &&
              guest.Room.Differences[2].Host == "1.4" && guest.Room.Differences[2].Guest == "1.5", "room-mods-difference-rows");

        // The host decides to play anyway.
        int seen = 0;
        host.Room.Admit = (remote, differences) => { seen = differences.Count; return remote.Mods.Count == 3; };
        guest.Room.Join(Now, code);
        bool ready = SpinRooms(() => host.Room.IsReady && guest.Room.IsReady, 5, host, guest);
        Check(ready && seen == 3 && host.Room.Status == "local_connected", "room-mods-admitted-by-host");
        Check(host.Room.Reason == SessionDecline.None && guest.Room.Reason == SessionDecline.None &&
              host.Room.Differences.Count == 3 && guest.Room.Differences.Count == 3, "room-mods-admitted-keeps-differences");
        host.Room.Dispose();
        guest.Room.Dispose();
    }

    private static void RoomLanAddress()
    {
        IPAddress address = LocalAddress();
        if (address == null)
        {
            Console.WriteLine("info room-lan skipped (no LAN address)");
            return;
        }
        // Not a local test: Create would need a lobby, a direct room does not.
        var host = new RoomEnd(Plain(), false);
        var guest = new RoomEnd(Plain(), false);
        host.Room.CreateLan(Now, " " + address + " ");
        Check(host.Room.HasLobby && host.Room.Status == "lan_waiting" && !host.Room.IsLoopback &&
              host.Room.Code.StartsWith("PSI-" + address + "-", StringComparison.Ordinal), "room-lan-hosted");
        guest.Room.Join(Now, host.Room.Code);
        bool ready = SpinRooms(() => host.Room.IsReady && guest.Room.IsReady, 5, host, guest);
        Check(ready && host.Room.Status == "lan_connected" && guest.Room.Status == "lan_connected" && !guest.Room.IsLoopback,
            "room-lan-ready");
        guest.Room.Leave();
        Check(guest.Room.Status == "idle", "room-lan-guest-left");
        Check(SpinRooms(() => host.Room.Status == "lan_waiting", 5, host, guest), "room-lan-host-waits-again");
        host.Room.Dispose();
        guest.Room.Dispose();
    }

    private static void RoomLobby()
    {
        var hub = new FakeHub();
        var host = new RoomEnd(Plain(), false, hub);
        var guest = new RoomEnd(Plain(), false, hub);
        host.Room.Create(Now);
        string code = host.Room.Code;
        Check(host.Room.HasLobby && host.Room.IsHost && host.Room.Status == "waiting" && host.Room.FriendsCanJoin &&
              !host.Room.IsLoopback && SteamCode.TryDecode(code, out _), "room-lobby-hosted");

        // A side that already has a room does not wander into another one.
        var busy = new RoomEnd(Plain(), true, hub);
        busy.Room.Create(Now);
        string busyCode = busy.Room.Code;
        busy.Room.Join(Now, code);
        busy.Room.JoinLobby(Now, hub.Next - 1);
        busy.Room.Poll(Now);
        host.Room.Poll(Now);
        Check(busy.Room.Code == busyCode && busy.Room.Status == "local_waiting" && busy.Room.IsHost &&
              host.Room.Status == "waiting" && !host.Room.CanApprove, "room-open-ignores-lobby-join");
        busy.Room.Leave();
        busy.Room.Join(Now, code);
        Check(busy.Room.IsGuest && busy.Room.Status == "await_approval" && !busy.Room.IsLoopback,
            "room-switches-carrier-after-leaving");
        busy.Room.Leave();
        host.Room.Poll(Now);
        Check(host.Room.Status == "waiting" && busy.Room.Status == "local_idle", "room-candidate-withdrew");
        busy.Room.Dispose();

        guest.Room.Join(Now, code.ToLowerInvariant());
        Check(guest.Room.IsGuest && guest.Room.Status == "await_approval", "room-lobby-awaits-approval");
        SpinRooms(() => false, 1.2, host, guest);
        Check(host.Room.Status == "approve" && host.Room.CanApprove && !host.Room.IsReady && !guest.Room.IsReady &&
              guest.Room.Status == "await_approval" && host.Room.Received == 0, "room-lobby-nothing-before-approval");
        guest.Room.Approve();
        Check(host.Room.CanApprove, "room-lobby-guest-cannot-approve");

        host.Room.Approve();
        bool ready = SpinRooms(() => host.Room.IsReady && guest.Room.IsReady, 5, host, guest);
        Check(ready && host.Room.Status == "connected" && guest.Room.Status == "connected" && !host.Room.CanApprove &&
              host.Room.PartnerName == "Partner", "room-lobby-ready");
        Check(guest.Room.Send(1, new byte[3000]) && SpinRooms(() => host.Got.Count == 1, 5, host, guest) &&
              host.Got[0] == "1:3000", "room-lobby-messages");

        // One link object lives through the reconnect; the session must not carry over.
        guest.Room.Leave();
        bool waiting = SpinRooms(() => host.Room.Status == "waiting", 5, host, guest);
        Check(waiting && !host.Room.IsReady && guest.Room.Status == "idle", "room-lobby-guest-left");
        guest.Room.JoinLobby(Now, hub.Next - 1);
        SpinRooms(() => host.Room.CanApprove, 2, host, guest);
        Check(host.Room.Status == "approve" && !host.Room.IsReady, "room-lobby-approval-needed-again");
        host.Room.Approve();
        bool again = SpinRooms(() => host.Room.IsReady && guest.Room.IsReady, 5, host, guest);
        Check(again && host.Readies == 2 && guest.Readies == 2, "room-lobby-rejoin");

        host.Room.Leave();
        bool told = SpinRooms(() => !guest.Room.HasLobby, 5, host, guest);
        Check(told && guest.Room.Status == "host_left" && host.Room.Status == "idle", "room-lobby-host-left");
        guest.Room.Join(Now, code);
        Check(guest.Room.Status == "join_failed" && !guest.Room.HasLobby, "room-lobby-gone");
        host.Room.Dispose();
        guest.Room.Dispose();
    }

    private static void RoomLobbyDecline()
    {
        var hub = new FakeHub();
        var host = new RoomEnd(Plain(), false, hub);
        var guest = new RoomEnd(Manifest(Game, "probably_coop=0.3.0", "item_details=1.4", "better_shelves=2.0"), false, hub);
        host.Room.Create(Now);
        guest.Room.Join(Now, host.Room.Code);
        SpinRooms(() => host.Room.CanApprove, 2, host, guest);
        host.Room.Approve();
        bool refused = SpinRooms(() => !guest.Room.HasLobby && host.Room.Status == "waiting", 5, host, guest);
        Check(refused && guest.Room.Status == "incompatible" && guest.Room.Reason == SessionDecline.Mods &&
              guest.Room.Differences.Count == 1 && guest.Room.Differences[0].Id == "probably_coop" &&
              guest.Room.Differences[0].Kind == ModDiffKind.Version, "room-lobby-own-version-refused");
        Check(host.Room.HasLobby && host.Readies == 0 && guest.Readies == 0 && host.Room.Reason == SessionDecline.Mods,
            "room-lobby-host-keeps-lobby");

        var good = new RoomEnd(Plain(), false, hub);
        good.Room.Join(Now, host.Room.Code);
        SpinRooms(() => host.Room.CanApprove, 2, host, good);
        host.Room.Approve();
        Check(SpinRooms(() => host.Room.IsReady && good.Room.IsReady, 5, host, good), "room-lobby-next-guest");
        host.Room.Dispose();
        guest.Room.Dispose();
        good.Room.Dispose();
    }

    private static void RoomMuteGuest()
    {
        // Twenty times real time: the negotiation limit passes in under a second while
        // the link itself stays alive.
        double origin = Now;
        double Fast() => origin + (Now - origin) * 20;
        var host = new RoomEnd(Plain());
        host.Room.Create(Fast());
        var mute = new LanTransport(Fast(), host.Room.Code);
        double openedAt = -1;
        bool handshake = false;
        double limit = Now + 4;
        while (Now < limit && host.Ends.Count == 0)
        {
            double now = Fast();
            host.Room.Poll(now);
            mute.Poll(now);
            host.Room.Flush(now);
            mute.Flush(now);
            if (mute.Link.State == NetState.Open)
            {
                if (openedAt < 0) openedAt = now;
                handshake |= host.Room.Status == "handshake";
            }
            Thread.Sleep(1);
        }
        double took = Fast() - openedAt;
        Check(openedAt >= 0 && handshake, "room-mute-guest-handshake-status");
        Check(host.Ends.Count == 1 && host.Ends[0] == NetClose.Timeout, "room-mute-guest-dropped");
        Check(took > NetSession.NegotiateTimeout - 1 && took < NetSession.NegotiateTimeout + 3, "room-mute-guest-after-limit");
        Check(host.Room.Status == "peer_timeout" && host.Room.HasLobby && host.Readies == 0, "room-mute-guest-room-survives");
        mute.Dispose();
        host.Room.Dispose();
    }
}
