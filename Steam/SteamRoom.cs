using System;
using System.Globalization;
using System.Runtime.InteropServices;
using ProbablyCoop.Core;
using Steamworks;
using UnityEngine;

namespace ProbablyCoop;

internal sealed class SteamRoom : ICoopRoom
{
    private const string LobbyKey = "probably_coop_v2_host";
    private const int ControlChannel = 27101;
    private const int CursorChannel = 27102;
    private readonly byte[] fingerprint;
    private readonly IntPtr[] incoming = new IntPtr[24];
    private readonly byte[] receiveBuffer = new byte[Wire.MaxSize];
    private readonly Callback<SteamNetworkingMessagesSessionRequest_t> sessionRequest;
    private readonly Callback<SteamNetworkingMessagesSessionFailed_t> sessionFailed;
    private CallResult<LobbyCreated_t> creating;
    private CallResult<LobbyEnter_t> joining;
    private CSteamID lobby;
    private CSteamID self;
    private CSteamID originalHost;
    private CSteamID candidate;
    private CSteamID peer;
    private bool disposed;
    private bool abandonOperation;
    private double lobbyDeadline;
    private double nextRosterCheck;
    private double nextSendError;
    private bool linkFailed;
    private bool publishedJoin;

    public CoopSession Session { get; private set; }
    public bool HasLobby => lobby != CSteamID.Nil;
    public bool Busy => creating != null || joining != null;
    public bool IsHost => HasLobby && originalHost == self;
    public bool IsGuest => joining != null || HasLobby && originalHost != self;
    public bool CanApprove => IsHost && candidate != CSteamID.Nil && peer == CSteamID.Nil;
    internal bool FriendsCanJoin => publishedJoin;
    public string Code => HasLobby ? RoomCode.Encode(lobby.m_SteamID) : "";
    public string Status { get; private set; } = "idle";
    public string Detail { get; private set; } = "";
    public string PartnerName { get; private set; } = "";
    public int Sent { get; private set; }
    public int Received { get; private set; }
    public event Action<Packet> RemoteCursor;

    private static double Now => Time.realtimeSinceStartupAsDouble;

    internal SteamRoom(byte[] fingerprint)
    {
        this.fingerprint = fingerprint;
        sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
        sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
    }

    public void Create()
    {
        if (!CanStart()) return;
        originalHost = self;
        creating = CallResult<LobbyCreated_t>.Create(OnCreated);
        var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeInvisible, 2);
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

