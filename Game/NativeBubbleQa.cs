using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Febucci.UI;
using Febucci.UI.Core;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine.Events;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string BubbleRole="qaBubble",BubbleDone="QA_BUBBLE_DONE",BubbleFactory="probably_coop_qa_bubble";
    private const long BubbleNumber=900067;
    private static readonly string BubbleText="QA native line "+string.Concat(Enumerable.Repeat("local animation ",18));
    private static readonly byte[][] bubbleSkipCommands=new byte[2][];
    private static readonly Dictionary<TypewriterCore,int> bubbleShows=new Dictionary<TypewriterCore,int>();
    private readonly Dictionary<TypewriterCore,UnityAction> bubbleShowListeners=new Dictionary<TypewriterCore,UnityAction>();
    private readonly Dialogue[,] bubbleNodes=new Dialogue[2,2];
    private readonly int[,] bubbleEnds=new int[2,2];
    private readonly bool[,] bubbleConfirmed=new bool[2,2];
    private (TypewriterByCharacter Writer,float Wait)[] bubbleWaits;
    private bool bubbleOldEffect,bubbleOldAuto,bubbleOptionsSaved,bubbleHostArmed;
    private StoreClientInstance bubblePreviousClient;
    private bool bubblePreviousArrived;
    private int bubbleHostPhase,bubbleFactoryCalls,bubbleStep,bubbleGroup,bubbleLocalEnds;
    private readonly bool typewriterOnly=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-typewriter-only")>=0;
    private double bubbleQaHostFinishAt;
    private double bubbleDeadline,bubbleSampleUntil;
    private ulong bubbleFirstLine,bubbleSecondLine,bubbleSampleRevision;
    private int bubbleSampleChars,bubbleInitialShows;
    private string bubbleFirstText;
    private static readonly FieldInfo BubbleApplied=AccessTools.Field(typeof(NativeWorld),"applied");
    private static readonly FieldInfo BubbleNpcCurrent=AccessTools.Field(typeof(DialogUIManager),"currentDialog");
    private static readonly FieldInfo BubbleNpcDisplaying=AccessTools.Field(typeof(DialogUIManager),"displayingUncompletedDialog");
    private static readonly FieldInfo BubblePhoneCurrent=AccessTools.Field(typeof(DialoguePhoneUIManager),"currentDialog");
    private static readonly FieldInfo BubblePhoneDisplaying=AccessTools.Field(typeof(DialoguePhoneUIManager),"displayingUncompletedDialog");
    private static readonly FieldInfo BubbleNpcState=AccessTools.Field(typeof(NativeWorld),"dialogueState");
    private static readonly FieldInfo BubblePhoneState=AccessTools.Field(typeof(NativeWorld),"phoneState");
    internal static void RememberBubbleSkip(bool phone,JObject command)=>bubbleSkipCommands[phone ? 1 : 0]??=Encoding.UTF8.GetBytes(command.ToString(Formatting.None));
    private static string BubbleReady(int group)=>"QA_BUBBLE_READY_"+group;
    private static string BubbleAck(int group,int index)=>"QA_BUBBLE_ACK_"+group+"_"+index;
    private static TextBubble BubbleUi(bool phone)=>phone ? DialoguePhoneUIManager.Instance.clientBubble : DialogUIManager.Instance.clientBubble;
    private static int BubbleChars(TextBubble bubble)=>bubble.typewriter.TextAnimator.Characters.Count(character=>character.isVisible);
    private void SaveBubbleOptions()
    {
        Require(!bubbleOptionsSaved,"bubble options saved twice");
        var option=EmporiumOption.Instance;
        bubbleOldEffect=option.enableTypewriterEffect; bubbleOldAuto=option.autoNextDialog;
        option.enableTypewriterEffect=true; option.autoNextDialog=false;
        bubbleWaits=new[]{DialogUIManager.Instance.clientBubble,DialogUIManager.Instance.playerBubble,DialogUIManager.Instance.secBubble,
            DialoguePhoneUIManager.Instance.clientBubble,DialoguePhoneUIManager.Instance.playerBubble}
            .Select(bubble=>(bubble.typewriter,bubble.typewriter.waitForNormalChars)).ToArray();
        foreach(var row in bubbleWaits) row.Writer.waitForNormalChars=host ? 0.001f : 0.03f;
        if(!host) foreach(var row in bubbleWaits)
        {
            var writer=row.Writer;
            UnityAction listener=()=> { bubbleShows.TryGetValue(writer,out int count); bubbleShows[writer]=count+1; };
            bubbleShowListeners.Add(writer,listener); writer.onTypewriterStart.AddListener(listener);
        }
        bubbleOptionsSaved=true;
    }
    private void RestoreBubbleOptions()
    {
        Require(bubbleOptionsSaved,"bubble options restoration missing");
        EmporiumOption.Instance.enableTypewriterEffect=bubbleOldEffect; EmporiumOption.Instance.autoNextDialog=bubbleOldAuto;
        foreach(var row in bubbleWaits) row.Writer.waitForNormalChars=row.Wait;
        foreach(var pair in bubbleShowListeners) pair.Key.onTypewriterStart.RemoveListener(pair.Value);
        bubbleShowListeners.Clear();
        bubbleOptionsSaved=false;
    }
    private void SeedBubbleQa()
    {
        ((MachineryPanelRow)qaMachine.contentWindow.childElement).Add(new MachineryPanel("Bubble QA",
            new MachineryButton("QA native typewriter",AdvanceHostBubbleQa),BubbleRole));
    }
    private void AdvanceHostBubbleQa()
    {
        if(bubbleHostPhase==0)
        {
            Require(DialogUIManager.Instance.currentDialog==null && !NativeWorld.Host.SharedPhoneBusy,"bubble fixture starts during live business dialogue");
            Require(!PlayerStore.Instance.PhoneClientDict.ContainsKey(BubbleNumber) && !PhoneClientList.DialogFuncs.ContainsKey(BubbleFactory),"bubble QA contact collided with existing content");
            SaveBubbleOptions();
            // ContinueConversation deliberately refuses an absent customer.
            // Use a real native instance without starting arrival/business
            // timers, and restore the previous source context after this probe.
            bubblePreviousClient=PlayerStore.Instance.currentClientInstance;
            bubblePreviousArrived=PlayerStore.Instance.isClientArrived;
            PlayerStore.Instance.currentClientInstance=StoreClientInstance.CreateClientInstance(StoreClientList.CreateSpacerClientFood());
            for(int group=0;group<2;group++)
            {
                int index=group;
                var first=new Dialogue().SetText("CLIENT",BubbleText).SetEndAction(()=>bubbleEnds[index,0]++);
                var second=first.NextDialogue().SetText("CLIENT",BubbleText).SetEndAction(()=>bubbleEnds[index,1]++);
                second.NextDialogue().SetText("CLIENT","QA manual hold").NextDialogue().SetText("CLIENT","QA finished");
                bubbleNodes[group,0]=first; bubbleNodes[group,1]=second;
            }
            PhoneClientList.DialogFuncs.Add(BubbleFactory,_=> { bubbleFactoryCalls++; return bubbleNodes[1,0]; });
            PlayerStore.Instance.PhoneClientDict.Add(BubbleNumber,new StorePhoneClient {
                phoneState=StorePhoneClient.PhoneState.Regular,locID=StorePhoneClient.GetBrewerPhoneClient().locID,dialogFuncId=BubbleFactory });
            Require(DialogUIManager.Instance.StartDialogue(bubbleNodes[0,0]),"native NPC typewriter fixture refused");
            bubbleHostArmed=true; bubbleHostPhase=1;
            return;
        }
        if(bubbleHostPhase==1)
        {
            Require(bubbleConfirmed[0,1] && ReferenceEquals(DialogUIManager.Instance.currentDialog,bubbleNodes[0,1]),"NPC same-text line did not retain its native identity");
            DialogUIManager.Instance.EndConversation();
            PlayerStore.Instance.currentClientInstance=bubblePreviousClient; PlayerStore.Instance.isClientArrived=bubblePreviousArrived;
            bubbleHostPhase=2; return;
        }
        Require(bubbleHostPhase==2 && bubbleConfirmed[1,1] && !NativeWorld.Host.SharedPhoneBusy && bubbleFactoryCalls==1,
            "native phone fixture did not finish once");
        for(int group=0;group<2;group++) for(int line=0;line<2;line++) Require(bubbleEnds[group,line]==1,"native typewriter end action repeated or missing");
        PlayerStore.Instance.PhoneClientDict.Remove(BubbleNumber); PhoneClientList.DialogFuncs.Remove(BubbleFactory);
        RestoreBubbleOptions(); bubbleHostPhase=3; bubbleHostArmed=false; qaMachine.EnableTag(BubbleDone);
        Record("PASS host-native-typewriters","two original NPC/phone same-text lines; source already complete when guest skipped, no unintended advance; each native end action once; source options/contact restored");
    }
    private void CheckHostBubbleQa()
    {
        if(!bubbleHostArmed) return;
        CheckHostAdvanceFocus();
        for(int group=0;group<2;group++)
        {
            bool phone=group==1; var bubble=BubbleUi(phone);
            var node=phone ? DialoguePhoneUIManager.Instance.currentDialog : DialogUIManager.Instance.currentDialog;
            for(int index=0;index<2;index++)
            {
                Require(bubbleEnds[group,index]<=1,"native typewriter end action duplicated");
                if(bubbleConfirmed[group,index] || !ReferenceEquals(node,bubbleNodes[group,index]) || bubble.typewriter.isShowingText) continue;
                if(index==0 && !qaMachine.IsTag(BubbleReady(group))) qaMachine.EnableTag(BubbleReady(group));
                if(!NativeWorld.Host.BubbleSkipObserved(bubble) || bubbleEnds[group,index]!=1) continue;
                bubbleConfirmed[group,index]=true; qaMachine.EnableTag(BubbleAck(group,index));
            }
        }
    }
    private void OriginalBubbleQaButton()
    {
        var button=(MachineryButton)((MachineryPanelRow)MachineReplica().contentWindow.childElement).Panels.Single(panel=>panel.role==BubbleRole).content;
        AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(button,null);
    }
    private void AssertLocalBubbleSkip(bool phone,bool keyboard=false)
    {
        object manager=phone ? DialoguePhoneUIManager.Instance : (object)DialogUIManager.Instance;
        var current=phone ? BubblePhoneCurrent : BubbleNpcCurrent;
        var displaying=phone ? BubblePhoneDisplaying : BubbleNpcDisplaying;
        var previous=current.GetValue(manager); var oldDisplaying=displaying.GetValue(manager);
        var sentinel=new Dialogue().SetText("CLIENT","local completion sentinel").SetEndAction(()=>bubbleLocalEnds++);
        try
        {
            current.SetValue(manager,sentinel); displaying.SetValue(manager,sentinel);
            string before=BubbleDebug(phone);
            if(keyboard)
            {
                ulong previousRequest=NativeWorld.Guest.LastSubmittedUiRequest;
                Require(NativeAdvanceInput.DispatchGuestAdvance(),"focused advance key was not dispatched");
                ulong firstRequest=NativeWorld.Guest.LastSubmittedUiRequest;
                Require(firstRequest!=previousRequest && !NativeAdvanceInput.DispatchGuestAdvance() &&
                    NativeWorld.Guest.LastSubmittedUiRequest==firstRequest,"same-frame advance key emitted two requests");
            }
            else if(phone) DialoguePhoneUIManager.Instance.OnAdvancePressed(); else DialogUIManager.Instance.OnAdvancePressed();
            Record("TRACE native-typewriter-skip",before+" -> "+BubbleDebug(phone));
            var animator=BubbleUi(phone).typewriter.TextAnimator;
            // Native SkipTypewriter intentionally retains appearance effects.
            // allLettersShown also waits for those fades; the input-frame
            // contract is the original visibility flags and stopped writer.
            Require(!BubbleUi(phone).typewriter.isShowingText && BubbleChars(BubbleUi(phone))==animator.CharactersCount &&
                animator.firstVisibleCharacter==0 && animator.maxVisibleCharacters>=animator.CharactersCount,
                "original local typewriter did not skip in its input frame");
            Require(ReferenceEquals(displaying.GetValue(manager),sentinel) && !sentinel.isEndActionExecuted,
                "guest native typewriter completion entered the original business callback");
        }
        finally { current.SetValue(manager,previous); displaying.SetValue(manager,oldDisplaying); }
    }
    private static string BubbleDebug(bool phone)
    {
        var bubble=BubbleUi(phone); var writer=bubble.typewriter; var animator=writer.TextAnimator;
        return "phone="+phone+" typing="+writer.isShowingText+" visible="+BubbleChars(bubble)+"/"+animator.CharactersCount+
            " first="+animator.firstVisibleCharacter+" max="+animator.maxVisibleCharacters+" all="+animator.allLettersShown+
            " active="+bubble.gameObject.activeInHierarchy+" line="+NativeWorld.Guest.BubbleLineIdentity(bubble);
    }
    private bool RunBubbleQa(ReplicaLink link,double now)
    {
        if(bubbleStep==99) return true;
        if(bubbleDeadline==0) bubbleDeadline=now+75;
        Require(now<bubbleDeadline,"native typewriter timed out group="+bubbleGroup+" step="+bubbleStep);
        if(QaWaiting(link)) return false;
        var world=NativeWorld.Guest; bool phone=bubbleGroup==1; var bubble=BubbleUi(phone);
        switch(bubbleStep)
        {
            case 0:
                SaveBubbleOptions(); OriginalBubbleQaButton(); bubbleStep=1; return false;
            case 1:
                if(!MachineReplica().IsTag(BubbleReady(bubbleGroup))) return false;
                Require(bubble.gameObject.activeInHierarchy && bubble.typewriter.isShowingText,"guest did not run its own native typewriter after the source finished");
                bubbleFirstLine=world.BubbleLineIdentity(bubble); bubbleFirstText=bubble.textfield.text;
                bubbleInitialShows=bubbleShows[bubble.typewriter];
                bubbleSampleChars=BubbleChars(bubble); bubbleSampleRevision=(ulong)BubbleApplied.GetValue(world); bubbleSampleUntil=now+3;
                bubbleStep=2; return false;
            case 2:
                Require(now<bubbleSampleUntil,"native local typewriter made no progress between two identical applied snapshots");
                int chars=BubbleChars(bubble); ulong revision=(ulong)BubbleApplied.GetValue(world);
                if(revision!=bubbleSampleRevision || chars<=bubbleSampleChars)
                { bubbleSampleRevision=revision; bubbleSampleChars=chars; return false; }
                Require(bubbleShows[bubble.typewriter]==bubbleInitialShows,"an unchanged snapshot restarted the native typewriter");
                if(!RunGuestAdvanceFocus(phone)) return false;
                Require(QaWaiting(link),"original text advance sent no bound skip request");
                bubbleStep=3; return false;
            case 3:
                if(!MachineReplica().IsTag(BubbleAck(bubbleGroup,0))) return false;
                Require(world.BubbleLineIdentity(bubble)==bubbleFirstLine && bubbleShows[bubble.typewriter]==bubbleInitialShows,
                    "skip of an already completed source line advanced or replayed the line");
                if(phone) DialoguePhoneUIManager.Instance.OnAdvancePressed(); else DialogUIManager.Instance.OnAdvancePressed();
                bubbleStep=4; return false;
            case 4:
                if(world.BubbleLineIdentity(bubble)==bubbleFirstLine) return false;
                Require(bubble.typewriter.isShowingText && bubble.textfield.text==bubbleFirstText && bubbleShows[bubble.typewriter]==bubbleInitialShows+1,
                    "a different original line with identical text did not restart exactly once");
                bubbleSecondLine=world.BubbleLineIdentity(bubble);
                var obsolete=JObject.Parse(Encoding.UTF8.GetString(bubbleSkipCommands[bubbleGroup]));
                var state=(JObject)(phone ? BubblePhoneState : BubbleNpcState).GetValue(world);
                obsolete["proof"]=state["proof"];
                Require(link.Submit(Encoding.UTF8.GetBytes(obsolete.ToString(Formatting.None)),now),"cannot replay previous line's skip under current dialogue proof");
                bubbleStep=5; return false;
            case 5:
                Require(QaResult(link)==(ushort)ShopResult.Stale && world.BubbleLineIdentity(bubble)==bubbleSecondLine,
                    "old line skip changed a new line with the same text");
                AssertLocalBubbleSkip(phone); bubbleStep=6; return false;
            case 6:
                if(!MachineReplica().IsTag(BubbleAck(bubbleGroup,1))) return false;
                Require(bubbleLocalEnds==0,"guest completion scheduled a delayed native end action");
                NativeOffscreenCapture.Capture(phone ? "qa-native-phone-typewriter" : "qa-native-dialog-typewriter");
                Record(phone ? "PASS native-phone-typewriter" : "PASS native-dialog-typewriter",
                    "original local geometry reveals characters without a newer snapshot; stable line does not restart, identical text/new line does; input-frame skip, completed-source no-op, obsolete-line rejection and no guest business completion");
                if(phone) { PhoneUIManager.Instance.CloseUI(); bubbleStep=8; }
                else { OriginalBubbleQaButton(); bubbleStep=7; }
                return false;
            case 7:
                if(world.HasDialogue) return false;
                bubbleGroup=1; PhoneUIManager.Instance.OpenUI(); PhoneUIManager.Instance.AutoCall(BubbleNumber); bubbleStep=1; return false;
            case 8:
                if(world.SharedPhoneBusy) return false;
                OriginalBubbleQaButton(); bubbleStep=9; return false;
            case 9:
                if(!MachineReplica().IsTag(BubbleDone) || PlayerStore.Instance.PhoneClientDict.ContainsKey(BubbleNumber)) return false;
                Require(bubbleLocalEnds==0,"guest native completion executed a shared callback");
                RestoreBubbleOptions(); RestoreAdvanceFocusQa(); bubbleStep=99; return true;
        }
        return false;
    }
}
