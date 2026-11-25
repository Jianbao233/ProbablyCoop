using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Room management only. All economic inputs belong to the original game UI;
// the old transport test protocol remains available to offline tests, not here.
internal sealed partial class CoopPanel : IDisposable
{
    private readonly RoomSelector room;
    private readonly bool localTest;
    private readonly CoopOpeningLobby opening;
    private GameObject menuButton;
    private TextMeshProUGUI menuLabel, originalMenuLabel;
    private readonly RoomPanelEntries entries = new();
    private readonly Ipv4RoomControls ipv4 = new();
    private CustomUIWindow window, checkpoints;
    private IEnumerator<CoopRun.Checkpoint> checkpointScan;
    private CustomUIBuilder checkpointBuilder;
    private CustomUIElement checkpointStatus;
    private readonly List<CoopRun.Checkpoint> checkpointRows=new();
    private static readonly IComparer<CoopRun.Checkpoint> newestCheckpointFirst =
        Comparer<CoopRun.Checkpoint>.Create((a,b)=>b.SavedAt.CompareTo(a.SavedAt));
    private bool checkpointHistory;
    private int checkpointStartFrame;
    private CustomUIElement code, roomCode, status, join, copy, leave, approve, decline, access;
    private CustomUIElement shopStatus, heading, carry, steamHint, playerNameInput, newRun, reopenOpening;
    private RectTransform panelBody;
    private ScrollRect panelScroll;
    private CustomUIElement lanToggle, joinFromMenu;
    private CustomUIElement joinProgressLabel, joinHistory, joinHistoryToggle;
    private RectTransform joinProgressFill;
    private GameObject joinProgressTrack;
    private CustomUIElement terminalSummary, terminalDetails, copyIssue, recoverDay;
    private NativeShop dayShop;
    private DaySettlement recoveryAttempt;
    private NativeShop.MutationFault mutationFault;
    private bool hostDayPending;
    private (string Code, int? Day)? recoveryExit;
    private string joinFailureCode;
    private bool issueCopied;
    private GameObject setupRow, joinColumn, lanControls, saveRow, roomActions, approvalRow, joinLog,
        progressSection;
    private bool showLanOptions;
    private double nextTerminalRefresh;
    private readonly Queue<string> joinSteps=new Queue<string>();
    private CoopSession joinSession;
    private string joinPhase;
    private bool joinHistoryOpen, joinPanelWasVisible, reopenJoinPanel;
    private int joinPages;
    private float joinPercentage;
    private string joinCounters="", joinFailureReason="";
    private bool checkedJoinUi;
    private bool notifiedJoinRequest;
    private bool inMenu=true, inShop;
    private double nextRefresh;
    private double copiedUntil;
    private double steamJoinBusyUntil;
    private string displaySignature;
    private int? joinRequestedFrame;
    private bool partnerPickerVisible, restoreAfterPartnerPicker;

    internal CoopPanel(RoomSelector room,bool localTest,CoopOpeningLobby opening)
    { this.room=room; this.localTest=localTest; this.opening=opening; }
    private static string Loc(string key)=>ModHelper.GetLocalized("probably_coop",key);

    internal void SetContext(bool menu,bool shop)
    {
        if(menu==inMenu && shop==inShop) return;
        inMenu=menu; inShop=shop;
        joinRequestedFrame=null;
        partnerPickerVisible=restoreAfterPartnerPicker=false;
        reopenJoinPanel |= joinPanelWasVisible && joinPhase!=null && room.IsGuest;
        if(window?.IsAlive==true) window.Close();
        CloseDiagnosticsWindows();
        entries.Dispose();
        if(checkpoints?.IsAlive==true) checkpoints.Close();
        if(menuButton!=null) UnityEngine.Object.Destroy(menuButton);
        menuButton=null;
        copiedUntil=0;
        steamJoinBusyUntil=0;
        nextTerminalRefresh=0;
        displaySignature=null;
    }
    internal void SetPartnerPickerVisible(bool visible)
    {
        if(partnerPickerVisible==visible) return;
        partnerPickerVisible=visible;
        if(visible)
        {
            // The join window can cover the original modal on the Android port.
            restoreAfterPartnerPicker=window?.IsVisible==true;
            if(restoreAfterPartnerPicker) window.Hide();
        }
        else if(restoreAfterPartnerPicker)
        {
            restoreAfterPartnerPicker=false;
            if(inMenu && window?.IsAlive==true) Open();
        }
    }

    internal void Tick()
    {
        if(CustomUIManager.Instance==null) return;
        TickConnectionDetails();
#if PROBABLY_COOP_GRID_QA
        TickJoinReviewQa();
#endif
        CoopNewGameFlow.CancelIfClosed();
        if(inMenu && menuButton==null) CreateMenuButton();
        if(reopenJoinPanel)
        {
            reopenJoinPanel=false;
            Open();
        }
        if(menuButton!=null)
        {
            var title=Loc("title");
            if(menuLabel.text!=title) menuLabel.text=title;
            if(originalMenuLabel!=null && menuLabel.font!=originalMenuLabel.font) menuLabel.font=originalMenuLabel.font;
        }
        entries.Tick(inMenu, menuButton != null, window?.IsVisible == true, Open);
        TickCheckpointScan();
        if(joinRequestedFrame is int frame && frame!=Time.frameCount)
        {
            joinRequestedFrame=null;
            if(window?.IsVisible==true) Join();
        }
        if(Input.GetKeyDown(KeyCode.F7) && !InputFocused(code) && !ipv4.InputFocused && !InputFocused(roomCode))
        {
            if(checkpoints?.IsVisible==true) checkpoints.Close();
            else if(window?.IsVisible==true) window.Hide();
            else Open();
        }
        if(window?.IsVisible==true && Time.realtimeSinceStartupAsDouble>=nextRefresh)
        { nextRefresh=Time.realtimeSinceStartupAsDouble+0.15; Refresh(); }
        joinPanelWasVisible=window?.IsVisible==true;
        if(NativeBackgroundQa.SynchronizationProfile && !checkedJoinUi && joinPhase is "sync_ready" or "sync_failed")
            VerifyJoinUi();
    }
    private static bool InputFocused(CustomUIElement element)=>element?.input!=null && element.input.isFocused;

