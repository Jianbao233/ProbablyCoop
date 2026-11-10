using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Endings are host events, while viewing the original summary and its tabs is local.
// Never execute PlayerStore.ExecuteGameOver on the replica (analytics/run mutation).
internal sealed partial class NativeWorld
{
    private uint endingSerial,endingAppliedSerial;
    private JObject endingState;
    private bool endingActive;
    private bool endingApplying;
    private JObject pendingEndingContinue;
    internal bool EndingActive => endingActive;
    internal static void PrepareEndingUi()
    {
        // The original letterboxed paper layer sits above the ending layer.
        // Endings must dismiss these local views on each frontend, even when only
        // the other player triggered the event. Single-player is not patched here.
        StoreUIManager.Instance.CloseAllUI();
        AdvCalendarUIManager.Instance.CloseUI();
        PaperUIManager.Instance.CloseAllPaperUI();
        MultiPageUIManager.Instance.CloseUI();
        ReputationUIManager.Instance.CloseUI();
        // The legacy ending text lacks TMPLocalizer in this game build. Resolve
        // its existing Noto Sans role just like the native localized panels do.
        var font=FontLoader.Instance.GetFontByRole(FontLoader.FontRole.Noto_Sans,UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale);
        GameOverUIManager.Instance.reason.font=font;
        GameOverUIManager.Instance.description.font=font;
    }
    internal void RecordEnding(string reason,string description,bool allowContinue)
    {
        EndSharedPhone(); pendingWildClose=null; wildAwaitingClose=false;
        RevokePartnerHand();
        proposedTrip=""; tripReady=0; TickTripUi();
        endingActive=true;
        endingState=new JObject { ["serial"]=++endingSerial,["active"]=true,
            ["id"]=(string)AccessTools.Field(typeof(GameOverUIManager),"endingId").GetValue(GameOverUIManager.Instance),
            ["reason"]=reason,["description"]=description,["continue"]=allowContinue };
        nextCapture=0;
    }
    private JObject CaptureEnding() => endingState ?? new JObject { ["serial"]=0,["active"]=false };
    private void ApplyEnding(JObject data)
    {
        uint serial=(uint)data["serial"];
        bool active=(bool)data["active"],wasActive=endingActive;
        endingState=data; endingActive=active;
        if(active && serial!=endingAppliedSerial)
        {
            endingAppliedSerial=serial; pendingEndingContinue=null;
            pendingWildClose=null; RunSummaryUI.Close();
            var ui=GameOverUIManager.Instance;
            ui.SetEnding((string)data["id"]);
            ui.DisplayUI((string)data["reason"],(string)data["description"],(bool)data["continue"]);
        }
        else if(!active && wasActive)
        {
            pendingEndingContinue=null;
            endingApplying=true;
            try { RunSummaryUI.Close(); GameOverUIManager.Instance.ContinuePlayClick(); }
            finally { endingApplying=false; }
        }
    }
    internal bool RequestEndingContinue()
    {
        if(endingApplying) return true;
        if(!endingActive || (bool?)endingState?["continue"]!=true) return false;
        // Continuing is a global cut. Retire unfinished local editing rather
        // than wait forever for mouse release behind the original ending UI.
        CancelAllHandActions();
        controlDrafts.Clear(); controlPreviews.Clear();
        continuousPreviews.Clear();
        pendingEndingContinue=new JObject { ["kind"]="ending",["serial"]=endingState["serial"] };
        // Native RunSummary destroys itself before invoking its callback. Keep the
        // original ending panel visible until the shared continuation is confirmed.
        GameOverUIManager.Instance.uiPanel.SetActive(true);
        FlushEndingContinue(); return false;
    }
    private void FlushEndingContinue()
    {
        if(pendingEndingContinue==null || HasPendingWork) return;
        if(!endingActive || (uint)pendingEndingContinue["serial"]!=(uint)endingState["serial"])
        { pendingEndingContinue=null; return; }
        if(Submit(pendingEndingContinue)) pendingEndingContinue=null;
    }
    private ushort ExecuteEnding(JObject command)
    {
        if(!endingActive || (uint?)command["serial"]!=(uint?)endingState?["serial"]) return (ushort)ShopResult.Stale;
        if((bool?)endingState["continue"]!=true) return (ushort)ShopResult.Unavailable;
        RunSummaryUI.Close(); GameOverUIManager.Instance.ContinuePlayClick();
        return (ushort)ShopResult.Applied;
    }
    internal void CompleteEnding()
    {
        if(!endingActive) return;
        endingActive=false; endingState["active"]=false; nextCapture=0;
    }
    internal bool CanContinueEnding => !endingActive || (bool?)endingState?["continue"]==true;
}

[HarmonyPatch(typeof(GameOverUIManager),nameof(GameOverUIManager.DisplayUI))]
internal static class NativeEndingShown
{
    private static void Prefix() { if(NativeGuest.Active || NativeWorld.Host!=null) NativeWorld.PrepareEndingUi(); }
    private static void Postfix(string givenReason,string givenDescription,bool showContinuePlay)
    { if(!NativeGuest.Active) NativeWorld.Host?.RecordEnding(givenReason,givenDescription,showContinuePlay); }
}
[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.ExecuteGameOver))]
internal static class NativeEndingNoReplicaExecution
{ private static bool Prefix() => !NativeGuest.Active; }
[HarmonyPatch(typeof(GameOverUIManager),nameof(GameOverUIManager.ContinuePlayClick))]
internal static class NativeEndingContinue
{
    private static bool Prefix(ref bool __state)
    { __state=NativeGuest.Active ? NativeWorld.Guest?.RequestEndingContinue()==true : NativeWorld.Host?.CanContinueEnding!=false; return __state; }
    private static void Postfix(bool __state) { if(__state && !NativeGuest.Active) NativeWorld.Host?.CompleteEnding(); }
}
