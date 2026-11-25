using System;
using System.Net;
using System.Net.Sockets;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed class LocalRoom : ICoopRoom
{
    private readonly byte[] fingerprint;
    private LoopbackLink link;
    private bool guest;
    private bool lan;

    public CoopSession Session => link?.Session;
    public bool HasLobby => link != null;
    public bool Busy => false;
    public bool IsHost => HasLobby && !guest;
    public bool IsGuest => HasLobby && guest;
    public bool CanApprove => IsHost && Session.JoinPending;
    public bool AcceptingJoins => IsHost && Session.AcceptingJoins;
    public string Code => link?.Code ?? "";
    internal bool IsLoopback => link != null && !lan;
    internal IPEndPoint Ipv4HostEndpoint => IsHost && lan ? link.LocalEndpoint : null;
    public string Status { get; private set; } = "local_idle";
    public string Detail { get; private set; } = "";
    public string PartnerName => "";
    public int Sent => link?.Sent ?? 0;
    public int Received => link?.Received ?? 0;
    public event Action<Packet> RemoteCursor;

    internal LocalRoom(byte[] fingerprint) => this.fingerprint = fingerprint;

    public void Create() => Open(null, null);

    internal void CreateLan(string address, string port = null)
    {
        if (HasLobby) return;
        if (!DirectRoomEndpoint.TryParseIpv4(address?.Trim(), out var ip))
        {
            Status = "bad_address";
            Detail = "";
            return;
        }
        int listenPort = 0;
        if (!string.IsNullOrWhiteSpace(port) && !DirectRoomEndpoint.TryParsePort(port.Trim(), out listenPort))
        {
            Status = "bad_udp_port";
            Detail = "";
            return;
        }
        Open(null, ip, listenPort);
    }

    internal void Join(string code, string mappedAddress = null)
    {
        if (HasLobby) return;
        IPEndPoint mappedEndpoint = null;
        if (code.StartsWith("PSI-", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(mappedAddress) &&
            !DirectRoomEndpoint.TryParseMappedEndpoint(mappedAddress.Trim(), out mappedEndpoint))
        {
            Status = "bad_mapping_address";
            Detail = "";
            return;
        }
        Open(code.Trim(), null, mappedEndpoint: mappedEndpoint);
    }

    private void Open(string code, IPAddress address, int listenPort = 0, IPEndPoint mappedEndpoint = null)
    {
        if (HasLobby) return;
        try { link = new LoopbackLink(fingerprint, Time.realtimeSinceStartupAsDouble, code, address, listenPort,
            mappedEndpoint,requireApproval:!NativeBackgroundQa.Enabled,measurementClock:()=>Time.realtimeSinceStartupAsDouble); }
        catch (FormatException)
        {
            Detail = "";
            Status = code != null ? "bad_code" : "bad_address";
            return;
        }
        catch (SocketException ex)
        {
            if (listenPort != 0 && ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Status = "udp_port_in_use";
                Detail = "";
                return;
            }
            Detail = ex.SocketErrorCode.ToString();
            Status = code == null && ex.SocketErrorCode == SocketError.AddressNotAvailable
                ? "udp_address_not_local" : code != null ? "join_failed" : "create_failed";
            Debug.LogWarning($"[probably_coop] UDP {(code == null ? "bind" : "join")} failed: " +
                $"{CoopIssueCode.DirectRoom(Status) ?? Status} ({ex.SocketErrorCode})");
            return;
        }
        guest = code != null;
        lan = link.Code.StartsWith("PSI-", StringComparison.Ordinal);
        Session.RemoteCursor += ForwardCursor;
        Detail = "";
        Status = guest ? "handshake" : lan ? "lan_waiting" : "local_waiting";
        Debug.Log(lan ? "[probably_coop] LAN endpoint opened" : guest
            ? "[probably_coop] local guest connecting" : $"[probably_coop] local test host ready, code={Code}");
    }

    private void ForwardCursor(Packet packet) => RemoteCursor?.Invoke(packet);

    public void Tick(Action beforeSessionTick = null)
    {
        if (!HasLobby) return;
        link.Tick(Time.realtimeSinceStartupAsDouble,beforeSessionTick);
        if(guest && Session.State is LinkState.JoinDeclined or LinkState.JoinClosed)
        {
            Leave(Session.State==LinkState.JoinClosed ? "join_closed" : "join_declined");
            return;
        }
        string status = Session.State switch
        {
            LinkState.Connected => lan ? "lan_connected" : "local_connected",
            LinkState.Handshake when Session.JoinPending => guest ? "await_approval" : "approve",
            LinkState.Handshake => "handshake",
            LinkState.TimedOut => "peer_timeout",
            LinkState.Incompatible => "incompatible",
            _ => lan ? "lan_waiting" : "local_waiting"
        };
        if (status != Status)
            Debug.Log($"[probably_coop] {status}, tx={Sent}, rx={Received}");
        if (guest && Session.State == LinkState.Incompatible)
        {
            Leave(status);
            return;
        }
        Status = status;
    }

    public void Approve() { if(IsHost) Session.ApproveJoin(); }
    public void Decline() { if(IsHost) Session.DeclineJoin(Time.realtimeSinceStartupAsDouble); }
    public void SetAcceptingJoins(bool enabled)
    { if(IsHost) Session.SetAcceptingJoins(enabled,Time.realtimeSinceStartupAsDouble); }

    public void Leave(string status = "idle")
    {
        if (HasLobby)
        {
            Session.RemoteCursor -= ForwardCursor;
            if(lan && NativeBackgroundQa.Enabled)
                Debug.Log($"[probably_coop QA] LAN drain peak_datagrams={link.PeakDrain} extended_ticks={link.ExtendedDrains}");
            link.Dispose();
            link = null;
        }
        Status = status == "idle" ? "local_idle" : status;
        Detail = "";
    }

    public void Dispose() => Leave();
}
