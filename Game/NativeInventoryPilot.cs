using System;
using System.Collections.Generic;
#if PROBABLY_COOP_GRID_QA
using System.Linq;
using System.Security.Cryptography;
#endif
using HarmonyLib;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProbablyCoop;

// Exercises effects in an isolated original game scene. The campaign option
// allocates a new native slot only on the isolated host.
internal sealed partial class NativeInventoryPilot
{
    internal const string Flag = "--probably-coop-next-grid";
    internal static NativeInventoryPilot Current { get; private set; }
    internal static bool OwnsScene => Current != null;
    internal static bool Interactive => Current?.Ready == true;
    internal static bool HasCampaign => Current?.campaign != null;
    internal static bool HoldsLocalItem(GameItem item) => item != null && Current?.held == item;
    internal static bool AuthoritativeCampaign => Current?.localHost == true && Current.campaign != null;
    internal static NativeActorState RemoteHostActor => Current is { localHost: false } guest ? guest.campaign?.RemoteHost : null;
    internal static bool ResumingCampaign => AuthoritativeCampaign && Current.resumeRun != Guid.Empty;
    internal uint Epoch => session.Host?.Barrier ?? session.Guest?.Barrier ?? 0;

    private readonly NetRoom room;
    private readonly NativeInventoryGrid world;
    private readonly WorldSession session;
    private readonly NativeEffectCampaign campaign;
    private InventoryEntry[] incoming;
    private byte[] incomingCampaign;
    private ulong ticket;
    private bool leaving, exitRequested, localHost;
    private Guid resumeRun;
    private GameItem held;
    private GameItem confirmingStock;
    private bool buildingDepartureChoices;
    private bool RunningNativeBoundary => localHost && campaign?.RunningNativeRules == true;
#if PROBABLY_COOP_GRID_QA
    internal static bool QaGuestAwaitingMenu { get; private set; }
    private ulong audited = ulong.MaxValue;
    private readonly GridQa qa;
    private int checkpointLoads;
#endif

    internal NativeInventoryPilot(NetRoom room, bool runCampaign)
    {
        this.room = room;
        world = new NativeInventoryGrid(room.IsHost);
        if (runCampaign)
        {
            if (!(room.IsHost ? NativeBackgroundQa.IsHostProduct : NativeBackgroundQa.IsGuestProduct) || !room.IsLoopback)
                throw new InvalidOperationException("The effect campaign needs isolated QA products.");
#if PROBABLY_COOP_GRID_QA
            if (Environment.GetCommandLineArgs().Contains("--probably-coop-grid-silent")) AudioListener.volume = 0;
#endif
            campaign = new NativeEffectCampaign(world, room.IsHost);
            session = new WorldSession(room, campaign, campaign.Capture, campaign.CanStart, campaign.CanCarry);
            campaign.Session = session;
        }
        else session = new WorldSession(room, world, world.Capture);
#if PROBABLY_COOP_GRID_QA
        if (campaign == null) qa = new GridQa(this);
#endif
    }

    private bool Ready => !leaving && room.IsLoopback && room.IsReady && world.Ready && campaign?.Busy != true &&
        (localHost
            ? session.Host?.Closed == false && session.Host.IsAttached(NetSession.GuestPeer)
            : session.Guest?.Closed == false && session.Guest.Aligned);