    private void CreateMenuButton()
    {
        var menu=MainMenuUIController.Instance;
        var original=menu?.moddingButton;
        if(original==null || original.transform.parent==null || original.transform.parent.GetComponent<LayoutGroup>()==null ||
            !original.TryGetComponent<Button>(out _)) return;
        originalMenuLabel=original.GetComponentInChildren<TextMeshProUGUI>(true);
        if(originalMenuLabel==null) return;

        int position=original.transform.GetSiblingIndex();
        foreach(Transform sibling in original.transform.parent)
        {
            if(!sibling.TryGetComponent<Button>(out var candidate)) continue;
            for(int index=0;index<candidate.onClick.GetPersistentEventCount();index++)
                if(candidate.onClick.GetPersistentTarget(index)==menu &&
                    candidate.onClick.GetPersistentMethodName(index)==nameof(MainMenuUIController.OnLoadGameClick))
                    position=sibling.GetSiblingIndex()+1;
        }
        menuButton=UnityEngine.Object.Instantiate(original,original.transform.parent,false);
        menuButton.name="ProbablyCoopMenuButton";
        menuButton.transform.SetSiblingIndex(position);
        var button=menuButton.GetComponent<Button>();
        button.onClick=new Button.ButtonClickedEvent();
        button.onClick.AddListener(()=> { menu.OnAnyButtonClick(); Open(); });
        foreach(var localization in menuButton.GetComponentsInChildren<LocalizeStringEvent>(true)) localization.enabled=false;
        menuLabel=menuButton.GetComponentInChildren<TextMeshProUGUI>(true);
        menuButton.SetActive(true);
    }

