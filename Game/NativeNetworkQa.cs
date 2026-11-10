using System;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;
internal sealed partial class NativeBackgroundQa
{
    private int wildStep,wildCash,wildFavor,wildCost,wildTab;
    private string wildId;
    private double wildNext,wildUntil;
    private bool RunWildQa(ReplicaLink link,double now)
    {
        if(wildStep==5) return true;
        if(now<wildNext || QaWaiting(link)) return false;
        if(wildStep==0)
        {
            PhoneUIManager.Instance.OpenUI(); PhoneUIManager.Instance.AutoCall(StorePhoneClient.WILD_CLIENT_NUMBER);
            Require(QaWaiting(link),"native Wild call not submitted"); wildStep=1; wildUntil=now+40; return false;
        }
        Require(now<wildUntil,"Wild phone/UI/purchase timeout step="+wildStep);
        if(wildStep==1)
        {
            Require(QaResult(link)==(ushort)ShopResult.Applied,"Wild call rejected");
            var choice=DialoguePhoneUIManager.Instance.dialogChoiceBubbles[0];
            if(!choice.gameObject.activeSelf) return false;
            choice.OnDialogChoiceClick(); wildStep=2; wildNext=now+1; return false;
        }
        if(wildStep==2)
        {
            if(!WildNetworkUI.IsOpen) return false;
            var store=PlayerStore.Instance;
            var slot=UnityEngine.Object.FindObjectsOfType<WildNetworkSlot>().FirstOrDefault(s=>store.networkUpgrade.TryGetValue(s.id,out var u) &&
                u.IsReady() && !u.IsUnlocked() && !u.isRepeatable && u.GetCost()>0 && u.GetCost()<=200);
            if(slot==null)
            {
                var activeFrame=(WildNetworkFrame)AccessTools.Field(typeof(WildNetworkUI),"frame").GetValue(null);
                var tabs=activeFrame.tabBar.GetComponentsInChildren<UnityEngine.UI.Button>();
                Require(++wildTab<tabs.Length,"no affordable ready native upgrade card");
                tabs[wildTab].onClick.Invoke(); wildNext=now+0.3; return false;
            }
            wildId=slot.id; var upgrade=store.networkUpgrade[wildId]; wildCost=upgrade.GetCost(); wildCash=store.playerCash; wildFavor=store.wildFavor;
            slot.onClick(wildId);
            var frame=(WildNetworkFrame)AccessTools.Field(typeof(WildNetworkUI),"frame").GetValue(null);
            Require(frame.useButton.interactable,"native upgrade use button disabled"); frame.useButton.onClick.Invoke();
            Require(QaWaiting(link),"native upgrade button emitted no request");
            NativeOffscreenCapture.Capture("qa-wild"); WildNetworkUI.Close();
            Require(!WildNetworkUI.IsOpen,"original upgrade window failed to close while purchase pending");
            wildStep=3; wildNext=now+1; return false;
        }
        if(wildStep==3)
        {
            Require(QaResult(link)==(ushort)ShopResult.Applied,"native upgrade purchase/queued close rejected");
            var store=PlayerStore.Instance;
            var state=(Newtonsoft.Json.Linq.JObject)AccessTools.Field(typeof(NativeWorld),"wildState").GetValue(NativeWorld.Guest);
            if((bool)state["active"]) return false;
            Require(store.networkUpgrade[wildId].IsUnlocked() && store.playerCash+store.wildFavor==wildCash+wildFavor-wildCost,"native upgrade cost/state mismatch");
            Record("PASS native-wild-phone-upgrade","phone dialogue -> actor-local original Wild UI -> native card/button; "+wildId+" cost="+wildCost);
            Record("PASS native-upgrade-close-inflight","original window closed before purchase acknowledgement; queued close applied to the same host opening exactly once");
            wildStep=4; wildNext=now+1; return false;
        }
        Require(!WildNetworkUI.IsOpen && QaResult(link)==(ushort)ShopResult.Applied,"native Wild close did not acknowledge");
        wildStep=5; return true;
    }
}