    internal void Tick()
    {
        if (Current == this && (exitRequested || !room.HasLobby || !localHost && !room.IsReady))
        {
            if (exitRequested) room.Leave();
            ReturnToMenu();
        }
        if (leaving || !room.IsReady || !room.IsLoopback)
        {
            if (Current == this && localHost)
            {
                campaign?.CloseDisconnectedPhone();
                campaign?.CloseTransitionUi();
            }
            if (Current == this) CancelDrag();
            return;
        }

        if (session.TryTakeCheckpoint(out var save, out ulong loading))
        {
            if (campaign != null) incomingCampaign = save;
            else if (!InventoryCheckpoint.TryDecode(save, out incoming))
            {
                session.FinishLoading(loading, false);
                session.Tick();
                return;
            }
            ticket = loading;
        }

        if (Current == null && (room.IsHost || incoming != null || incomingCampaign != null))
        {
            if (SceneManager.GetActiveScene().name != "EmporiumMenu") return;
            EnterScene();
            return;
        }
        if (Current != this || SceneManager.GetActiveScene().name != "InventoryScene") return;
        var shop = EmporiumEntry.Instance;
        if (shop?.invElement == null || !PlayerStore.IsInstanceExist()) return;

        if (!world.Bound)
        {
            world.Bind(shop, localHost ? NativeActorSession.Current?.Carry : null);
#if PROBABLY_COOP_GRID_QA
            if (campaign == null) GridQa.RegisterFixtures();
#endif
            if (localHost && campaign != null)
            {
#if PROBABLY_COOP_GRID_QA
                if (!ResumingCampaign) InitializeCampaignQa();
#endif
                campaign.StartHost(ResumingCampaign);
            }
            else if (localHost)
            {
                var initial = new List<InventoryEntry>
                {
                    new InventoryEntry(1001, "processed_juice", 1,
                        new InventoryPlacement(new InventoryAddress(InventoryRoot.Main), new GridPose(1, 1, 0))),
                    new InventoryEntry(1002, "toilet_paper", 1,
                        new InventoryPlacement(new InventoryAddress(InventoryRoot.Main), new GridPose(6, 1, 0))),
                    new InventoryEntry(1003, "backpack_small", 1,
                        new InventoryPlacement(new InventoryAddress(InventoryRoot.Main), new GridPose(16, 1, 0))),
                };
#if PROBABLY_COOP_GRID_QA
                GridQa.AddFixtures(initial);
#endif
                if (!world.Load(initial.ToArray()))
                    throw new InvalidOperationException("The native inventory fixture could not be initialized.");
            }
        }
        if (incoming != null || incomingCampaign != null)
        {
            CancelDrag();
            bool installed = campaign != null ? campaign.Install(incomingCampaign) : world.Load(incoming);
            incoming = null;
            incomingCampaign = null;
#if PROBABLY_COOP_GRID_QA
            if (installed) checkpointLoads++;
#endif
            if (!session.FinishLoading(ticket, installed))
            {
                session.Tick();
                return;
            }
        }
        campaign?.Tick();
        session.Tick();
        if (held != null && (!Ready || HeldByPeer(held.uniqueId))) CancelDrag();
        world.CollectRetiredItems();
#if PROBABLY_COOP_GRID_QA
        if (!FullStockQa) Audit();
        qa?.Tick();
        TickCampaignQa();
#endif
    }

    internal void RequestExit() => exitRequested = true;

    [HarmonyPatch(typeof(EscapeUIManager), nameof(EscapeUIManager.OnMainMenu))]
    private static class ReturnToMenuPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(EscapeUIManager __instance)
        {
            if (Current == null) return true;
            if (!Current.exitRequested) __instance.OnAnyClick();
            Current.RequestExit();
            return false;
        }
    }

    private void EnterScene()
    {
        localHost = room.IsHost;
        Current = this;
        SceneManager.sceneLoaded += OnSceneLoaded;
        if (localHost && campaign != null) PrepareCampaignResume();
        if (!ResumingCampaign) NewGameData.HardReset();
        if (localHost && campaign != null)
        {
            NativeQaPlatformWrites.Verify();
            if (!ResumingCampaign)
            {
                NewGameData.Instance.startType = NewGameData.StartType.WATER_MERCHANT;
                NewGameData.Instance.skipIntro = true;
                NewGameData.Instance.isInMainMenu = false;
#if PROBABLY_COOP_GRID_QA
                NewGameData.Instance.startingPerks = new List<StartingPerk>(
                    NativeActorPartnerSetup.ValidateSelection(CampaignQaPerks(true)));
#endif
                CoopRun.Armed = true;
            }
        }
        SceneManager.LoadScene("InventoryScene", LoadSceneMode.Single);
    }

    private void PrepareCampaignResume()
    {
#if PROBABLY_COOP_GRID_QA
        string[] args = Environment.GetCommandLineArgs();
        int load = Array.IndexOf(args, "--probably-coop-grid-campaign-load");
        if (load < 0) return;
        if (load + 1 >= args.Length || !Guid.TryParseExact(args[load + 1], "N", out var id) || id == Guid.Empty)
            throw new InvalidOperationException("The isolated campaign reload has no run identity.");
        var checkpoint = CoopRun.ListCheckpoints()
            .SingleOrDefault(save => save.Run == id && save.UsesEffectReplication && save.HasActors);
        if (!CoopRun.PrepareResume(checkpoint))
            throw new InvalidOperationException("The isolated campaign reload could not verify its checkpoint.");
        resumeRun = id;
#endif
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "EmporiumMenu") return;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (Current == this) Current = null;
        world.Reset();
        incoming = null;
        incomingCampaign = null;
        ticket = 0;
        resumeRun = Guid.Empty;
        held = null;
        exitRequested = leaving = false;