    private void Open()
    {
        if(partnerPickerVisible) return;
        // Do this only in response to opening the co-op panel. Keeping the
        // native report open while waiting is otherwise harmless.
        if(recoveryAttempt!=null && AdvCalendarUIManager.Instance is { } report &&
            report.UIPanel?.activeInHierarchy==true) report.CloseUI();
        if(window?.IsAlive==true) { window.Show(); window.BringToFront(); displaySignature=null; Refresh(); return; }
        var builder=CustomUIManager.Window("probably_coop_panel",Loc("title"),inMenu ? "menu" : "overlay")
            .SetSize(1160,660).Center().SetDraggable(true).SetCloseOnEscape();
        builder.BeginScrollFlexible(160);
        panelScroll=builder.CurrentContainer.GetComponentInParent<ScrollRect>();
        if(panelScroll!=null)
        {
            panelScroll.horizontal=false;
            panelScroll.verticalScrollbarVisibility=ScrollRect.ScrollbarVisibility.AutoHide;
        }
        builder.BeginColumn(14);
        panelBody=builder.CurrentContainer;
        builder.AddLabel(Loc(inShop ? "native_header" : "room_header")); heading=builder.Last;
        builder.AddLabel(""); status=builder.Last; status.text.richText=false;
        BeginSection(builder);
        builder.BeginRow(12,TextAnchor.MiddleLeft);
        builder.AddHeader(Loc("terminal_title"));
        builder.AddFlexibleSpace();
        builder.AddButton(Loc("terminal_details_title"),OpenConnectionDetails,"connection_details");
        builder.End();
        builder.AddLabel(""); terminalSummary=builder.Last; terminalSummary.text.richText=false;
        builder.End();

        builder.BeginPanel(alignment:TextAnchor.UpperLeft,padding:16,spacing:10);
        progressSection=builder.CurrentContainer.gameObject;
        builder.AddLabel(""); joinProgressLabel=builder.Last;
        // Rev13 does not supply either progress or slider prefabs in all scenes.
        joinProgressTrack=new GameObject("Join progress",typeof(RectTransform),typeof(Image),typeof(LayoutElement));
        joinProgressTrack.transform.SetParent(builder.CurrentContainer,false);
        var trackLayout=joinProgressTrack.GetComponent<LayoutElement>();
        trackLayout.minHeight=trackLayout.preferredHeight=8;
        trackLayout.flexibleWidth=1;
        var track=joinProgressTrack.GetComponent<Image>();
        track.color=new Color32(35,31,47,255);
        track.raycastTarget=false;
        var fill=new GameObject("Fill",typeof(RectTransform),typeof(Image)).GetComponent<Image>();
        fill.transform.SetParent(joinProgressTrack.transform,false);
        fill.color=new Color32(126,192,192,255);
        fill.raycastTarget=false;
        joinProgressFill=fill.rectTransform;
        joinProgressFill.anchorMin=Vector2.zero;
        joinProgressFill.anchorMax=Vector2.up;
        joinProgressFill.offsetMin=joinProgressFill.offsetMax=Vector2.zero;
        builder.AddButton(Loc("sync_show_log"),()=>{ joinHistoryOpen=!joinHistoryOpen; displaySignature=null; Refresh(); });
        joinHistoryToggle=builder.Last;
        builder.BeginColumn(6);
        joinLog=builder.CurrentContainer.gameObject;
        builder.AddLabel(""); joinHistory=builder.Last; joinHistory.text.richText=false;
        builder.End().End();
        builder.AddLabel(Loc("steam_friend_hint")); steamHint=builder.Last;
        ipv4.AddListening(builder);

        builder.BeginRow(18);
        setupRow=builder.CurrentContainer.gameObject;
        BeginSection(builder, balanced:true);
        joinColumn=builder.CurrentContainer.gameObject;
        builder.AddHeader(Loc("join_room_heading"));
        builder.AddInput(Loc("join_code_hint"),onTyping:_=>RefreshAvailability()); code=builder.Last;
        code.input.characterLimit=64;
        code.input.onSubmit.AddListener(_=>RequestJoin());
        builder.BeginRow(6);
        builder.AddButton(Loc("paste_room_code"),PasteRoomCode);
        builder.AddButton(Loc("join"),RequestJoin); join=builder.Last;
        builder.End();
        ipv4.AddMapping(builder, RequestJoin);
        if(NativePlatform.IsAndroid) builder.AddLabel(Loc("status_android_guest_only"));
        builder.End();

        BeginSection(builder, balanced:true);
        var hostColumn=builder.CurrentContainer.gameObject;
        builder.AddHeader(Loc("host_room_heading"));
        builder.AddButton(Loc(localTest ? "local_create" : "create"),()=>{room.Create();Refresh();});
        builder.AddButton(Loc("show_lan_options"),()=>{
            showLanOptions=!showLanOptions;
            Refresh();
        }); lanToggle=builder.Last;
        builder.BeginColumn(10);
        lanControls=builder.CurrentContainer.gameObject;
        ipv4.AddHost(builder, HostLan, compact:true);
        hostColumn.SetActive(NativePlatform.SupportsHosting);
        builder.End().End().End();
        builder.AddLabel(Loc("join_from_menu")); joinFromMenu=builder.Last;

        builder.BeginRow(6);
        roomActions=builder.CurrentContainer.gameObject;
        builder.AddInput(Loc("room_code")); roomCode=builder.Last;
        roomCode.input.readOnly=true;
        builder.AddButton(Loc("copy"),CopyRoomCode); copy=builder.Last;
        builder.AddButton(Loc("leave"),LeaveRoom); leave=builder.Last;
        builder.End();
        builder.AddButton(Loc("opening_reopen"),opening.Reopen); reopenOpening=builder.Last;
        builder.AddButton(Loc("pause_joins"),()=>{ room.SetAcceptingJoins(!room.AcceptingJoins); Refresh(); }); access=builder.Last;
        builder.BeginRow(6);
        approvalRow=builder.CurrentContainer.gameObject;
        builder.AddButton(Loc("approve"),()=>{ room.Approve(); Refresh(); }); approve=builder.Last;
        if(approve.text!=null) approve.text.richText=false;
        builder.AddButton(Loc("decline_join"),()=>{ room.Decline(); Refresh(); }); decline=builder.Last;
        builder.End();
        BeginSection(builder);
        saveRow=builder.CurrentContainer.gameObject;
        builder.AddHeader(Loc("coop_saves_heading"));
        builder.BeginRow(12);
        builder.AddButton(Loc("new_run"),StartCoopGame,"coop_new_run"); newRun=builder.Last;
        builder.AddButton(Loc("resume_run"),()=>OpenCheckpoints());
        builder.AddButton(Loc("solo_import_button"),OpenSoloCheckpoints,"import_solo");
        builder.End();
        builder.End();
        BeginSection(builder);
        builder.AddHeader(Loc("player_options_heading"));
        builder.BeginRow(12,TextAnchor.MiddleLeft);
        builder.AddLabel(Loc("player_name"));
        builder.AddInput(Loc("player_name_input"),CoopSettings.PointerName); playerNameInput=builder.Last;
        playerNameInput.input.characterLimit=PointerProfile.NameLimit;
        var nameLayout=playerNameInput.root.GetComponent<LayoutElement>() ?? playerNameInput.root.AddComponent<LayoutElement>();
        nameLayout.minWidth=170;
        nameLayout.flexibleWidth=1;
        playerNameInput.input.onSubmit.AddListener(_=>SavePlayerName());
        builder.AddButton(Loc("change_player_name"),SavePlayerName);
        builder.End();
        if(!inMenu) entries.AddLauncherOption(builder);
        builder.AddButton(Loc("personal_carry"),()=>NativeActorCarryUi.Toggle()); carry=builder.Last;
        builder.AddLabel(""); shopStatus=builder.Last;
        builder.End();

        BeginSection(builder);
        builder.AddHeader(Loc("contact_title"));
        builder.BeginRow(12);
        builder.AddButton(Loc("contact_qq_group"),()=>CopyContact("1132567943","contact_qq_group_copied"));
        builder.AddButton(Loc("contact_author_qq"),()=>CopyContact("1600597561","contact_author_qq_copied"));
        builder.End().BeginRow(12);
        builder.AddButton(Loc("contact_discord"),()=>CopyContact("jianbao1954","contact_discord_copied"));
        builder.AddButton(Loc("contact_bilibili"),()=>CopyContact("https://space.bilibili.com/234054413","contact_bilibili_copied"));
        builder.End().End().End().End();
        window=builder.Show(); window.BringToFront(); displaySignature=null; Refresh();
        Debug.Log("[probably_coop] room panel opened");
    }

    private void SavePlayerName()
    {
        if(playerNameInput==null) return;
        CoopSettings.SetPointerName(playerNameInput.GetString());
        playerNameInput.SetString(CoopSettings.PointerName);
        room.Session?.SetPointerName(CoopSettings.PointerName);
        StoreUIManager.Instance?.Notify(Loc("player_name_changed"));
    }

    private static void CopyContact(string value,string messageKey)
    {
        GUIUtility.systemCopyBuffer=value;
        StoreUIManager.Instance?.Notify(Loc(messageKey));
    }

    private static void BeginSection(CustomUIBuilder builder,bool balanced=false)
    {
        builder.BeginPanel(alignment:TextAnchor.UpperLeft,padding:16,spacing:10);
        if(!balanced) return;
        var layout=builder.CurrentContainer.GetComponent<LayoutElement>();
        layout.minWidth=0;
        layout.preferredWidth=0;
    }

    private void PasteRoomCode()
    {
        if(!inMenu || !CanConnect) return;
        var pasted=GUIUtility.systemCopyBuffer?.Trim();
        if(!string.IsNullOrEmpty(pasted) && pasted.Length<=code.input.characterLimit)
        {
            code.SetString(pasted);
            Refresh();
        }
    }
    internal void ShowSteamJoin(bool roomBusy)
    {
        steamJoinBusyUntil=roomBusy ? Time.realtimeSinceStartupAsDouble+5 : 0;
        Open();
        window.BringToFront();
    }

