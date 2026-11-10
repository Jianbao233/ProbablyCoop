using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

[HarmonyPatch(typeof(GameItemElement), nameof(GameItemElement.ValidateSelect))]
internal static class NativeGuestDragVisual
{
    private static readonly AccessTools.FieldRef<ItemMouseDragHandler, int> DragCount =
        AccessTools.FieldRefAccess<ItemMouseDragHandler, int>("currentRemove");

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
        if (NativeWorld.Guest?.IsHoldingSingleItem(__instance) != true ||
            !__instance.fakeBackground.image.enabled) return;

        var rotator = __instance.fakeRotator.rectTransform;
        var (x, y) = __instance.GetCenteredPosition();
        __state = new DragPose
        {
            Count = DragCount(ItemMouseDragHandler.current),
            Center = new Vector2(x, y),
            Pivot = rotator.pivot,
            Rotation = rotator.localRotation,
            Scale = rotator.localScale
        };
    }

    private static void Postfix(GameItemElement __instance, DragPose __state)
    {
        if (__state.Count <= 0 || __instance.IsDestroyed() ||
            NativeWorld.Guest?.IsHoldingSingleItem(__instance) != true) return;

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
    private static readonly AccessTools.FieldRef<ItemMouseDragHandler, HighlightManager> Manager =
        AccessTools.FieldRefAccess<ItemMouseDragHandler, HighlightManager>("highlightManager");
    private static readonly AccessTools.FieldRef<HighlightManager, List<TreeNodeRender>> HoverNodes =
        AccessTools.FieldRefAccess<HighlightManager, List<TreeNodeRender>>("highlightedThisNodes");
    private static readonly AccessTools.FieldRef<ItemMouseDragHandler, List<TreeNodeRender>> CandidateNodes =
        AccessTools.FieldRefAccess<ItemMouseDragHandler, List<TreeNodeRender>>("highlightedMayNodes");

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

    private static List<TreeNodeRender> Highlight(GameInventory inventory, SlotMarker slot,
        RenderHandler.BackgroundColor color, bool replace)
    {
        var drag=ItemMouseDragHandler.current;
        if(drag==null || (!NativeGuest.Active && NativeWorld.Host==null))
            return inventory.HighlightSlot(slot,color,replace);
        if(slot?.itemGridShape==null) return null;

        // The native item-target branches draw directly on the inventory.
        // Register them with the same cleanup as a normal placement preview.
        var nodes=inventory.HighlightSlot(slot,color,replace);
        if(nodes==null) return null;
        var tracked=color==RenderHandler.BackgroundColor.Blue ? CandidateNodes(drag) : HoverNodes(Manager(drag));
        foreach(var node in nodes)
            if(!tracked.Contains(node)) tracked.Add(node);
        return nodes;
    }
}

[HarmonyPatch(typeof(ItemMouseDragHandler), "OnEventRelease")]
internal static class NativeGuestDropPointer
{
    private static readonly AccessTools.FieldRef<InputActionManager, Vector3> PreviousPointer =
        AccessTools.FieldRefAccess<InputActionManager, Vector3>("lastMousePosition");
    private static readonly Action<ItemMouseDragHandler, Vector3> UpdateNativeHover =
        AccessTools.MethodDelegate<Action<ItemMouseDragHandler, Vector3>>(
            AccessTools.Method(typeof(ItemMouseDragHandler), "Drag"));

    private static void Prefix(ItemMouseDragHandler __instance, ISet<KeyCode> keysReleased,
        KeyCode ___lastMouseClickedKey, GameItemElement ___currentItem)
    {
        if (___currentItem == null || ___currentItem.IsDestroyed() ||
            NativeWorld.Guest?.IsHoldingSingleItem(___currentItem) != true ||
            !keysReleased.Contains(___lastMouseClickedKey) ||
            !ReleasedThisFrame(___lastMouseClickedKey)) return;

        // InputActionManager dispatches release before movement in the same
        // frame. Reuse the native raycast and slot rules at the release point
        // before EndDrag can submit the previous frame's inventory target.
        UpdateNativeHover(__instance, Input.mousePosition - PreviousPointer(InputActionManager.current));
    }

    private static bool ReleasedThisFrame(KeyCode key) => Input.GetKeyUp(key) ||
        key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6 &&
        Input.GetMouseButtonUp((int)key - (int)KeyCode.Mouse0);
}
