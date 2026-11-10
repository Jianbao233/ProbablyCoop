using System;
using System.IO;
using System.Security.Cryptography;
using ProbablyCoop.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProbablyCoop;

internal sealed class CoopRuntime : MonoBehaviour
{
    private readonly bool localTest = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-local") >= 0;
    private readonly string[] args = Environment.GetCommandLineArgs();
    private readonly SteamJoinInbox steamJoin = new SteamJoinInbox();
    private RoomSelector room;
    private CoopPanel panel;
    private NativeShop shop;
    private NativeGuest guest;
    private NativeWorld world;
    private NativePointer pointer;
    private readonly NativeDayUi dayUi = new NativeDayUi();
    private readonly NativePartnerJoin partnerJoin = new NativePartnerJoin();
    private bool returningGuest;
    private PlayerStore checkedStore;
    private bool stopped;
    private Action captureAfterReceive;
    private readonly NativeBackgroundQa qa = NativeBackgroundQa.Create();
    private readonly NativeArchitectureQa architectureQa = NativeArchitectureQa.Create();

    private void Update()
    {
        NativePerformance.Frame(room?.Session?.State==LinkState.Connected &&
            (world!=null && shop!=null || NativeWorld.Guest?.Ready==true));
        using var performance = NativePerformance.Measure("runtime.update");
        if (stopped) return;
        if (architectureQa != null) { architectureQa.Tick(); return; }
        try
        {
            if(NativeActorFaultRecovery.TryReturnToMenu(CloseSession)) return;
            var scene = SceneManager.GetActiveScene().name;
            bool inMenu = scene == "EmporiumMenu" || MainMenuUIController.Instance != null &&
                EmporiumEntry.Instance == null;
            if (inMenu)
            {
                if (returningGuest)
                {
                    NativeGuest.ReturnToMenu();
                    returningGuest = false;
                }
                if (shop != null) CloseSession();
                NativeActorSession.Current?.Dispose();
                checkedStore = null;
            }
            else if (returningGuest)
            {
                return;
            }
            else if (guest == null)
            {
                if (scene != "InventoryScene")
                {
                    CloseSession();
                    return;
                }
                if (!PlayerStore.IsInstanceExist() || EmporiumEntry.Instance?.invElement == null) return;
                var player = PlayerStore.Instance;
                if (!ReferenceEquals(checkedStore, player) || shop != null && !shop.Matches(player))
                {
                    checkedStore = player;
                    if (shop != null || room?.IsGuest == true) CloseSession();
                    if (CoopRun.IsRegistered(player))
                    {
                        NativeActorSession.EnsureForExisting(player);
                        shop = new NativeShop(player);
                    }
                    CoopRun.Armed = false;
                }
                if (shop == null)
                {
                    CloseSession();
                    return;
                }
            }
            if (CustomUIManager.Instance == null) return;
            if (room == null)
            {
                var fingerprint = ReadFingerprint();
                room = new RoomSelector(fingerprint, localTest);
                panel = new CoopPanel(room, localTest);
                pointer = new NativePointer(room);
                if (inMenu && localTest)
                {
                    int join = Array.IndexOf(args, "--probably-coop-join");
                    int qaBind = NativeBackgroundQa.Enabled ? Array.IndexOf(args,"--probably-coop-qa-host-ip") : -1;
                    if (join >= 0 && join + 1 < args.Length)
                        room.Join(args[join + 1]);
                    else if(qaBind>=0 && qaBind+1<args.Length)
                        room.CreateLan(args[qaBind+1]);
                    else if (Array.IndexOf(args, "--probably-coop-host") >= 0)
                        room.Create();
                }
                Debug.Log("[probably_coop] main-menu prototype ready");
            }
            shop?.Tick(room.Session?.State == LinkState.Connected);
            panel.SetContext(inMenu, shop != null || guest != null);
            if (!localTest)
            {
                steamJoin.Listen(args);
                if (inMenu && steamJoin.TryTake(out var lobby))
                {
                    if (!room.HasLobby && !room.Busy)
                    {
                        Debug.Log("[probably_coop] Steam join handoff received");
                        room.JoinSteam(lobby);
                        panel.ShowSteamJoin(false);
                    }
                    else panel.ShowSteamJoin(true);
                }
            }
            room.Session?.Shop.SetAuthority(shop);
            if (shop != null && room.Session != null)
            {
                world ??= new NativeWorld(true);
                world.UpdatePeerConnection(room.Session.State==LinkState.Connected,room.Session.Native);
                world.BindHost(shop, room.Session.Native);
            }
            NativeShop.Gate = shop != null && (shop.Settling || shop.GuestOutside || room.IsHost)
                ? shop : null;
            captureAfterReceive ??= CaptureHostAfterReceive;
            room.Tick(captureAfterReceive);
            var connection = room.Session;
            partnerJoin.ObserveConnection(room.IsGuest && connection?.State==LinkState.Connected && connection.Shop.View.Active);
            // Transport callbacks may replace/drop the peer during room.Tick.
            // Clear consent/ownership now, before later UI callbacks in this frame.
            if(world!=null)
            {
                bool connected=connection?.State==LinkState.Connected;
                world.UpdatePeerConnection(connected,connection?.Native);
                world.ShootingConnectionChanged(connected);
            }
            if (guest != null)
            {
                if (!room.IsGuest || connection?.State != LinkState.Connected || !connection.Shop.View.Active ||
                    !guest.Matches(connection))
                {
                    guest = null;
                    dayUi.Reset();
                    returningGuest = true;
                    room.Leave();
                    NativeGuest.ReturnToMenu();
                    return;
                }
                guest.Tick(connection);
            }
            else if (inMenu && room.IsGuest && connection?.State == LinkState.Connected &&
                connection.Shop.View.Active)
            {
                if(!partnerJoin.Ready(room)) { panel.Tick(); return; }
                guest = NativeGuest.Enter(connection);
                if (guest != null) return;
            }
            dayUi.Tick((shop != null || guest != null) && connection?.State == LinkState.Connected
                ? connection.Shop : null);
            NativeActorCarryUi.Tick();
            pointer.Tick(connection, inMenu || shop != null || guest != null);
            panel.Tick();
            qa?.Tick(room, shop, guest, inMenu);
        }
        catch (Exception exception)
        {
            if(NativeActorSession.Current is NativeActorSession actors)
            {
                actors.MarkFault(exception);
                Debug.LogError($"[probably_coop] cooperative runtime failed: {exception}");
                // The business stack has unwound. Leave the failing native
                // run through the no-save recovery boundary on the next tick.
                return;
            }
            Debug.LogError($"[probably_coop] runtime stopped: {exception}");
            Stop();
        }
    }

