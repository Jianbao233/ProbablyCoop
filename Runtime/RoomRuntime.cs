using System;
using ProbablyCoop.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProbablyCoop;

// The rebuilt stack's entry: room, negotiation and panel on Net/, nothing from
// the legacy replication in Game/. Opt-in until it can carry a shared shop.
internal sealed class RoomRuntime : MonoBehaviour
{
    internal const string Flag = "--probably-coop-next";

    internal static bool Selected => Array.IndexOf(Environment.GetCommandLineArgs(), Flag) >= 0;

    private readonly string[] args = Environment.GetCommandLineArgs();
    private readonly bool localTest = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-local") >= 0;
    private readonly SteamJoinInbox steamJoin = new SteamJoinInbox();
    private NetRoom room;
    private RoomPanel panel;
    private RoomPointer pointer;
    private NativeInventoryPilot inventory;
    private bool? localShopHost;
    private bool stopped;
#if PROBABLY_COOP_GRID_QA
    private int steamProbePhase;
    private double steamProbeDeadline;
    private string steamProbeCode;
    private int localEntryPhase;
#endif

    private void Update()
    {
        if (stopped) return;
        bool activeHost = room?.IsHost == true && NativeInventoryPilot.Interactive;
        NativePerformance.Frame(activeHost);
        using var timing = NativePerformance.MeasureHostEffectFrame(activeHost);
        try
        {
            Step();
        }
        catch (Exception exception)
        {
            if (room != null && (inventory != null || room.HasLobby || room.Busy))
            {
                RecoverSession(exception);
                return;
            }
            Debug.LogError($"[probably_coop] room runtime stopped: {exception}");
            Stop();
        }
    }

    private void RecoverSession(Exception exception)
    {
        Debug.LogError($"[probably_coop] session stopped: {exception}");
        var failed = inventory;
        inventory = null;
        try { room.Leave("session_error"); }
        finally
        {
            failed?.Stop(quitting: false);
            NativeRoomPacing.Update(false, false, false);
        }
    }

    private void Step()
    {
        if (CustomUIManager.Instance == null) return;
#if PROBABLY_COOP_GRID_QA
        NativeEffectInputGuideQa.Tick();
#endif
        double now = Time.realtimeSinceStartupAsDouble;
        bool inMenu = SceneManager.GetActiveScene().name == "EmporiumMenu" ||
            MainMenuUIController.Instance != null && EmporiumEntry.Instance == null;
        if (room == null)
        {
            if (localTest && Array.IndexOf(args, NativeInventoryPilot.Flag) >= 0)
            {
                if (NativeBackgroundQa.IsHostProduct) localShopHost = true;
                else if (NativeBackgroundQa.IsGuestProduct) localShopHost = false;
            }
            room = new NetRoom(ContentManifest.Read, Lobby, localTest);
            panel = new RoomPanel(room, localTest, localShopHost);
            pointer = new RoomPointer(room);
            room.Ready += () => Debug.Log($"[probably_coop] session ready peer={room.Peer} host={room.IsHost}");
            room.Ended += reason => Debug.Log($"[probably_coop] session ended reason={reason} declined={room.Reason}");
            if (inMenu && localTest) Launch(now);
            Debug.Log("[probably_coop] room runtime ready");
        }
        // A pending test room has no world if its owner starts an ordinary
        // game before the partnership begins. Close it before polling joins.
        if (localShopHost.HasValue && !inMenu && room.IsLoopback && !NativeInventoryPilot.OwnsScene)
            room.Leave("local_start_from_menu");
        if (inventory != null && !room.IsLoopback && !NativeInventoryPilot.OwnsScene)
        {
            inventory.Stop(quitting: false);
            inventory = null;
        }
        if (inventory == null && room.IsLoopback && localShopHost == room.IsHost)
        {
            bool campaign = false;
#if PROBABLY_COOP_GRID_QA
            campaign = Array.IndexOf(args, "--probably-coop-grid-campaign") >= 0;
#endif
            inventory = new NativeInventoryPilot(room, campaign);
            if (room.IsHost) Debug.Log($"[probably_coop GRID] loopback room={room.Code}");
        }
        room.Poll(now);
        inventory?.Tick();
        pointer.Tick(NativeInventoryPilot.Interactive, inventory?.Epoch ?? 0);
#if PROBABLY_COOP_GRID_QA
        if (inMenu && !localTest && Array.IndexOf(args, "--probably-coop-next-steam-smoke") >= 0)
            ProbeSteam(now);
#endif
        if (!localTest)
        {
            steamJoin.Listen(args);
            if (inMenu && steamJoin.TryTake(out var lobby))
            {
                bool free = !room.HasLobby && !room.Busy;
                if (free) room.JoinLobby(now, lobby.m_SteamID);
                panel.ShowSteamJoin(!free);
            }
        }
        panel.SetContext(inMenu);
        panel.Tick(now);
#if PROBABLY_COOP_GRID_QA
        if (inMenu && NativeInventoryPilot.QaGuestAwaitingMenu && panel.HasNativeMenuEntry)
            NativeInventoryPilot.QaMenuRestored(panel);
        if (localShopHost.HasValue && Array.IndexOf(args, "--probably-coop-grid-entry-qa") >= 0)
            VerifyLocalEntry(inMenu);
#endif
        room.Flush(now);
        NativeRoomPacing.Update(room.HasLobby || room.Busy, room.IsReady, room.IsLoopback);
    }