    internal void ObserveJoin(bool entering,bool ready)
    {
        if(room.CanApprove && !notifiedJoinRequest)
        {
            if(inMenu) Open();
            else StoreUIManager.Instance?.Notify(Loc("join_request_notification"));
        }
        notifiedJoinRequest=room.CanApprove;
        if(!room.IsGuest)
        {
            if(joinPhase!="sync_failed" && room.Status!="partner_setup_rejected")
            {
                if(joinSession!=null && room.Status=="peer_timeout") JoinFailed();
                else ResetJoin();
            }
            return;
        }
        var session=room.Session;
        if(!ReferenceEquals(joinSession,session))
        {
            ResetJoin();
            joinSession=session;
            if(NativeBackgroundQa.SynchronizationProfile) reopenJoinPanel=true;
        }
        if(ready) { SetJoinPhase("sync_ready",1f); return; }
        var snapshot=session?.Native;
        if(snapshot?.Snapshot!=null)
        {
            NoteReceivedPages(snapshot.Snapshot.Count,snapshot.Snapshot.Count,snapshot.Snapshot.Bytes);
            SetJoinPhase(entering ? "sync_installing" : "sync_checking",0.95f);
            return;
        }
        var transfer=snapshot?.Transfer;
        if(transfer.HasValue && transfer.Value.Pages>0)
        {
            var value=transfer.Value;
            NoteReceivedPages(value.CompletePages,value.Pages,value.ReceivedBytes);
            joinCounters=$"{value.CompletePages}/{value.Pages} · {value.ReceivedBytes/1024:N0} KiB";
            float fraction=value.PageBytes==0 ? 0 : (float)value.ReceivedPageBytes/value.PageBytes;
            SetJoinPhase("sync_receiving",0.95f*(value.CompletePages+fraction)/value.Pages);
        }
        else if(joinPhase!="sync_failed")
            SetJoinPhase(session?.State==LinkState.Connected ? "sync_preparing" :
                room.Status=="await_approval" ? "status_await_approval" : "sync_connecting",0f);
    }
    internal void JoinFailed(ModExtensionJoinException mismatch=null,NativeSyncFailure reason=NativeSyncFailure.None,
        bool localError=false,bool duringPlay=false)
    {
        joinFailureCode=CoopIssueCode.Synchronization(reason,localError,mismatch!=null);
        Debug.LogWarning($"[probably_coop] {joinFailureCode} guest synchronization stopped ({reason})");
        SetJoinPhase("sync_failed",joinPercentage);
        if(mismatch!=null)
        {
            joinFailureReason=string.Format(Loc(mismatch.MessageKey),mismatch.ModId);
            AddJoinStep(joinFailureReason);
        }
        else if(reason!=NativeSyncFailure.None || localError)
        {
            joinFailureReason=Loc(reason switch
            {
                NativeSyncFailure.HostCapture=>"sync_fail_host_capture",
                NativeSyncFailure.HostEncode=>"sync_fail_host_encode",
                NativeSyncFailure.GuestPageFormat=>"sync_fail_guest_format",
                NativeSyncFailure.GuestTransferBudget=>"sync_fail_guest_budget",
                _=>"sync_fail_guest_apply"
            });
            AddJoinStep(joinFailureReason);
        }
        reopenJoinPanel=!duringPlay;
        issueCopied=false;
    }
    private void ResetJoin()
    {
        joinSession=null; joinPhase=null; joinPages=0; joinPercentage=0; joinCounters=joinFailureReason="";
        joinSteps.Clear();
        joinFailureCode=null;
        triedWithDifferences=false;
        issueCopied=false;
    }
    private void NoteReceivedPages(int count,int total,int bytes)
    {
        if(count<=joinPages) return;
        joinPages=count;
        AddJoinStep(string.Format(Loc("sync_batches_received"),count,total,bytes/1024));
        joinCounters=$"{count}/{total} · {bytes/1024:N0} KiB";
    }
    private void SetJoinPhase(string phase,float progress)
    {
        joinPercentage=progress;
        if(joinPhase==phase) return;
        joinPhase=phase;
        AddJoinStep(Loc(phase));
        AddConnectionEvent(phase=="sync_failed" ? "ERROR" : "INFO",
            Loc(phase=="sync_ready" && triedWithDifferences ? "join_mod_ready" : phase));
    }
    private void AddJoinStep(string line)
    {
        if(joinSteps.Count==6) joinSteps.Dequeue();
        joinSteps.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
    }
    private void VerifyJoinUi()
    {
        if(window?.IsVisible!=true) return;
        Refresh();
        if(joinProgressTrack?.activeSelf!=true || !joinProgressLabel.text.text.Contains(Loc(joinPhase))) return;
        float progress=joinProgressFill.anchorMax.x;
        if(joinPhase=="sync_ready" && progress<0.999f) return;
        if(joinHistoryOpen) joinHistoryToggle.button.onClick.Invoke();
        joinHistoryToggle.button.onClick.Invoke();
        if(!joinHistoryOpen || !joinLog.activeSelf || !joinHistory.text.text.Contains(Loc(joinPhase))) return;
        joinHistoryToggle.button.onClick.Invoke();
        if(joinHistoryOpen || joinLog.activeSelf) return;
        checkedJoinUi=true;
        Debug.Log($"[probably_coop QA] PASS join-progress-ui phase={joinPhase} progress={joinPercentage:F3} counters={joinCounters}");
    }

