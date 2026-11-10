using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private sealed class DocumentRead
    { internal GameItem Item; internal ulong Context; }
    private readonly Queue<DocumentRead> documentReads=new Queue<DocumentRead>();
    private ActivityRequests.Entry documentReadAttempt;
    private string documentReadToken="";
    private bool pumpingDocumentReads;
    private static readonly System.Reflection.FieldInfo DocumentTutorialStage=AccessTools.Field(typeof(TutorialUIManager),"tutorialStage");
    private static string DocumentReadProof()=>HashData(new JObject {
        ["stage"]=(int)DocumentTutorialStage.GetValue(TutorialUIManager.Instance),["enabled"]=PlayerStore.Instance.tutorialEnable });
    private void QueueDocumentRead(GameItem item)
    {
        if(documentReads.Count>=8) { CommandNotice("阅读操作仍在确认，请稍后再试。"); return; }
        documentReads.Enqueue(new DocumentRead { Item=item,Context=observedOperationContext });
        PumpDocumentReads();
    }
    private void PumpDocumentReads()
    {
        if(pumpingDocumentReads) return;
        pumpingDocumentReads=true;
        try { DispatchDocumentReads(); }
        finally { pumpingDocumentReads=false; }
    }
    private void DispatchDocumentReads()
    {
        if(host || !Ready) return;
        if(documentReadAttempt!=null)
        {
            // Preserve local read order and use the stage proof from the actual
            // post-command graph, even when a different player progressed it.
            if(!documentReadAttempt.Result.HasValue || documentReadAttempt.Serial>frontendThrough) return;
            documentReadAttempt=null;
        }
        while(documentReads.Count>0)
        {
            var read=documentReads.Peek();
            if(read.Context!=observedOperationContext || !TryData(read.Item,out var data)) { documentReads.Dequeue(); continue; }
            var command=new JObject { ["kind"]="documentRead",["id"]=data["entity"],["token"]=data["moveToken"],["proof"]=documentReadToken };
            if(!Submit(command)) return;
            documentReads.Dequeue(); documentReadAttempt=UiRequestState(LastSubmittedUiRequest);
            if(NativeBackgroundQa.Enabled) NativeBackgroundQa.RememberDocumentRead(command);
            return;
        }
    }
    private ushort ExecuteDocumentRead(JObject command)
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        if((string)command["proof"]!=DocumentReadProof() || !CurrentItem(command,out var item) || !Accessible(item.parentInventory)) return (ushort)ShopResult.Stale;
        if(!item.IsTag("TUTORIAL_PAPER_TAG") || !item.MayActivateSlotItem() || !item.CanActivateSlotItem()) return (ushort)ShopResult.Unavailable;
        if(HandTouches(item)) return (ushort)ShopResult.Busy;
        // Shared side effects of the original TutorialPage activation. Its
        // PaperUI text/layout/audio already ran on the initiating frontend.
        TutorialUIManager.Instance.OnDoubleClick(); TutorialUIManager.Instance.OnPageRead();
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
}