#if PROBABLY_COOP_GRID_QA
        audited = ulong.MaxValue;
        if (qa?.ShouldVerifyMenu == true || campaignDone && !localHost) QaGuestAwaitingMenu = true;
        checkpointLoads = 0;
#endif
    }

#if PROBABLY_COOP_GRID_QA
    private static string QaJoinAgain;
    private static bool QaRejoinedFromMenu;

    internal static void QaMenuRestored(RoomPanel panel)
    {
        QaGuestAwaitingMenu = false;
        Debug.Log("[probably_coop GRID] PASS guest returned to original menu without saving");
        Debug.Log("[probably_coop GRID] PASS native co-op menu entry restored");
        if (QaJoinAgain != null)
        {
            string code = QaJoinAgain;
            QaJoinAgain = null;
            QaRejoinedFromMenu = true;
            CampaignCheck(panel.QaOpenMenuAndJoin(code), "restored co-op menu button opens a joinable room panel");
            return;
        }
        Application.Quit();
    }
#endif

    private void ReturnToMenu()
    {
        if (leaving) return;
        leaving = true;
        try
        {
            CancelDrag();
            campaign?.Retire();
        }
        finally
        {
            CoopRun.Armed = false;
            EmporiumEntry.ResetData();
            PlayerStore.ResetData();
            StoreStation.ResetData();
            NewGameData.HardReset();
            SceneManager.LoadScene("EmporiumMenu", LoadSceneMode.Single);
        }
    }

    internal void Stop(bool quitting)
    {
#if PROBABLY_COOP_GRID_QA
        qa?.Dispose();
#endif
        session.Dispose();
        if (Current == this && !quitting) ReturnToMenu();
        else if (Current == this) campaign?.Retire();
    }

    private bool HeldByPeer(int id)
    {
        var key = EntityKey.Item(id);
        return localHost ? session.Host?.IsHeldByOther(key) == true : session.Guest?.IsHeldByOther(key) == true;
    }

    private bool BeginDrag(GameItem item)
    {
        if (!CanMove(item) || held != null) return false;
        var key = EntityKey.Item(item.uniqueId);
        if (!(localHost ? session.Host.Hold(key) : session.Guest.Hold(key))) return false;
        held = item;
        if (item is GameItemElement element) NativeGuestDragVisual.MakeTransparentToInput(element);
        return true;
    }

    private bool CanMove(GameItem item) => Ready && world.CanInteract(item) && !HeldByPeer(item.uniqueId);

    private void Release()
    {
        if (held == null) return;
        var key = EntityKey.Item(held.uniqueId);
        held = null;
        if (localHost) session.Host?.Release(key);
        else session.Guest?.Release(key);
    }

    private void CancelDrag()
    {
        if (held == null) return;
        ItemMouseDragHandler.current?.Cancel();
        Release();
    }

#if PROBABLY_COOP_GRID_QA
    private void Audit()
    {
        if (!Ready || session.Guest?.PendingCount > 0) return;
        ulong sequence = localHost ? session.Host.Sequence : session.Guest.Sequence;
        if (sequence == audited) return;
        audited = sequence;
        using var sha = SHA256.Create();
        string digest = BitConverter.ToString(sha.ComputeHash(world.Capture())).Replace("-", "");
        Debug.Log($"[probably_coop GRID] role={(localHost ? "host" : "guest")} sequence={sequence} inventory={digest}");
    }