    private void Refresh()
    {
        var session=room.Session;
        bool real=session?.Shop.View.Active==true;
        bool justCopied=Time.realtimeSinceStartupAsDouble<copiedUntil;
        bool steamJoinBusy=Time.realtimeSinceStartupAsDouble<steamJoinBusyUntil;
        bool showSetup=!room.HasLobby && !room.Busy;
        bool showJoin = inMenu && showSetup;
        bool showMapping = showJoin && Ipv4RoomControls.IsIpv4Code(code.GetString());
        bool showLan = showSetup && showLanOptions;
        bool showProgress=joinPhase!=null;
        bool showHistory=showProgress && joinHistoryOpen;
        bool showAccess=room.IsHost && room.HasLobby;
        bool showSaves=NativePlatform.SupportsHosting && inMenu && !room.IsGuest;
        var issue = CurrentIssue();
        var connectionState=RoomConnectionStatus.Format(room,joinPhase,joinCounters);
        terminalSummary.SetText(issue.Code==null ? connectionState.Summary : $"[{issue.Code}] {issue.Advice}");
        string history=showHistory ? string.Join("\n",joinSteps) : "";
        Vector2 available=PanelViewport();
        var endpoint = room.Ipv4HostEndpoint;
        string signature=$"{room.Status}/{room.Detail}/{room.Code}/{room.Busy}/{room.CanApprove}/"+
            $"{room.PartnerName}/{session?.State}/{real}/{inShop}/{NativeActorCarryUi.Available}/{MapUIManager.Instance?.IsOutside}/{justCopied}/{steamJoinBusy}/{room.FriendsCanJoin}/{showMapping}"+
            $"/{joinPhase}/{joinPercentage:F3}/{joinCounters}/{joinFailureReason}/{history}/{showHistory}/{session?.Native.SynchronizationFailed}/{room.AcceptingJoins}/{showLan}/{issue.Code}/{opening.HasGuestDraft}/{available}";
        if(signature==displaySignature) return;
        displaySignature=signature;
        heading.SetText(Loc(inShop || real ? "native_header" : "room_header"));
        heading.SetVisible(!showSetup);
        steamHint.SetVisible(room.FriendsCanJoin);
        setupRow.SetActive(showSetup);
        joinColumn.SetActive(showJoin);
        lanControls.SetActive(showLan);
        lanToggle.SetText(Loc(showLan ? "hide_lan_options" : "show_lan_options"));
        joinFromMenu.SetVisible(showSetup && !inMenu && !localTest);
        ipv4.Refresh(showLan, showMapping, endpoint);
        roomActions.SetActive(room.HasLobby || room.Busy);
        approvalRow.SetActive(room.CanApprove);
        roomCode.SetVisible(room.HasLobby && room.Code.Length!=0);
        roomCode.SetString(room.Code);
        copy.SetVisible(room.HasLobby && room.IsHost && room.Code.Length!=0);
        copy.SetText(Loc(justCopied ? "code_copied" : "copy"));
        leave.SetVisible(room.HasLobby || room.Busy);
        leave.SetText(Loc(room.IsHost ? "close_room" : room.Busy ? "cancel_connection" : "leave"));
        access.SetVisible(showAccess);
        access.SetText(Loc(room.AcceptingJoins ? "pause_joins" : "resume_joins"));
        approve.SetVisible(room.CanApprove);
        approve.SetText(string.IsNullOrEmpty(room.PartnerName) ? Loc("approve") : Loc("approve")+" "+room.PartnerName);
        decline.SetVisible(room.CanApprove);
        string detail=room.Detail.Length==0 ? "" : $" ({room.Detail})";
        status.SetText(CoopIssueCode.DirectRoom(room.Status)!=null ? $"[{issue.Code}] {issue.Advice}" :
            room.IsHost && session?.Native.SynchronizationFailed==true ? Loc("status_sync_host_failed") :
            steamJoinBusy ? Loc("status_steam_join_busy") :
            Loc(real && room.Status=="connected" ? "status_shop_connected" : "status_"+room.Status)+detail+
            (showAccess && !room.AcceptingJoins ? "\n"+Loc("joins_paused") : ""));
        status.SetVisible(!showSetup || room.Status!="idle" || steamJoinBusy);
        progressSection.SetActive(showProgress);
        joinProgressTrack.SetActive(showProgress);
        joinProgressLabel.SetVisible(showProgress);
        if(showProgress)
        {
            string label=(joinPhase=="sync_failed" && joinFailureCode!=null ? $"[{joinFailureCode}] " : "")+Loc(joinPhase);
            if(joinCounters.Length!=0)
                label+=" · "+(joinPhase=="sync_failed" ? string.Format(Loc("sync_previous_progress"),joinCounters) : joinCounters);
            if(joinFailureReason.Length!=0) label+="\n"+joinFailureReason;
            joinProgressLabel.SetText(label);
            joinProgressFill.anchorMax=new Vector2(joinPercentage,1f);
        }
        joinHistoryToggle.SetVisible(showProgress);
        joinHistoryToggle.SetText(Loc(showHistory ? "sync_hide_log" : "sync_show_log"));
        joinLog.SetActive(showHistory);
        if(showHistory) joinHistory.SetText(history);
        saveRow.SetActive(showSaves);
        bool pairedOpening=NativeOpening.CanHost && room.IsHost;
        bool waitingForPartner=pairedOpening && session?.State!=LinkState.Connected;
        newRun.SetText(Loc(pairedOpening ? "opening_title" : "new_run"));
        newRun.SetInteractable(!waitingForPartner);
        reopenOpening.SetVisible(inMenu && room.IsGuest && opening.HasGuestDraft);
        carry.SetVisible(!inMenu && NativeActorCarryUi.Available);
        carry.SetInteractable(MapUIManager.Instance?.IsOutside==false);
        if(playerNameInput?.input?.isFocused!=true && playerNameInput.GetString()!=CoopSettings.PointerName)
            playerNameInput.SetString(CoopSettings.PointerName);
        shopStatus.SetVisible(inShop || inMenu && room.IsHost);
        shopStatus.SetText(Loc(inShop ? "native_hint" : waitingForPartner ? "opening_connect_partner" : "host_start_hint"));
        RefreshAvailability();
        ResizePanel(available);
    }

