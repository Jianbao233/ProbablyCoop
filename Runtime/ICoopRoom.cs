using System;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal interface ICoopRoom : IDisposable
{
    CoopSession Session { get; }
    bool HasLobby { get; }
    bool Busy { get; }
    bool IsHost { get; }
    bool IsGuest { get; }
    bool CanApprove { get; }
    string Code { get; }
    string Status { get; }
    string Detail { get; }
    string PartnerName { get; }
    int Sent { get; }
    int Received { get; }
    event Action<Packet> RemoteCursor;
    void Create();
    void Leave(string status = "idle");
    void Tick(Action beforeSessionTick = null);
}
