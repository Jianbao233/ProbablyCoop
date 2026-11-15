using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

// The guest does not run StoreUIManager's business/debug Update. Keep its local
// hit testing and ordinary panel/navigation keys alive independently of snapshots.
[HarmonyPatch(typeof(StoreUIManager),"Update")]
internal static class NativeLocalInput
{
    private static void Postfix(StoreUIManager __instance)
    {
        if (NativeInventoryPilot.CampaignReplica && NativeInventoryPilot.Interactive)
        {
            if (!Application.isFocused || __instance.IsTyping()) return;
            MachineStorageCoordinator.HandleInput();
            HopperHelper.HandleInput();
            if (KeyBindHelper.GetBackDown() && !GameOptionUIManager.ConsumedEscapeThisFrame() &&
                !MachineStorageCoordinator.ConsumedEscapeThisFrame() && !HopperHelper.ConsumedEscapeThisFrame())
                __instance.ResolveEscape();
            if (KeyBindHelper.GetKeyDown("advance_dialogue"))
            {
                switch (NativeAdvanceInput.SelectTarget())
                {
                    case NativeAdvanceInput.Target.Customer: DialogUIManager.Instance.OnAdvancePressed(); break;
                    case NativeAdvanceInput.Target.Phone: DialoguePhoneUIManager.Instance.OnAdvancePressed(); break;
                }
            }
            return;
        }
        if(!NativeGuest.Active) NativeWorld.Host?.CloseInaccessibleLogistics();
        if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true) return;
        __instance.CheckRaycast();
        if(__instance.isMagnifyingMode) AccessTools.Method(typeof(StoreUIManager),"UpdateMagnifyCamera").Invoke(__instance,null);
        NativeWorld.Guest.TickLogistics();
        if(!Application.isFocused) return;
        __instance.gameOptionUIManager?.PollKeyCapture();
        if(!__instance.IsTyping() && KeyBindHelper.GetKeyDown("control_hints")) __instance.ToggleControlHintOverlay();
        if(!__instance.IsTyping())
        {
            ToolboxHelper.HandleHotkeys();
            MachineStorageCoordinator.HandleInput();
            HopperHelper.HandleInput();
            ItemPinHelper.HandleInput();
        }
        if(KeyBindHelper.GetBackDown() && !GameOptionUIManager.ConsumedEscapeThisFrame() &&
            !ItemPinHelper.ConsumedEscapeThisFrame() && !MachineStorageCoordinator.ConsumedEscapeThisFrame() &&
            !HopperHelper.ConsumedEscapeThisFrame()) __instance.ResolveEscape();
        if(!__instance.IsTyping() && KeyBindHelper.GetKeyDown("trash_item") && !MapUIManager.Instance.IsOutside &&
            !ItemMouseDragHandler.current.IsDraggingItem && !UIDragHandler.isDraggingAnyUI)
        {
            var item=RenderHandler.RaycastElement<GameItemElement>(Input.mousePosition);
            if(item!=null && GeneralHelper.IsItemOwned(item) && item!=EmporiumEntry.Instance.trashcan &&
                EmporiumEntry.Instance.trashcan?.contentWindow?.child is GameInventory trash && item.parentInventory!=trash)
                NativeWorld.Guest.RequestGroup(new[]{item},trash);
        }
    }
}
