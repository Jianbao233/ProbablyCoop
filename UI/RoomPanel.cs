using System;
using System.Text;
using ProbablyCoop.Net;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace ProbablyCoop;

// Room management on the rebuilt stack: create, join, approve, leave, and why a
// join was turned away.
internal sealed class RoomPanel : IDisposable
{
    private const int ShownDifferences = 6;
    private readonly NetRoom room;
    private readonly bool localTest;
    private readonly bool? localShopHost;
    private GameObject menuButton;
    private TextMeshProUGUI menuLabel, originalMenuLabel;
    private readonly RoomPanelEntries entries = new();
    private readonly Ipv4RoomControls ipv4 = new();
    private CustomUIWindow window;
    private CustomUIElement heading, status, steamHint, code, join, create, returnToMenu, roomCode, copy, leave, approve;
    private CustomUIElement joinHeading, hostHeading, joinFromMenu;
    private bool inMenu = true;
    private bool createWhenInMenu;
    private double clock, nextRefresh, copiedUntil, steamJoinBusyUntil;
    private string displaySignature;

    internal RoomPanel(NetRoom room, bool localTest, bool? localShopHost)
    {
        this.room = room;
        this.localTest = localTest;
        this.localShopHost = localShopHost;
    }

#if PROBABLY_COOP_GRID_QA
    internal bool HasNativeMenuEntry => menuButton != null && menuButton.activeInHierarchy;
    internal void QaVerifyShopEntries() => entries.QaVerify(Open);

    internal bool QaOpenMenuAndJoin(string codeToJoin)
    {
        if (!QaOpenMenu()) return false;
        code.input.text = codeToJoin;
        return QaClick(join) && (room.HasLobby || room.Busy);
    }

    internal bool QaHostFromMenu() => QaOpenMenu() && QaClick(create) && room.IsHost;

    internal bool QaConfirmHostFromStore()
    {
        Open();
        var confirm = StoreUIManager.Instance?.confirmPanelUIManager;
        if (confirm == null || !QaClick(create) || room.HasLobby || !confirm.panel.activeSelf) return false;
        confirm.OnNoClick();
        if (window?.IsVisible != true || !QaClick(create) || !confirm.panel.activeSelf || room.HasLobby) return false;
        confirm.OnYesClick();
        return true;
    }

    private bool QaOpenMenu()
    {
        if (!HasNativeMenuEntry) return false;
        menuButton.GetComponent<Button>().onClick.Invoke();
        return window?.IsVisible == true;
    }

    private static bool QaClick(CustomUIElement control)
    {
        if (control?.button == null || !control.button.isActiveAndEnabled || !control.button.IsInteractable()) return false;
        control.button.onClick.Invoke();
        return true;
    }
#endif

    private static string Loc(string key, params object[] values) => ModHelper.GetLocalized("probably_coop", key, values);

    internal void SetContext(bool menu)
    {
        if (menu == inMenu) return;
        inMenu = menu;
        CloseWindows();
        copiedUntil = 0;
        steamJoinBusyUntil = 0;
    }

    internal void Tick(double now)
    {
        clock = now;
        if (CustomUIManager.Instance == null) return;
        if (inMenu && menuButton == null) CreateMenuButton();
        if (menuButton != null)
        {
            string title = Loc("title");
            if (menuLabel.text != title) menuLabel.text = title;
            if (originalMenuLabel != null && menuLabel.font != originalMenuLabel.font) menuLabel.font = originalMenuLabel.font;
            if (inMenu && createWhenInMenu)
            {
                createWhenInMenu = false;
                if (CanConnect) room.Create(now);
                Open();
            }
        }
        entries.Tick(inMenu, menuButton != null, window?.IsVisible == true, Open);
        if (Input.GetKeyDown(KeyCode.F7) && !Focused(code) && !ipv4.InputFocused && !Focused(roomCode))
        {
            if (window?.IsVisible == true) window.Hide();
            else Open();
        }
        if (window?.IsVisible == true && now >= nextRefresh)
        {
            nextRefresh = now + 0.15;
            Refresh();
        }
    }