    private void CaptureHostAfterReceive()
    {
        // Polling can replace the epoch or drop the peer. Reconcile before
        // reading native state or publishing a completed encoding worker.
        var connection=room?.Session;
        if(stopped || shop==null || world==null || connection==null) return;
        world.UpdatePeerConnection(connection.State==LinkState.Connected,connection.Native);
        world.TickHost(shop,connection.Native,connection.Shop);
    }

    internal void Stop()
    {
        if (stopped) return;
        stopped = true;
        steamJoin.Dispose();
        if (architectureQa != null)
        {
            architectureQa.Stop();
            Application.Quit();
        }
        var closingScene=SceneManager.GetActiveScene();
        var closingActors=NativeActorSession.Current;
        bool cooperativeHost=closingActors!=null && PlayerStore.HasInstance &&
            ReferenceEquals(PlayerStore.Instance,closingActors.Store) &&
            closingActors.Store.saveSlotId==closingActors.NativeSaveSlot &&
            Guid.TryParse(closingActors.Store.runID,out var closingRun) && closingRun==closingActors.Run.Run;
        if ((NativeGuest.Active || cooperativeHost) && closingScene.name == "InventoryScene" && !NativeBackgroundQa.QaStopping)
        {
            // A cooperative Host cannot keep running vanilla nights after the
            // actor/carry routes are removed. Request the no-save native exit
            // before cleanup, even if subsequent disposal itself fails.
            Application.Quit();
            Debug.Log("[probably_coop] cooperative runtime shutdown requested before actor cleanup; no save");
            foreach(var root in closingScene.GetRootGameObjects())
                if(root!=null)
                    try { root.SetActive(false); }
                    catch(Exception failure) { Debug.LogError("[probably_coop] shutdown scene cleanup: "+failure); }
        }
        CoopRun.Armed = false;
        NativeActorFaultRecovery.Reset();
        CloseSession();
        NativeActorSession.Current?.Dispose();
    }

    private void OnDestroy() => Stop();

    private void OnApplicationQuit() => Stop();

    private void CloseSession()
    {
        NativeActorCarryUi.Reset();
        partnerJoin.Dispose();
        dayUi.Reset();
        pointer?.Dispose();
        pointer = null;
        panel?.Dispose();
        panel = null;
        room?.Dispose();
        room = null;
        shop?.Dispose();
        shop = null;
        world?.Close(); world = null;
    }

    private static byte[] ReadFingerprint()
    {
        using var sha = SHA256.Create();
        var fingerprint = new byte[Wire.FingerprintSize];
        using (var game = File.OpenRead(typeof(IMod).Assembly.Location))
            sha.ComputeHash(game).CopyTo(fingerprint, 0);
        using (var mod = File.OpenRead(typeof(CoopRuntime).Assembly.Location))
            sha.ComputeHash(mod).CopyTo(fingerprint, 32);
        return fingerprint;
    }
}
