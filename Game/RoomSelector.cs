using System;
using System.Net;
using ProbablyCoop.Core;
using Steamworks;
using UnityEngine;

namespace ProbablyCoop;

internal sealed class RoomSelector : ICoopRoom
{
    internal enum TransportKind : byte { None, Steam, Ipv4, Loopback }

    private readonly byte[] fingerprint;
    private readonly bool localTest;
    private ICoopRoom active;
    private string selectionStatus = "idle";

    internal RoomSelector(byte[] fingerprint, bool localTest)
    {
        this.fingerprint = fingerprint;
        this.localTest = localTest;
    }

    public CoopSession Session => active?.Session;
    public bool HasLobby => active?.HasLobby == true;
    public bool Busy => active?.Busy == true;
    public bool IsHost => active?.IsHost == true;
    public bool IsGuest => active?.IsGuest == true;
    public bool CanApprove => active?.CanApprove == true;
    public bool AcceptingJoins => active?.AcceptingJoins == true;
    public string Code => active?.Code ?? "";
    public string Status => active?.Status ?? selectionStatus;
    public string Detail => active?.Detail ?? "";
    public string PartnerName => active?.PartnerName ?? "";
    internal bool IsLoopback => active is LocalRoom local && local.IsLoopback;
    internal TransportKind Transport => !HasLobby && !Busy ? TransportKind.None : active switch
    {
        LocalRoom local when local.IsLoopback => TransportKind.Loopback,
        LocalRoom => TransportKind.Ipv4,
        SteamRoom => TransportKind.Steam,
        _ => TransportKind.None
    };
    internal IPEndPoint Ipv4HostEndpoint => (active as LocalRoom)?.Ipv4HostEndpoint;
    internal bool CanReloadInShop => IsHost && Session is { WorldReloadPending: false } connection &&
        (connection.State == LinkState.Waiting ||
            (connection.State == LinkState.Connected && connection.Native.FullRevision != 0));
    internal bool FriendsCanJoin => active is SteamRoom steam && steam.FriendsCanJoin;
    internal (int Ping, float IncomingDelivery, float OutgoingDelivery, int SendCapacity)? SteamQuality() =>
        (active as SteamRoom)?.ConnectionQuality();
    public int Sent => active?.Sent ?? 0;
    public int Received => active?.Received ?? 0;
    public event Action<Packet> RemoteCursor;

    public void Create()
    {
        if (CanHost() && Select(localTest)) active.Create();
        NativeRoomPacing.Update(this);
    }

    internal void CreateLan(string address, string port = null)
    {
        if (CanHost() && Select(direct: true)) ((LocalRoom)active).CreateLan(address, port);
        NativeRoomPacing.Update(this);
    }

    internal void Join(string input, string mappedAddress = null)
    {
        string code = (input ?? "").Trim();
        bool direct = code.StartsWith("PSL-", StringComparison.Ordinal) ||
            code.StartsWith("PSI-", StringComparison.Ordinal);
        if (direct)
        {
            if (Select(true)) ((LocalRoom)active).Join(code, mappedAddress);
            NativeRoomPacing.Update(this);
            return;
        }
        if (!RoomCode.TryDecode(code, out ulong id))
        {
            if (!HasLobby && !Busy)
            {
                DisposeActive();
                selectionStatus = "bad_code";
            }
            return;
        }
        JoinSteam(new CSteamID(id));
    }

    internal void JoinSteam(CSteamID id)
    {
        if (!Select(false)) return;
        ((SteamRoom)active).Join(id);
        NativeRoomPacing.Update(this);
    }

    public void Approve() => active?.Approve();
    public void Decline() => active?.Decline();
    public void SetAcceptingJoins(bool enabled) => active?.SetAcceptingJoins(enabled);

    private bool CanHost()
    {
        if (HasLobby || Busy) return false;
        if (NativePlatform.SupportsHosting) return true;
        DisposeActive();
        selectionStatus = "android_guest_only";
        return false;
    }

    private bool Select(bool direct)
    {
        if (HasLobby || Busy) return false;
        if (active != null && (active is LocalRoom) == direct) return true;
        DisposeActive();
        if (!direct && (NativePlatform.IsAndroid || !SteamManager.Initialized))
        {
            selectionStatus = NativePlatform.IsAndroid ? "android_no_steam" : "steam_unavailable";
            return false;
        }

        active = direct ? (ICoopRoom)new LocalRoom(fingerprint) : new SteamRoom(fingerprint);
        active.RemoteCursor += ForwardCursor;
        return true;
    }

    private void ForwardCursor(Packet packet) => RemoteCursor?.Invoke(packet);

    public void Leave(string status = "idle")
    {
        active?.Leave(status);
        NativeRoomPacing.Update(this);
    }

    public void Tick(Action beforeSessionTick = null)
    {
        active?.Tick(beforeSessionTick);
        NativeRoomPacing.Update(this);
    }

    internal void AnnounceSceneLoad()
    {
        if (IsHost && Session?.BeginWorldReload(Time.realtimeSinceStartupAsDouble) == true)
            Tick();
    }

    private void DisposeActive()
    {
        if (active == null) return;
        active.RemoteCursor -= ForwardCursor;
        active.Dispose();
        active = null;
    }

    public void Dispose()
    {
        DisposeActive();
        NativeRoomPacing.Update(null);
    }
}
