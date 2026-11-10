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
    public bool CanApprove => false;
    public string Code => link?.Code ?? "";
    internal bool IsLoopback => link != null && !lan;
    public string Status { get; private set; } = "local_idle";
    public string Detail { get; private set; } = "";
    public string PartnerName => guest ? "Host" : "Guest";
    public int Sent => link?.Sent ?? 0;
    public int Received => link?.Received ?? 0;
    public event Action<Packet> RemoteCursor;

    internal LocalRoom(byte[] fingerprint) => this.fingerprint = fingerprint;

    public void Create() => Open(null, null);

    internal void CreateLan(string address)
    {
        if (!IPAddress.TryParse(address.Trim(), out var ip) || IPAddress.IsLoopback(ip))
        {
            Status = "bad_address";
            Detail = "";
            return;
        }
        Open(null, ip);
    }

    internal void Join(string code) => Open(code.Trim(), null);

    private void Open(string code, IPAddress address)
    {
        if (HasLobby) return;
        try { link = new LoopbackLink(fingerprint, Time.realtimeSinceStartupAsDouble, code, address); }
        catch (FormatException)
        {
            Detail = "";
            Status = code != null ? "bad_code" : "bad_address";
            return;
        }
        catch (SocketException ex)
        {
            Detail = ex.SocketErrorCode.ToString();
            Status = code != null ? "join_failed" : "create_failed";
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
        string status = Session.State switch
        {
            LinkState.Connected => lan ? "lan_connected" : "local_connected",
            LinkState.Handshake => "handshake",
            LinkState.TimedOut => "peer_timeout",
            LinkState.Incompatible => "incompatible",
            _ => lan ? "lan_waiting" : "local_waiting"
        };
        if (status != Status)
            Debug.Log($"[probably_coop] {status}, tx={Sent}, rx={Received}");
        Status = status;
    }

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
