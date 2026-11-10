using System;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int serviceStep,requestCash,requestReward,bulkQuantity;
    private double serviceNext,serviceDeadline;
    private bool hostPhoneIndependence;
    private byte[] fulfilledRequest;
    private byte[] beforeTransferIntent;
    private StoreRequestElement pendingPcRow;
    private bool requestReplaySent;
    private StoreReputation requestReputation;
    private double requestReputationBefore;
    private void CheckHostPhoneIndependence()
    {
        var world=NativeWorld.Host;
        if(hostPhoneIndependence || world?.RemotePhoneOwned!=true || !world.HostPhoneHidden) return;
        var dialog=DialoguePhoneUIManager.Instance;
        if(dialog.currentDialog==null || !dialog.dialogChoiceBubbles.Any(b=>b.gameObject.activeSelf)) return;
        var before=dialog.currentDialog; string status=PhoneUIManager.Instance.statusDisplay.text;
        // Invoke the game's own panel lifecycle on the private QA desktop. No OS input.
        PhoneUIManager.Instance.OpenUI();
        Require(!world.HostPhoneHidden && PhoneUIManager.Instance.statusDisplay.text==status,"host phone reveal reset the remote call");
        PhoneUIManager.Instance.CloseUI();
        Require(world.HostPhoneHidden && world.SharedPhoneBusy && dialog.currentDialog==before,"host close cancelled guest conversation");
        AdvCalendarUIManager.Instance.OpenUI();
        Require((bool)AccessTools.Method(typeof(StoreUIManager),"CloseTopmostEscapeTarget").Invoke(StoreUIManager.Instance,null),"host Escape target did not close visible panel");
        Require(world.SharedPhoneBusy && world.HostPhoneHidden && dialog.currentDialog==before,"host local navigation interrupted remote telephone");
        hostPhoneIndependence=true;
        Record("PASS phone-host-independent-view","host open/close/Escape retain guest-owned native call and active coroutine");
    }
    private void SeedServices()
    {
        var request=StoreRequestList.RevPeat();
        PlayerStore.Instance.storeRequests.Add(request);
        if(!PlayerStore.Instance.storeRequests.Any(r=>r.id=="medshortage_ll"))
            PlayerStore.Instance.storeRequests.Add(StoreRequestList.MedShortageLL());
        var paper=DirectoryMaster.Item("newspaper"); GeneralHelper.SetItemOwned(paper,true);
        Require(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.invElement,paper)==0 && paper.parentInventory!=null,"QA newspaper does not fit");
        foreach(string id in new[]{"water_guide","handnote","intel_sec","joe_card","procurement_slip","slip","sec_slip"})
        {
            var document=DirectoryMaster.Item(id); GeneralHelper.SetItemOwned(document,true);
            document.EnableTag("PROBABLY_COOP_QA_DOCUMENT");
            if(id=="handnote")
            {
                document.ModifyTag("HANDNOTE_TITLE",s=>s.Enable().SetString("合作备忘"));
                document.ModifyTag("HANDNOTE_BODY",s=>s.Enable().SetString("房主维护商品与现金，双方独立阅读同一份便条。"));
                document.ModifyTag("HANDNOTE_SIGN",s=>s.Enable().SetString("QA"));
            }
            if(id=="procurement_slip")
            {
                document.ModifyTag("PROCUREMENT_HEADER",s=>s.Enable().SetString("合作采购单"));
                document.ModifyTag("PROCUREMENT_CLIENT_NAME",s=>s.Enable().SetString("QA采购员"));
                document.ModifyTag("PROCUREMENT_ORDER_BODY",s=>s.Enable().SetString("<b>货物</b> ×2\n检查完整正文"));
                document.ModifyTag("PROCUREMENT_FINAL_REWARD",s=>s.Enable().SetInt(17));
                document.ModifyTag("PROCUREMENT_DAY",s=>s.Enable().SetInt(3));
            }
            if(id=="slip")
            {
                document.ModifyTag("SLIP_TITLE",s=>s.Enable().SetString("合作纸条"));
                document.ModifyTag("SLIP_BODY",s=>s.Enable().SetString("<b>原版纸条</b>\n双方各自阅读，不提交交易。"));
            }
            if(id=="sec_slip")
            {
                document.ModifyTag("title",s=>s.Enable().SetString("QA安保单据"));
                document.ModifyTag("body",s=>s.Enable().SetString("<b>登记内容</b>\n保留房主提供的全部正文。"));
            }
            Require(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.invElement,document)==0 && document.parentInventory!=null,"QA document does not fit: "+id);
        }
        CaptureHostPaperFonts();
        for(int i=0;i<request.requiredItemCount;i++)
        {
            var item=DirectoryMaster.Item(request.requiredItem); GeneralHelper.SetItemOwned(item,true);
            var slot=EmporiumEntry.Instance.backInvinvElement.TryFindOneValidInventorySlot(item);
            Require(slot?.IsValid()==true && slot.TryAcceptOnce()==item.unitCount,"request fixture does not fit");
        }
        StorePhoneClient.GetWildPhoneClient().phoneState=StorePhoneClient.PhoneState.Regular;
        StorePhoneClient.GetBrewerPhoneClient().phoneState=StorePhoneClient.PhoneState.Regular;
        PlayerStore.Instance.QueueFuturClient("wild_upgrade_reminder",2);
        PlayerStore.Instance.QueueFuturClient("annoyedEngineer",1);
        PlayerStore.Instance.QueueFuturClient("farmerMinor",2);
        PlayerStore.Instance.QueueFuturClient("wanted8",2);
        PlayerStore.Instance.QueueFuturClient("clinicVolunteer",2);
        Record("FIXTURE services","native rev_peat request, four owned items on back counter, regular brewer contact; day 8 native upgrade reminder and farmer ensure mixed story/business coverage; QA only");
    }
    private bool RunServicesQa(ReplicaLink link,double now)
    {
        if(serviceStep==8) return true;
        if(now<serviceNext || QaWaiting(link)) return false;
        if(serviceStep>0) Require(now<serviceDeadline,"phone/PC/bulk timeout step="+serviceStep);
        var store=PlayerStore.Instance; var e=EmporiumEntry.Instance;
        if(serviceStep==0)
        {
            var previousIntent=NativeWorld.Guest.PcIntent(store.storeRequests.Single(r=>r.id=="rev_peat"));
            Require(previousIntent!=null,"no PC inventory proof before bulk transfer");
            beforeTransferIntent=System.Text.Encoding.UTF8.GetBytes(previousIntent.ToString(Newtonsoft.Json.Formatting.None));
            bulkQuantity=e.invElement.childItems.Concat(e.backInvinvElement.childItems).Sum(i=>i.unitCount);
            e.frontInvWindow.Hide();
            UnityEngine.Object.FindObjectOfType<StoreTransferButton>().OnClick();
            Require(QaWaiting(link),"native transfer button emitted no request");
            serviceStep=1; serviceDeadline=now+25; return false;
        }
        if(serviceStep==1 || serviceStep==3)
        {
            if(QaWaiting(link)) return false;
            Require(QaResult(link)==(ushort)ShopResult.Applied,"native service rejected step="+serviceStep+" result="+QaResult(link));
            serviceStep++; serviceNext=now+0.8; return false;
        }
        if(serviceStep==2)
        {
            Require(!e.backInvinvElement.childItems.Any(GeneralHelper.IsItemOwned),"bulk transfer left owned counter items");
            Require(bulkQuantity==e.invElement.childItems.Concat(e.backInvinvElement.childItems).Sum(i=>i.unitCount),"bulk quantity conservation failed");
            Record("PASS native-bulk-counter","original transfer button moved all owned back-counter items; unowned retained; exact quantity conserved");
            var pc=PCUIManager.Instance.storePC;
            Require(pc.letter.activeSelf && pc.dot.activeSelf && !PCUIManager.Instance.panel.activeSelf,"guest computer did not notify unopened request");
            pc.OnClick();
            Require(PCUIManager.Instance.panel.activeSelf && !pc.dot.activeSelf,"original computer click failed to clear local unread indicator");
            Record("PASS native-pc-unread","unopened guest computer shows new mail; original click opens original rows and clears only local unread marker");
            var row=PCUIManager.Instance.panel.GetComponentsInChildren<StoreRequestElement>().First(r=>r.storeRequest.id=="rev_peat");
            Require(PCUIManager.Instance.panel.GetComponentsInChildren<StoreRequestElement>().Any(r=>r.storeRequest.id=="medshortage_ll"),"second live PC request row missing");
            UnityEngine.Object.FindObjectOfType<FeedbackBox>().GetComponent<Interactable>().OnClick();
            Require(ReputationUIManager.Instance.uiPanel.activeSelf,"original feedback box did not open native reputation UI");
            requestReputation=store.storeReputations.First(r=>r.factionId==row.storeRequest.factionId);
            requestReputationBefore=requestReputation.GetReputationExact();
            Require(row.acceptButton.interactable,"original PC fulfil button unavailable");
            requestCash=store.playerCash; requestReward=row.storeRequest.creditReward;
            var intent=NativeWorld.Guest.PcIntent(row.storeRequest);
            Require(intent!=null && NativeWorld.Guest.PcIntent(new StoreRequest { id=row.storeRequest.id })==null,
                "original PC click must use a current request and complete projected inventory");
            fulfilledRequest=System.Text.Encoding.UTF8.GetBytes(intent.ToString(Newtonsoft.Json.Formatting.None));
            Require(!beforeTransferIntent.SequenceEqual(fulfilledRequest) && link.Submit(beforeTransferIntent,now),
                "bulk transfer did not invalidate the old PC proof");
            pendingPcRow=row; serviceStep=23; serviceNext=now+0.8; return false;
        }
        if(serviceStep==23)
        {
            Require(QaResult(link)==(ushort)ShopResult.Stale && store.playerCash==requestCash && pendingPcRow!=null &&
                PCUIManager.Instance.panel.activeSelf && pendingPcRow.acceptButton.interactable &&
                store.storeRequests.Contains(pendingPcRow.storeRequest),"stale PC delivery altered reward, original button or order");
            Record("PASS pc-live-inventory-proof","pre-transfer declaration rejected; current original row and cash retained");
            pendingPcRow.acceptButton.onClick.Invoke(); Require(QaWaiting(link),"native PC row emitted no request");
            pendingPcRow=null;
            serviceStep=3; return false;
        }
        if(serviceStep==4)
        {
            Require(store.playerCash==requestCash+requestReward && !store.storeRequests.Any(r=>r.id=="rev_peat"),"PC delivery reward/request mismatch");
            Require(store.storeRequests.Any(r=>r.id=="medshortage_ll") &&
                PCUIManager.Instance.panel.GetComponentsInChildren<StoreRequestElement>().Any(r=>r.storeRequest.id=="medshortage_ll"),
                "fulfilling one request removed a different original PC row");
            Record("PASS independent-pc-requests","two original computer rows; one host delivery and reward; the other job remains available");
            Require(!PCUIManager.Instance.panel.GetComponentsInChildren<StoreRequestElement>().Any(r=>r.storeRequest.id=="rev_peat"),"PC row not refreshed");
            if(!requestReplaySent) { Require(link.Submit(fulfilledRequest,now),"duplicate PC request not submitted"); requestReplaySent=true; serviceNext=now+0.8; return false; }
            Require(QaResult(link)==(ushort)ShopResult.Stale,"fulfilled PC request accepted twice");
            Record("PASS native-pc-duplicate","obsolete fulfilled request with new request ID rejected; cash reward remained exactly once");
            Record("PASS native-pc-delivery","native row -> host FillRequest; cash="+requestCash+"->"+store.playerCash+"; request removed");
            Require(ReferenceEquals(requestReputation,store.storeReputations.First(r=>r.factionId==requestReputation.factionId)) && requestReputation.GetReputationExact()>requestReputationBefore,"live reputation projection lost original UI object or reward");
            var reputationRow=ReputationUIManager.Instance.layout.GetComponentsInChildren<ReputationElement>().First(r=>r.reputationTitle.text==requestReputation.factionDisplay);
            Require(reputationRow.reputationNumber.text==requestReputation.GetReputationExact().ToString("F2")+"/200","open reputation UI did not update after native PC reward");
            PCUIManager.Instance.HideUI();
            CaptureReputationView();
            Record("PASS native-reputation-live","original feedback box and original rows stay open through PC fulfil; shared amount updates in place, perk references retained");
            ReputationUIManager.Instance.CloseUI();
            PCUIManager.Instance.HideUI(); PhoneUIManager.Instance.OpenUI(); PhoneUIManager.Instance.AutoCall(StorePhoneClient.RED_IMP_REP_NUMBER);
            Require(QaWaiting(link),"native phone quick dial emitted no request");
            serviceStep=5; serviceDeadline=now+40; serviceNext=now+0.8; return false;
        }
        if(QaWaiting(link)) return false;
        var phone=DialoguePhoneUIManager.Instance;
        if(serviceStep==5)
        {
            Require(QaResult(link)==(ushort)ShopResult.Applied,"phone call rejected result="+QaResult(link));
            if(!phone.dialogChoiceBubbles[0].gameObject.activeSelf) return false;
            Require(phone.dialogChoiceBubbles[0].IsInteractable(),"native phone option disabled");
            NativeOffscreenCapture.Capture("qa-phone");
            phone.SelectChoice(0); Require(QaWaiting(link),"native phone choice emitted no request");
            serviceStep=6; serviceNext=now+1; return false;
        }
        if(serviceStep==6)
        {
            Require(QaResult(link)==(ushort)ShopResult.Applied,"phone choice rejected");
            if(StorePhoneClient.GetBrewerPhoneClient().phoneState!=StorePhoneClient.PhoneState.Cooldown) return false;
            Record("PASS native-phone-order","native phone panel and option -> host brewer order/cooldown; no guest dialogue factory");
            PhoneUIManager.Instance.CloseUI(); Require(QaWaiting(link),"native phone close emitted no hangup");
            serviceStep=7; serviceNext=now+0.8; return false;
        }
        Require(QaResult(link)==(ushort)ShopResult.Applied && !NativeWorld.Guest.SharedPhoneBusy,"phone close failed");
        Record("PASS native-phone-hangup","original close cancels authoritative conversation and local draft"); serviceStep=8; return true;
    }
    private bool hostReputationShown,hostReputationCaptured;
    private double hostReputationAt;
    private void CheckHostInformation(double now)
    {
        if(hostReputationCaptured || !observedConnection || PlayerStore.Instance.storeRequests.Any(r=>r.id=="rev_peat")) return;
        if(!hostReputationShown) { AdvCalendarUIManager.Instance.CloseUI(); ReputationUIManager.Instance.OpenUI(); hostReputationShown=true; hostReputationAt=now+1; return; }
        if(now<hostReputationAt) return;
        CaptureReputationView(); ReputationUIManager.Instance.CloseUI(); hostReputationCaptured=true;
    }
    private static void CaptureReputationView()
    {
        var ui=ReputationUIManager.Instance;
        NativeOffscreenCapture.Capture("qa-reputation");
        var texts=new Newtonsoft.Json.Linq.JArray(ui.layout.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(t=>new Newtonsoft.Json.Linq.JObject {
            ["name"]=t.name,["text"]=t.text,["active"]=t.gameObject.activeInHierarchy,["font"]=t.font?.name,
            ["position"]=t.transform.position.ToString(),["scale"]=t.transform.lossyScale.ToString(),["layer"]=t.gameObject.layer,
            ["canvas"]=t.canvas?.name,["camera"]=t.canvas?.worldCamera?.name,["cull"]=t.canvasRenderer.cull,["alpha"]=t.canvasRenderer.GetAlpha(),
            ["bounds"]=Newtonsoft.Json.Linq.JObject.FromObject(NativeOffscreenCapture.Bounds(t.rectTransform)) }));
        ModHelper.WriteModDataFileText("probably_coop","qa-reputation.json",new Newtonsoft.Json.Linq.JObject { ["ui"]=NativeNegotiationProbe.Capture(ui.layout.transform),["diagnostics"]=texts }.ToString());
    }
}
