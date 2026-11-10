using System;
using ProbablyCoop.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private string bellToken="";
    private string BellProof()
    {
        var store=PlayerStore.Instance;
        return HashData(new JObject {
            ["day"]=StoreStation.Instance.dayCounter,["phase"]=(int)store.storeState,
            ["client"]=model.Id(store.currentClientInstance?.GetClientBlueprint()),
            ["arrived"]=store.isClientArrived,["locked"]=store.lockNextBellFlag,
            ["queue"]=store.storeClientManager.clientStack.Count,
            ["pendingDialogue"]=StoreUIManager.Instance.isAboutToStartMainDialog,
            ["dialogue"]=DialogueProof() });
    }
    internal bool RequestBell(bool dismissOnly=false)=>Submit(new JObject { ["kind"]="bell",["proof"]=bellToken,["dismissOnly"]=dismissOnly });
    private ushort ExecuteBell(JObject command)
    {
        if(shop.Settling || MapUIManager.Instance.IsOutside) return (ushort)ShopResult.Busy;
        if((string)command["proof"]!=BellProof()) return (ushort)ShopResult.Stale;
        var store=PlayerStore.Instance;
        if(NegociationUIManager.IsTradeLocked() || Interactable.isPaused ||
            store.lockNextBellFlag>0 || store.storeState!=PlayerStore.StoreState.OPEN ||
            (store.IsClientPresent() && !store.IsClientDimissable())) return (ushort)ShopResult.Unavailable;
        if((bool?)command["dismissOnly"]==true)
        {
            if(!store.IsClientPresent()) return (ushort)ShopResult.Unavailable;
            var dismiss=UnityEngine.Object.FindObjectOfType<StoreDismissButton>();
            if(dismiss==null) return (ushort)ShopResult.Unavailable;
            dismiss.OnClick(); nextCapture=0; return (ushort)ShopResult.Applied;
        }
        var bell=UnityEngine.Object.FindObjectOfType<StoreBell>();
        if(bell==null) return (ushort)ShopResult.Unavailable;
        bell.RingBell(); nextCapture=0; return (ushort)ShopResult.Applied;
    }
}

internal sealed class NativeStoreEvents
{
    private Dialogue current;
    private uint dialogueId;

    internal void Capture(ShopView view)
    {
        var manager = DialogUIManager.Instance;
        UpdateIdentity(manager?.currentDialog);
        if (current == null || manager == null) return;
        view.DialogueId = dialogueId;
        if (manager.secBubble?.gameObject.activeSelf == true) view.DialogueSpeaker = 3;
        else if (manager.playerBubble?.gameObject.activeSelf == true) view.DialogueSpeaker = 2;
        else if (manager.clientBubble?.gameObject.activeSelf == true) view.DialogueSpeaker = 1;

        var bubble = view.DialogueSpeaker switch
        {
            1 => manager.clientBubble,
            2 => manager.playerBubble,
            3 => manager.secBubble,
            _ => null
        };
        view.DialogueLine = bubble?.textfield?.text ?? current.plainText ?? "";
        int count = Math.Min(ShopProtocol.MaxDialogueChoices,
            Math.Min(current.choices.Count, manager.dialogChoiceBubbles.Count));
        if (count == 0 || !manager.dialogChoiceBubbles[0].gameObject.activeSelf) return;
        view.DialogueChoices = new string[count];
        for (int i = 0; i < count; i++)
        {
            var choice = manager.dialogChoiceBubbles[i];
            view.DialogueChoices[i] = choice.TMP.text ?? "";
            if (choice.gameObject.activeSelf && choice.IsInteractable()) view.DialogueChoicesEnabled |= (byte)(1 << i);
        }
    }

    private void UpdateIdentity(Dialogue dialog)
    {
        if (ReferenceEquals(current, dialog)) return;
        current = dialog;
        dialogueId = dialogueId == 0xffffff ? 1 : dialogueId + 1;
    }

    internal ShopResult Apply(PlayerStore store, ShopAction action, uint entity)
    {
        if (action is ShopAction.AdvanceDialogue or ShopAction.ChooseDialogue)
        {
            var manager = DialogUIManager.Instance;
            UpdateIdentity(manager?.currentDialog);
            if (current == null || manager == null) return ShopResult.Stale;
            if (action == ShopAction.AdvanceDialogue)
            {
                if (entity != dialogueId || manager.IsChoice()) return ShopResult.Stale;
                manager.OnAdvancePressed();
                return ShopResult.Applied;
            }
            int choice = (byte)entity;
            if (entity >> 8 != dialogueId || choice >= current.choices.Count ||
                choice >= manager.dialogChoiceBubbles.Count || !manager.dialogChoiceBubbles[choice].gameObject.activeSelf ||
                !manager.dialogChoiceBubbles[choice].IsInteractable()) return ShopResult.Stale;
            manager.SelectChoice(choice);
            return ShopResult.Applied;
        }
        if (entity != 0 || NegociationUIManager.IsTradeLocked() || Interactable.isPaused)
            return ShopResult.Unavailable;
        switch (action)
        {
            case ShopAction.ToggleShutter:
                if (store.lockShutterButtonFlag > 0 || StoreShutterButton.Instance == null ||
                    (store.storeState == PlayerStore.StoreState.MORNING ? !store.CanStartDay() :
                        store.storeState != PlayerStore.StoreState.OPEN || !store.CanEndDay()))
                    return ShopResult.Unavailable;
                StoreShutterButton.Instance.OnClick();
                return ShopResult.Applied;
            case ShopAction.RingBell:
                if (store.lockNextBellFlag > 0 || store.storeState != PlayerStore.StoreState.OPEN ||
                    (store.IsClientPresent() && !store.IsClientDimissable()))
                    return ShopResult.Unavailable;
                var bell = UnityEngine.Object.FindObjectOfType<StoreBell>();
                if (bell == null) return ShopResult.Unavailable;
                bell.RingBell();
                return ShopResult.Applied;
            case ShopAction.DismissCustomer:
                if (store.lockNextBellFlag > 0 || !store.IsClientPresent() || !store.IsClientDimissable())
                    return ShopResult.Unavailable;
                store.TryPlayerDismissCurrentClient(callNextWhenPossible: false);
                return ShopResult.Applied;
            default:
                return ShopResult.Unavailable;
        }
    }
}
