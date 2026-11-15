using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using ProbablyCoop.Core;

namespace ProbablyCoop.Net;

// What a room needs from whatever carries its link: a LAN socket or a Steam lobby.
// Status is a localization key suffix ("status_" + Status).
internal interface INetCarrier : IDisposable
{
    // Null while the carrier has nothing open.
    NetLink Link { get; }
    bool HasLobby { get; }
    bool Busy { get; }
    bool IsHost { get; }
    bool IsGuest { get; }
    bool CanApprove { get; }
    bool FriendsCanJoin { get; }
    string Code { get; }
    string Status { get; }
    string Detail { get; }
    string PartnerName { get; }
    int Sent { get; }
    int Received { get; }
    void Approve();
    void Leave(string status = "idle");
    void Poll(double now);
    void Flush(double now);
}

// A carrier whose rooms are found through a lobby service.
internal interface INetLobby : INetCarrier
{
    void Create(double now);
    void Join(double now, ulong lobby);
}

// Direct UDP rooms: loopback for two processes on one machine, or one LAN address.
internal sealed class LanCarrier : INetCarrier
{
    private LanTransport transport;
    private bool opened;

    public NetLink Link => transport?.Link;
    public bool HasLobby => transport != null;
    public bool Busy => false;
    public bool IsHost => transport != null && transport.IsHost;
    public bool IsGuest => transport != null && !transport.IsHost;
    public bool CanApprove => false;
    public bool FriendsCanJoin => false;
    public string Code => transport == null ? "" : transport.Code;
    public string Status { get; private set; } = "idle";
    public string Detail { get; private set; } = "";
    public string PartnerName => transport == null ? "" : transport.IsHost ? "Guest" : "Host";
    public int Sent => transport == null ? 0 : transport.Sent;
    public int Received => transport == null ? 0 : transport.Received;
    internal bool IsLoopback => transport != null && !transport.Lan;
    internal IPEndPoint Ipv4HostEndpoint => IsHost && transport.Lan ? transport.LocalEndpoint : null;

    // address == null hosts on loopback; otherwise on that IPv4 interface.
    internal void Host(double now, string address, string port = null)
    {
        if (transport != null) return;
        IPAddress bind = null;
        if (address != null && !DirectRoomEndpoint.TryParseIpv4(address.Trim(), out bind))
        {
            Set("bad_address");
            return;
        }
        int listenPort = 0;
        if (!string.IsNullOrWhiteSpace(port) && !DirectRoomEndpoint.TryParsePort(port.Trim(), out listenPort))
        {
            Set("bad_udp_port");
            return;
        }
        if (Open(now, null, bind, "bad_address", "create_failed", listenPort))
            Set(transport.Lan ? "lan_waiting" : "local_waiting");
    }

    internal void Join(double now, string code, string mappedAddress)
    {
        if (transport != null) return;
        IPEndPoint mappedEndpoint = null;
        if (LanTransport.IsIpv4Code(code) && !string.IsNullOrWhiteSpace(mappedAddress) &&
            !DirectRoomEndpoint.TryParseMappedEndpoint(mappedAddress.Trim(), out mappedEndpoint))
        {
            Set("bad_mapping_address");
            return;
        }
        if (Open(now, code ?? "", null, "bad_code", "join_failed", mappedEndpoint: mappedEndpoint)) Set("handshake");
    }

    public void Approve() { }

    public void Leave(string status = "idle")
    {
        transport?.Dispose();
        transport = null;
        opened = false;
        Set(status);
    }

    public void Poll(double now)
    {
        transport?.Poll(now);
        Settle();
    }

    public void Flush(double now)
    {
        transport?.Flush(now);
        Settle();
    }

    public void Dispose() => Leave();