    internal void Join(CSteamID id)
    {
        if (HasLobby || Busy || disposed) return;
        if (!id.IsLobby() || !id.IsValid())
        {
            SetStatus("bad_code");
            return;
        }
        if (!CanStart()) return;
        joining = CallResult<LobbyEnter_t>.Create(OnJoined);
        var call = SteamMatchmaking.JoinLobby(id);
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

    internal void Approve()
    {
        if (!CanApprove || !ContainsMember(candidate)) return;
        AttachPeer(candidate);
        var identity = Identity(peer);
        SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
        SetStatus("handshake");
    }

    public void Leave(string status = "idle")
    {
        abandonOperation = Busy;
        PublishFriendJoin(false);
        ClearPeer();
        candidate = CSteamID.Nil;
        Session = null;
        if (HasLobby)
        {
            var previousLobby = lobby;
            lobby = CSteamID.Nil;
            SteamMatchmaking.LeaveLobby(previousLobby);
        }
        SetStatus(status);
    }

    public void Tick(Action beforeSessionTick = null)
    {
        if (disposed) return;
        double now = Now;
        if (Busy && !abandonOperation && now >= lobbyDeadline)
            Leave("lobby_timeout");
        if (!HasLobby) return;

        if (now >= nextRosterCheck)
        {
            nextRosterCheck = now + 0.25;
            UpdateRoster();
        }
        if (!HasLobby) return;
        if (peer == CSteamID.Nil)
        {
            beforeSessionTick?.Invoke();
            Session?.Tick(now);
            return;
        }

        Drain(ControlChannel, now);
        Drain(CursorChannel, now);
        if (Session == null) return;
        beforeSessionTick?.Invoke();
        Session.Tick(now);

        if (Session.State == LinkState.Connected && Status != "connected")
            SetStatus("connected");
        if (Session.State == LinkState.Incompatible)
        {
            // Give the reliable rejection a chance to arrive before closing channels.
            if (!IsHost) Leave("incompatible");
            else if (Status != "incompatible") SetStatus("incompatible");
        }
        else if (linkFailed || Session.State == LinkState.TimedOut)
        {
            if (IsHost)
            {
                ClearPeer();
                SetStatus("peer_timeout");
            }
            else Leave("peer_timeout");
        }
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
        SteamNetworkingUtils.InitRelayNetworkAccess();
        return true;
    }

    private void StartOperation(string status)
    {
        Sent = Received = 0;
        abandonOperation = false;
        lobbyDeadline = Now + 25;
        SetStatus(status);
    }

    private void OnCreated(LobbyCreated_t result, bool ioFailure)
    {
        creating.Dispose();
        creating = null;
        if (result.m_eResult != EResult.k_EResultOK || ioFailure || result.m_ulSteamIDLobby == 0)
        {
            if (!abandonOperation && !disposed) SetStatus("create_failed", ioFailure ? "Steam I/O" : result.m_eResult.ToString());
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
        Session = MakeSession(true);
        nextRosterCheck = 0;
        PublishFriendJoin(true);
        SetStatus("waiting");
    }

    private void OnJoined(LobbyEnter_t result, bool ioFailure)
    {
        joining.Dispose();
        joining = null;
        var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
        if (response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess || ioFailure || result.m_ulSteamIDLobby == 0)
        {
            if (!abandonOperation && !disposed) SetStatus("join_failed", ioFailure ? "Steam I/O" : response.ToString());
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
        Session = MakeSession(false);
        nextRosterCheck = 0;
        AttachPeer(originalHost);
        SetStatus("await_approval");
    }

    private CoopSession MakeSession(bool host)
    {
        var result = new CoopSession(host, fingerprint, Send);
        result.RemoteCursor += packet => RemoteCursor?.Invoke(packet);
        result.Received += kind =>
        {
            Received++;
            if (kind is Kind.Hello or Kind.Welcome or Kind.Ready or Kind.State or Kind.ChangeTile)
                Debug.Log($"[probably_coop] rx {kind} state={result.State} rev={result.Revision}");
        };
        return result;
    }

    private void UpdateRoster()
    {
        if (SteamMatchmaking.GetLobbyOwner(lobby) != originalHost || !ContainsMember(self))
        {
            Leave("host_left");
            return;
        }
        if (!IsHost) return;

        var next = CSteamID.Nil;
        int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (int i = 0; i < count; i++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            if (member == self) continue;
            next = member;
            break;
        }
        if (candidate != next)
        {
            if (peer != CSteamID.Nil && peer != next) ClearPeer();
            candidate = next;
            PartnerName = NameOf(candidate);
            PublishFriendJoin(candidate == CSteamID.Nil);
            SetStatus(candidate == CSteamID.Nil ? "waiting" : "approve");
        }
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

    private void AttachPeer(CSteamID id)
    {
        peer = id;
        PartnerName = NameOf(id);
        Session.Connect(Now);
        linkFailed = false;
    }

    private void ClearPeer()
    {
        var previous = peer;
        peer = CSteamID.Nil;
        Session?.DropPeer();
        if (previous == CSteamID.Nil) return;
        var identity = Identity(previous);
        SteamNetworkingMessages.CloseChannelWithUser(ref identity, ControlChannel);
        SteamNetworkingMessages.CloseChannelWithUser(ref identity, CursorChannel);
        Debug.Log($"[probably_coop] link closed, tx={Sent}, rx={Received}");
    }

    private static string NameOf(CSteamID id)
    {
        if (id == CSteamID.Nil) return "";
        var name = SteamFriends.GetFriendPersonaName(id) ?? "";
        return name.Length > 48 ? name.Substring(0, 48) : name;
    }

    private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
    {
        var identity = request.m_identityRemote;
        if (peer != CSteamID.Nil && identity.GetSteamID() == peer && ContainsMember(peer))
            SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
    }

    private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failure)
    {
        if (peer == CSteamID.Nil || failure.m_info.m_identityRemote.GetSteamID() != peer) return;
        // Before approval the guest can see an initial failure. The Hello retry can restart that channel.
        if (Session?.State == LinkState.Connected) linkFailed = true;
        Debug.Log($"[probably_coop] Steam session failure, reason={failure.m_info.m_eEndReason}");
    }

    private bool Send(byte[] bytes, bool cursor)
    {
        if (peer == CSteamID.Nil || !ContainsMember(peer)) return false;
        var identity = Identity(peer);
        var data = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            int flags = cursor ? Constants.k_nSteamNetworkingSend_UnreliableNoDelay :
                Constants.k_nSteamNetworkingSend_ReliableNoNagle | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
            var result = SteamNetworkingMessages.SendMessageToUser(ref identity, data.AddrOfPinnedObject(),
                (uint)bytes.Length, flags, cursor ? CursorChannel : ControlChannel);
            if (result == EResult.k_EResultOK)
            {
                Sent++;
                return true;
            }
            if (!cursor && Now >= nextSendError)
            {
                nextSendError = Now + 2;
                Debug.Log($"[probably_coop] Steam send: {result}");
            }
            return false;
        }
        finally { data.Free(); }
    }

    private void Drain(int channel, double now)
    {
        int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, incoming, incoming.Length);
        try
        {
            for (int i = 0; i < count; i++)
            {
                var message = SteamNetworkingMessage_t.FromIntPtr(incoming[i]);
                if (message.m_identityPeer.GetSteamID() != peer || !ContainsMember(peer) ||
                    message.m_cbSize < Wire.HeaderSize || message.m_cbSize > Wire.MaxSize ||
                    message.m_pData == IntPtr.Zero) continue;
                Marshal.Copy(message.m_pData, receiveBuffer, 0, message.m_cbSize);
                if (Session.Receive(receiveBuffer.AsSpan(0, message.m_cbSize), channel == CursorChannel, now)
                    && channel == CursorChannel) Received++;
            }
        }
        finally
        {
            for (int i = 0; i < count; i++)
            {
                SteamNetworkingMessage_t.Release(incoming[i]);
                incoming[i] = IntPtr.Zero;
            }
        }
    }

    private static SteamNetworkingIdentity Identity(CSteamID user)
    {
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID(user);
        return identity;
    }

    private void SetStatus(string status, string detail = "")
    {
        if (status != Status || detail != Detail) Debug.Log($"[probably_coop] {status} {detail}");
        Status = status;
        Detail = detail;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        sessionRequest.Dispose();
        sessionFailed.Dispose();
        // Steam cannot cancel lobby creation/entry. Pending one-shot callbacks only leave a late lobby.
        Leave();
    }
}
