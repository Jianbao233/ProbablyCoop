using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProbablyCoop;

/// <summary>Native scene backed by host snapshots and operation requests.</summary>
internal sealed class NativeGuest
{
    internal static bool Active { get; private set; }
    internal static bool MenuExitRequested { get; private set; }
    internal bool EnteredShop { get; private set; }
    private readonly Dictionary<uint, GameItemElement> replicas = new Dictionary<uint, GameItemElement>();
    private bool awaitingReply;
    private readonly NativeGuestEvents events = new NativeGuestEvents();
    private readonly NativeGuestTrade trade = new NativeGuestTrade();
    private readonly NativeWorld world = new NativeWorld(false);
    private readonly ReplicaLink.Stamp connectionStamp;

    private NativeGuest(CoopSession session) { connectionStamp=session.Native.CaptureStamp(); }
    internal bool Matches(CoopSession session) => session!=null && connectionStamp.IsCurrent(session.Native);

    internal static NativeGuest Enter(CoopSession session)
    {
        if (session?.State != LinkState.Connected || !session.Shop.View.Active ||
            SceneManager.GetActiveScene().name != "EmporiumMenu" || NewGameData.Instance == null)
            return null;

        // An untouched new-run descriptor lets EmporiumEntry build the original UI without a load slot.
        NewGameData.HardReset();
        Active = true;
        SceneManager.LoadScene("InventoryScene", LoadSceneMode.Single);
        Debug.Log("[probably_coop] opening native guest scene");
        return new NativeGuest(session);
    }

    internal void Tick(CoopSession session)
    {
        if (SceneManager.GetActiveScene().name != "InventoryScene") return;
        EnteredShop = true;
        var link = session.Shop;
        var entry = EmporiumEntry.Instance;
        if (!link.View.Active || !PlayerStore.IsInstanceExist() ||
            entry == null || entry.invElement == null) return;
        if (!world.TickGuest(session.Native, replicas)) return;
        trade.Tick(link,world.View,world.ShopPresentation);
        events.Tick(link,world.View);
        if (awaitingReply && !link.Waiting && link.Result is not (ShopResult.None or ShopResult.Applied))
            StoreUIManager.Instance?.Notify(ModHelper.GetLocalized("probably_coop", "result_" + link.Result));
        awaitingReply = link.Waiting;
        // Original input handlers resolve local windows/slots; business sinks emit requests.
    }

    internal bool AuditProjection(ShopView view, out string detail) => world.AuditProjection(out detail);
    internal GameItemElement Replica(uint id) => replicas.TryGetValue(id, out var item) ? item : null;

    internal bool FindQaPlacement(ShopItem item, byte container, out ShopPlacement placement)
    {
        placement = default;
        if (!replicas.TryGetValue(item.Id, out var replica)) return false;
        var entry = EmporiumEntry.Instance;
        GameGridInventory grid = container switch
        {
            1 => entry.invElement,
            2 => entry.backInvinvElement,
            7 => entry.showcaseElement,
            _ => null
        };
        var slot = grid?.TryFindOneValidInventorySlot(replica, keepOrientation: true);
        if (slot?.IsValid() != true || slot.targetItem != null || slot.itemGridShape == null ||
            slot.numTransfer != replica.unitCount) return false;
        var shape = slot.itemGridShape;
        placement = new ShopPlacement(container, (byte)shape.minX, (byte)shape.minY,
            (byte)((shape.flipped ? 4 : 0) | shape.orientation));
        return true;
    }

    internal static void ReturnToMenu()
    {
        if (!Active) return;
        NativeWorld.Guest?.Close();
        NativeGuestTrade.Reset();
        NativeGuestEvents.Reset();
        if (SceneManager.GetActiveScene().name == "InventoryScene")
        {
            EmporiumEntry.ResetData();
            PlayerStore.ResetData();
            StoreStation.ResetData();
            NewGameData.HardReset();
            SceneManager.LoadScene("EmporiumMenu", LoadSceneMode.Single);
            return;
        }
        Active = false;
        MenuExitRequested = false;
    }

    [HarmonyPatch(typeof(EscapeUIManager), nameof(EscapeUIManager.OnMainMenu))]
    private static class LeaveGuestFromNativeMenu
    {
        private static bool Prefix(EscapeUIManager __instance)
        {
            if (!Active) return true;
            if (!MenuExitRequested) __instance.OnAnyClick();
            // Keep the scene alive until the runtime has released the replica
            // and room. Vanilla resets the store before loading the menu.
            MenuExitRequested = true;
            return false;
        }
    }