    private bool Open(double now, string code, IPAddress bind, string malformed, string failed,
        int listenPort = 0, IPEndPoint mappedEndpoint = null)
    {
        try
        {
            transport = new LanTransport(now, code, bind, listenPort, mappedEndpoint);
            opened = false;
            return true;
        }
        catch (FormatException) { Set(malformed); }
        catch (SocketException exception)
        {
            if (listenPort != 0 && exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
                Set("udp_port_in_use");
            else Set(failed, exception.SocketErrorCode.ToString());
        }
        return false;
    }

    private void Settle()
    {
        if (transport == null) return;
        NetLink link = transport.Link;
        if (link.State == NetState.Open)
        {
            if (opened) return;
            opened = true;
            Set(transport.Lan ? "lan_connected" : "local_connected");
            return;
        }
        if (link.State != NetState.Closed || link.Reason == NetClose.Local) return;
        if (transport.IsHost)
        {
            // The transport listens again on its next poll.
            opened = false;
            Set(link.Reason == NetClose.Timeout ? "peer_timeout" : transport.Lan ? "lan_waiting" : "local_waiting");
        }
        else if (link.Reason == NetClose.Refused) Leave("incompatible");
        else if (link.Reason == NetClose.Remote) Leave("host_left");
        else Leave("peer_timeout");
    }

    private void Set(string status, string detail = "")
    {
        Status = status;
        Detail = detail;
    }
}

// One co-op room as the game sees it: picks the carrier, runs the session negotiation
// on its link and reports a single status. Knows nothing about the game.
//
// Each frame: Poll (handlers run), game logic, Flush.
internal sealed class NetRoom : IDisposable
{
    private readonly Func<SessionManifest> manifest;
    private readonly Func<INetLobby> lobbies;
    private readonly bool localTest;
    private INetCarrier carrier;
    private NetSession session;
    private NetLink bound;
    private string notice;

    // lobbies returns null when the lobby service is unavailable. localTest makes
    // Create open a loopback room instead of a lobby.
    internal NetRoom(Func<SessionManifest> manifest, Func<INetLobby> lobbies, bool localTest)
    {
        this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        this.lobbies = lobbies;
        this.localTest = localTest;
    }

    internal bool HasLobby => carrier != null && carrier.HasLobby;
    internal bool Busy => carrier != null && carrier.Busy;
    internal bool IsHost => carrier != null && carrier.IsHost;
    internal bool IsGuest => carrier != null && carrier.IsGuest;
    internal bool CanApprove => carrier != null && carrier.CanApprove;
    internal bool FriendsCanJoin => carrier != null && carrier.FriendsCanJoin;
    internal bool IsLoopback => carrier is LanCarrier lan && lan.IsLoopback;
    internal IPEndPoint Ipv4HostEndpoint => (carrier as LanCarrier)?.Ipv4HostEndpoint;
    internal string Code => carrier == null ? "" : carrier.Code;
    internal string Detail => carrier == null || Showing(notice) ? "" : carrier.Detail;
    internal string PartnerName => carrier == null ? "" : carrier.PartnerName;
    internal int Sent => carrier == null ? 0 : carrier.Sent;
    internal int Received => carrier == null ? 0 : carrier.Received;

    internal string Status
    {
        get
        {
            if (Showing(notice)) return notice;
            string status = carrier == null ? "idle" : carrier.Status;
            // The carrier calls an open link connected; the room is not until both
            // sides agreed on what they run.
            if (bound != null && bound.State == NetState.Open && !IsReady) status = "handshake";
            return status == "idle" && localTest ? "local_idle" : status;
        }
    }

    internal bool IsReady => session != null && session.State == SessionState.Ready;
    internal byte Peer => session == null ? (byte)0 : session.Peer;
    internal SessionManifest Remote => session?.Remote;

    // Outcome of the last negotiation, kept after the room is left.
    internal SessionDecline Reason { get; private set; }
    internal List<ModDiff> Differences { get; private set; } = new List<ModDiff>();
    internal int DifferenceCount { get; private set; }

    // Host only: decides about a guest whose mods differ. Unset admits identical sets.
    internal Func<SessionManifest, List<ModDiff>, bool> Admit { get; set; }
    internal Action Ready;
    internal Action<NetClose> Ended;
    internal Action<byte, byte[]> Message;
    internal event Action<byte, byte[]> Datagram;

