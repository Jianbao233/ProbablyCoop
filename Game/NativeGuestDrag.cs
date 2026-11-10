using System;
using System.Collections.Generic;
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
