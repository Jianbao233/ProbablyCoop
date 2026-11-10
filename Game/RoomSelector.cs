using System;
using ProbablyCoop.Core;
using Steamworks;

namespace ProbablyCoop;

internal sealed class RoomSelector : ICoopRoom
{
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
    public string Code => active?.Code ?? "";
    public string Status => active?.Status ?? selectionStatus;
    public string Detail => active?.Detail ?? "";
    public string PartnerName => active?.PartnerName ?? "";
    internal bool IsLoopback => active is LocalRoom local && local.IsLoopback;
    internal bool FriendsCanJoin => active is SteamRoom steam && steam.FriendsCanJoin;
    public int Sent => active?.Sent ?? 0;
    public int Received => active?.Received ?? 0;
    public event Action<Packet> RemoteCursor;

    public void Create()
    {
        if (Select(localTest)) active.Create();
        NativeRoomPacing.Update(this);
    }

    internal void CreateLan(string address)
    {
        if (Select(direct: true)) ((LocalRoom)active).CreateLan(address);
        NativeRoomPacing.Update(this);
    }

    internal void Join(string input)
    {
        string code = (input ?? "").Trim();
        bool direct = code.StartsWith("PSL-", StringComparison.Ordinal) ||
            code.StartsWith("PSI-", StringComparison.Ordinal);
        if (direct)
        {
            if (Select(true)) ((LocalRoom)active).Join(code);
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

    internal void Approve() => (active as SteamRoom)?.Approve();

    private bool Select(bool direct)
    {
        if (HasLobby || Busy) return false;
        if (active != null && (active is LocalRoom) == direct) return true;
        DisposeActive();
        if (!direct && !SteamManager.Initialized)
        {
            selectionStatus = "steam_unavailable";
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
