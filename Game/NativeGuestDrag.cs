using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

internal static class NativeLocalDrag
{
    internal static bool IsHolding(GameItemElement item) => NativeInventoryPilot.HoldsLocalItem(item) ||
        NativeWorld.Guest?.IsHoldingSingleItem(item) == true;
}

[HarmonyPatch(typeof(GameItemElement), nameof(GameItemElement.ValidateSelect))]
internal static class NativeGuestDragVisual
{
    private static readonly FieldInfo DragCount =
        AccessTools.Field(typeof(ItemMouseDragHandler), "currentRemove");

    internal static void MakeTransparentToInput(GameItemElement item)
    {
        // Vanilla only ignores fakeBackground when finding a drop target.
        // The entire held preview, including sprite layers and counts, is visual.
        var preview = item.fakeMiddle.gameObject;
        var group = preview.GetComponent<CanvasGroup>() ?? preview.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
    }

    private struct DragPose
    {
        internal int Count;
        internal Vector2 Center;
        internal Vector2 Pivot;
        internal Quaternion Rotation;
        internal Vector3 Scale;
    }

    private static void Prefix(GameItemElement __instance, out DragPose __state)
    {
        __state = default;
        if (!NativeLocalDrag.IsHolding(__instance) ||
            !__instance.fakeBackground.image.enabled) return;

        var rotator = __instance.fakeRotator.rectTransform;
        var (x, y) = __instance.GetCenteredPosition();
        __state = new DragPose
        {
            Count = (int)DragCount.GetValue(ItemMouseDragHandler.current),
            Center = new Vector2(x, y),
            Pivot = rotator.pivot,
            Rotation = rotator.localRotation,
            Scale = rotator.localScale
        };
    }

    private static void Postfix(GameItemElement __instance, DragPose __state)
    {
        if (__state.Count <= 0 || __instance.IsDestroyed() ||
            !NativeLocalDrag.IsHolding(__instance)) return;

        // ValidateSelect updates the confirmed render but also hides the native
        // drag preview and resets its transform, even for a tag-only change.
        __instance.UpdateFakeReal(__state.Count);
        var rotator = __instance.fakeRotator.rectTransform;
        rotator.pivot = __state.Pivot;
        rotator.localRotation = __state.Rotation;
        rotator.localScale = __state.Scale;
        __instance.SetCenteredPosition(__state.Center.x, __state.Center.y);
    }
}

[HarmonyPatch(typeof(ItemMouseDragHandler), "Drag")]
internal static class NativeDragTargetHighlight
{
    private static readonly FieldInfo Manager = AccessTools.Field(typeof(ItemMouseDragHandler), "highlightManager");
    private static readonly FieldInfo HoverNodes = AccessTools.Field(typeof(HighlightManager), "highlightedThisNodes");
    private static readonly FieldInfo CandidateNodes = AccessTools.Field(typeof(ItemMouseDragHandler), "highlightedMayNodes");

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var native=AccessTools.Method(typeof(GameInventory), nameof(GameInventory.HighlightSlot));
        var tracked=AccessTools.Method(typeof(NativeDragTargetHighlight), nameof(Highlight));
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(native))
            {
                instruction.opcode=OpCodes.Call;
                instruction.operand=tracked;
            }
            yield return instruction;
        }
    }

    internal static List<TreeNodeRender> Highlight(GameInventory inventory, SlotMarker slot,
        RenderHandler.BackgroundColor color, bool replace)
    {
        var drag=ItemMouseDragHandler.current;
        if(drag==null || (!NativeInventoryPilot.OwnsScene && !NativeGuest.Active && NativeWorld.Host==null))
            return inventory.HighlightSlot(slot,color,replace);
        if(slot==null || inventory is GameGridInventory && slot.itemGridShape==null) return null;

        // The native item-target branches draw directly on the inventory.
        // Register them with the same cleanup as a normal placement preview.
        var nodes=inventory.HighlightSlot(slot,color,replace);
        if(nodes==null) return null;
        var tracked=(List<TreeNodeRender>)(color==RenderHandler.BackgroundColor.Blue
            ? CandidateNodes.GetValue(drag) : HoverNodes.GetValue(Manager.GetValue(drag)));
        foreach(var node in nodes)
            if(!tracked.Contains(node)) tracked.Add(node);
        return nodes;
    }
}

[HarmonyPatch(typeof(ItemMouseDragHandler), "OnEventRelease")]
internal static class NativeDropPointer
{
    private static readonly FieldInfo PreviousPointer =
        AccessTools.Field(typeof(InputActionManager), "lastMousePosition");

    private static void Prefix(ItemMouseDragHandler __instance, ISet<KeyCode> keysReleased,
        KeyCode ___lastMouseClickedKey, GameItemElement ___currentItem)
    {
        if (___currentItem == null || ___currentItem.IsDestroyed() || InputActionManager.current == null ||
            !keysReleased.Contains(___lastMouseClickedKey) ||
            !ReleasedThisFrame(___lastMouseClickedKey)) return;

        bool androidHostDrag = NativePlatform.IsAndroid && NativeWorld.Host != null && __instance.IsDraggingItem;
        if (!androidHostDrag && !NativeLocalDrag.IsHolding(___currentItem)) return;

        // InputActionManager dispatches release before movement in the same
        // frame. Reuse the native raycast and slot rules at the release point
        // before EndDrag can submit the previous frame's inventory target.
        var previous=(Vector3)PreviousPointer.GetValue(InputActionManager.current);
        ModReflect.Call(__instance, "Drag", Input.mousePosition - previous);
    }

    private static bool ReleasedThisFrame(KeyCode key) => Input.GetKeyUp(key) ||
        key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6 &&
        Input.GetMouseButtonUp((int)key - (int)KeyCode.Mouse0);
}
