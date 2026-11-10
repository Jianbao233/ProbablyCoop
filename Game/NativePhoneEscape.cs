using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

// The host keeps the native phone coroutine alive for a remote call. Its hidden
// execution panel must not become an Escape target on the host's independent UI.
[HarmonyPatch(typeof(StoreUIManager),"CloseTopmostEscapeTarget")]
internal static class NativePhoneEscape
{
    private static bool Prefix(StoreUIManager __instance,ref bool __result)
    {
        if(NativeWorld.Host?.HostPhoneHidden!=true) return true;
        PixelWindow window=null;
        foreach(var candidate in WindowsHandler.current.visibleWindows)
            if(candidate.canMove && (window==null || candidate.focusStamp>=window.focusStamp)) window=candidate;
        GameObject panel=null; long stamp=-1;
        var canvas=__instance.paperUICanvasObject.transform;
        var panels=canvas.Cast<Transform>().Select(t=>t.gameObject).Where(p=>p.activeSelf)
            .Concat(UIDragHandler.GetActiveDockedPanels(canvas));
        foreach(var candidate in panels)
        {
            if(candidate==PhoneUIManager.Instance.phonePanel) continue;
            long value=PaperPanelFocusStamp.GetStamp(candidate);
            if(panel==null || value>=stamp) { panel=candidate; stamp=value; }
        }
        if(window==null && panel==null) { __result=false; return false; }
        if(panel==null || window!=null && window.focusStamp>stamp) WindowsHandler.CloseWindow(window);
        else if(panel==NegociationUIManager.Instance.uiObject) NegociationUIManager.Instance.CloseUI();
        else panel.SetActive(false);
        __result=true; return false;
    }
}
