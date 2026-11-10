using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

// One local advance-key press targets one visible activity. Network commands
// and explicit native buttons remain separate inputs, not frame-deduplicated.
internal static class NativeAdvanceInput
{
    internal enum Target { Unresolved=-2, Unscoped=-1, None, Customer, Phone }
    private static Target sourceScope=Target.Unscoped;
    private static int guestFrame=int.MinValue;
    private static bool Visible(GameObject panel)
    {
        if(panel==null || !panel.activeInHierarchy) return false;
        foreach(var group in panel.GetComponentsInParent<CanvasGroup>())
        {
            if(group.alpha<=0 || !group.interactable || !group.blocksRaycasts) return false;
            if(group.ignoreParentGroups) break;
        }
        return true;
    }
    private static bool CustomerAvailable()
    {
        var ui=DialogUIManager.Instance; if(ui==null) return false;
        return NativeGuest.Active ? NativeWorld.Guest?.HasDialogue==true ||
            ui.clientBubble.gameObject.activeInHierarchy || ui.playerBubble.gameObject.activeInHierarchy || ui.secBubble.gameObject.activeInHierarchy :
            ui.currentDialog!=null || ui.clientBubble.gameObject.activeInHierarchy || ui.playerBubble.gameObject.activeInHierarchy || ui.secBubble.gameObject.activeInHierarchy;
    }
    internal static Target SelectTarget()
    {
        var store=StoreUIManager.Instance; if(store==null || store.IsTyping()) return Target.None;
        GameObject front=null; long stamp=long.MinValue;
        if(WindowsHandler.current!=null) foreach(var window in WindowsHandler.current.visibleWindows)
            if(window.canMove && Visible(window.rectTransform?.gameObject) && window.focusStamp>=stamp)
            { front=window.rectTransform.gameObject; stamp=window.focusStamp; }
        var canvas=store.paperUICanvasObject?.transform;
        if(canvas!=null) foreach(var panel in canvas.Cast<Transform>().Select(child=>child.gameObject)
            .Concat(UIDragHandler.GetActiveDockedPanels(canvas)))
        {
            if(!Visible(panel)) continue;
            long candidate=PaperPanelFocusStamp.GetStamp(panel);
            if(candidate>=stamp) { front=panel; stamp=candidate; }
        }
        if(front==PhoneUIManager.Instance?.phonePanel)
        {
            bool busy=NativeGuest.Active ? NativeWorld.Guest?.SharedPhoneBusy==true : NativeWorld.Host?.SharedPhoneBusy==true;
            return busy && NativeWorld.Host?.HostPhoneHidden!=true ? Target.Phone : Target.None;
        }
        bool customer=CustomerAvailable();
        if(front==null || front==NegociationUIManager.Instance?.uiObject || front==BargainUIManager.Instance?.uiObject)
            return customer ? Target.Customer : Target.None;
        return Target.None;
    }
    internal static Target BeginSourceUpdate()
    {
        var previous=sourceScope;
        if(!NativeGuest.Active && NativeWorld.Host!=null) sourceScope=Target.Unresolved;
        return previous;
    }
    internal static void EndSourceUpdate(Target previous)=>sourceScope=previous;
    internal static bool SourceAllows(Target target)
    {
        if(sourceScope==Target.Unscoped) return true;
        if(sourceScope==Target.Unresolved) sourceScope=SelectTarget();
        return sourceScope==target;
    }
    internal static void PollGuest()
    {
        if(Application.isFocused && NativeWorld.Guest?.Ready==true && !StoreUIManager.Instance.IsTyping() &&
            KeyBindHelper.GetKeyDown("advance_dialogue")) DispatchGuestAdvance();
    }
    internal static bool DispatchGuestAdvance()
    {
        if(NativeWorld.Guest?.Ready!=true || guestFrame==Time.frameCount) return false;
        guestFrame=Time.frameCount;
        switch(SelectTarget())
        {
            case Target.Customer: DialogUIManager.Instance.OnAdvancePressed(); return true;
            case Target.Phone: DialoguePhoneUIManager.Instance.OnAdvancePressed(); return true;
            default:return false;
        }
    }
    internal static void Reset() { sourceScope=Target.Unscoped; guestFrame=int.MinValue; }
    [HarmonyPatch(typeof(StoreUIManager),"Update")]
    private static class ScopeOriginalAdvanceKey
    {
        private static void Prefix(out Target __state)=>__state=BeginSourceUpdate();
        private static Exception Finalizer(Exception __exception,Target __state)
        { EndSourceUpdate(__state); return __exception; }
    }
}
