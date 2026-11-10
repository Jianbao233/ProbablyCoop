using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private static readonly FieldInfo[] PhoneFields=typeof(StorePhoneClient).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private int phoneOwner;
    private JObject phoneState;
    private CanvasGroup phoneCanvas;
    private bool phoneHidden,phoneCanvasCreated;
    private float phoneAlpha;
    private bool phoneInteractable,phoneRaycasts;
    private bool phoneDialPending,phoneCloseRequested;
    private ulong phoneDialRequest;
    private string phoneStopProof;
    internal static bool ExecutingPhone;
    private sealed class PhoneEndGeneration { internal int Value; }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dialogue,PhoneEndGeneration> phoneEnds=new System.Runtime.CompilerServices.ConditionalWeakTable<Dialogue,PhoneEndGeneration>();
    private int phoneCancelGeneration;
    internal void TrackPhoneEnd(Dialogue dialogue)
    { if(dialogue!=null) { phoneEnds.Remove(dialogue); phoneEnds.Add(dialogue,new PhoneEndGeneration { Value=phoneCancelGeneration }); } }
    internal void CancelPhoneEnds()=>phoneCancelGeneration++;
    internal bool PhoneEndAllowed(Dialogue dialogue) => !phoneEnds.TryGetValue(dialogue,out var generation) || generation.Value==phoneCancelGeneration;
    private static object PhoneField(string name)=>AccessTools.Field(typeof(PhoneUIManager),name).GetValue(PhoneUIManager.Instance);
    private static void SetPhoneField(string name,object value)=>AccessTools.Field(typeof(PhoneUIManager),name).SetValue(PhoneUIManager.Instance,value);
    private bool PhoneBusy => (bool)PhoneField("isTalking") || (bool)PhoneField("isCalling") || (float)PhoneField("sequenceTimer")>0 || (float)PhoneField("clickTimer")>0;
    private string PhoneProof()
    {
        var dialog=DialoguePhoneUIManager.Instance.currentDialog;
        return HashData(new JObject { ["dialog"]=model.Id(dialog),["epoch"]=(int)AccessTools.Field(typeof(DialoguePhoneUIManager),"conversationEpoch").GetValue(DialoguePhoneUIManager.Instance),
            ["number"]=(long)PhoneField("currentNumber"),["owner"]=phoneOwner,["talking"]=PhoneField("isTalking")==null ? false : (bool)PhoneField("isTalking") });
    }
    private static JObject CaptureBubble(TextBubble bubble)=>Host.CaptureNativeBubble(bubble);
    private JObject CapturePhone(NativeDeferredFields fields)
    {
        var ui=PhoneUIManager.Instance; var dialog=DialoguePhoneUIManager.Instance;
        if(ui==null || dialog==null) return null;
        if(!PhoneBusy) { phoneOwner=0; ReleaseHiddenPhone(); }
        else if(phoneOwner==0) phoneOwner=1;
        return new JObject { ["owner"]=phoneOwner,["proof"]=PhoneProof(),["number"]=(long)PhoneField("currentNumber"),["status"]=ui.statusDisplay.text,
            ["client"]=CaptureBubble(dialog.clientBubble),["player"]=CaptureBubble(dialog.playerBubble),["name"]=dialog.clientNameTMP.text,
            ["nameVisible"]=dialog.clientNameBubble.activeSelf,
            ["choices"]=new JArray(dialog.dialogChoiceBubbles.Select(b=>new JObject { ["visible"]=b.gameObject.activeSelf,["text"]=b.TMP.text,["enabled"]=b.IsInteractable() })),
            ["contacts"]=new JObject(PlayerStore.Instance.PhoneClientDict.Select(p=>new JProperty(p.Key.ToString(),fields.CaptureBusinessFields(p.Value,PhoneFields)))) };
    }
    private void ApplyPhoneBubble(TextBubble bubble,JObject data)=>ApplyNativeBubble(bubble,data);
    private void ApplyPhone(JObject data)
    {
        if(data==null || PhoneUIManager.Instance==null || DialoguePhoneUIManager.Instance==null) return;
        bool contactsChanged=!JToken.DeepEquals(phoneState?["contacts"],data["contacts"]);
        int previousOwner=phoneOwner;
        phoneState=data; phoneOwner=(int)data["owner"];
        if(phoneOwner!=0) phoneDialPending=false;
        if(previousOwner!=0 && phoneOwner==0) { ClearPhoneDraft(); phoneCloseRequested=false; phoneStopProof=null; }
        var store=PlayerStore.Instance;
        foreach(var p in ((JObject)data["contacts"]).Properties())
        {
            long number=long.Parse(p.Name);
            if(!store.PhoneClientDict.TryGetValue(number,out var contact)) store.PhoneClientDict[number]=contact=new StorePhoneClient();
            ReadBusinessFields(contact,PhoneFields,(JObject)p.Value);
        }
        foreach(long number in store.PhoneClientDict.Keys.Where(n=>data["contacts"][n.ToString()]==null).ToArray()) store.PhoneClientDict.Remove(number);
        var ui=PhoneUIManager.Instance; var dialog=DialoguePhoneUIManager.Instance;
        if(contactsChanged && ui.phoneBookUIManager.gameObject.activeInHierarchy)
        {
            var book=ui.phoneBookUIManager;
            var rows=book.layoutGroup.GetComponentsInChildren<ContactElement>();
            var numbers=store.PhoneClientDict.Where(p=>p.Value.ShownInPhoneBook()).Select(p=>p.Key).ToArray();
            if(!rows.Select(r=>r.number).SequenceEqual(numbers)) book.OpenUI();
            else foreach(var row in rows) row.OnInit(row.number);
        }
        if(phoneOwner!=0) { SetPhoneField("clickTimer",-1f); ui.numberDisplay.text=((long)data["number"]).ToString(); ui.statusDisplay.text=(string)data["status"]; }
        else if((float)PhoneField("clickTimer")<=0) ui.statusDisplay.text=(string)data["status"];
        ApplyPhoneBubble(dialog.clientBubble,(JObject)data["client"]); ApplyPhoneBubble(dialog.playerBubble,(JObject)data["player"]);
        dialog.clientNameBubble.SetActive((bool)data["nameVisible"]); dialog.clientNameTMP.text=(string)data["name"];
        var choices=(JArray)data["choices"];
        for(int i=0;i<dialog.dialogChoiceBubbles.Count;i++)
        {
            var b=dialog.dialogChoiceBubbles[i]; bool visible=i<choices.Count && (bool)choices[i]["visible"];
            b.gameObject.SetActive(visible); if(!visible) continue;
            b.TMP.text=(string)choices[i]["text"]; b.SetInteractable((bool)choices[i]["enabled"]);
        }
    }
    internal bool RequestPhone(string action,long value=0)
    {
        JArray skip=null;
        if(action=="advance" && SkipLocalBubbles(true,out skip)) action="skip";
        if(UiPending("phone")) return false;
        var command=new JObject { ["kind"]="phone",["action"]=action,["value"]=value,["proof"]=phoneState?["proof"] };
        if(skip!=null) command["skip"]=skip;
        bool sent=phoneState!=null && Submit(command);
        if(sent && skip!=null && NativeBackgroundQa.Enabled) NativeBackgroundQa.RememberBubbleSkip(true,command);
        if(sent && (action=="dial" || action=="quick")) { phoneDialRequest=LastSubmittedUiRequest; phoneDialPending=true; phoneStopProof=null; }
        if(sent && action=="stop") phoneStopProof=(string)phoneState["proof"];
        return sent;
    }
    private void ObservePhoneReply(ulong serial,ushort result)
    {
        if(serial!=phoneDialRequest) return;
        phoneDialRequest=0;
        if(result!=(ushort)ShopResult.Applied) { phoneDialPending=false; phoneCloseRequested=false; }
    }
    internal void RequestPhoneClose()
    {
        phoneCloseRequested=phoneOwner==2 || phoneDialPending;
        if(phoneOwner==2 && !UiPending("phone")) RequestPhone("stop");
        ClearPhoneDraft();
    }
    private ushort ExecutePhone(JObject command)
    {
        if(shop.Settling || MapUIManager.Instance.IsOutside) return (ushort)ShopResult.Busy;
        string action=(string)command["action"];
        if((string)command["proof"]!=PhoneProof()) return (ushort)ShopResult.Stale;
        var ui=PhoneUIManager.Instance; var dialog=DialoguePhoneUIManager.Instance;
        ExecutingPhone=true;
        try
        {
            if(action=="dial" || action=="quick")
            {
                long number=(long)command["value"];
                if(PhoneBusy || number<1 || number>999999999) return (ushort)ShopResult.Busy;
                phoneOwner=2;
                if(!ui.phonePanel.activeSelf) HideHostPhone();
                if(action=="quick") ui.AutoCall(number);
                else { SetPhoneField("currentNumber",number); ui.StartCalling(); }
            }
            else if(action=="stop") { if(phoneOwner!=2) return (ushort)ShopResult.Unavailable; ui.StopCall(); phoneOwner=0; ReleaseHiddenPhone(); }
            else if(action=="skip") return ExecuteBubbleSkip(true,command["skip"] as JArray);
            else if(action=="advance") { if(dialog.currentDialog==null) return (ushort)ShopResult.Stale; dialog.OnAdvancePressed(); }
            else if(action=="choice")
            {
                int index=(int)command["value"];
                if(dialog.currentDialog==null || index<0 || index>=dialog.currentDialog.choices.Count || index>=dialog.dialogChoiceBubbles.Count ||
                    !dialog.dialogChoiceBubbles[index].gameObject.activeSelf || !dialog.currentDialog.IsChoiceEnabledAt(index)) return (ushort)ShopResult.Stale;
                dialog.SelectChoice(index);
            }
            else return (ushort)ShopResult.Unavailable;
        }
        finally { ExecutingPhone=false; }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    internal void TickPhoneInput()
    {
        var ui=PhoneUIManager.Instance;
        if(ui==null || phoneState==null) return;
        if(phoneCloseRequested && phoneOwner==2 && !UiPending("phone") && phoneStopProof!=(string)phoneState["proof"]) RequestPhone("stop");
        float timer=(float)PhoneField("clickTimer");
        if(timer>0) { timer-=Time.deltaTime; SetPhoneField("clickTimer",timer); if(timer<=0) RequestPhone("dial",(long)PhoneField("currentNumber")); }
        if(!ui.phonePanel.activeSelf) return;
        if(phoneOwner==0) ui.numberDisplay.text=(long)PhoneField("currentNumber")==0 ? "" : PhoneField("currentNumber").ToString();
        if(Application.isFocused) for(int i=0;i<=9;i++) if(Input.GetKeyDown(KeyCode.Keypad0+i)) AccessTools.Method(typeof(PhoneUIManager),"PressKeypad").Invoke(ui,new object[]{i});
    }
    internal bool RemotePhoneOwned=>phoneOwner==2;
    private ReplicaLink.Stamp peerStamp;
    internal void UpdatePeerConnection(bool connected,ReplicaLink connection)
    {
        if(closed) return;
        connected=connected && connection!=null;
        if(peerConnected!=connected || connection!=null && !peerStamp.IsCurrent(connection))
        {
            peerGeneration++; nextCapture=0;
            // Consent and temporary ownership belong to a connection, not to
            // player slot 2 forever. A new partner must explicitly agree again.
            if(host)
            {
                ResetDynamicChoice();
                ClearFrontends();
                authorityBasis=null; authorityBaseRevision=0; awaitingFull=null; verifiedRecordPublish=0;
                int released=PartnerHandCount;
                ResetPartnerHand();
                runClock.Reset();
                if(NativeBackgroundQa.Enabled && released!=0)
                    Debug.Log("[probably_coop QA] peer-generation released native hand items="+released);
                if(phoneOwner==2) EndSharedPhone();
                proposedTrip=""; tripReady=0; tripProof=""; proposedBackpack=0; proposedBackpackProof="";
                remoteViews.Clear(); remoteTools.Clear(); viewsExpiry=0;
                remoteHover=remoteInterest=servedInterest=0; ClearTooltipSchedule();
            }
        }
        peerStamp=connection?.CaptureStamp() ?? default;
        peerConnected=connected;
    }
    internal bool HostPhoneHidden=>phoneHidden;
    internal bool SharedPhoneBusy=>host ? PhoneBusy : phoneOwner!=0;
    internal void HideHostPhone()
    {
        var panel=PhoneUIManager.Instance.phonePanel;
        if(phoneCanvas==null)
        {
            phoneCanvas=panel.GetComponent<CanvasGroup>(); phoneCanvasCreated=phoneCanvas==null;
            if(phoneCanvasCreated) phoneCanvas=panel.AddComponent<CanvasGroup>();
            phoneAlpha=phoneCanvas.alpha; phoneInteractable=phoneCanvas.interactable; phoneRaycasts=phoneCanvas.blocksRaycasts;
        }
        phoneHidden=true; phoneCanvas.alpha=0; phoneCanvas.interactable=false; phoneCanvas.blocksRaycasts=false;
        panel.SetActive(true);
    }
    internal void RevealHostPhone()
    {
        if(phoneCanvas==null) return;
        phoneHidden=false; phoneCanvas.alpha=phoneAlpha; phoneCanvas.interactable=phoneInteractable; phoneCanvas.blocksRaycasts=phoneRaycasts;
    }
    private void ReleaseHiddenPhone()
    {
        if(phoneCanvas==null) return;
        bool hidden=phoneHidden; RevealHostPhone();
        if(hidden) PhoneUIManager.Instance.phonePanel.SetActive(false);
        if(phoneCanvasCreated) UnityEngine.Object.Destroy(phoneCanvas);
        phoneCanvas=null;
    }
    internal void ClearPhoneDraft() { SetPhoneField("clickTimer",-1f); SetPhoneField("currentNumber",0L); PhoneUIManager.Instance.numberDisplay.text=""; }
    internal void EndSharedPhone()
    {
        if(!host || PhoneUIManager.Instance==null) return;
        ExecutingPhone=true;
        try { PhoneUIManager.Instance.StopCall(); phoneOwner=0; ReleaseHiddenPhone(); }
        finally { ExecutingPhone=false; }
    }
}
[HarmonyPatch(typeof(PhoneUIManager),nameof(PhoneUIManager.OpenUI))]
internal static class ReplicaPhoneOpenView
{
    private static void Prefix(PhoneUIManager __instance,ref string __state)
    { if(NativeWorld.Host?.SharedPhoneBusy==true) __state=__instance.statusDisplay.text; }
    private static void Postfix(PhoneUIManager __instance,string __state)
    { if(!NativeGuest.Active) { NativeWorld.Host?.RevealHostPhone(); if(__state!=null) __instance.statusDisplay.text=__state; } }
}
[HarmonyPatch(typeof(PhoneUIManager),nameof(PhoneUIManager.CloseUI))]
internal static class ReplicaPhoneCloseView
{ private static bool Prefix() { if(NativeGuest.Active || NativeWorld.Host?.RemotePhoneOwned!=true || NativeWorld.ExecutingPhone) return true; NativeWorld.Host.HideHostPhone(); return false; } }
[HarmonyPatch(typeof(PhoneUIManager),"Update")]
internal static class ReplicaPhoneTick
{ private static bool Prefix() { if(!NativeGuest.Active) return true; NativeWorld.Guest?.TickPhoneInput(); return false; } }
[HarmonyPatch(typeof(PhoneUIManager),nameof(PhoneUIManager.AutoCall))]
internal static class ReplicaQuickDial
{ private static bool Prefix(long number) { if(!NativeGuest.Active) return NativeWorld.Host?.RemotePhoneOwned!=true || NativeWorld.ExecutingPhone; NativeWorld.Guest?.RequestPhone("quick",number); return false; } }
[HarmonyPatch(typeof(PhoneUIManager),nameof(PhoneUIManager.StartCalling))]
internal static class ReplicaDial
{ private static bool Prefix() { if(!NativeGuest.Active) return NativeWorld.Host?.RemotePhoneOwned!=true || NativeWorld.ExecutingPhone; return false; } }
[HarmonyPatch(typeof(PhoneUIManager),nameof(PhoneUIManager.StopCall))]
internal static class ReplicaHangup
{ private static bool Prefix() { if(!NativeGuest.Active) return NativeWorld.Host?.RemotePhoneOwned!=true || NativeWorld.ExecutingPhone; NativeWorld.Guest?.RequestPhoneClose(); return false; } }
[HarmonyPatch(typeof(PhoneUIManager),nameof(PhoneUIManager.PressNumber))]
internal static class ReplicaPhoneDigits
{ private static bool Prefix(int number) => NativeGuest.Active ? number==-1 || NativeWorld.Guest?.SharedPhoneBusy!=true : NativeWorld.Host?.RemotePhoneOwned!=true || NativeWorld.ExecutingPhone; }
[HarmonyPatch(typeof(DialoguePhoneUIManager),nameof(DialoguePhoneUIManager.InterruptDialog))]
internal static class ReplicaPhoneInterrupt
{ private static bool Prefix() { if(NativeGuest.Active || NativeWorld.Host?.RemotePhoneOwned==true && !NativeWorld.ExecutingPhone) return false; NativeWorld.Host?.CancelPhoneEnds(); return true; } }
[HarmonyPatch(typeof(DialoguePhoneUIManager),nameof(DialoguePhoneUIManager.OnCurrentTextDisplayed))]
internal static class TrackNativePhoneEnd
{ private static bool Prefix(DialoguePhoneUIManager __instance) { if(NativeGuest.Active) return false; NativeWorld.Host?.TrackPhoneEnd(__instance.currentDialog); return true; } }
[HarmonyPatch(typeof(Dialogue),nameof(Dialogue.ExecuteEndAction))]
internal static class CancelObsoletePhoneEnd
{ private static bool Prefix(Dialogue __instance) => NativeWorld.Host?.PhoneEndAllowed(__instance)!=false; }
[HarmonyPatch(typeof(DialoguePhoneUIManager),nameof(DialoguePhoneUIManager.SelectChoice))]
internal static class ReplicaPhoneChoice
{ private static bool Prefix(int index) { if(!NativeGuest.Active) return true; NativeWorld.Guest?.RequestPhone("choice",index); return false; } }
[HarmonyPatch(typeof(DialoguePhoneUIManager),nameof(DialoguePhoneUIManager.OnAdvancePressed))]
internal static class ReplicaPhoneAdvance
{
    private static bool Prefix()
    {
        if(!NativeGuest.Active)
        {
            if(!NativeWorld.ExecutingPhone && (NativeWorld.Host?.HostPhoneHidden==true || !NativeAdvanceInput.SourceAllows(NativeAdvanceInput.Target.Phone))) return false;
            NativeWorld.Host?.RecordAdvanceSkip(true); return true;
        }
        NativeWorld.Guest?.RequestPhone("advance"); return false;
    }
}
