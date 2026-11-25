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
internal sealed class CoopPanel : IDisposable
{
    private readonly RoomSelector room;
    private readonly bool localTest;
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
    private CustomUIElement shopStatus, heading, carry, steamHint, playerNameInput;
    private CustomUIElement lanToggle, joinFromMenu;
    private CustomUIElement joinProgressLabel, joinHistory, joinHistoryToggle;
    private RectTransform joinProgressFill;
    private GameObject joinProgressTrack;
    private CustomUIElement terminalSummary, terminalButton, terminalDetails;
    private GameObject setupRow, joinColumn, lanControls, saveRow, roomActions, approvalRow, joinLog,
        playerNameRow, contactsRow, terminalRow, terminalLog;
    private bool showLanOptions, showTerminalDetails;
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

    internal CoopPanel(RoomSelector room,bool localTest) { this.room=room; this.localTest=localTest; }
    private static string Loc(string key)=>ModHelper.GetLocalized("probably_coop",key);

    internal void SetContext(bool menu,bool shop)
    {
        if(menu==inMenu && shop==inShop) return;
        inMenu=menu; inShop=shop;
        reopenJoinPanel |= joinPanelWasVisible && joinPhase!=null && room.IsGuest;
        if(window?.IsAlive==true) window.Close();
        entries.Dispose();
        if(checkpoints?.IsAlive==true) checkpoints.Close();
        if(menuButton!=null) UnityEngine.Object.Destroy(menuButton);
        menuButton=null;
        copiedUntil=0;
        steamJoinBusyUntil=0;
        nextTerminalRefresh=0;
        displaySignature=null;
    }
    internal void Tick()
    {
        if(CustomUIManager.Instance==null) return;
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
        if(window?.IsAlive==true) { window.Show(); window.BringToFront(); displaySignature=null; Refresh(); return; }
        var builder=CustomUIManager.Window("probably_coop_panel",Loc("title"),inMenu ? "menu" : "overlay")
            .SetSize(1040,590).Center().SetDraggable(true).SetCloseOnEscape();
        builder.AddLabel(Loc(inShop ? "native_header" : "room_header")); heading=builder.Last;
        builder.AddLabel(""); status=builder.Last; status.text.richText=false;
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
        joinLog=BeginScrollBlock(builder,112);
        builder.AddLabel(""); joinHistory=builder.Last; joinHistory.text.richText=false;
        builder.End();
        builder.AddLabel(Loc("steam_friend_hint")); steamHint=builder.Last;
        ipv4.AddListening(builder);

        builder.BeginRow(9);
        setupRow=builder.CurrentContainer.gameObject;
        builder.BeginPanel(width:500,alignment:TextAnchor.UpperLeft,padding:8,spacing:6);
        joinColumn=builder.CurrentContainer.gameObject;
        builder.AddHeader(Loc("join_room_heading"));
        builder.AddInput(Loc("join_code_hint"),onTyping:_=>RefreshAvailability()); code=builder.Last;
        code.input.characterLimit=64;
        code.input.onSubmit.AddListener(_=>Join());
        builder.BeginRow(6);
        builder.AddButton(Loc("paste_room_code"),PasteRoomCode);
        builder.AddButton(Loc("join"),Join); join=builder.Last;
        builder.End();
        ipv4.AddMapping(builder, Join);
        builder.End();

        builder.BeginPanel(width:inMenu ? 500 : -1,alignment:TextAnchor.UpperLeft,padding:8,spacing:6);
        builder.AddHeader(Loc("host_room_heading"));
        builder.AddButton(Loc(localTest ? "local_create" : "create"),()=>{room.Create();Refresh();});
        builder.AddButton(Loc("show_lan_options"),()=>{
            showLanOptions=!showLanOptions;
            Refresh();
        }); lanToggle=builder.Last;
        builder.BeginColumn(6);
        lanControls=builder.CurrentContainer.gameObject;
        ipv4.AddHost(builder, HostLan, compact:true);
        builder.End().End().End();
        builder.AddLabel(Loc("join_from_menu")); joinFromMenu=builder.Last;

        builder.BeginRow(6);
        roomActions=builder.CurrentContainer.gameObject;
        builder.AddInput(Loc("room_code")); roomCode=builder.Last;
        roomCode.input.readOnly=true;
        builder.AddButton(Loc("copy"),CopyRoomCode); copy=builder.Last;
        builder.AddButton(Loc("leave"),LeaveRoom); leave=builder.Last;
        builder.End();
        builder.AddButton(Loc("pause_joins"),()=>{ room.SetAcceptingJoins(!room.AcceptingJoins); Refresh(); }); access=builder.Last;
        builder.BeginRow(6);
        approvalRow=builder.CurrentContainer.gameObject;
        builder.AddButton(Loc("approve"),()=>{ room.Approve(); Refresh(); }); approve=builder.Last;
        if(approve.text!=null) approve.text.richText=false;
        builder.AddButton(Loc("decline_join"),()=>{ room.Decline(); Refresh(); }); decline=builder.Last;
        builder.End();
        builder.BeginColumn(5);
        saveRow=builder.CurrentContainer.gameObject;
        builder.AddHeader(Loc("coop_saves_heading"));
        builder.BeginRow(6);
        builder.AddButton(Loc("new_run"),StartCoopGame);
        builder.AddButton(Loc("resume_run"),()=>OpenCheckpoints());
        builder.AddButton(Loc("solo_import_button"),OpenSoloCheckpoints,"import_solo");
        builder.End().End();
        builder.AddButton(Loc("personal_carry"),()=>{ NativeActorCarryUi.Toggle(); }); carry=builder.Last;
        builder.AddLabel(""); shopStatus=builder.Last;
        if(!inMenu) entries.AddLauncherOption(builder);
        builder.BeginRow(6);
        playerNameRow=builder.CurrentContainer.gameObject;
        builder.AddLabel(Loc("player_name"));
        builder.AddInput(Loc("player_name_input"),CoopSettings.PointerName); playerNameInput=builder.Last;
        playerNameInput.input.characterLimit=PointerProfile.NameLimit;
        var playerNameLayout=playerNameInput.root.GetComponent<LayoutElement>() ?? playerNameInput.root.AddComponent<LayoutElement>();
        playerNameLayout.minWidth=220;
        playerNameLayout.flexibleWidth=1;
        playerNameInput.input.onSubmit.AddListener(_=>SavePlayerName());
        builder.AddButton(Loc("change_player_name"),SavePlayerName);
        builder.End();
        builder.BeginRow(6);
        terminalRow=builder.CurrentContainer.gameObject;
        builder.AddLabel(Loc("terminal_title")); terminalSummary=builder.Last;
        terminalSummary.text.richText=false;
        terminalSummary.text.enableWordWrapping=false;
        terminalSummary.text.overflowMode=TextOverflowModes.Ellipsis;
        var summaryLayout=terminalSummary.root.GetComponent<LayoutElement>() ?? terminalSummary.root.AddComponent<LayoutElement>();
        summaryLayout.minWidth=280;
        summaryLayout.preferredWidth=400;
        summaryLayout.flexibleWidth=1;
        builder.AddButton(Loc("terminal_show_details"),()=>{
            showTerminalDetails=!showTerminalDetails;
            nextTerminalRefresh=0;
            Refresh();
        }); terminalButton=builder.Last;
        builder.End();
        terminalLog=BeginScrollBlock(builder,88);
        builder.AddLabel(""); terminalDetails=builder.Last; terminalDetails.text.richText=false;
        builder.End();
        builder.BeginRow(6);
        contactsRow=builder.CurrentContainer.gameObject;
        builder.AddLabel(Loc("contact_title"));
        builder.AddButton(Loc("contact_qq_group"),()=>CopyContact("1132567943", "contact_qq_group_copied"));
        builder.AddButton(Loc("contact_author_qq"),()=>CopyContact("1600597561", "contact_author_qq_copied"));
        builder.AddButton(Loc("contact_discord"),()=>CopyContact("jianbao1954", "contact_discord_copied"));
        builder.AddButton(Loc("contact_bilibili"),()=>CopyContact("https://space.bilibili.com/234054413", "contact_bilibili_copied"));
        builder.End();
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

    // BeginScroll selects its content as CurrentContainer. Hide the full
    // scroll widget so a collapsed record does not leave a fixed-height gap.
    private static GameObject BeginScrollBlock(CustomUIBuilder builder,float height)
    {
        var parent=builder.CurrentContainer;
        int sibling=parent.childCount;
        builder.BeginScroll(height);
        return parent.GetChild(sibling).gameObject;
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
    internal void JoinFailed(ModExtensionJoinException mismatch=null,NativeSyncFailure reason=NativeSyncFailure.None,bool localError=false)
    {
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
        reopenJoinPanel=true;
    }
    private void ResetJoin()
    {
        joinSession=null; joinPhase=null; joinPages=0; joinPercentage=0; joinCounters=joinFailureReason="";
        joinSteps.Clear();
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
        bool showSaves=inMenu && !room.IsGuest;
        bool showTerminal=!inMenu || room.HasLobby || room.Busy || showProgress;
        bool expandedTerminal=showTerminal && showTerminalDetails;
        RefreshTerminal(showTerminal,expandedTerminal);
        string history=showHistory ? string.Join("\n",joinSteps) : "";
        var endpoint = room.Ipv4HostEndpoint;
        string signature=$"{room.Status}/{room.Detail}/{room.Code}/{room.Busy}/{room.CanApprove}/"+
            $"{room.PartnerName}/{session?.State}/{real}/{inShop}/{NativeActorCarryUi.Available}/{MapUIManager.Instance?.IsOutside}/{justCopied}/{steamJoinBusy}/{room.FriendsCanJoin}/{showMapping}"+
            $"/{joinPhase}/{joinPercentage:F3}/{joinCounters}/{joinFailureReason}/{history}/{showHistory}/{session?.Native.SynchronizationFailed}/{room.AcceptingJoins}/{showLan}/{expandedTerminal}";
        if(signature==displaySignature) return;
        displaySignature=signature;
        heading.SetText(Loc(inShop || real ? "native_header" : "room_header"));
        heading.SetVisible(!showSetup);
        int baseHeight=showSetup ? (inMenu ? 300 : 274) : (showSaves ? 246 : 214);
        window.SetSize(1040,baseHeight+42+(showLan ? 106 : 0)+(showMapping ? 35 : 0)+
            (room.FriendsCanJoin || endpoint!=null ? 30 : 0)+(showAccess ? 35 : 0)+(room.CanApprove ? 38 : 0)+
            (showProgress ? 92 : 0)+(joinFailureReason.Length>0 ? 30 : 0)+(showHistory ? 116 : 0)+
            (showTerminal ? 36 : 0)+(expandedTerminal ? 92 : 0));
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
        status.SetText(room.IsHost && session?.Native.SynchronizationFailed==true ? Loc("status_sync_host_failed") :
            steamJoinBusy ? Loc("status_steam_join_busy") :
            Loc(real && room.Status=="connected" ? "status_shop_connected" : "status_"+room.Status)+detail+
            (showAccess && !room.AcceptingJoins ? "\n"+Loc("joins_paused") : ""));
        status.SetVisible(!showSetup || room.Status!="idle" || steamJoinBusy);
        joinProgressTrack.SetActive(showProgress);
        joinProgressLabel.SetVisible(showProgress);
        if(showProgress)
        {
            string label=Loc(joinPhase);
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
        carry.SetVisible(!inMenu && NativeActorCarryUi.Available);
        carry.SetInteractable(MapUIManager.Instance?.IsOutside==false);
        playerNameRow.SetActive(true);
        if(playerNameInput?.input?.isFocused!=true && playerNameInput.GetString()!=CoopSettings.PointerName)
            playerNameInput.SetString(CoopSettings.PointerName);
        contactsRow.SetActive(true);
        shopStatus.SetVisible(inShop || inMenu && room.IsHost);
        shopStatus.SetText(Loc(inShop ? "native_hint" : "host_start_hint"));
        RefreshAvailability();
    }

    private void RefreshTerminal(bool visible,bool expanded)
    {
        terminalRow.SetActive(visible);
        terminalLog.SetActive(expanded);
        double now=Time.realtimeSinceStartupAsDouble;
        if(!visible || now<nextTerminalRefresh) return;
        nextTerminalRefresh=now+0.5;
        var state=RoomConnectionStatus.Format(room,joinPhase,joinCounters);
        terminalSummary.SetText(state.Summary);
        terminalButton.SetText(Loc(expanded ? "terminal_hide_details" : "terminal_show_details"));
        if(expanded) terminalDetails.SetText(string.IsNullOrEmpty(state.Details) ? Loc("terminal_no_session") : state.Details);
    }

    private bool CanConnect=>!room.HasLobby && !room.Busy;

    private void RefreshAvailability()
    {
        join?.SetInteractable(inMenu && CanConnect && !string.IsNullOrWhiteSpace(code.GetString()));
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
        if (!inMenu || room.IsGuest) return;
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
        if (!inMenu || room.IsGuest) return;
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

    private void Resume(CoopRun.Checkpoint checkpoint)
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
        if(!inMenu || room.IsGuest || menu==null || !CoopNewGameFlow.Begin(menu)) return;
        checkpoints?.Close();
        window.Hide();
    }

#if PROBABLY_COOP_GRID_QA
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
        entries.Dispose();
        if(checkpoints?.IsAlive==true) checkpoints.Close();
        if(menuButton!=null) UnityEngine.Object.Destroy(menuButton);
        CoopNewGameFlow.Cancel();
    }
}