    private Vector2 PanelViewport()
    {
        var parent=window.Rect.parent as RectTransform;
        var canvas=parent?.GetComponentInParent<Canvas>()?.rootCanvas;
        if(canvas==null || parent==null) return new Vector2(1920,1080);
        var pixels=canvas.pixelRect;
        var camera=canvas.renderMode==RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if(RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,pixels.min,camera,out var min) &&
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,pixels.max,camera,out var max))
            return max-min;
        return ((RectTransform)canvas.transform).rect.size;
    }

    private void ResizePanel(Vector2 available)
    {
        var rect=window.Rect;
        float width=Mathf.Min(1160,Mathf.Max(480,available.x-64));
        if(!Mathf.Approximately(rect.rect.width,width))
            window.SetSize(width,rect.rect.height);
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        float contentHeight=LayoutUtility.GetPreferredHeight(panelBody);
        var viewport=panelScroll?.viewport;
        float frameHeight=viewport!=null ? Mathf.Max(50,rect.rect.height-viewport.rect.height) : 72;
        float maxHeight=Mathf.Max(260,available.y-64);
        window.SetSize(width,Mathf.Min(maxHeight,Mathf.Max(280,contentHeight+frameHeight+18)));
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
    }

    internal void ObserveDay(NativeShop shop,bool guestWaiting,(string Code,int? Day)? lastRecovery)
    {
        var attempt=shop?.RecoveryCandidate;
        var fault=shop?.FailedMutation;
        if(ReferenceEquals(dayShop,shop) && ReferenceEquals(recoveryAttempt,attempt) &&
            ReferenceEquals(mutationFault,fault) && hostDayPending==guestWaiting && recoveryExit==lastRecovery) return;
        if (!ReferenceEquals(mutationFault,fault))
        {
            pendingMutationRecovery = null;
            if (fault!=null) AddConnectionEvent("ERROR", $"{fault.Code}  {fault.Operation}  ·  {fault.Reason}");
        }
        mutationFault=fault;
        dayShop=shop; recoveryAttempt=attempt; hostDayPending=guestWaiting; recoveryExit=lastRecovery;
        nextRefresh=nextTerminalRefresh=0;
        issueCopied=false;
    }

    private (string Code,string Advice,string Context) CurrentIssue()
    {
        if (mutationFault is { } fault)
            return (fault.Code,Loc("diag_"+fault.Code),
                $"role=host day={StoreStation.GetDayCounter()} operation={fault.Operation} reason={fault.Reason}");
        if(recoveryAttempt is DaySettlement attempt)
        {
            string code=CoopIssueCode.Settlement(attempt);
            return (code,Loc("diag_"+code),
                $"day={attempt.ExpectedDay} save_verified={attempt.SaveVerified} " +
                $"morning_completed={attempt.MorningCompleted} timed_out={attempt.TimedOut} failed={attempt.Failed}");
        }
        if(hostDayPending)
            return ("DAY-301",Loc("diag_DAY-301"),$"role=guest day={room.Session?.Shop.View.Day} phase=SaveUnverified");
        if(dayShop?.PendingSettlement == true)
            return ("DAY-200",Loc("diag_DAY-200"),$"role=host day={StoreStation.GetDayCounter()} phase=Settling");
        if(dayShop?.CurrentNightBlocker is NativeShop.NightBlocker blocker && blocker!=NativeShop.NightBlocker.None)
        {
            string context = $"role=host day={StoreStation.GetDayCounter()} rejected_before_settlement={blocker}";
            if (blocker == NativeShop.NightBlocker.Activity && NativeActorSession.Current?.Activities.BlockingChannel is string channel)
                context += " activity=" + channel;
            return ($"DAY-{(int)blocker}",Loc("day_block_"+blocker),context);
        }
        if(joinFailureCode!=null)
            return (joinFailureCode,Loc("diag_"+joinFailureCode),$"role=guest progress={joinCounters}");
        if(room.IsHost && room.Session?.Native is { SynchronizationFailed:true } native)
        {
            string hostCode=CoopIssueCode.Synchronization(native.FailureReason,false,false);
            return (hostCode,Loc("diag_"+hostCode),$"role=host revision={native.Revision} reason={native.FailureReason}");
        }
        if(CoopIssueCode.DirectRoom(room.Status) is string directCode)
            return (directCode,Loc("status_"+room.Status),$"transport=udp status={room.Status}");
        if(recoveryExit is { } result)
        {
            string resume=result.Day.HasValue ?
                string.Format(Loc("diag_reloaded_from"),result.Day.Value) : Loc("diag_no_checkpoint");
            string explanation=result.Code=="DAY-206" ? Loc("diag_DAY-206")+" "+resume : resume;
            return (result.Code,explanation,$"role=host stopped=true confirmed_day={result.Day?.ToString() ?? "none"}");
        }
        return default;
    }

    private static string FormatIssue((string Code,string Advice,string Context) issue) =>
        $"[{issue.Code}] {issue.Advice}\n{issue.Context}";

    private void RecoverDay()
    {
        if (mutationFault is { } fault && dayShop!=null)
        {
            SetMutationRecoveryPrompt(fault);
            return;
        }
        if(recoveryAttempt==null || dayShop==null) return;
        var issue=CurrentIssue();
        dayShop.RequestSettlementRecovery(recoveryAttempt,FormatIssue(issue));
    }

    private void SetMutationRecoveryPrompt(NativeShop.MutationFault fault)
    {
        pendingMutationRecovery = fault;
        nextTerminalRefresh = 0;
        TickConnectionDetails();
    }

    private void ConfirmMutationRecovery()
    {
        var fault = pendingMutationRecovery;
        SetMutationRecoveryPrompt(null);
        if (fault == null || !ReferenceEquals(fault, mutationFault) || dayShop == null) return;
        if (dayShop.RequestMutationRecovery(fault)) return;
        string message = Loc("mutation_recovery_unavailable");
        AddConnectionEvent("ERROR", message);
        StoreUIManager.Instance?.Notify(message);
    }

    private bool CanConnect=>!room.HasLobby && !room.Busy;

    private void RefreshAvailability()
    {
        join?.SetInteractable(inMenu && CanConnect && !string.IsNullOrWhiteSpace(code.GetString()));
    }

    private void RequestJoin()
    {
        // Let TextMeshPro finish handling the soft keyboard before hiding its input.
        if(NativePlatform.IsAndroid) joinRequestedFrame=Time.frameCount;
        else Join();
    }

    private void Join()
    {
        if(!inMenu || !CanConnect || string.IsNullOrWhiteSpace(code.GetString())) return;
        ResetJoin();
        room.Join(code.GetString(), ipv4.Mapping);
        Refresh();
    }

    private void HostLan()
    {
        if(!CanConnect || string.IsNullOrWhiteSpace(ipv4.Address)) return;
        room.CreateLan(ipv4.Address, ipv4.Port);
        issueCopied=false;
        nextTerminalRefresh=0;
        Refresh();
    }

    private void CopyRoomCode()
    {
        if(!room.IsHost || room.Code.Length==0) return;
        GUIUtility.systemCopyBuffer=room.Code;
        copiedUntil=Time.realtimeSinceStartupAsDouble+1.5;
        Refresh();
    }

    private void LeaveRoom()
    {
        var session=room.Session;
        var confirm=inMenu ? MainMenuUIController.Instance?.confirmPanelUIManager : StoreUIManager.Instance?.confirmPanelUIManager;
        if(session?.State!=Core.LinkState.Connected || confirm==null)
        {
            room.Leave();
            copiedUntil=0;
            Refresh();
            return;
        }
        window.Hide();
        confirm.OpenPanel(Loc(room.IsHost ? "confirm_close_room" : "confirm_leave_room"),()=>{
            if(!ReferenceEquals(room.Session,session)) return;
            room.Leave();
            copiedUntil=0;
        },()=>{ if(window?.IsAlive==true) window.Show(); });
    }

    private void OpenCheckpoints(bool includeHistory=false)
    {
        if (!NativePlatform.SupportsHosting || !inMenu || room.IsGuest) return;
        if (checkpoints?.IsAlive == true) checkpoints.Close();
        var builder = CustomUIManager.Window("probably_coop_checkpoints", Loc("resume_run"), "menu")
            .SetSize(540, 440).Center().SetDraggable(true).SetCloseOnEscape()
            .OnClose(()=>{ CancelCheckpointScan(); if(inMenu && window?.IsAlive==true) window.Show(); });
        builder.BeginRow(6);
        builder.AddLabel(Loc("scan_checkpoints_loading")); checkpointStatus=builder.Last;
        if(!includeHistory)
            builder.AddButton(Loc("scan_history"),()=>{ checkpoints?.Close(); OpenCheckpoints(true); });
        builder.End();
        builder.BeginScroll(330);
        checkpointBuilder=builder;
        checkpointHistory=includeHistory;
        checkpointScan=CoopRun.ScanCheckpoints(includeHistory,previews:true).GetEnumerator();
        checkpointStartFrame=Time.frameCount+1;
        checkpoints = builder.Show();
        checkpoints.BringToFront();
        window?.Hide();
    }

    private void TickCheckpointScan()
    {
        if(checkpointBuilder==null || Time.frameCount<=checkpointStartFrame) return;
        if(!inMenu || room.IsGuest || checkpoints?.IsAlive!=true)
        {
            CancelCheckpointScan();
            return;
        }
        if(!checkpoints.IsVisible) return;
        var timer=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            int added=0;
            for(int i=0;i<16 && added<8 && timer.Elapsed.TotalMilliseconds<4;i++)
            {
                if(!checkpointScan.MoveNext())
                {
                    if(checkpointRows.Count==0) checkpointBuilder.AddLabel(Loc("no_checkpoints"));
                    checkpointStatus.SetText(Loc(checkpointHistory ? "history_scan_complete" : "history_scan_deferred"));
                    CancelCheckpointScan();
                    return;
                }
                var checkpoint=checkpointScan.Current;
                if(checkpoint==null) continue;
                int row=checkpointRows.BinarySearch(checkpoint,newestCheckpointFirst);
                if(row<0) row=~row;
                AddCheckpointRow(checkpointBuilder,checkpoint);
                checkpointBuilder.Last.root.transform.SetSiblingIndex(row);
                checkpointRows.Insert(row,checkpoint);
                added++;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[probably_coop] could not list checkpoints: {ex.Message}");
            checkpointStatus.SetText(Loc("checkpoint_unavailable"));
            CancelCheckpointScan();
        }
    }

    private void CancelCheckpointScan()
    {
        checkpointScan?.Dispose();
        checkpointScan=null;
        checkpointBuilder=null;
        checkpointRows.Clear();
    }

    private void AddCheckpointRow(CustomUIBuilder builder,CoopRun.Checkpoint checkpoint)
    {
        string label=$"{checkpoint.Name} · {Loc("day")} {checkpoint.Day} · {checkpoint.Cash}"+
            (checkpoint.Historical ? " · "+Loc("history_checkpoint") : "")+
            (checkpoint.NeedsRecovery ? " · "+Loc("recover_checkpoint") : "");
        builder.AddButton(label,()=>Resume(checkpoint));
        if(builder.Last.text!=null) builder.Last.text.richText=false;
        builder.Last.SetInteractable(checkpoint.HasActors);
        builder.Last.SetTooltip(checkpoint.HasActors ? checkpoint.SavedAt.ToString("yyyy-MM-dd HH:mm")+
            (checkpoint.NeedsRecovery ? "\n"+Loc(checkpoint.Historical ? "recover_checkpoint_history" :
                checkpoint.StoredSlot!=checkpoint.Slot ? "recover_checkpoint_copy" : "recover_checkpoint_register") : "") :
            Loc("incompatible_checkpoint")+"\n"+checkpoint.UnavailableReason);
    }

    private void OpenSoloCheckpoints()
    {
        if (!NativePlatform.SupportsHosting || !inMenu || room.IsGuest) return;
        if (checkpoints?.IsAlive == true) checkpoints.Close();
        var builder = CustomUIManager.Window("probably_coop_solo_checkpoints", Loc("solo_import"), "menu")
            .SetSize(610, 460).Center().SetDraggable(true).SetCloseOnEscape()
            .OnClose(() => { if (inMenu && window?.IsAlive == true) window.Show(); });
        builder.AddLabel(Loc("solo_import_hint"));
        builder.AddWarning("", "solo_import_error");
        var errorLabel = builder.Last;
        if (errorLabel.text != null) errorLabel.text.richText = false;
        errorLabel.SetVisible(false);
        builder.BeginScrollFlexible(180);
        try
        {
            var saves = CoopRun.ListSoloCheckpoints();
            if (saves.Count == 0) builder.AddLabel(Loc("solo_import_no_saves"));
            foreach (var save in saves)
            {
                string label = string.Format(Loc("solo_import_row"), save.Name, save.Slot + 1, save.Day, save.Cash);
                builder.AddButton(label, () => ConfirmSoloImport(save, errorLabel), "solo_slot_" + save.Slot);
                if (builder.Last.text != null) builder.Last.text.richText = false;
                builder.Last.SetTooltip(save.SavedAt.ToString("yyyy-MM-dd HH:mm"));
            }
        }
        catch (Exception error)
        {
            ShowSoloImportError(errorLabel, error.Message);
            Debug.LogWarning($"[probably_coop] could not list single-player saves: {error.Message}");
        }
        builder.End();
        builder.AddButton(Loc("solo_import_back"), () => checkpoints?.Close(), "solo_import_back");
        checkpoints = builder.Show();
        window?.Hide();
    }

    private void ConfirmSoloImport(CoopRun.Checkpoint selected, CustomUIElement errorLabel)
    {
        var confirmation = MainMenuUIController.Instance?.confirmPanelUIManager;
        var source = checkpoints;
        if (!inMenu || room.IsGuest || confirmation == null || source?.IsVisible != true) return;
        errorLabel.SetVisible(false);
        source.Hide();
        bool answered = false;
        confirmation.OpenPanel(string.Format(Loc("solo_import_confirm"), selected.Slot + 1, selected.Day, selected.Cash), () =>
        {
            if (answered) return;
            answered = true;
            if (!inMenu || room.IsGuest || !ReferenceEquals(checkpoints, source) || !source.IsAlive) return;
            CoopRun.Checkpoint imported;
            string reason;
            try { imported = CoopRun.ImportSolo(selected, out reason); }
            catch (Exception error)
            {
                imported = null;
                reason = error.Message;
                Debug.LogWarning($"[probably_coop] could not import single-player save: {error.Message}");
            }
            if (imported == null)
            {
                ShowSoloImportError(errorLabel, reason);
                source.Show();
                return;
            }
            source.Close();
            Resume(imported);
        }, () =>
        {
            if (answered) return;
            answered = true;
            if (inMenu && !room.IsGuest && ReferenceEquals(checkpoints, source) && source.IsAlive) source.Show();
        });
    }

    private static void ShowSoloImportError(CustomUIElement label, string reason)
    {
        label.SetText(string.IsNullOrWhiteSpace(reason)
            ? Loc("solo_import_failed")
            : string.Format(Loc("solo_import_failure_reason"), reason));
        label.SetVisible(true);
    }

    internal void Resume(CoopRun.Checkpoint checkpoint)
    {
        var menu = MainMenuUIController.Instance;
        if (!inMenu || menu == null || room.IsGuest) return;
        void Load()
        {
            try
            {
                if (!CoopRun.PrepareResume(checkpoint))
                {
                    menu.confirmPanelUIManager.OpenPanel(Loc("checkpoint_unavailable"), hideNoButton: true);
                    return;
                }
                checkpoints?.Close();
                window?.Hide();
                menu.StartNewGameClick();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[probably_coop] could not resume checkpoint: {ex.Message}");
                menu.confirmPanelUIManager.OpenPanel(Loc("checkpoint_unavailable"), hideNoButton: true);
            }
        }

        string locale = LocHelper.GetCurrentLocaleCode();
        if (!string.IsNullOrEmpty(checkpoint.Locale) && checkpoint.Locale != locale)
        {
            string warning = LocHelper.GetLocalizedMainMenu("load_locale_mismatch_warning", checkpoint.Locale, locale);
            menu.confirmPanelUIManager.OpenPanel(warning, Load);
        }
        else Load();
    }

    private void StartCoopGame()
    {
        var menu=MainMenuUIController.Instance;
        if(!NativePlatform.SupportsHosting || !inMenu || room.IsGuest || menu==null) return;
        bool paired=NativeOpening.CanHost && room.IsHost;
        if(paired && room.Session?.State!=LinkState.Connected) return;
        Action<MainMenuUIController, NewGameData> open = paired ? (controller,data) =>
        {
            if(!opening.BeginHost(room,controller,data))
                throw new InvalidOperationException(Loc("opening_rejected"));
        } : null;
        if(!CoopNewGameFlow.Begin(menu,open)) return;
        checkpoints?.Close();
        window.Hide();
    }

#if PROBABLY_COOP_GRID_QA
    internal void QaStartPairedOpening()
    {
        Open();
        window.Get("coop_new_run").button.onClick.Invoke();
    }

    internal CoopOpeningLobby QaOpeningLobby => opening;

    internal void QaVerifyLauncherOption() => entries.QaVerifyLauncherOption(Open);
    internal bool QaSelectSoloImport(int slot)
    {
        Open();
        window.Get("import_solo").button.onClick.Invoke();
        var row = checkpoints.Get("solo_slot_" + slot);
        if (row?.button == null) return false;
        row.button.onClick.Invoke();
        return MainMenuUIController.Instance.confirmPanelUIManager.panel.activeSelf;
    }
#endif

    public void Dispose()
    {
        if(window?.IsAlive==true) window.Close();
        CloseDiagnosticsWindows();
        entries.Dispose();
        if(checkpoints?.IsAlive==true) checkpoints.Close();
        if(menuButton!=null) UnityEngine.Object.Destroy(menuButton);
        CoopNewGameFlow.Cancel();
    }
}
