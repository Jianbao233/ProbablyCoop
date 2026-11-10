using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;
internal sealed partial class NativeBackgroundQa
{
    private int hostEndingStep,guestEndingStep,endingCash;
    private double hostEndingAt,guestEndingAt;
    private byte[] oldEndingCommand;
    private static JObject Ending(NativeWorld world) => (JObject)AccessTools.Field(typeof(NativeWorld),"endingState").GetValue(world);
    private void CheckHostEnding(NativeShop shop,double now)
    {
        if(!saveRoundTrip || StoreStation.Instance.dayCounter!=9) return;
        if(hostEndingStep==0 && shop.Capture().HeldItem!=0)
        {
            NativeWorld.Host.CaptureNativeChoice(new System.Collections.Generic.List<GameItem>{ChoicePreview("Pending choice")},
                "QA pending ending choice",_=>true,()=>qaMachine.EnableTag("QA_ENDING_CHOICE_RESUMED"),
                "Cancel",ButtonElement.Color.Red,false,false);
            // Explicit terminal fixtures after the complete three-day/save test.
            // Execute the original ending, not the UI alone; all QA files are restored.
            PlayerStore.Instance.ExecuteGameOver("buyout"); hostEndingStep=1; hostEndingAt=now+8;
            Record("FIXTURE native-ending","after three saved days; original buyout ending then rent ending");
        }
        if(hostEndingStep==1 && now>=hostEndingAt)
        {
            CaptureEndingUi("buyout"); GameOverUIManager.Instance.OnMenuButtonClick();
            Require(RunSummaryUI.IsOpen,"host original run summary missing");
            hostEndingStep=2; hostEndingAt=now+0.7;
        }
        if(hostEndingStep==2 && now>=hostEndingAt)
        { CaptureSummaryUi(); hostEndingStep=3; }
        if(hostEndingStep==3 && !NativeWorld.Host.EndingActive && qaMachine.IsTag("QA_ENDING_CHOICE_RESUMED"))
        { Record("PASS host-ending-continue","guest native summary continuation closed both ending views"); hostEndingStep=4; hostEndingAt=now+2; }
        if(hostEndingStep==4 && now>=hostEndingAt)
        { PlayerStore.Instance.ExecuteGameOver("rent"); hostEndingStep=5; hostEndingAt=now+8; }
        if(hostEndingStep==5 && now>=hostEndingAt)
        { CaptureEndingUi("rent"); hostEndingStep=6; }
    }
    private bool RunGuestEnding(CoopSession session,double now)
    {
        if(!saveRoundTrip) return true;
        if(now<guestEndingAt || QaWaiting(session.Native)) return false;
        var world=NativeWorld.Guest; var data=Ending(world); var ui=GameOverUIManager.Instance;
        if(guestEndingStep==0)
        {
            if(!world.EndingActive) return false;
            Require((string)data["id"]=="buyout" && ui.uiPanel.activeInHierarchy,"guest original buyout ending missing");
            Require(ui.reason.text==LocHelper.GetLocalizedMechanic("mech_end_buyout_reason") && ui.description.text=="  "+LocHelper.GetLocalizedMechanic("mech_end_buyout_desc"),"ending text differs from native");
            endingCash=PlayerStore.Instance.playerCash;
            oldEndingCommand=Encoding.UTF8.GetBytes(new JObject { ["kind"]="ending",["serial"]=data["serial"] }.ToString(Formatting.None));
            guestEndingStep=1; guestEndingAt=now+8; return false;
        }
        if(guestEndingStep==1)
        {
            CaptureEndingUi("buyout"); ui.OnMenuButtonClick();
            Require(RunSummaryUI.IsOpen,"guest original summary missing"); guestEndingStep=2; guestEndingAt=now+1; return false;
        }
        if(guestEndingStep==2)
        {
            CaptureSummaryUi();
            var root=(GameObject)AccessTools.Field(typeof(RunSummaryUI),"root").GetValue(null);
            var button=root.GetComponentsInChildren<Button>().Single(b=>b.GetComponentsInChildren<TMPro.TMP_Text>().Any(t=>t.text==LocHelper.GetLocalizedMainMenu("new_continue")));
            button.onClick.Invoke(); Require(QaWaiting(session.Native),"original summary continue emitted no request");
            guestEndingStep=3; guestEndingAt=now+0.5; return false;
        }
        if(guestEndingStep==3)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Applied,"ending continuation rejected");
            if(world.EndingActive) return false;
            Require(!ui.IsShowing && !ui.darkScreen.activeSelf,"ending overlay survived shared continuation");
            Record("PASS native-shared-ending-continue","original buyout UI/summary/continue button; host stats and both screens resumed");
            Require(OverlayChoiceWindow().IsVisible() && OverlayChoices().childItems.Count==1,
                "continuable ending discarded the pending native choice");
            ((Action)AccessTools.Field(typeof(OverlayHandler),"currentCancelAction").GetValue(OverlayHandler.current))();
            guestEndingStep=30; guestEndingAt=now+0.5; return false;
        }
        if(guestEndingStep==30)
        {
            if(!MachineReplica().IsTag("QA_ENDING_CHOICE_RESUMED")) return false;
            Require(!OverlayChoiceWindow().IsVisible(),"resumed chooser cancellation did not close the native view");
            Record("PASS native-choice-ending-resume","native chooser suspended during ending, original view and callback resumed after continuation; cancellation committed once");
            Require(session.Native.Submit(oldEndingCommand,now),"could not replay obsolete ending"); guestEndingStep=4; guestEndingAt=now+0.5; return false;
        }
        if(guestEndingStep==4)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Stale,"obsolete ending continue accepted");
            guestEndingStep=5;
        }
        if(guestEndingStep==5)
        {
            if(!world.EndingActive || (string)data["id"]!="rent") return false;
            guestEndingStep=50; guestEndingAt=now+8; return false;
        }
        if(guestEndingStep==50)
        {
            Require(ui.uiPanel.activeInHierarchy && !(bool)data["continue"],"terminal rent UI/state missing");
            ui.ContinuePlayClick(); Require(world.EndingActive && ui.uiPanel.activeSelf && !QaWaiting(session.Native),"guest locally bypassed terminal ending");
            CaptureEndingUi("rent");
            Require(session.Native.Submit(Encoding.UTF8.GetBytes(new JObject { ["kind"]="ending",["serial"]=data["serial"] }.ToString(Formatting.None)),now),"terminal continuation test send failed");
            guestEndingStep=6; guestEndingAt=now+0.5; return false;
        }
        if(guestEndingStep==6)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Unavailable,"host accepted continuation of non-continuable ending");
            Require(session.Native.Submit(Encoding.UTF8.GetBytes("{\"kind\":\"phone\",\"action\":\"quick\"}"),now),"post-ending business test send failed");
            guestEndingStep=7; guestEndingAt=now+0.5; return false;
        }
        Require(QaResult(session.Native)==(ushort)ShopResult.Busy && world.EndingActive && PlayerStore.Instance.playerCash==endingCash,"terminal ending allowed business or changed cash");
        Require(PlayerStore.Instance.saveSlotId==-1,"ending granted guest save slot");
        Record("PASS native-terminal-ending","rent reason/description projected; no local bypass, new-ID obsolete continuation rejected, all further business refused; no guest save");
        return true;
    }
    private static void CaptureEndingUi(string ending)
    {
        NativeOffscreenCapture.Capture("qa-ending-"+ending);
        var ui=GameOverUIManager.Instance;
        ModHelper.WriteModDataFileText("probably_coop","qa-ending-"+ending+".json",new JObject { ["reason"]=ui.reason.text,["description"]=ui.description.text,
            ["continue"]=(bool)AccessTools.Field(typeof(GameOverUIManager),"canKeepPlaying").GetValue(ui),["stats"]=JObject.FromObject(PlayerStore.Instance.runStats),
            ["ui"]=NativeNegotiationProbe.Capture(ui.uiPanel.transform),
            ["textRender"]=new JArray(new[]{ui.reason,ui.description}.Select(t=>new JObject { ["name"]=t.name,["visibleCharacters"]=t.maxVisibleCharacters,["color"]=t.color.ToString(),["vertices"]=t.textInfo?.characterCount,["scale"]=t.transform.lossyScale.ToString(),["enabled"]=t.enabled })) }.ToString());
    }
    private void CaptureSummaryUi()
    {
        Require(Enabled && (IsHostProduct || IsGuestProduct),"summary actor oracle requires an isolated QA product");
        var store=PlayerStore.Instance; var actors=NativeActorSession.Current;
        Require(store.runStats!=null,"summary oracle must not initialize missing run statistics");
        var originalStats=store.runStats; var originalHealth=store.healthData; var originalPerks=store.startingPerks;
        var before=SummaryInvariant(); JObject capture=null;
        NativeSummaryQaSideEffectGuard.Watching=true; NativeSummaryQaSideEffectGuard.Attempts=0;
        try
        {
            capture=CaptureSummaryView(true);
            if(host)
            {
                Require(actors!=null && actors.Run.CurrentActor==NativeActorRun.Slot.Host,"source summary requires the settled Host actor");
                try
                {
                    using(actors.Enter(NativeActorRun.Slot.Partner))
                    {
                        RebuildSummaryView();
                        capture["partnerReference"]=CaptureSummaryView(false);
                    }
                }
                finally
                {
                    // The real native builder installs its original callbacks
                    // again after the temporary, read-only Partner rendering.
                    RebuildSummaryView();
                }
                capture["restoredHost"]=CaptureSummaryView(false);
            }
            var after=SummaryInvariant();
            Require(ReferenceEquals(store,PlayerStore.Instance) && ReferenceEquals(originalStats,store.runStats) &&
                ReferenceEquals(originalHealth,store.healthData) && ReferenceEquals(originalPerks,store.startingPerks) &&
                JToken.DeepEquals(before,after) && NativeSummaryQaSideEffectGuard.Attempts==0,
                "native summary rendering changed actor/shared state, save identity, or invoked a business/save callback");
            capture["purity"]=new JObject { ["before"]=before,["after"]=after,["forbiddenCalls"]=NativeSummaryQaSideEffectGuard.Attempts };
        }
        finally { NativeSummaryQaSideEffectGuard.Watching=false; }
        ModHelper.WriteModDataFileText("probably_coop","qa-ending-summary.json",capture.ToString());
        Record(host ? "PASS summary-source-actor-reference" : "PASS summary-guest-personal-render",
            host ? "native Host six tabs; original Partner-scope six-tab reference; original Host rebuilt with callbacks; actor/shared/save invariants unchanged" :
                "native local Partner six tabs; current two-actor evidence and shared/save invariants unchanged; full render compared offline with source Partner");
    }
    private static JObject SummaryInvariant()
    {
        var store=PlayerStore.Instance;
        JObject personal;
        if(IsHostProduct) personal=NativeActorSession.Current.Run.CapturePersonalCheckpoint();
        else
        {
            var world=NativeWorld.Guest;
            Require(world?.HasActorProjection==true && world.RemoteHostActor!=null,"guest summary has no actor projection");
            personal=new JObject { ["schema"]=1,["run"]=((Guid)AccessTools.Field(typeof(NativeWorld),"actorProjectionRun").GetValue(world)).ToString("D"),
                ["actors"]=new JArray(world.RemoteHostActor.Serialize(),NativeActorState.Capture(store).Serialize()) };
        }
        return new JObject { ["personal"]=personal,["stats"]=JObject.FromObject(store.runStats),["cash"]=store.playerCash,
            ["day"]=StoreStation.Instance.dayCounter,["slot"]=store.saveSlotId,["nativeRun"]=store.runID,
            ["lastSave"]=store.lastSaveTime.ToString("O"),["storedPlayTime"]=store.runPlayTimeSeconds };
    }
    private static void RebuildSummaryView()
    {
        // OnMenuButtonClick's success path only hides this panel and calls Open;
        // call that same native builder directly so a failed QA rebuild cannot
        // silently take its fallback GoToMainMenu branch.
        var ui=GameOverUIManager.Instance;
        Require(RunSummaryUI.CanOpen(),"native summary builder is unavailable");
        var old=(GameObject)AccessTools.Field(typeof(RunSummaryUI),"root").GetValue(null);
        if(old!=null) old.SetActive(false); // Destroy is deferred until end of frame.
        ui.uiPanel.SetActive(false);
        Require(RunSummaryUI.Open((string)AccessTools.Field(typeof(GameOverUIManager),"endingId").GetValue(ui),
            (bool)AccessTools.Field(typeof(GameOverUIManager),"canKeepPlaying").GetValue(ui),ui.GoToMainMenu,ui.ContinuePlayClick),
            "original actor-scoped summary rebuild failed");
    }
    private static JObject CaptureSummaryView(bool screenshots)
    {
        if(screenshots) NativeOffscreenCapture.Capture("qa-ending-summary");
        var root=(GameObject)AccessTools.Field(typeof(RunSummaryUI),"root").GetValue(null);
        Require(root!=null && RunSummaryUI.IsOpen,"native summary root missing during actor capture");
        var pages=new JArray();
        var tabs=(System.Collections.Generic.List<CustomUIElement>)AccessTools.Field(typeof(RunSummaryUI),"tabs").GetValue(null);
        Require(tabs.Count==6,"native summary tab count changed");
        for(int page=0;page<tabs.Count;page++)
        {
            tabs[page].button.onClick.Invoke();
            pages.Add(NativeNegotiationProbe.Capture(root.transform));
            if(screenshots) NativeOffscreenCapture.Capture("qa-ending-summary-"+page);
        }
        tabs[0].button.onClick.Invoke();
        Require(NativeSummaryQaClock.Text!=null,"native summary clock render was not observed");
        var uiCapture=NativeNegotiationProbe.Capture(root.transform);
        var originalPages=(System.Collections.Generic.List<GameObject>)AccessTools.Field(typeof(RunSummaryUI),"pages").GetValue(null);
        var overviewShared=new JArray(); int personalBlocks=0;
        // BuildOverview puts the complete personal perk column in one direct
        // child. Preserve every other direct block, including layout, relative
        // to its own section so zero versus many personal perks may reflow it.
        foreach(RectTransform block in originalPages[0].transform)
        {
            if(block.GetComponentsInChildren<RectTransform>(true).Any(node=>node.name=="perk_slot")) { personalBlocks++; continue; }
            overviewShared.Add(new JObject { ["w"]=Math.Round(block.rect.width,3),["h"]=Math.Round(block.rect.height,3),
                ["ui"]=NativeNegotiationProbe.Capture(block) });
        }
        var authority=NativeActorSession.Current;
        int actor=IsHostProduct ? (int)authority.Run.CurrentActor : 1;
        var state=IsHostProduct && actor==1 ? authority.Run.Partner : NativeActorState.Capture(PlayerStore.Instance);
        string run=IsHostProduct ? authority.Run.Run.ToString("D") : ((Guid)AccessTools.Field(typeof(NativeWorld),"actorProjectionRun").GetValue(NativeWorld.Guest)).ToString("D");
        Require(personalBlocks==(state.startingPerks.Count>0 ? 1 : 0),"summary personal section does not match its actor's perk list");
        return new JObject { ["schema"]=2,["actor"]=new JObject { ["slot"]=actor,["run"]=run,["state"]=state.Serialize() },
            ["ui"]=uiCapture,["pages"]=pages,["overviewShared"]=overviewShared,
            ["clockText"]=NativeSummaryQaClock.Text,["clockRenderedSeconds"]=NativeSummaryQaClock.Seconds,
            ["clockSeconds"]=PlayerStore.Instance.GetCurrentRunPlayTime() };
    }
}

