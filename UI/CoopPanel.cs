using System;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace ProbablyCoop;

// Room management only. All economic inputs belong to the original game UI;
// the old transport test protocol remains available to offline tests, not here.
internal sealed class CoopPanel : IDisposable
{
    private readonly RoomSelector room;
    private readonly bool localTest;
    private GameObject menuButton;
    private TextMeshProUGUI menuLabel, originalMenuLabel;
    private CustomUIWindow launcher, window, checkpoints;
    private CustomUIElement resume, lanHost, code, address, roomCode, status, create, join, copy, leave, approve;
    private CustomUIElement newRun, shopStatus, heading, carry, steamHint;
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
        if(window?.IsAlive==true) window.Close();
        if(launcher?.IsAlive==true) launcher.Close();
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
        if(menuButton!=null)
        {
            var title=Loc("title");
            if(menuLabel.text!=title) menuLabel.text=title;
            if(originalMenuLabel!=null && menuLabel.font!=originalMenuLabel.font) menuLabel.font=originalMenuLabel.font;
            if(launcher?.IsAlive==true) launcher.Close();
        }
        else if(launcher==null || !launcher.IsAlive) CreateLauncher();
        if(Input.GetKeyDown(KeyCode.F7) && !InputFocused(code) && !InputFocused(address) && !InputFocused(roomCode))
        {
            if(checkpoints?.IsVisible==true) checkpoints.Close();
            else if(window?.IsVisible==true) window.Hide();
            else Open();
        }
        if(window?.IsVisible==true && Time.realtimeSinceStartupAsDouble>=nextRefresh)
        { nextRefresh=Time.realtimeSinceStartupAsDouble+0.15; Refresh(); }
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

    private void CreateLauncher()
    {
        var builder=CustomUIManager.Window("probably_coop_launcher",Loc("title"),inMenu ? "menu" : "overlay").SetSize(225,104);
        builder.AddButton(Loc("open"),Open);
        launcher=builder.Show();
        launcher.Rect.anchorMin=launcher.Rect.anchorMax=new Vector2(0,0.5f);
        launcher.Rect.pivot=new Vector2(0,0.5f);
        launcher.Rect.anchoredPosition=new Vector2(inMenu ? -875 : 26,inMenu ? -190 : 250);
        Debug.Log("[probably_coop] launcher shown");
    }
    private void Open()
    {
        if(window?.IsAlive==true) { window.Show(); Refresh(); return; }
        var builder=CustomUIManager.Window("probably_coop_panel",Loc("title"),inMenu ? "menu" : "overlay")
            .SetSize(660,480).Center().SetDraggable(true).SetCloseOnEscape();
        builder.AddLabel(Loc(inShop ? "native_header" : "room_header")); heading=builder.Last;
        builder.AddLabel(""); status=builder.Last; status.text.richText=false;
        builder.AddLabel(Loc("steam_friend_hint")); steamHint=builder.Last;

        builder.BeginRow(6);
        builder.AddInput(Loc("join_code_hint"),onTyping:_=>RefreshAvailability()); code=builder.Last;
        code.input.characterLimit=64;
        code.input.onSubmit.AddListener(_=>Join());
        builder.AddButton(Loc("join"),Join); join=builder.Last;
        builder.End();

        builder.BeginRow(6);
        builder.AddInput(Loc("host_address_hint"),onTyping:_=>RefreshAvailability()); address=builder.Last;
        address.input.characterLimit=15;
        address.input.onSubmit.AddListener(_=>HostLan());
        builder.AddButton(Loc("lan_create"),HostLan); lanHost=builder.Last;
        builder.End();
        builder.AddButton(Loc(localTest ? "local_create" : "create"),()=>{room.Create();Refresh();}); create=builder.Last;

        builder.BeginRow(6);
        builder.AddInput(Loc("room_code")); roomCode=builder.Last;
        roomCode.input.readOnly=true;
        builder.AddButton(Loc("copy"),CopyRoomCode); copy=builder.Last;
        builder.AddButton(Loc("leave"),LeaveRoom); leave=builder.Last;
        builder.End();
        builder.AddButton(Loc("approve"),room.Approve); approve=builder.Last;
        if(approve.text!=null) approve.text.richText=false;
        builder.BeginRow(6);
        builder.AddButton(Loc("new_run"),StartCoopGame); newRun=builder.Last;
        builder.AddButton(Loc("resume_run"),OpenCheckpoints); resume=builder.Last;
        builder.End();
        builder.AddButton(Loc("personal_carry"),()=>{ NativeActorCarryUi.Toggle(); }); carry=builder.Last;
        builder.AddLabel(""); shopStatus=builder.Last;
        window=builder.Show(); window.BringToFront(); displaySignature=null; Refresh();
        Debug.Log("[probably_coop] room panel opened");
    }
    internal void ShowSteamJoin(bool roomBusy)
    {
        steamJoinBusyUntil=roomBusy ? Time.realtimeSinceStartupAsDouble+5 : 0;
        Open();
        window.BringToFront();
    }

