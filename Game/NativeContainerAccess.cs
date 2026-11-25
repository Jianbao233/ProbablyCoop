using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

internal static class NativeContainerAccess
{
    internal static bool IsSealed(GameItem item) =>
        item!=null && item.IsTag(LockHelper.LOCKED_CONTAINER_TAG) && item.GetTagReadonly(LockHelper.LOCK_STATUS).GetBool();

    internal static bool BehindLock(GameInventory inventory)
    {
        if(inventory?.parent==null || inventory.parent is PixelWindow { parentItems.Count: 0 }) return false;
        return GraphUtils.FindParentType<GameItem>(inventory,IsSealed,passthrough:static _=>true)!=null;
    }

    internal static void HideContents(GameItem container)
    {
        var window=container.contentWindow;
        if(window.handler!=null && window.IsVisible()) window.Hide();
        foreach(var child in GraphUtils.FindAllChildrenType<GameItem>(window,passthrough:static _=>true))
        {
            Hide(child.contentWindow);
            Hide(child.examineWindow);
        }
    }

    private static void Hide(PixelWindow window)
    {
        if(window?.handler!=null && window.IsVisible()) window.Hide();
    }
}

[HarmonyPatch]
internal static class ReplicaLockedContainerWindow
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PixelWindow),nameof(PixelWindow.Show),System.Type.EmptyTypes);
        yield return AccessTools.Method(typeof(PixelWindow),nameof(PixelWindow.Show),new[]{typeof(Vector2),typeof(Vector2),typeof(bool)});
    }

    private static bool Prefix(PixelWindow __instance,ref PixelWindow __result)
    {
        if(!NativeGuest.Active) return true;
        foreach(var owner in __instance.parentItems)
        {
            if(!NativeContainerAccess.BehindLock(owner.parentInventory) &&
                (!ReferenceEquals(owner.contentWindow,__instance) || !NativeContainerAccess.IsSealed(owner))) continue;
            __result=__instance;
            return false;
        }
        return true;
    }
}