[HarmonyPatch]
internal static class NativeSummaryQaSideEffectGuard
{
    internal static bool Watching;
    internal static int Attempts;
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PlayerStore),nameof(PlayerStore.InitialSave));
        yield return AccessTools.Method(typeof(PlayerStore),nameof(PlayerStore.SaveGame));
        yield return AccessTools.Method(typeof(PlayerStore),"WriteSlotFile");
        yield return AccessTools.Method(typeof(PlayerStore),nameof(PlayerStore.ExecuteGameOver));
        yield return AccessTools.Method(typeof(GameOverUIManager),nameof(GameOverUIManager.GoToMainMenu));
        yield return AccessTools.Method(typeof(GameOverUIManager),nameof(GameOverUIManager.ContinuePlayClick));
    }
    [HarmonyPriority(Priority.First)]
    private static void Prefix(System.Reflection.MethodBase __originalMethod)
    {
        if(!Watching) return;
        Attempts++;
        throw new InvalidOperationException("Native summary read invoked a forbidden callback: "+__originalMethod.Name);
    }
}

// The vanilla header formats the clock once, when opened; six screenshots
// later the live clock can have crossed a minute boundary. Observe the actual
// formatter result used by the header, rather than formatting a different time.
[HarmonyPatch(typeof(RunSummaryUI),"PlayTimeText")]
internal static class NativeSummaryQaClock
{
    internal static string Text;
    internal static float Seconds;
    private static void Prefix(out float __state)=>__state=NativeBackgroundQa.Enabled ? PlayerStore.Instance.GetCurrentRunPlayTime() : 0;
    private static void Postfix(string __result,float __state)
    { if(NativeBackgroundQa.Enabled) { Text=__result; Seconds=__state; } }
}
