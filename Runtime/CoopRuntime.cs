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
    private readonly CoopOpeningLobby opening = new CoopOpeningLobby();
    private bool returningGuest;
    private (string Code, NativeActorCheckpoint.Envelope Verified, string Context)? pendingRecovery;
    private (string Code, CoopRun.Checkpoint Target, string Context)? recoveryExit;
    private CoopRun.Checkpoint mutationResume;
    private (CoopRun.Checkpoint Checkpoint, CoopSession Connection)? queuedReload;
    private CoopRun.Checkpoint loadingCheckpoint;
    private double reloadDeadline;
    private int reloadOrigin;
    private PlayerStore checkedStore;
    private bool stopped;
    private Action captureAfterReceive;
#if PROBABLY_COOP_GRID_QA
    private readonly NativeBackgroundQa qa = NativeBackgroundQa.Create();
    private readonly NativeArchitectureQa architectureQa = NativeArchitectureQa.Create();
#endif

    private void Update()
    {
        bool host = world != null && shop != null;
        bool frameConnected = room?.Session?.State == LinkState.Connected;
#if PROBABLY_COOP_GRID_QA
        NativeFrameAttribution.Boundary(host, frameConnected);
        using var frameRuntime = NativeFrameAttribution.Measure(NativeFrameAttribution.Stage.Runtime);
#endif
        NativePerformance.Frame(frameConnected && (host || NativeWorld.Guest?.Ready == true));
        using var performance = NativePerformance.Measure("runtime.update");
        if (stopped) return;
#if PROBABLY_COOP_GRID_QA
        if (architectureQa != null) { architectureQa.Tick(); return; }
#endif
        try
        {
            if (NativeActorFaultRecovery.TryReturnToMenu(CloseSession, RecordHostFaultRecovery)) return;
            var activeScene = SceneManager.GetActiveScene();
            if (queuedReload is { } requested)
            {
                queuedReload = null;
                if (TryReload(activeScene, requested.Checkpoint, requested.Connection)) return;
            }
            if (loadingCheckpoint != null && !CheckHostReload(activeScene)) return;
            var scene = activeScene.name;
            bool inMenu = scene == "EmporiumMenu" || MainMenuUIController.Instance != null &&
                EmporiumEntry.Instance == null;
            if (guest != null && (NativeGuest.MenuExitRequested || scene == "EmporiumMenu" && guest.EnteredShop))
            {
                LeaveGuest();
                return;
            }
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
#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
            if (inMenu && room == null && NativePlatform.PrepareAndroidHosting()) return;
#endif
            if (room == null)
            {
                var fingerprint = ReadFingerprint();
                room = new RoomSelector(fingerprint, localTest);
                panel = new CoopPanel(room, localTest, opening, QueueReload);
                pointer = new NativePointer(room);
                if (inMenu && localTest)
                {
                    int join = Array.IndexOf(args, "--probably-coop-join");
#if PROBABLY_COOP_GRID_QA
                    int qaBind = NativeBackgroundQa.Enabled ? Array.IndexOf(args, "--probably-coop-qa-host-ip") : -1;
#endif
                    if (join >= 0 && join + 1 < args.Length)
                        room.Join(args[join + 1]);
#if PROBABLY_COOP_GRID_QA
                    else if (qaBind >= 0 && qaBind + 1 < args.Length)
                        room.CreateLan(args[qaBind + 1]);
#endif
                    else if (Array.IndexOf(args, "--probably-coop-host") >= 0)
                        room.Create();
                }
                Debug.Log("[probably_coop] main-menu prototype ready");
            }
            shop?.Tick(room.Session?.State == LinkState.Connected);
            panel.SetContext(inMenu, shop != null || guest != null);
            if (inMenu && pendingRecovery is { } recovery)
            {
                pendingRecovery = null;
                var target = mutationResume ?? CoopRun.FindVerifiedCheckpoint(recovery.Verified);
                recoveryExit = (recovery.Code, target, recovery.Context);
            }
            if (inMenu && mutationResume != null && MainMenuUIController.Instance != null)
            {
                var target = mutationResume;
                mutationResume = null;
                panel.Resume(target);
                return;
            }
            if (!localTest && !NativePlatform.IsAndroid)
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
            NativeShop.Gate = shop != null && (shop.BlocksWorldCommands || shop.GuestOutside || room.IsHost)
                ? shop : null;
            captureAfterReceive ??= CaptureHostAfterReceive;
            room.Tick(captureAfterReceive);
            var connection = room.Session;
            if (guest?.ReloadRequested(connection) == true)
            {
                guest = null;
                returningGuest = true;
                dayUi.Reset();
                NativeActorCarryUi.Reset();
                panel.ObserveWorldReload();
                NativeGuest.ReturnToMenu();
                return;
            }
            opening.Tick(room, panel, inMenu);
            panel.ObserveJoin(guest!=null,NativeWorld.Guest?.Ready==true);
            if(room.IsGuest && connection?.Native.SynchronizationFailed==true)
            {
                FailGuestSynchronization();
                return;
            }
            bool reviewingGuest = inMenu && guest == null && room.IsGuest && connection?.State==LinkState.Connected && connection.Shop.View.Active;
            partnerJoin.ObserveConnection(reviewingGuest);
            if(!reviewingGuest && !opening.HasGuestDraft) panel.CloseModReview();
            panel.SetPartnerPickerVisible(partnerJoin.PickerVisible || opening.CoversRoomPanel);
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
                    LeaveGuest(room.Status=="peer_timeout" ? panel.PeerTimedOut() : "idle");
                    return;
                }
                try { guest.Tick(connection); dayUi.Tick(connection.Shop); }
                catch(Exception error) { FailGuestSynchronization(error); return; }
            }
            else if (inMenu && room.IsGuest && connection?.State == LinkState.Connected &&
                connection.Shop.View.Active)
            {
                try
                {
                    if(!partnerJoin.Ready(room,panel))
                    {
                        panel.SetPartnerPickerVisible(partnerJoin.PickerVisible);
                        panel.Tick();
                        return;
                    }
                    guest = NativeGuest.Enter(connection);
                }
                catch(Exception error) { FailGuestSynchronization(error); return; }
                if (guest != null) return;
            }
            if (!inMenu) recoveryExit = null;
            panel.ObserveDay(shop,
                guest != null && connection?.Shop.View.Phase == ShopDay.SaveUnverified,
                recoveryExit);
            NativeActorCarryUi.Tick();
            pointer.Tick(connection, inMenu || shop != null || guest != null);
            panel.Tick();
