using System;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private bool hostHiddenAdvanceChecked,focusMachineWasVisible,focusMachineSaved,focusTabletWasVisible;
    private readonly int[] guestAdvanceSteps=new int[2];
    private void CheckHostAdvanceFocus()
    {
        var world=NativeWorld.Host; var phone=DialoguePhoneUIManager.Instance;
        if(hostHiddenAdvanceChecked || !world.HostPhoneHidden || phone.currentDialog!=bubbleNodes[1,0] || phone.clientBubble.typewriter.isShowingText) return;
        var before=phone.currentDialog; ulong line=world.BubbleLineIdentity(phone.clientBubble);
        // Actual original method, once directly and once within the same scope
        // used by original StoreUI.Update's two calls. No OS input injection.
        phone.OnAdvancePressed();
        Require(ReferenceEquals(phone.currentDialog,before) && world.BubbleLineIdentity(phone.clientBubble)==line,
            "hidden source phone accepted unrelated local advance");
        var previous=NativeAdvanceInput.BeginSourceUpdate();
        try { DialogUIManager.Instance.OnAdvancePressed(); phone.OnAdvancePressed(); }
        finally { NativeAdvanceInput.EndSourceUpdate(previous); }
        Require(ReferenceEquals(phone.currentDialog,before) && !world.BubbleSkipObserved(phone.clientBubble),
            "source global advance leaked into the hidden partner call");
        hostHiddenAdvanceChecked=true;
        Record("PASS host-hidden-phone-advance","original hidden phone advance and original StoreUI two-sink scope did not skip or advance partner's line; authenticated phone commands remain separately allowed");
    }
    private bool RunGuestAdvanceFocus(bool phone)
    {
        int group=phone ? 1 : 0;
        if(guestAdvanceSteps[group]==0)
        {
            var machine=MachineReplica();
            if(!focusMachineSaved) { focusMachineWasVisible=machine.contentWindow.IsVisible(); focusMachineSaved=true; }
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine,new Vector2(400,240));
            Require(NativeAdvanceInput.SelectTarget()==NativeAdvanceInput.Target.None,"machine foreground did not protect background conversations");
            ulong before=NativeWorld.Guest.LastSubmittedUiRequest;
            Require(!NativeAdvanceInput.DispatchGuestAdvance() && !NativeAdvanceInput.DispatchGuestAdvance() &&
                NativeWorld.Guest.LastSubmittedUiRequest==before,"foreground machine's advance key emitted a background dialogue command");
            if(phone) PaperPanelFocusStamp.BumpPanel(PhoneUIManager.Instance.phonePanel);
            else
            {
                focusTabletWasVisible=NegociationUIManager.Instance.uiObject.activeSelf;
                NegociationUIManager.Instance.OpenUI();
            }
            Require(NativeAdvanceInput.SelectTarget()==(phone ? NativeAdvanceInput.Target.Phone : NativeAdvanceInput.Target.Customer),
                "native focus change did not select the intended conversation");
            guestAdvanceSteps[group]=1; return false;
        }
        AssertLocalBubbleSkip(phone,keyboard:true);
        if(!phone && !focusTabletWasVisible) NegociationUIManager.Instance.CloseUI();
        guestAdvanceSteps[group]=2;
        Record(phone ? "PASS guest-phone-advance-focus" : "PASS guest-customer-advance-focus",
            "native machine foreground blocks global conversation advance; native focus chooses one conversation; same-frame entry is deduplicated; focused local skip is immediate and original completion stays isolated");
        return true;
    }
    private void RestoreAdvanceFocusQa()
    {
        if(focusMachineSaved && !focusMachineWasVisible && MachineReplica().contentWindow.IsVisible())
            WindowsHandler.CloseWindow(MachineReplica().contentWindow);
    }
}
