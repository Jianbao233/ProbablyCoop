using System;
using System.Diagnostics;
using System.Globalization;
using ProbablyCoop.Net;
using Steamworks;
using Debug = UnityEngine.Debug;

namespace ProbablyCoop;

// Steam carrier for one NetLink: an invisible two-member lobby finds the partner, the
// host approves them by name, and link frames travel as Steam P2P packets.
//
// Only the managed-array P2P calls are used (SendP2PPacket / ReadP2PPacket), so this
// file needs the "steam" capability and nothing else. Every frame is sent unreliable:
// ordering, retransmission and fragmentation belong to NetLink, exactly as on UDP.
//
// UNVERIFIED IN GAME: written against Steamworks.NET in build 26525552 and compiled,
// never run with two Steam accounts.
internal sealed class SteamTransport : INetLobby
{
    // Old 0.1.0 rooms use "probably_coop_v2_host"; the two versions refuse each other's
    // lobbies as wrong_lobby instead of failing later in the handshake.
    private const string LobbyKey = "probably_coop_v3_host";
    private const int Channel = 27103;
    // Unreliable P2P packets are limited to 1200 bytes.
    internal const int MaxFrame = 1100;
    private const int MinFrame = 6;
    private const int BaseDrain = 24;
    private const int MaxDrain = 128;

    private readonly byte[] frame = new byte[MaxFrame];
    private readonly Callback<P2PSessionRequest_t> sessionRequest;
    private readonly Callback<P2PSessionConnectFail_t> sessionFailed;
    private CallResult<LobbyCreated_t> creating;
    private CallResult<LobbyEnter_t> joining;
    private CSteamID lobby;
    private CSteamID self;
    private CSteamID originalHost;
    private CSteamID candidate;
    private CSteamID peer;
    private bool disposed;
    private bool abandonOperation;
    private bool opened;
    private bool publishedJoin;
    private double clock;
    private double lobbyDeadline;
    private double nextRosterCheck;
    private double nextSendError;

    internal SteamTransport()
    {
        Link = new NetLink(Send, MaxFrame);
        sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
        sessionFailed = Callback<P2PSessionConnectFail_t>.Create(OnSessionFailed);
    }

    public NetLink Link { get; }
    public bool HasLobby => lobby != CSteamID.Nil;
    public bool Busy => creating != null || joining != null;
    public bool IsHost => HasLobby && originalHost == self;
    public bool IsGuest => joining != null || HasLobby && originalHost != self;
    public bool CanApprove => IsHost && candidate != CSteamID.Nil && peer == CSteamID.Nil;
    public bool FriendsCanJoin => publishedJoin;
    public string Code => HasLobby ? SteamCode.Encode(lobby.m_SteamID) : "";
    public string Status { get; private set; } = "idle";
    public string Detail { get; private set; } = "";
    public string PartnerName { get; private set; } = "";
    public int Sent { get; private set; }
    public int Received { get; private set; }