#if PROBABLY_COOP_GRID_QA
            qa?.Tick(room, panel, shop, guest, inMenu);
#endif
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

    private void RecordHostFaultRecovery(NativeActorSession session)
    {
        mutationResume = shop?.MutationResume;
        var attempt = shop?.RecoveryCandidate;
        var extension = session.ExtensionActionFailure;
        string code = shop?.FailedMutation?.Code;
        if (code == null && attempt != null) code = CoopIssueCode.Settlement(attempt);
        string context = extension?.Context;
        if (code == null && extension != null) code = ModExtensionActionFailure.Code;
        if (code == null && session.TravelTransitionFailed)
        {
            code = NativeActorSession.TravelFaultCode;
            context = "operation=joint_travel stage=scene_transition";
        }
        pendingRecovery = (code ?? "DAY-205", session.VerifiedCheckpoint, context);
    }

    private bool QueueReload(CoopRun.Checkpoint checkpoint)
    {
        var connection = room?.Session;
        if (checkpoint?.HasActors != true || queuedReload.HasValue || !CanReload(connection)) return false;
        queuedReload = (checkpoint, connection);
        return true;
    }

    private bool CanReload(CoopSession connection)
    {
        var actors = NativeActorSession.Current;
        return loadingCheckpoint == null && room?.CanReloadInShop == true && ReferenceEquals(room.Session, connection) &&
            shop?.CanReloadWorld == true &&
            actors is { CanCommit: true, TransitionActive: false } && shop.Matches(actors.Store);
    }

    private bool TryReload(Scene scene, CoopRun.Checkpoint checkpoint, CoopSession connection)
    {
        if (scene.name != "InventoryScene" || !CanReload(connection))
        {
            StoreUIManager.Instance?.Notify(ModHelper.GetLocalized("probably_coop", "world_reload_busy"));
            return false;
        }
        if (!CoopRun.PrepareResume(checkpoint, out var verified))
        {
            StoreUIManager.Instance?.Notify(verified?.UnavailableReason ??
                ModHelper.GetLocalized("probably_coop", "checkpoint_unavailable"));
            return false;
        }

        loadingCheckpoint = verified;
        reloadOrigin = scene.handle;
        reloadDeadline = Time.realtimeSinceStartupAsDouble + 60;
        try
        {
            bool notifyPeer = connection.State == LinkState.Connected;
            if (notifyPeer && !connection.BeginWorldReload(Time.realtimeSinceStartupAsDouble))
                throw new InvalidOperationException("Unable to begin the co-op world reload.");
            connection.Shop.SetAuthority(null);
            panel.SetContext(false, false);
            if (notifyPeer) room.Tick();
            world?.Close(); world = null;
            shop.Dispose(); shop = null;
            dayUi.Reset();
            NativeActorCarryUi.Reset();
            NativeActorSession.Current.Dispose();
            checkedStore = null;
            FreezeScene(scene);
            EmporiumEntry.ResetData();
            PlayerStore.ResetData();
            StoreStation.ResetData();
            EmporiumEntry.sceneLoadStartTime = Time.realtimeSinceStartup;
            SceneManager.LoadSceneAsync("InventoryScene", LoadSceneMode.Single);
        }
        catch (Exception error) { AbortHostReload(error); }
        return true;
    }

    private bool CheckHostReload(Scene scene)
    {
        var target = loadingCheckpoint;
        var loaded = NativeActorSession.Current?.VerifiedCheckpoint;
        if (scene.handle != reloadOrigin && scene.name == "InventoryScene" &&
            loaded?.Run == target.Run && loaded.Slot == target.Slot && loaded.Day == target.Day &&
            loaded.Checkpoint == target.Nonce && NativeActorSession.Current?.Faulted == false)
        {
            loadingCheckpoint = null;
            Debug.Log($"[probably_coop] cooperative checkpoint reloaded in room, day={loaded.Day}, slot={loaded.Slot}");
            return true;
        }
        if (Time.realtimeSinceStartupAsDouble >= reloadDeadline || loaded != null && scene.handle != reloadOrigin)
            AbortHostReload(new InvalidOperationException("The native scene did not load the selected co-op checkpoint."));
        // During an unattended reload, leave incoming joins queued until we
        // can attach the new shop. A connected peer still needs the reload notice.
        else if (room.Session?.State == LinkState.Connected) room.Tick();
        return false;
    }

    private void AbortHostReload(Exception error)
    {
        loadingCheckpoint = null;
        Debug.LogError("[probably_coop] in-room checkpoint reload failed: " + error);
        CloseSession();
        NativeActorSession.Current?.Dispose();
        FreezeScene(SceneManager.GetActiveScene());
        EmporiumEntry.ResetData();
        PlayerStore.ResetData();
        StoreStation.ResetData();
        NewGameData.HardReset();
        SceneManager.LoadScene("EmporiumMenu", LoadSceneMode.Single);
    }

    private static void FreezeScene(Scene scene)
    {
        if (scene.name != "InventoryScene") return;
        foreach (var root in scene.GetRootGameObjects())
            if (root != null) root.SetActive(false);
    }

    private void FailGuestSynchronization(Exception error=null)
    {
        var native=room.Session?.Native;
        var extension = error as ModExtensionProjectionException;
        string detail = extension == null ? error?.ToString() :
            $"extension={extension.ModId} callback=ProjectState error={extension.InnerException.GetType().Name}";
        Debug.LogWarning($"[probably_coop] guest synchronization failed; room can be rejoined. " +
            $"reason={native?.FailureReason} receivedRevision={native?.Revision} fullRevision={native?.FullRevision} {detail}");
        panel.JoinFailed(error as ModExtensionJoinException,native?.FailureReason ?? NativeSyncFailure.None,
            error!=null, duringPlay:guest?.EnteredShop==true, failedExtension:extension?.ModId);
        panel.RecordProjectionFailure(error,native?.FailureReason ?? NativeSyncFailure.None);
        partnerJoin.Dispose();
        if(guest!=null || NativeGuest.Active) LeaveGuest("sync_failed");
        else room.Leave("sync_failed");
    }

    private void LeaveGuest(string status="idle")
    {
        guest = null;
        returningGuest = true;
        dayUi.Reset();
        NativeActorCarryUi.Reset();
        room.Leave(status);
        NativeGuest.ReturnToMenu();
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
#if PROBABLY_COOP_GRID_QA
        if (architectureQa != null)
        {
            architectureQa.Stop();
            Application.Quit();
        }
#endif
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
        opening.Dispose();
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