    private void Refresh()
    {
        var session=room.Session;
        bool real=session?.Shop.View.Active==true;
        bool justCopied=Time.realtimeSinceStartupAsDouble<copiedUntil;
        bool steamJoinBusy=Time.realtimeSinceStartupAsDouble<steamJoinBusyUntil;
        string signature=$"{room.Status}/{room.Detail}/{room.Code}/{room.Busy}/{room.CanApprove}/"+
            $"{room.PartnerName}/{session?.State}/{real}/{inShop}/{NativeActorCarryUi.Available}/{MapUIManager.Instance?.IsOutside}/{justCopied}/{steamJoinBusy}/{room.FriendsCanJoin}";
        if(signature==displaySignature) return;
        displaySignature=signature;
        heading.SetText(Loc(inShop || real ? "native_header" : "room_header"));
        bool showSetup=!room.HasLobby && !room.Busy;
        window.SetSize(660,showSetup ? (inMenu ? 360 : 320) : room.FriendsCanJoin ? 324 : 290);
        steamHint.SetVisible(room.FriendsCanJoin);
        code.SetVisible(inMenu && showSetup);
        join.SetVisible(inMenu && showSetup);
        address.SetVisible(showSetup);
        lanHost.SetVisible(showSetup);
        create.SetVisible(showSetup);
        roomCode.SetVisible(room.HasLobby && room.Code.Length!=0);
        roomCode.SetString(room.Code);
        copy.SetVisible(room.HasLobby && room.IsHost && room.Code.Length!=0);
        copy.SetText(Loc(justCopied ? "code_copied" : "copy"));
        leave.SetVisible(room.HasLobby || room.Busy);
        leave.SetText(Loc(room.IsHost ? "close_room" : room.Busy ? "cancel_connection" : "leave"));
        approve.SetVisible(room.CanApprove); approve.SetText(Loc("approve")+" "+room.PartnerName);
        string detail=room.Detail.Length==0 ? "" : $" ({room.Detail})";
        status.SetText(steamJoinBusy ? Loc("status_steam_join_busy") :
            Loc(real && room.Status=="connected" ? "status_shop_connected" : "status_"+room.Status)+detail);
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
        lanHost?.SetInteractable(CanConnect && !string.IsNullOrWhiteSpace(address.GetString()));
    }

    private void Join()
    {
        if(!inMenu || !CanConnect || string.IsNullOrWhiteSpace(code.GetString())) return;
        room.Join(code.GetString());
        Refresh();
    }

    private void HostLan()
    {
        if(!CanConnect || string.IsNullOrWhiteSpace(address.GetString())) return;
        room.CreateLan(address.GetString());
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
                    (checkpoint.Historical ? " · "+Loc("history_checkpoint") : "");
                builder.AddButton(label, () =>
                    Resume(checkpoint));
                if (builder.Last.text != null) builder.Last.text.richText = false;
                builder.Last.SetInteractable(checkpoint.HasActors);
                builder.Last.SetTooltip(checkpoint.HasActors ? checkpoint.SavedAt.ToString("yyyy-MM-dd HH:mm") :
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

    public void Dispose()
    {
        if(window?.IsAlive==true) window.Close();
        if(launcher?.IsAlive==true) launcher.Close();
        if(checkpoints?.IsAlive==true) checkpoints.Close();
        if(menuButton!=null) UnityEngine.Object.Destroy(menuButton);
        CoopNewGameFlow.Cancel();
    }
}