    internal void ShowSteamJoin(bool roomBusy)
    {
        steamJoinBusyUntil = roomBusy ? clock + 5 : 0;
        Open();
        window.BringToFront();
    }

    private static bool Focused(CustomUIElement element) => element?.input != null && element.input.isFocused;

    private void CreateMenuButton()
    {
        var menu = MainMenuUIController.Instance;
        var original = menu?.moddingButton;
        if (original == null || original.transform.parent == null ||
            original.transform.parent.GetComponent<LayoutGroup>() == null || !original.TryGetComponent<Button>(out _)) return;
        originalMenuLabel = original.GetComponentInChildren<TextMeshProUGUI>(true);
        if (originalMenuLabel == null) return;

        int position = original.transform.GetSiblingIndex();
        foreach (Transform sibling in original.transform.parent)
        {
            if (!sibling.TryGetComponent<Button>(out var candidate)) continue;
            for (int index = 0; index < candidate.onClick.GetPersistentEventCount(); index++)
                if (candidate.onClick.GetPersistentTarget(index) == menu &&
                    candidate.onClick.GetPersistentMethodName(index) == nameof(MainMenuUIController.OnLoadGameClick))
                    position = sibling.GetSiblingIndex() + 1;
        }
        menuButton = UnityEngine.Object.Instantiate(original, original.transform.parent, false);
        menuButton.name = "ProbablyCoopMenuButton";
        menuButton.transform.SetSiblingIndex(position);
        var button = menuButton.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => { menu.OnAnyButtonClick(); Open(); });
        foreach (var localization in menuButton.GetComponentsInChildren<LocalizeStringEvent>(true)) localization.enabled = false;
        menuLabel = menuButton.GetComponentInChildren<TextMeshProUGUI>(true);
        menuButton.SetActive(true);
    }

    private void Open()
    {
        if (window?.IsAlive == true) { window.Show(); window.BringToFront(); displaySignature = null; Refresh(); return; }
        var builder = CustomUIManager.Window("probably_coop_panel", Loc("title"), inMenu ? "menu" : "overlay")
            .SetSize(660, 400).Center().SetDraggable(true).SetCloseOnEscape();
        builder.AddLabel(Loc("room_header")); heading = builder.Last;
        builder.AddLabel(""); status = builder.Last;
        // Mod ids and versions shown here come from the other machine.
        if (status.text != null) status.text.richText = false;
        builder.AddLabel(Loc("steam_friend_hint")); steamHint = builder.Last;

        builder.AddHeader(Loc("join_room_heading")); joinHeading = builder.Last;
        builder.BeginRow(6);
        builder.AddInput(Loc("join_code_hint"), onTyping: _ => RefreshAvailability()); code = builder.Last;
        code.input.characterLimit = 64;
        code.input.onSubmit.AddListener(_ => Join());
        builder.AddButton(Loc("join"), Join); join = builder.Last;
        builder.End();

        ipv4.AddMapping(builder, Join);
        builder.AddHeader(Loc("host_room_heading")); hostHeading = builder.Last;
        builder.AddButton(Loc(localTest ? "local_create" : "create"), CreateRoom); create = builder.Last;
        ipv4.AddHost(builder, HostLan);
        builder.AddLabel(Loc("join_from_menu")); joinFromMenu = builder.Last;
        if (localShopHost == false)
        {
            builder.AddButton(Loc("local_return_to_menu"), () => ConfirmReturnToMenu(false));
            returnToMenu = builder.Last;
        }

        builder.BeginRow(6);
        builder.AddInput(Loc("room_code")); roomCode = builder.Last;
        roomCode.input.readOnly = true;
        builder.AddButton(Loc("copy"), CopyRoomCode); copy = builder.Last;
        builder.AddButton(Loc("leave"), LeaveRoom); leave = builder.Last;
        builder.End();
        builder.AddButton(Loc("approve"), room.Approve); approve = builder.Last;
        if (approve.text != null) approve.text.richText = false;
        entries.AddLauncherOption(builder);
        window = builder.Show();
        window.BringToFront();
        displaySignature = null;
        Refresh();
    }

    private void Refresh()
    {
        bool justCopied = clock < copiedUntil;
        bool steamJoinBusy = clock < steamJoinBusyUntil;
        bool shopReady = localShopHost.HasValue && NativeInventoryPilot.Interactive;
        bool showSetup = !room.HasLobby && !room.Busy;
        bool showJoin = inMenu && showSetup && localShopHost != true;
        bool showMapping = showJoin && !localShopHost.HasValue && Ipv4RoomControls.IsIpv4Code(code.GetString());
        var endpoint = room.Ipv4HostEndpoint;
        string signature = $"{room.Status}/{room.Detail}/{room.Code}/{room.Busy}/{room.HasLobby}/{room.CanApprove}/{room.PartnerName}/" +
            $"{room.IsReady}/{room.Reason}/{room.DifferenceCount}/{room.FriendsCanJoin}/{justCopied}/{steamJoinBusy}/{shopReady}/{showMapping}";
        if (signature == displaySignature) return;
        displaySignature = signature;
        string outcome = Outcome(out int lines);
        window.SetSize(740, (showSetup ? (inMenu ? 440 : 395) + (showMapping ? 40 : 0) :
            room.FriendsCanJoin || endpoint != null ? 324 : 290) + 40 + 26 * lines);
        steamHint.SetVisible(room.FriendsCanJoin);
        joinHeading.SetVisible(showJoin);
        code.SetVisible(showJoin);
        join.SetVisible(showJoin);
        hostHeading.SetVisible(showSetup && localShopHost != false);
        joinFromMenu.SetVisible(showSetup && !inMenu && !localTest);
        ipv4.Refresh(showSetup && !localShopHost.HasValue, showMapping, endpoint);
        create.SetVisible(showSetup && localShopHost != false);
        create.SetText(Loc(localShopHost == true && !inMenu ? "local_return_and_create" : localTest ? "local_create" : "create"));
        returnToMenu?.SetVisible(showSetup && !inMenu);
        roomCode.SetVisible(room.HasLobby && room.Code.Length != 0);
        roomCode.SetString(room.Code);
        copy.SetVisible(room.HasLobby && room.IsHost && room.Code.Length != 0);
        copy.SetText(Loc(justCopied ? "code_copied" : "copy"));
        leave.SetVisible(room.HasLobby || room.Busy);
        leave.SetText(Loc(room.IsHost ? "close_room" : room.Busy ? "cancel_connection" : "leave"));
        approve.SetVisible(room.CanApprove);
        approve.SetText(Loc("approve") + " " + room.PartnerName);
        string detail = room.Detail.Length == 0 ? "" : $" ({room.Detail})";
        string roomStatus = room.Status;
        if (localShopHost.HasValue)
        {
            if (room.IsReady) roomStatus = shopReady ? "shop_connected" : "local_loading_shop";
            else if (!inMenu && showSetup) roomStatus = "local_start_from_menu";
            else if (localShopHost == false && roomStatus == "local_idle") roomStatus = "local_join_idle";
        }
        status.SetText(steamJoinBusy ? Loc("status_steam_join_busy") : Loc("status_" + roomStatus) + detail + outcome);
        RefreshAvailability();
    }

    // What the last negotiation concluded, for either side.
    private string Outcome(out int lines)
    {
        lines = 0;
        var text = new StringBuilder();
        if (room.Reason != SessionDecline.None)
        {
            text.Append('\n').Append(Loc(room.Reason == SessionDecline.Game ? "decline_game" :
                room.Reason == SessionDecline.Mods ? "decline_mods" : "decline_protocol"));
            lines++;
        }
        else if (room.DifferenceCount != 0 && room.IsReady)
        {
            text.Append('\n').Append(Loc("mods_differ"));
            lines++;
        }
        else return "";
        var differences = room.Differences;
        int shown = Math.Min(differences.Count, ShownDifferences);
        for (int index = 0; index < shown; index++)
        {
            var difference = differences[index];
            text.Append('\n').Append(difference.Kind switch
            {
                ModDiffKind.HostOnly => Loc("diff_host_only", difference.Id, difference.Host),
                ModDiffKind.GuestOnly => Loc("diff_guest_only", difference.Id, difference.Guest),
                ModDiffKind.Adapter => Loc("diff_adapter", difference.Id, difference.Host, difference.Guest),
                _ => Loc("diff_version", difference.Id, difference.Host, difference.Guest),
            });
            lines++;
        }
        int hidden = Math.Max(room.DifferenceCount, differences.Count) - shown;
        if (hidden > 0)
        {
            text.Append('\n').Append(Loc("diff_more", hidden));
            lines++;
        }
        return text.ToString();
    }

    private bool CanConnect => !room.HasLobby && !room.Busy;

    private void RefreshAvailability()
    {
        join?.SetInteractable(inMenu && localShopHost != true && CanConnect && !string.IsNullOrWhiteSpace(code.GetString()));
    }

    private void Join()
    {
        if (!inMenu || localShopHost == true || !CanConnect || string.IsNullOrWhiteSpace(code.GetString())) return;
        room.Join(clock, code.GetString(), loopbackOnly: localShopHost.HasValue, mappedAddress: ipv4.Mapping);
        Refresh();
    }

    private void CreateRoom()
    {
        if (localShopHost == false || !CanConnect) return;
        if (localShopHost == true && !inMenu)
        {
            ConfirmReturnToMenu(true);
            return;
        }
        room.Create(clock);
        Refresh();
    }

    private void ConfirmReturnToMenu(bool createRoom)
    {
        var store = StoreUIManager.Instance;
        if (inMenu || !CanConnect || store?.confirmPanelUIManager == null || store.escapeUIManager == null) return;
        window.Hide();
        string prompt = Loc(createRoom ? "confirm_local_return_and_create" : "confirm_local_return");
        store.confirmPanelUIManager.OpenPanel(prompt, () =>
        {
            createWhenInMenu = createRoom;
            store.escapeUIManager.OnMainMenu();
        }, () => { if (window?.IsAlive == true) window.Show(); });
    }

    private void HostLan()
    {
        if (localShopHost.HasValue || !CanConnect || string.IsNullOrWhiteSpace(ipv4.Address)) return;
        room.CreateLan(clock, ipv4.Address, ipv4.Port);
        Refresh();
    }

    private void CopyRoomCode()
    {
        if (!room.IsHost || room.Code.Length == 0) return;
        GUIUtility.systemCopyBuffer = room.Code;
        copiedUntil = clock + 1.5;
        Refresh();
    }

    private void LeaveRoom()
    {
        var confirm = inMenu ? MainMenuUIController.Instance?.confirmPanelUIManager : StoreUIManager.Instance?.confirmPanelUIManager;
        if (!room.IsReady || confirm == null)
        {
            room.Leave();
            copiedUntil = 0;
            Refresh();
            return;
        }
        string asked = room.Code;
        window.Hide();
        confirm.OpenPanel(Loc(room.IsHost ? "confirm_close_room" : "confirm_leave_room"), () =>
        {
            if (!room.HasLobby || room.Code != asked) return;
            room.Leave();
            copiedUntil = 0;
        }, () => { if (window?.IsAlive == true) window.Show(); });
    }

    private void CloseWindows()
    {
        if (window?.IsAlive == true) window.Close();
        entries.Dispose();
        if (menuButton != null) UnityEngine.Object.Destroy(menuButton);
        menuButton = null;
        displaySignature = null;
    }

    public void Dispose()
    {
        CloseWindows();
    }
}