#endif

    [HarmonyPatch(typeof(ItemMouseDragHandler), "StartDrag")]
    private static class DragPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(GameItemElement ___currentItem) => Current == null || Current.BeginDrag(___currentItem);
        private static void Postfix(bool ___isDragging) { if (!___isDragging) Current?.Release(); }
    }

    [HarmonyPatch(typeof(ItemMouseDragHandler), "EndDrag")]
    private static class EndDragPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static void Prefix(out NativeInventoryPilot __state) => __state = EnterTargetGesture();
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state)
        {
            try { __state?.ExitTargetGesture(__exception == null); }
            finally { Current?.Release(); }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ItemMouseDragHandler), "OnEventReset")]
    private static class ResetDragPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static void Postfix() => Current?.Release();
    }

    [HarmonyPatch(typeof(StoreResponse), nameof(StoreResponse.ShowAfterhourResponse))]
    private static class CampaignBackpackSelection
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static void Prefix(out NativeInventoryPilot __state)
        {
            __state = HasCampaign ? Current : null;
            if (__state == null) return;
            EmporiumEntry.Instance.selectedBackpack = null;
            __state.buildingDepartureChoices = true;
        }

        private static void Finalizer(NativeInventoryPilot __state)
        {
            if (__state != null) __state.buildingDepartureChoices = false;
        }
    }

    [HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.GetAllOwnedItemsByTag))]
    private static class CampaignAvailableBackpacks
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static void Postfix(string tag, List<GameItem> __result)
        {
            var pilot = Current;
            if (pilot?.buildingDepartureChoices == true && tag == "BACKPACK_TAG")
                __result.RemoveAll(item => !pilot.world.CanInteract(item) ||
                    item.uniqueId == pilot.campaign.OtherBackpack);
        }
    }

    [HarmonyPatch(typeof(MapUIManager), nameof(MapUIManager.Travel))]
    private static class CampaignTravelPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(AfterhourLocation location)
        {
            var game = Current?.campaign;
            if (game == null || game.Dispatching) return true;
            if (location != null)
                game.RequestTransition(TeamTransitionKind.Depart, location.Id, EmporiumEntry.Instance.selectedBackpack);
            return false;
        }
    }

    [HarmonyPatch(typeof(MapUIManager), nameof(MapUIManager.ReturnNow))]
    private static class CampaignReturnPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix()
        {
            var game = Current?.campaign;
            if (game == null || game.Dispatching) return true;
            game.RequestTransition(TeamTransitionKind.Return);
            return false;
        }
    }

    [HarmonyPatch(typeof(EndOfDayUIManager), nameof(EndOfDayUIManager.OnEndDayButtonClick))]
    private static class CampaignSleepPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix()
        {
            var game = Current?.campaign;
            if (game == null || game.Dispatching) return true;
            game.RequestTransition(TeamTransitionKind.Sleep);
            return false;
        }
    }

    [HarmonyPatch]
    private static class CampaignShutterPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(StoreShutterButton), nameof(StoreShutterButton.OpenShutter));
            yield return AccessTools.Method(typeof(StoreShutterButton), nameof(StoreShutterButton.EndAndClose));
        }

        private static bool Prefix(System.Reflection.MethodBase __originalMethod, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary) return true;
            if (!pilot.localHost)
            {
                if (pilot.Ready && __originalMethod.Name == nameof(StoreShutterButton.OpenShutter) && pilot.campaign.ShutterAvailable)
                    pilot.campaign.RequestBusiness(BusinessAction.OpenShop);
                else if (pilot.Ready && __originalMethod.Name == nameof(StoreShutterButton.EndAndClose))
                    pilot.campaign.ConfirmCloseShutter();
                return false;
            }
            if (!pilot.Ready || !pilot.campaign.BeginAuthorityAction()) return false;
            __state = pilot;
            return true;
        }

        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state)
            => CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch(typeof(StoreShutterButton), nameof(StoreShutterButton.CloseShutter))]
    private static class CampaignClosePromptPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix()
        {
            var pilot = Current;
            if (pilot?.campaign == null || pilot.localHost) return true;
            if (pilot.Ready) pilot.campaign.PromptCloseShutter();
            return false;
        }
    }

    [HarmonyPatch(typeof(ScavHelper), nameof(ScavHelper.ScavengeDumpingGrounds))]
    private static class CampaignScavengingPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix()
        {
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary) return true;
            if (pilot.Ready) pilot.campaign.RequestScavenge();
            return false;
        }
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.ActivateSlotItem))]
    private static class CampaignActivationPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(GameItem __instance, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null) return true;
            if (pilot.RunningNativeBoundary)
                return !__instance.IsTag(InsStampHelper.FOOD_STAMP_TAG) ||
                    NativeEffectCampaign.ValidStockActivation(__instance, NativeEffectCampaign.StockActivationSource(__instance));
            if (!pilot.localHost && AppraisalRankHelper.GetCertificateRank(__instance) >= 0)
            {
                if (pilot.Ready) pilot.campaign.OpenAppraisal(__instance);
                return false;
            }
            var source = NativeEffectCampaign.StockActivationSource(__instance);
            if (source == null) return true;
            if (!NativeEffectCampaign.ValidStockActivation(__instance, source) || !pilot.CanMove(__instance) ||
                !__instance.MayActivateSlotItem() || !__instance.CanActivateSlotItem()) return false;
            if (source == typeof(MaterialDirectory) && !NetworkUpgrade.IsUnlocked("RETIRED_CHEMIST"))
            {
                __state = pilot;
                pilot.confirmingStock = __instance;
                return true;
            }
            pilot.campaign.RequestItemUse(__instance, ItemUse.Activate);
            return false;
        }

        private static void Finalizer(NativeInventoryPilot __state)
        {
            if (__state != null) __state.confirmingStock = null;
        }
    }

    [HarmonyPatch(typeof(MaterialDirectory), "UnpackOrWarn")]
    private static class CampaignUnpackConfirmationPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(ref Action unpack)
        {
            var pilot = Current;
            if (pilot?.campaign?.ExecutingStockActivation == true)
            {
                unpack();
                return false;
            }
            if (pilot?.confirmingStock is GameItem pack)
            {
                ulong proof = pilot.campaign.StockProof(pack);
                unpack = () =>
                {
                    if (Current == pilot && pilot.CanMove(pack) && pilot.campaign.StockProof(pack) == proof)
                        pilot.campaign.RequestItemUse(pack, ItemUse.Activate);
                };
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.TryUnloadAll), new[] { typeof(bool) })]
    private static class CampaignUnloadPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(GameItem __instance, bool skipPinned, ref GameItem __result)
        {
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary || pilot.world.Applying ||
                !pilot.world.Tracks(__instance)) return true;
            if (pilot.CanMove(__instance)) pilot.campaign.RequestItemUse(__instance, ItemUse.Unload, skipPinned ? (byte)1 : (byte)0);
            __result = __instance;
            return false;
        }
    }

    [HarmonyPatch(typeof(ItemPinHelper), nameof(ItemPinHelper.SetPinned))]
    private static class CampaignPinPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(GameItem item, bool pinned)
        {
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary || pilot.world.Applying ||
                !pilot.world.Tracks(item)) return true;
            if (pilot.CanMove(item)) pilot.campaign.RequestItemUse(item, ItemUse.Pin, pinned ? (byte)1 : (byte)0);
            return false;
        }
    }

    [HarmonyPatch(typeof(AppraisalItemsWindow), "SetAllowed")]
    private static class CampaignAppraisalPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(AppraisalItemType type, bool allowed, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary) return true;
            if (!pilot.Ready) return false;
            if (!pilot.localHost)
            {
                pilot.campaign.RequestAppraisal(type, allowed);
                return false;
            }
            if (!pilot.campaign.BeginAuthorityAction()) return false;
            __state = pilot;
            return true;
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state)
            => CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch(typeof(AppraisalItemsWindow), nameof(AppraisalItemsWindow.Open), new[] { typeof(bool) })]
    private static class CampaignAppraisalPresentationPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix() =>
            Current?.campaign?.ApplyingGuestAppraisal != true || NativeEffectCampaign.AppraisalViewOpen;
    }

    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.ExecuteEndAction))]
    private static class CampaignDialogueResultPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(Dialogue __instance, out NativeInventoryPilot __state)
        {
            __state = null;
            if (__instance.endAction == null || __instance.isEndActionExecuted) return true;
            if (AuthoritativeCampaign && !Current.campaign.AllowPhoneEnd(__instance)) return false;
            return BeginOriginalAuthorityAction(out __state);
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.OnEstimate))]
    private static class CampaignEstimateResultPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(GameItem gameItem, int value, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null) return true;
            if (pilot.RunningNativeBoundary) return true;
            if (value < 0 || !pilot.CanMove(gameItem) ||
                gameItem.parentInventory != EmporiumEntry.Instance.backInvinvElement ||
                !gameItem.IsTag("APPRAISAL_ITEM") || PlayerStore.Instance.currentClientInstance?.GetClientBlueprint()
                    .clientIntent != StoreClient.ClientIntent.APPRAISAL_SERVICE) return false;
            if (!pilot.localHost)
            {
                pilot.campaign.RequestBusiness(BusinessAction.Estimate, value, gameItem.uniqueId);
                return false;
            }
            return BeginOriginalAuthorityAction(out __state);
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state)
            => CompleteAuthorityAction(__state, __exception);
    }

    private static bool BeginOriginalAuthorityAction(out NativeInventoryPilot pilot)
    {
        pilot = null;
        var active = Current;
        if (active?.campaign == null || active.RunningNativeBoundary) return true;
        if (!active.Ready || !active.campaign.BeginAuthorityAction()) return false;
        pilot = active;
        return true;
    }

    private static Exception CompleteAuthorityAction(NativeInventoryPilot pilot, Exception original)
    {
        if (pilot == null) return original;
        try { pilot.campaign.EndAuthorityAction(original == null); }
        catch (Exception error) { original = error; }
        if (original != null) pilot.RequestExit();
        return original;
    }
}
