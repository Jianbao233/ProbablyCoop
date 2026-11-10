using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed class NativeGuestEvents
{
    private static ShopLink current;
    private ShopView shown;

    internal void Tick(ShopLink link,ShopView coherentView)
    {
        current = link;
        var manager = DialogUIManager.Instance;
        var view = coherentView;
        if (manager == null) return;
        if (NativeWorld.Guest?.Ready==true)
        {
            NativeAdvanceInput.PollGuest();
            return;
        }
        if (!ReferenceEquals(shown, view))
        {
            if (view.DialogueId != 0) Render(manager, view);
            else if (shown?.DialogueId != 0 && shown != null)
            {
                manager.HideAllDialogUI();
                manager.secBubble.gameObject.SetActive(false);
                manager.secNameBubble.SetActive(false);
            }
            shown = view;
        }

        if (Application.isFocused && view.DialogueId != 0 &&
            !StoreUIManager.Instance.IsTyping() && KeyBindHelper.GetKeyDown("advance_dialogue"))
            Submit(ShopAction.AdvanceDialogue, view.DialogueId);
    }

    private static void Render(DialogUIManager manager, ShopView view)
    {
        RenderLine(manager.clientBubble, view.DialogueSpeaker == 1, view.DialogueLine);
        RenderLine(manager.playerBubble, view.DialogueSpeaker == 2, view.DialogueLine);
        RenderLine(manager.secBubble, view.DialogueSpeaker == 3, view.DialogueLine);
        manager.clientNameBubble.SetActive(view.DialogueSpeaker == 1 && view.Customer.Length > 0);
        manager.clientNameTMP.text = view.Customer;
        manager.secNameBubble.SetActive(view.DialogueSpeaker == 3);
        for (int i = 0; i < manager.dialogChoiceBubbles.Count; i++)
        {
            var bubble = manager.dialogChoiceBubbles[i];
            bool visible = i < view.DialogueChoices.Length;
            bubble.gameObject.SetActive(visible);
            if (!visible) continue;
            bubble.TMP.richText = false;
            bubble.TMP.text = view.DialogueChoices[i];
            bubble.SetInteractable((view.DialogueChoicesEnabled & (1 << i)) != 0);
        }
    }

    private static void RenderLine(TextBubble bubble, bool visible, string line)
    {
        bubble.gameObject.SetActive(visible);
        if (!visible) return;
        bubble.textfield.richText = false;
        bubble.textfield.text = line;
        bubble.textfield.maxVisibleCharacters = int.MaxValue;
    }

    internal static void Submit(ShopAction action, uint value = 0)
    {
        if (current?.View.GuestOutside == false)
            current.Submit(action, value, Time.realtimeSinceStartupAsDouble);
    }

    internal static void Reset() => current = null;

    [HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.SelectChoice))]
    private static class ChooseOnHost
    {
        private static bool Prefix(int index)
        {
            if (!NativeGuest.Active) { NativeWorld.Host?.MarkDialogueActor(); return true; }
            if(NativeWorld.Guest?.Ready==true) { NativeWorld.Guest.RequestDialogue(index); return false; }
            var view = current?.View;
            if (view?.DialogueId > 0 && index >= 0 && index < view.DialogueChoices.Length &&
                (view.DialogueChoicesEnabled & (1 << index)) != 0)
                Submit(ShopAction.ChooseDialogue, (view.DialogueId << 8) | (uint)index);
            return false;
        }
    }

    [HarmonyPatch(typeof(StoreBell), nameof(StoreBell.RingBell))]
    private static class RingHostBell
    {
        private static bool Prefix()
        {
            if (!NativeGuest.Active) return true;
            if(NativeWorld.Guest?.Ready==true) { NativeWorld.Guest.RequestBell(); return false; }
            Submit(ShopAction.RingBell);
            return false;
        }
    }
    [HarmonyPatch(typeof(StoreDismissButton), nameof(StoreDismissButton.OnClick))]
    private static class DismissHostCustomer
    {
        private static bool Prefix()
        {
            if(!NativeGuest.Active) return true;
            if(NativeWorld.Guest?.Ready==true) NativeWorld.Guest.RequestBell(dismissOnly:true);
            return false;
        }
    }
}
