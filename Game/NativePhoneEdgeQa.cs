using System;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;
internal sealed partial class NativeBackgroundQa
{
    private int phoneEdgeStep,phoneEdgeCash,phoneEdgeQueue;
    private double phoneEdgeUntil,phoneEdgeNext;
    private bool RunPhoneEdges(ReplicaLink link,double now)
    {
        if(phoneEdgeStep==4) return true;
        if(now<phoneEdgeNext) return false;
        var ui=PhoneUIManager.Instance; var store=PlayerStore.Instance;
        if(phoneEdgeStep==0)
        {
            Require(!StorePhoneClient.IsNumberExist(999999999),"QA invalid phone number now exists");
            phoneEdgeCash=store.playerCash; phoneEdgeQueue=store.futurStoreClientIdQueue.Count;
            ui.OpenUI();
            var key=ui.phonePanel.GetComponentsInChildren<PhoneButton>(true).First(b=>b.number==9);
            for(int i=0;i<9;i++) key.SimulateClick();
            Require((long)AccessTools.Field(typeof(PhoneUIManager),"currentNumber").GetValue(ui)==999999999,"native number keys did not maintain local draft");
            phoneEdgeStep=1; phoneEdgeUntil=now+25; return false;
        }
        Require(now<phoneEdgeUntil,"native phone edge timeout step="+phoneEdgeStep);
        if(phoneEdgeStep==1)
        {
            if(!NativeWorld.Guest.SharedPhoneBusy) return false;
            phoneEdgeStep=2; return false;
        }
        if(phoneEdgeStep==2)
        {
            if(QaWaiting(link) || NativeWorld.Guest.SharedPhoneBusy) return false;
            Require(ui.statusDisplay.text==LocHelper.GetLocalizedUI("ui_phone_no_answer") && ui.numberDisplay.text=="","native no-answer status/number differs from original: status="+ui.statusDisplay.text+" number="+ui.numberDisplay.text);
            Require(store.playerCash==phoneEdgeCash && store.futurStoreClientIdQueue.Count==phoneEdgeQueue,"invalid number caused business effects");
            Record("PASS native-phone-number-no-answer","original numeric keys -> host native calling timer -> no-answer and cleared original number display; no business mutation");
            ui.AutoCall(StorePhoneClient.RED_IMP_REP_NUMBER); Require(QaWaiting(link),"cancel-race dial did not start");
            ui.CloseUI();
            phoneEdgeStep=3; phoneEdgeNext=now+3; return false;
        }
        if(QaWaiting(link) || NativeWorld.Guest.SharedPhoneBusy) return false;
        Require(!ui.phonePanel.activeSelf && store.futurStoreClientIdQueue.Count==phoneEdgeQueue && store.playerCash==phoneEdgeCash,"immediate phone close left effects or reopened local panel");
        Record("PASS native-phone-close-before-ack","close immediately after quick dial; queued hangup released host phone and did not enqueue another delivery");
        phoneEdgeStep=4; return true;
    }
}