    internal void Create(double now)
    {
        if (!Begin()) return;
        if (localTest) Lan().Host(now, null);
        else Lobby()?.Create(now);
        Bind();
    }

    internal void CreateLan(double now, string address, string port = null)
    {
        if (!Begin()) return;
        Lan().Host(now, address ?? "", port);
        Bind();
    }

    // Accepts a direct code (PSL-/PSI-) or a lobby code (PS1-).
    internal void Join(double now, string input, bool loopbackOnly = false, string mappedAddress = null)
    {
        if (!Begin()) return;
        input = (input ?? "").Trim();
        if (loopbackOnly && !LanTransport.IsLoopbackCode(input))
        {
            notice = "local_code_required";
            return;
        }
        if (LanTransport.IsCode(input)) Lan().Join(now, input, mappedAddress);
        else if (SteamCode.TryDecode(input, out ulong lobby)) Lobby()?.Join(now, lobby);
        else notice = "bad_code";
        Bind();
    }

    internal void JoinLobby(double now, ulong lobby)
    {
        if (!Begin()) return;
        Lobby()?.Join(now, lobby);
        Bind();
    }

    internal void Approve() => carrier?.Approve();

    internal void Leave(string status = "idle")
    {
        notice = null;
        carrier?.Leave(status);
        Bind();
    }

    internal void Poll(double now)
    {
        carrier?.Poll(now);
        Bind();
    }

    internal void Flush(double now)
    {
        session?.Tick(now);
        carrier?.Flush(now);
        Bind();
    }

    internal bool Send(byte channel, byte[] message) => session != null && session.Send(channel, message);

    internal bool SendLoose(byte channel, byte[] payload) => session != null && session.SendLoose(channel, payload);

    public void Dispose()
    {
        carrier?.Dispose();
        carrier = null;
        Bind();
    }

    private bool Showing(string text) => text != null && !HasLobby && !Busy;

    private bool Begin()
    {
        if (HasLobby || Busy) return false;
        notice = null;
        Reason = SessionDecline.None;
        Differences = new List<ModDiff>();
        DifferenceCount = 0;
        return true;
    }

    private LanCarrier Lan()
    {
        if (carrier is LanCarrier lan) return lan;
        carrier?.Dispose();
        lan = new LanCarrier();
        carrier = lan;
        return lan;
    }

    private INetLobby Lobby()
    {
        if (carrier is INetLobby lobby) return lobby;
        lobby = lobbies?.Invoke();
        if (lobby == null)
        {
            notice = "steam_unavailable";
            return null;
        }
        carrier?.Dispose();
        carrier = lobby;
        return lobby;
    }

    // Keeps one session on whatever link the carrier currently has. Runs right after
    // every carrier call, so a new link has its session before it receives anything.
    private void Bind()
    {
        NetLink link = carrier?.Link;
        if (ReferenceEquals(link, bound)) return;
        bound = link;
        session = null;
        if (link == null) return;
        var next = new NetSession(link, manifest());
        // Identical content needs no decision.
        next.Admit = (remote, differences) => differences.Count == 0 || Admit != null && Admit(remote, differences);
        next.Ready = () =>
        {
            Keep(next);
            Ready?.Invoke();
        };
        next.Declined = _ =>
        {
            Keep(next);
            // Neither carrier reacts to a link its own side closed, so leave here.
            if (!next.IsHost) carrier?.Leave("incompatible");
        };
        next.Ended = reason => Ended?.Invoke(reason);
        next.Message = (channel, message) => Message?.Invoke(channel, message);
        next.Datagram = (channel, payload) => Datagram?.Invoke(channel, payload);
        session = next;
    }

    private void Keep(NetSession from)
    {
        Reason = from.Reason;
        Differences = from.Differences;
        DifferenceCount = from.DifferenceCount;
    }
}