    public void Create(double now)
    {
        clock = now;
        if (!CanStart()) return;
        originalHost = self;
        creating = CallResult<LobbyCreated_t>.Create(OnCreated);
        SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeInvisible, 2);
        if (call == SteamAPICall_t.Invalid)
        {
            creating.Dispose();
            creating = null;
            SetStatus("create_failed", "InvalidCall");
            return;
        }
        creating.Set(call);
        StartOperation("creating");
    }

    public void Join(double now, ulong id) => Join(now, new CSteamID(id));

    internal void Join(double now, CSteamID id)
    {
        clock = now;
        if (HasLobby || Busy || disposed) return;
        if (!id.IsLobby() || !id.IsValid())
        {
            SetStatus("bad_code");
            return;
        }
        if (!CanStart()) return;
        joining = CallResult<LobbyEnter_t>.Create(OnJoined);
        SteamAPICall_t call = SteamMatchmaking.JoinLobby(id);
        if (call == SteamAPICall_t.Invalid)
        {
            joining.Dispose();
            joining = null;
            SetStatus("join_failed", "InvalidCall");
            return;
        }
        joining.Set(call);
        StartOperation("joining");
    }

    // The host lets the lobby member in. Until then their packets are not accepted.
    public void Approve()
    {
        if (!CanApprove || !ContainsMember(candidate)) return;
        peer = candidate;
        PartnerName = NameOf(peer);
        opened = false;
        SteamNetworking.AcceptP2PSessionWithUser(peer);
        SetStatus("handshake");
    }

    public void Leave(string status = "idle")
    {
        abandonOperation = Busy;
        PublishFriendJoin(false);
        // Close while the peer is still set so the Bye frames go out.
        Link.Close();
        DropPeer();
        candidate = CSteamID.Nil;
        if (HasLobby)
        {
            CSteamID previous = lobby;
            lobby = CSteamID.Nil;
            SteamMatchmaking.LeaveLobby(previous);
        }
        SetStatus(status);
    }

    // Reads pending packets and hands them to the link. Message handlers run in here.
    public void Poll(double now)
    {
        if (disposed) return;
        clock = now;
        if (Busy && !abandonOperation && now >= lobbyDeadline) Leave("lobby_timeout");
        if (!HasLobby) return;

        if (now >= nextRosterCheck)
        {
            nextRosterCheck = now + 0.25;
            UpdateRoster();
            if (!HasLobby) return;
        }
        Drain(now);
        if (!HasLobby) return;
        Settle(now);
    }

    // Runs link timers and sends what is queued.
    public void Flush(double now)
    {
        if (disposed || !HasLobby) return;
        clock = now;
        Link.Tick(now);
        Settle(now);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        sessionRequest.Dispose();
        sessionFailed.Dispose();
        // Steam cannot cancel lobby creation/entry. A pending one-shot callback only
        // leaves the late lobby again.
        Leave();
    }

    // Reacts to what the link did: reports the open connection, lets a host room wait
    // for the next partner, and takes a guest out of a room whose host is gone.
    private void Settle(double now)
    {
        if (Link.State == NetState.Open)
        {
            if (opened) return;
            opened = true;
            SetStatus("connected");
            return;
        }
        if (Link.State != NetState.Closed || Link.Reason == NetClose.Local) return;

        NetClose reason = Link.Reason;
        if (IsHost)
        {
            // A host room outlives one guest connection. The partner has to be approved
            // again before the next connection.
            DropPeer();
            Link.Listen();
            if (reason == NetClose.Remote) SetStatus(candidate == CSteamID.Nil ? "waiting" : "approve");
            else SetStatus("peer_timeout");
            return;
        }
        if (reason == NetClose.Refused) Leave("incompatible");
        else if (!opened && reason == NetClose.Timeout)
        {
            // Still waiting for the host to approve: keep knocking while the lobby exists.
            Link.Connect(now);
        }
        else Leave(reason == NetClose.Remote ? "host_left" : "peer_timeout");
    }

    private bool CanStart()
    {
        if (HasLobby || Busy || disposed) return false;
        if (!SteamManager.Initialized || !SteamUser.BLoggedOn())
        {
            SetStatus("steam_offline");
            return false;
        }
        self = SteamUser.GetSteamID();
        if (self == CSteamID.Nil)
        {
            SetStatus("steam_offline");
            return false;
        }
        SteamNetworking.AllowP2PPacketRelay(true);
        return true;
    }

    private void StartOperation(string status)
    {
        Sent = Received = 0;
        abandonOperation = false;
        lobbyDeadline = clock + 25;
        SetStatus(status);
    }

    private void OnCreated(LobbyCreated_t result, bool ioFailure)
    {
        creating.Dispose();
        creating = null;
        if (result.m_eResult != EResult.k_EResultOK || ioFailure || result.m_ulSteamIDLobby == 0)
        {
            if (!abandonOperation && !disposed)
                SetStatus("create_failed", ioFailure ? "Steam I/O" : result.m_eResult.ToString());
            return;
        }
        var id = new CSteamID(result.m_ulSteamIDLobby);
        if (abandonOperation || disposed)
        {
            SteamMatchmaking.LeaveLobby(id);
            return;
        }
        lobby = id;
        if (!SteamMatchmaking.SetLobbyData(lobby, LobbyKey, self.m_SteamID.ToString(CultureInfo.InvariantCulture)))
        {
            Leave("metadata_failed");
            return;
        }
        opened = false;
        Link.Listen();
        nextRosterCheck = 0;
        PublishFriendJoin(true);
        SetStatus("waiting");
    }

    private void OnJoined(LobbyEnter_t result, bool ioFailure)
    {
        joining.Dispose();
        joining = null;
        var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
        if (response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess || ioFailure ||
            result.m_ulSteamIDLobby == 0)
        {
            if (!abandonOperation && !disposed)
                SetStatus("join_failed", ioFailure ? "Steam I/O" : response.ToString());
            return;
        }
        var id = new CSteamID(result.m_ulSteamIDLobby);
        if (abandonOperation || disposed)
        {
            SteamMatchmaking.LeaveLobby(id);
            return;
        }
        lobby = id;
        originalHost = SteamMatchmaking.GetLobbyOwner(lobby);
        string marker = SteamMatchmaking.GetLobbyData(lobby, LobbyKey);
        if (originalHost == CSteamID.Nil || originalHost == self ||
            marker != originalHost.m_SteamID.ToString(CultureInfo.InvariantCulture))
        {
            Leave("wrong_lobby");
            return;
        }
        peer = originalHost;
        PartnerName = NameOf(peer);
        opened = false;
        Link.Connect(clock);
        nextRosterCheck = 0;
        SetStatus("await_approval");
    }

    private void UpdateRoster()
    {
        if (SteamMatchmaking.GetLobbyOwner(lobby) != originalHost || !ContainsMember(self))
        {
            Link.Abort(NetClose.Remote);
            Leave("host_left");
            return;
        }
        if (!IsHost) return;

        CSteamID next = CSteamID.Nil;
        int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (int i = 0; i < count; i++)
        {
            CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            if (member == self) continue;
            next = member;
            break;
        }
        if (candidate == next) return;
        // The approved partner left the lobby: the link ends without waiting for silence.
        if (peer != CSteamID.Nil && peer != next) Link.Abort(NetClose.Remote);
        candidate = next;
        PartnerName = NameOf(candidate);
        PublishFriendJoin(candidate == CSteamID.Nil);
        if (peer == CSteamID.Nil) SetStatus(candidate == CSteamID.Nil ? "waiting" : "approve");
    }

    private void PublishFriendJoin(bool available)
    {
        if (publishedJoin == available) return;
        if (!available)
        {
            publishedJoin = false;
            if (SteamManager.Initialized) SteamFriends.SetRichPresence("connect", "");
            return;
        }
        publishedJoin = SteamFriends.SetRichPresence("connect", SteamJoinInbox.ConnectString(lobby));
        Debug.Log(publishedJoin
            ? "[probably_coop] Steam friend join advertised"
            : "[probably_coop] Steam friend join unavailable; room code remains available");
    }

    private bool ContainsMember(CSteamID id)
    {
        if (!HasLobby || id == CSteamID.Nil) return false;
        int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (int i = 0; i < count; i++)
            if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == id) return true;
        return false;
    }

    private void DropPeer()
    {
        CSteamID previous = peer;
        peer = CSteamID.Nil;
        opened = false;
        if (previous == CSteamID.Nil) return;
        if (SteamManager.Initialized) SteamNetworking.CloseP2PSessionWithUser(previous);
        Debug.Log($"[probably_coop] link closed, tx={Sent}, rx={Received}");
    }

    private static string NameOf(CSteamID id)
    {
        if (id == CSteamID.Nil) return "";
        string name = SteamFriends.GetFriendPersonaName(id) ?? "";
        return name.Length > 48 ? name.Substring(0, 48) : name;
    }

    private void OnSessionRequest(P2PSessionRequest_t request)
    {
        if (peer != CSteamID.Nil && request.m_steamIDRemote == peer && ContainsMember(peer))
            SteamNetworking.AcceptP2PSessionWithUser(peer);
    }

    private void OnSessionFailed(P2PSessionConnectFail_t failure)
    {
        if (peer == CSteamID.Nil || failure.m_steamIDRemote != peer) return;
        Debug.Log($"[probably_coop] Steam session failure, reason={(EP2PSessionError)failure.m_eP2PSessionError}");
        // Before approval the guest sees an initial failure; the Hello retry restarts
        // the session. An open link has lost its route.
        if (Link.State == NetState.Open) Link.Abort(NetClose.Timeout);
    }

    private bool Send(byte[] bytes, bool reliable)
    {
        if (peer == CSteamID.Nil) return false;
        // Unreliable is held by Steam while the route is being set up; NoDelay is
        // dropped instead, which is right for pointer positions.
        EP2PSend mode = reliable ? EP2PSend.k_EP2PSendUnreliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
        if (SteamNetworking.SendP2PPacket(peer, bytes, (uint)bytes.Length, mode, Channel))
        {
            Sent++;
            return true;
        }
        if (reliable && clock >= nextSendError)
        {
            nextSendError = clock + 2;
            Debug.Log("[probably_coop] Steam send failed");
        }
        return false;
    }

    private void Drain(double now)
    {
        long start = Stopwatch.GetTimestamp();
        for (int count = 0; count < MaxDrain && HasLobby &&
             SteamNetworking.IsP2PPacketAvailable(out uint size, Channel); count++)
        {
            // Past the base budget, keep draining for at most 3 ms in total.
            if (count >= BaseDrain && Stopwatch.GetTimestamp() - start >= Stopwatch.Frequency / 333) break;
            if (!SteamNetworking.ReadP2PPacket(frame, (uint)frame.Length, out uint read, out CSteamID sender, Channel))
                break;
            if (size > frame.Length || read < MinFrame || read > frame.Length) continue;
            if (peer == CSteamID.Nil || sender != peer) continue;
            if (Link.Receive(frame, (int)read, now)) Received++;
        }
    }

    private void SetStatus(string status, string detail = "")
    {
        if (status != Status || detail != Detail) Debug.Log($"[probably_coop] {status} {detail}");
        Status = status;
        Detail = detail;
    }
}