    [HarmonyPatch]
    private static class PreventNativeMutations
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.InitialSave));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.SaveGame));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.LoadGame));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.TryCallNextClient));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.HandleSkipIntro));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.EndNight));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AddedItemToDisplayCase));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.RemovedItemToDisplayCase));
            yield return AccessTools.Method(typeof(StoreStation), nameof(StoreStation.StartDay));
            yield return AccessTools.Method(typeof(EmporiumEntry), nameof(EmporiumEntry.AddStartingItem));
            yield return AccessTools.Method(typeof(StartingPerkContent), nameof(StartingPerkContent.HandleNewGamePerkBeforeItems));
            yield return AccessTools.Method(typeof(StartingPerkContent), nameof(StartingPerkContent.HandleNewGamePerk));
            yield return AccessTools.Method(typeof(Interactable), nameof(Interactable.OnDoubleClick));
            yield return AccessTools.Method(typeof(StoreUIManager), "Update");
            yield return AccessTools.Method(typeof(StoreService), nameof(StoreService.UpdateCost));
        }

        private static bool Prefix() => !Active;
    }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.OnClick))]
    private static class InteractWithReplica
    {
        private static bool Prefix(Interactable __instance)
        {
            if (!Active) return true;
            if(NativeWorld.Guest?.Ready!=true) return false;
            if(__instance.GetComponent<StoreCalendar>()!=null || __instance.GetComponent<StoreCreditDisplay>()!=null ||
                __instance.GetComponent<StorePC>()!=null || __instance.GetComponent<StoreTransferButton>()!=null ||
                __instance.GetComponent<StorePhoneObject>()!=null || __instance.GetComponent<StoreSecButton>()!=null ||
                __instance.GetComponent<FeedbackBox>()!=null || __instance.GetComponent<NewsPaper>()!=null ||
                __instance.GetComponent<StoreClienteleDisplay>()!=null || __instance.GetComponent<StoreRecorder>()!=null ||
                __instance.GetComponent<StoreMagnifyingGlass>()!=null || __instance.GetComponent<StorePoster>()!=null ||
                __instance.GetComponent<StoreVendingMachine>()!=null || __instance.GetComponent<StoreVendingFountain>()!=null ||
                __instance.GetComponent<StoreScale>()!=null || __instance.GetComponent<Shotgun>()!=null) return true;
            if (__instance.GetComponent<StoreBedroll>() != null) NativeDayUi.ShowGuestPlan();
            else if (__instance.GetComponent<StoreDoor>() != null) return true;
            else if (__instance.GetComponent<StoreTablet>() != null && NativeWorld.Guest?.Ready==true) return true;
            else if (__instance.GetComponent<StoreClientMono>() != null) NativeGuestTrade.Toggle();
            else if (__instance.GetComponent<StoreShutterButton>() != null) return true;
            else if (__instance.GetComponent<StoreBell>() != null || __instance.GetComponent<StoreDismissButton>() != null) return true;
            else if (__instance.GetComponent<StoreNotebook>() != null) NotebookUIManager.Instance?.Toggle();
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.PreLoadStore))]
    private static class PreventPreLoad
    {
        private static bool Prefix(ref PlayerStore __result)
        {
            if (!Active) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.StartNewGame))]
    private static class InitializeReplica
    {
        private static bool Prefix(PlayerStore __instance)
        {
            if (!Active) return true;
            __instance.tutorialEnable = false;
            __instance.networkUpgrade = NetworkUpgrade.InitUpgradeDict();
            __instance.storeService = StoreService.InitService();
            __instance.storeClientData = new StoreClientData();
            __instance.saveVersion = PlayerStore.VERSION;
            StoreReputation.InitReputation();
            EmporiumOption.Instance.OnArriveGameScene();
            StoreVendingFountain.Instance.gameObject.SetActive(false);
            StoreVendingMachine.Instance.gameObject.SetActive(false);
            return false;
        }
    }

    [HarmonyPatch(typeof(EmporiumEntry), "Update")]
    private static class RenderWithoutSimulation
    {
        private static bool Prefix(EmporiumEntry __instance)
        {
            if (!Active) return true;
            __instance.Validate(force: false);
            if (NativeWorld.Guest?.Ready == true)
            {
                StoreUIManager.Instance.CheckRaycast();
                if (StoreUIManager.Instance.hoveringStoreUI) __instance.DisableAllHandlersExceptDrag();
                else __instance.EnableAllHandlers();
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.EnableAllHandlers))]
    private static class DisableReplicaInteraction
    {
        private static bool Prefix(EmporiumEntry __instance)
        {
            // The native setup also disables automatic tool selection on left
            // press. Without that policy, selectable items can capture the
            // press before ItemMouseDragHandler can start its gesture.
            if (!Active || NativeWorld.Guest?.Ready == true) return true;
            ItemTooltipHandler.current.Enable();
            return false;
        }
    }
}