    private void Launch(double now)
    {
        int join = Array.IndexOf(args, "--probably-coop-join");
        if (join >= 0 && join + 1 < args.Length && localShopHost != true)
            room.Join(now, args[join + 1], loopbackOnly: localShopHost.HasValue);
        else if (Array.IndexOf(args, "--probably-coop-host") >= 0 && localShopHost != false)
            room.Create(now);
    }

    private INetLobby Lobby() => !localTest && SteamManager.Initialized ? new SteamTransport() : null;

#if PROBABLY_COOP_GRID_QA
    private void VerifyLocalEntry(bool inMenu)
    {
        bool host = localShopHost == true;
        if (localEntryPhase == 0 && inMenu && panel.HasNativeMenuEntry)
        {
            if (host)
            {
                CheckLocalEntry(panel.QaHostFromMenu(), "the host main-menu button did not create a room");
                if (Array.IndexOf(args, "--probably-coop-grid-entry-from-shop") >= 0)
                {
                    NewGameData.Instance.startType = NewGameData.StartType.WATER_MERCHANT;
                    NewGameData.Instance.skipIntro = true;
                    MainMenuUIController.Instance.StartNewGameClick();
                    localEntryPhase = 1;
                    return;
                }
            }
            else
            {
                CheckLocalEntry(!panel.QaOpenMenuAndJoin("PSI-test") && room.Status == "local_code_required",
                    "the test menu did not reject a LAN room code");
                int code = Array.IndexOf(args, "--probably-coop-grid-entry-code");
                CheckLocalEntry(code >= 0 && code + 1 < args.Length && panel.QaOpenMenuAndJoin(args[code + 1]),
                    "the guest main-menu button did not join the loopback room");
                Debug.Log("[probably_coop ENTRY] PASS guest menu rejected PSI and joined through the Join button");
            }
            localEntryPhase = 2;
        }
        if (localEntryPhase == 1 && !inMenu && PlayerStore.IsInstanceExist() && StoreUIManager.Instance != null)
        {
            CheckLocalEntry(!room.HasLobby && !NativeInventoryPilot.OwnsScene && !CoopRun.IsRegistered(PlayerStore.Instance),
                "an ordinary shop kept the unstarted test room open");
            CheckLocalEntry(panel.QaConfirmHostFromStore(), "the native return and create confirmation did not work");
            Debug.Log("[probably_coop ENTRY] PASS ordinary shop closed the waiting room and accepted return and create");
            localEntryPhase = 2;
        }
        if (localEntryPhase == 2)
        {
            if (host && !room.IsHost) return;
            if (host) Debug.Log($"[probably_coop ENTRY] room={room.Code}");
            localEntryPhase = 3;
        }
        if (localEntryPhase != 3 || !NativeInventoryPilot.Interactive) return;
        CheckLocalEntry(SceneManager.GetActiveScene().name == "InventoryScene" &&
            (host ? CoopRun.IsRegistered(PlayerStore.Instance) : PlayerStore.Instance.saveSlotId == -1),
            "the participant did not enter the cooperative shop");
        panel.QaVerifyShopEntries();
        Debug.Log($"[probably_coop ENTRY] PASS role={(host ? "host" : "guest")} native cooperative shop ready");
        localEntryPhase = 4;
    }

    private static void CheckLocalEntry(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Campaign QA: " + message);
    }

    private void ProbeSteam(double now)
    {
        if (!NativeBackgroundQa.IsHostProduct)
            throw new InvalidOperationException("Steam room probe requires the isolated Host game.");
        if (steamProbeDeadline == 0) steamProbeDeadline = now + 50;
        if (now >= steamProbeDeadline)
            throw new InvalidOperationException("Steam room probe timed out: " + room.Status);
        if (steamProbePhase == 0 && SteamManager.Initialized)
        {
            NativeQaPlatformWrites.Verify();
            room.Create(now);
            steamProbePhase = 1;
        }
        else if (steamProbePhase == 1 && room.HasLobby && room.FriendsCanJoin)
        {
            steamProbeCode = room.Code;
            if (!SteamCode.TryDecode(steamProbeCode, out _))
                throw new InvalidOperationException("Steam room probe returned an invalid lobby code.");
            Debug.Log("[probably_coop STEAM QA] PASS real invisible lobby and friend presence");
            room.Leave();
            if (room.HasLobby || room.FriendsCanJoin)
                throw new InvalidOperationException("Steam room probe failed to clear its own room.");
            Debug.Log("[probably_coop STEAM QA] PASS lobby left and presence cleared");
            steamProbePhase = 2;
        }
        else if (steamProbePhase == 2 && !room.Busy)
        {
            room.Join(now, steamProbeCode);
            steamProbePhase = 3;
        }
        else if (steamProbePhase == 3 && !room.Busy && !room.HasLobby &&
                 (room.Status == "join_failed" || room.Status == "wrong_lobby"))
        {
            Debug.Log("[probably_coop STEAM QA] PASS closed lobby code reached the native join result");
            steamProbePhase = 4;
            Application.Quit();
        }
    }
#endif

    internal void Stop(bool quitting = false)
    {
        if (stopped) return;
        stopped = true;
        steamJoin.Dispose();
        inventory?.Stop(quitting);
        inventory = null;
        pointer?.Dispose();
        pointer = null;
        panel?.Dispose();
        panel = null;
        room?.Dispose();
        room = null;
        NativeRoomPacing.Update(false, false, false);
    }

    private void OnDestroy() => Stop();

    private void OnApplicationQuit() => Stop(quitting: true);
}
