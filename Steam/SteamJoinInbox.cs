using System;
using System.Globalization;
using Steamworks;

namespace ProbablyCoop;

// Steam can hand off a join before the lobby panel or transport exists.
internal sealed class SteamJoinInbox : IDisposable
{
    private const string JoinArgument = "+probably_coop_join";
    private const string LobbyArgument = "+connect_lobby";
    private Callback<GameLobbyJoinRequested_t> lobbyJoin;
    private Callback<GameRichPresenceJoinRequested_t> friendJoin;
    private CSteamID pending;

    internal static string ConnectString(CSteamID lobby) =>
        JoinArgument + " " + lobby.m_SteamID.ToString(CultureInfo.InvariantCulture);

    internal void Listen(string[] processArguments)
    {
        if (lobbyJoin != null || !SteamManager.Initialized) return;
        lobbyJoin = Callback<GameLobbyJoinRequested_t>.Create(request => Queue(request.m_steamIDLobby));
        friendJoin = Callback<GameRichPresenceJoinRequested_t>.Create(request =>
        {
            if (TryReadArguments(Split(request.m_rgchConnect), out var lobby)) Queue(lobby);
        });
        SteamApps.GetLaunchCommandLine(out string launch, 2048);
        if (TryReadArguments(Split(launch), out var id) || TryReadArguments(processArguments, out id))
            Queue(id);
    }

    private static string[] Split(string arguments) =>
        (arguments ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    internal static bool TryReadArguments(string[] arguments, out CSteamID lobby)
    {
        lobby = CSteamID.Nil;
        for (int index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] != JoinArgument && arguments[index] != LobbyArgument) continue;
            if (lobby != CSteamID.Nil || ++index == arguments.Length ||
                !ulong.TryParse(arguments[index], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                return false;
            lobby = new CSteamID(id);
            if (!lobby.IsLobby() || !lobby.IsValid()) return false;
        }
        return lobby != CSteamID.Nil;
    }

    private void Queue(CSteamID lobby)
    {
        if (lobby.IsLobby() && lobby.IsValid()) pending = lobby;
    }

    internal bool TryTake(out CSteamID lobby)
    {
        lobby = pending;
        pending = CSteamID.Nil;
        return lobby != CSteamID.Nil;
    }

    public void Dispose()
    {
        lobbyJoin?.Dispose();
        friendJoin?.Dispose();
        pending = CSteamID.Nil;
    }
}
