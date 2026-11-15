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
    private CustomUIElement resume, code, roomCode, status, create, join, copy, leave, approve, decline, access;
    private CustomUIElement newRun, shopStatus, heading, carry, steamHint;
    private CustomUIElement joinHeading, hostHeading, joinFromMenu;
    private CustomUIElement joinProgress, joinProgressLabel, joinHistory, joinHistoryToggle;
    private RectTransform joinProgressFill;
    private readonly Queue<string> joinSteps=new Queue<string>();
    private CoopSession joinSession;
    private string joinPhase;
    private bool joinHistoryOpen, joinPanelWasVisible, reopenJoinPanel;
    private int joinPages;
    private float joinPercentage;
    private string joinCounters="";
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
            .SetSize(660,480).Center().SetDraggable(true).SetCloseOnEscape();
        builder.AddLabel(Loc(inShop ? "native_header" : "room_header")); heading=builder.Last;
        builder.AddLabel(""); status=builder.Last; status.text.richText=false;
        builder.AddLabel(""); joinProgressLabel=builder.Last;
        builder.AddLabel(""); joinProgress=builder.Last;
        var barLayout=joinProgress.root.GetComponent<LayoutElement>() ?? joinProgress.root.AddComponent<LayoutElement>();
        barLayout.minHeight=barLayout.preferredHeight=12f;
        var track=ProgressImage(joinProgress.Rect,"SyncTrack",new Color32(39,36,50,255));
        joinProgressFill=ProgressImage(track,"SyncFill",new Color32(91,188,176,255));
        builder.AddButton(Loc("sync_show_log"),()=>{ joinHistoryOpen=!joinHistoryOpen; displaySignature=null; Refresh(); });
        joinHistoryToggle=builder.Last;
        builder.AddLabel(""); joinHistory=builder.Last; joinHistory.text.richText=false;
        builder.AddLabel(Loc("steam_friend_hint")); steamHint=builder.Last;

        builder.AddHeader(Loc("join_room_heading")); joinHeading=builder.Last;
        builder.BeginRow(6);
        builder.AddInput(Loc("join_code_hint"),onTyping:_=>RefreshAvailability()); code=builder.Last;
        code.input.characterLimit=64;
        code.input.onSubmit.AddListener(_=>Join());
        builder.AddButton(Loc("join"),Join); join=builder.Last;
        builder.End();

        ipv4.AddMapping(builder, Join);
        builder.AddHeader(Loc("host_room_heading")); hostHeading=builder.Last;
        builder.AddButton(Loc(localTest ? "local_create" : "create"),()=>{room.Create();Refresh();}); create=builder.Last;
        ipv4.AddHost(builder, HostLan);
        builder.AddLabel(Loc("join_from_menu")); joinFromMenu=builder.Last;

        builder.BeginRow(6);
        builder.AddInput(Loc("room_code")); roomCode=builder.Last;
        roomCode.input.readOnly=true;
        builder.AddButton(Loc("copy"),CopyRoomCode); copy=builder.Last;
        builder.AddButton(Loc("leave"),LeaveRoom); leave=builder.Last;
        builder.End();
        builder.AddButton(Loc("pause_joins"),()=>{ room.SetAcceptingJoins(!room.AcceptingJoins); Refresh(); }); access=builder.Last;
        builder.BeginRow(6);
        builder.AddButton(Loc("approve"),()=>{ room.Approve(); Refresh(); }); approve=builder.Last;
        if(approve.text!=null) approve.text.richText=false;
        builder.AddButton(Loc("decline_join"),()=>{ room.Decline(); Refresh(); }); decline=builder.Last;
        builder.End();
        builder.BeginRow(6);
        builder.AddButton(Loc("new_run"),StartCoopGame); newRun=builder.Last;
        builder.AddButton(Loc("resume_run"),OpenCheckpoints); resume=builder.Last;
        builder.End();
        builder.AddButton(Loc("personal_carry"),()=>{ NativeActorCarryUi.Toggle(); }); carry=builder.Last;
        builder.AddLabel(""); shopStatus=builder.Last;
        entries.AddLauncherOption(builder);
        window=builder.Show(); window.BringToFront(); displaySignature=null; Refresh();
        Debug.Log("[probably_coop] room panel opened");
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
    internal void JoinFailed()
    {
        SetJoinPhase("sync_failed",joinPercentage);
        reopenJoinPanel=true;
    }
    private void ResetJoin()
    {
        joinSession=null; joinPhase=null; joinPages=0; joinPercentage=0; joinCounters="";
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
    private static RectTransform ProgressImage(RectTransform parent,string name,Color color)
    {
        var child=new GameObject(name,typeof(RectTransform),typeof(Image));
        var rect=(RectTransform)child.transform;
        rect.SetParent(parent,false);
        rect.anchorMin=Vector2.zero;
        rect.anchorMax=Vector2.one;
        rect.offsetMin=rect.offsetMax=Vector2.zero;
        var image=child.GetComponent<Image>();
        image.color=color;
        image.raycastTarget=false;
        return rect;
    }
    private void VerifyJoinUi()
    {
        if(window?.IsVisible!=true) return;
        Refresh();
        if(joinProgressFill==null || !joinProgressLabel.text.text.Contains(Loc(joinPhase))) return;
        if(joinPhase=="sync_ready" && joinProgressFill.anchorMax.x<0.999f) return;
        if(joinHistoryOpen) joinHistoryToggle.button.onClick.Invoke();
        joinHistoryToggle.button.onClick.Invoke();
        if(!joinHistoryOpen || !joinHistory.root.activeSelf || !joinHistory.text.text.Contains(Loc(joinPhase))) return;
        joinHistoryToggle.button.onClick.Invoke();
        if(joinHistoryOpen || joinHistory.root.activeSelf) return;
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
        bool showProgress=joinPhase!=null;
        bool showHistory=showProgress && joinHistoryOpen;
        bool showAccess=room.IsHost && room.HasLobby;
        string history=showHistory ? string.Join("\n",joinSteps) : "";
        var endpoint = room.Ipv4HostEndpoint;
        string signature=$"{room.Status}/{room.Detail}/{room.Code}/{room.Busy}/{room.CanApprove}/"+
            $"{room.PartnerName}/{session?.State}/{real}/{inShop}/{NativeActorCarryUi.Available}/{MapUIManager.Instance?.IsOutside}/{justCopied}/{steamJoinBusy}/{room.FriendsCanJoin}/{showMapping}"+
            $"/{joinPhase}/{joinPercentage:F3}/{joinCounters}/{history}/{showHistory}/{session?.Native.SynchronizationFailed}/{room.AcceptingJoins}";
        if(signature==displaySignature) return;
        displaySignature=signature;
        heading.SetText(Loc(inShop || real ? "native_header" : "room_header"));
        window.SetSize(740, (showSetup ? (inMenu ? 440 : 395) + (showMapping ? 40 : 0) :
            room.FriendsCanJoin || endpoint != null ? 324 : 290) + 40+(showAccess ? 40 : 0)+(room.CanApprove ? 40 : 0)+
            (showProgress ? 102 : 0)+(showHistory ? 150 : 0));
        steamHint.SetVisible(room.FriendsCanJoin);
        joinHeading.SetVisible(showJoin);
        code.SetVisible(showJoin);
        join.SetVisible(showJoin);
        hostHeading.SetVisible(showSetup);
        joinFromMenu.SetVisible(showSetup && !inMenu && !localTest);
        ipv4.Refresh(showSetup, showMapping, endpoint);
        create.SetVisible(showSetup);
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
        joinProgress.SetVisible(showProgress);
        joinProgressLabel.SetVisible(showProgress);
        if(showProgress)
        {
            string label=Loc(joinPhase);
            if(joinCounters.Length!=0) label+=" · "+joinCounters;
            joinProgressLabel.SetText(label);
            joinProgressFill.anchorMax=new Vector2(joinPercentage,1f);
        }
        joinHistoryToggle.SetVisible(showProgress);
        joinHistoryToggle.SetText(Loc(showHistory ? "sync_hide_log" : "sync_show_log"));
        joinHistory.SetVisible(showHistory);
        if(showHistory) joinHistory.SetText(history);
        newRun.SetVisible(inMenu && !room.IsGuest); resume.SetVisible(inMenu && !room.IsGuest);
        carry.SetVisible(!inMenu && NativeActorCarryUi.Available);
        carry.SetInteractable(MapUIManager.Instance?.IsOutside==false);
        shopStatus.SetVisible(inShop || inMenu && room.IsHost);
        shopStatus.SetText(Loc(inShop ? "native_hint" : "host_start_hint"));
        RefreshAvailability();
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

    private void OpenCheckpoints()
    {
        if (!inMenu || room.IsGuest) return;
        if (checkpoints?.IsAlive == true) checkpoints.Close();
        var builder = CustomUIManager.Window("probably_coop_checkpoints", Loc("resume_run"), "menu")
            .SetSize(540, 440).Center().SetDraggable(true).SetCloseOnEscape()
            .OnClose(()=>{ if(inMenu && window?.IsAlive==true) window.Show(); });
        builder.BeginScroll(310);
        try
        {
            var saves = CoopRun.ListCheckpoints(includeHistory:true);
            if (saves.Count == 0) builder.AddLabel(Loc("no_checkpoints"));
            foreach (var checkpoint in saves)
            {
                string label=$"{checkpoint.Name} · {Loc("day")} {checkpoint.Day} · {checkpoint.Cash}"+
                    (checkpoint.Historical ? " · "+Loc("history_checkpoint") : "")+
                    (checkpoint.NeedsRecovery ? " · "+Loc("recover_checkpoint") : "");
                builder.AddButton(label, () =>
                    Resume(checkpoint));
                if (builder.Last.text != null) builder.Last.text.richText = false;
                builder.Last.SetInteractable(checkpoint.HasActors);
                builder.Last.SetTooltip(checkpoint.HasActors ? checkpoint.SavedAt.ToString("yyyy-MM-dd HH:mm")+
                    (checkpoint.NeedsRecovery ? "\n"+Loc(checkpoint.Historical ? "recover_checkpoint_history" :
                        checkpoint.StoredSlot!=checkpoint.Slot ? "recover_checkpoint_copy" : "recover_checkpoint_register") : "") :
                    Loc("incompatible_checkpoint")+"\n"+checkpoint.UnavailableReason);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[probably_coop] could not list checkpoints: {ex.Message}");
            builder.AddLabel(Loc("checkpoint_unavailable"));
        }
        builder.End();
        checkpoints = builder.Show();
        checkpoints.BringToFront();
        window?.Hide();
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
