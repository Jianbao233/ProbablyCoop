using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private JObject dialogueState;
    private StoreClient remoteDialogueClient;
    internal static bool ExecutingRemoteDialogue;
    internal bool RemoteDialogueOwned=>remoteDialogueClient!=null && ReferenceEquals(remoteDialogueClient,PlayerStore.Instance.currentClientInstance?.GetClientBlueprint());
    internal void MarkDialogueActor() { remoteDialogueClient=ExecutingRemoteDialogue ? PlayerStore.Instance.currentClientInstance?.GetClientBlueprint() : null; }
    private string DialogueProof()
    {
        var ui=DialogUIManager.Instance;
        return HashData(new JObject { ["dialog"]=model.Id(ui.currentDialog),
            ["epoch"]=(int)AccessTools.Field(typeof(DialogUIManager),"conversationEpoch").GetValue(ui) });
    }
    private JObject CaptureDialogue()
    {
        var ui=DialogUIManager.Instance;
        if(ui==null) return null;
        return new JObject { ["proof"]=DialogueProof(),["bellProof"]=BellProof(),["active"]=ui.currentDialog!=null,
            ["pendingStart"]=StoreUIManager.Instance.isAboutToStartMainDialog,
            ["client"]=CaptureBubble(ui.clientBubble),["player"]=CaptureBubble(ui.playerBubble),["sec"]=CaptureBubble(ui.secBubble),
            ["name"]=ui.clientNameTMP.text,["nameVisible"]=ui.clientNameBubble.activeSelf,["secNameVisible"]=ui.secNameBubble.activeSelf,
            ["choices"]=new JArray(ui.dialogChoiceBubbles.Select(b=>new JObject { ["visible"]=b.gameObject.activeSelf,
                ["text"]=b.TMP.text,["rich"]=b.TMP.richText,["enabled"]=b.IsInteractable() })) };
    }
    private void ApplyDialogue(JObject data)
    {
        if(data==null || DialogUIManager.Instance==null) return;
        dialogueState=data;
        bellToken=(string)data["bellProof"];
        StoreUIManager.Instance.isAboutToStartMainDialog=(bool)data["pendingStart"];
        var ui=DialogUIManager.Instance;
        ApplyPhoneBubble(ui.clientBubble,(JObject)data["client"]); ApplyPhoneBubble(ui.playerBubble,(JObject)data["player"]);
        ApplyPhoneBubble(ui.secBubble,(JObject)data["sec"]);
        ui.clientNameTMP.text=(string)data["name"]; ui.clientNameBubble.SetActive((bool)data["nameVisible"]);
        ui.secNameBubble.SetActive((bool)data["secNameVisible"]);
        var choices=(JArray)data["choices"];
        for(int i=0;i<ui.dialogChoiceBubbles.Count;i++)
        {
            var b=ui.dialogChoiceBubbles[i]; bool visible=i<choices.Count && (bool)choices[i]["visible"];
            b.gameObject.SetActive(visible); if(!visible) continue;
            b.TMP.richText=(bool)choices[i]["rich"]; b.TMP.text=(string)choices[i]["text"]; b.SetInteractable((bool)choices[i]["enabled"]);
        }
    }
    internal bool HasDialogue => dialogueState!=null && (bool)dialogueState["active"];
    internal bool PendingMainDialogue => dialogueState!=null && (bool)dialogueState["pendingStart"];
    internal bool RequestDialogue(int choice=-1)
    {
        JArray skip=null;
        if(choice==-1) SkipLocalBubbles(false,out skip);
        if(!HasDialogue) return skip!=null;
        var command=new JObject { ["kind"]="dialogue",["choice"]=choice,["proof"]=dialogueState["proof"] };
        if(skip!=null) command["skip"]=skip;
        bool accepted=Submit(command);
        if(accepted && skip!=null && NativeBackgroundQa.Enabled) NativeBackgroundQa.RememberBubbleSkip(false,command);
        return accepted;
    }
    private ushort ExecuteDialogue(JObject command)
    {
        var ui=DialogUIManager.Instance;
        if(shop.Settling || MapUIManager.Instance.IsOutside) return (ushort)ShopResult.Busy;
        if(ui.currentDialog==null || (string)command["proof"]!=DialogueProof()) return (ushort)ShopResult.Stale;
        if(command["skip"]!=null) return ExecuteBubbleSkip(false,command["skip"] as JArray);
        int index=(int)command["choice"];
        ExecutingRemoteDialogue=true;
        try
        {
            if(index==-1) { if(ui.IsChoice()) return (ushort)ShopResult.Unavailable; ui.OnAdvancePressed(); }
            else
            {
                if(index<0 || index>=ui.currentDialog.choices.Count || index>=ui.dialogChoiceBubbles.Count ||
                    !ui.dialogChoiceBubbles[index].gameObject.activeSelf || !ui.currentDialog.IsChoiceEnabledAt(index)) return (ushort)ShopResult.Stale;
                ui.SelectChoice(index);
            }
        }
        finally { ExecutingRemoteDialogue=false; }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
}
[HarmonyPatch(typeof(DialogUIManager),nameof(DialogUIManager.OnAdvancePressed))]
internal static class ReplicaNativeDialogueAdvance
{
    private static bool Prefix()
    {
        if(!NativeGuest.Active)
        {
            if(!NativeWorld.ExecutingRemoteDialogue && !NativeAdvanceInput.SourceAllows(NativeAdvanceInput.Target.Customer)) return false;
            NativeWorld.Host?.MarkDialogueActor(); NativeWorld.Host?.RecordAdvanceSkip(false); return true;
        }
        NativeWorld.Guest?.RequestDialogue(); return false;
    }
}
