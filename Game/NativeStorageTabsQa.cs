using System;
using System.Collections;
using System.Linq;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string StorageTabsFixture="QA_STORAGE_TAB_RENAME_FIXTURE";
    private GameItem storageTabsQaBay;
    private int storageTabsQaStep;
    private double storageTabsQaDeadline;
    private static int storageTabsQaWrites;
    private static byte[] storageTabsQaFirstCommand;
    private bool StorageTabsQaHostDone { get; set; }
    private bool storageTabsQaHostUiStarted;
    private static bool storageTabsQaHostDraftTouched;

    internal static void RememberStorageTabCommand(JObject command)
    { storageTabsQaFirstCommand??=Encoding.UTF8.GetBytes(command.ToString(Formatting.None)); }

    private void SeedStorageTabsQa()
    {
        storageTabsQaBay=DirectoryMaster.Item("storage_bay");
        GeneralHelper.SetItemOwned(storageTabsQaBay,true);
        storageTabsQaBay.EnableTag(StorageTabsFixture);
        Require(TryPlaceInEmptyCell(storageTabsQaBay,EmporiumEntry.Instance.invElement),"storage tabs fixture does not fit");
        var kit=StorageExpansionHelper.CreateKit();
        StorageExpansionHelper.Expand(storageTabsQaBay,kit);
        Require(NativeStorageTabs.Tabs(storageTabsQaBay)?.count==3,"original storage expansion did not create three pages");
        storageTabsQaWrites=0;
    }

    private void CheckHostStorageTabsQa()
    {
        if(StorageTabsQaHostDone || storageTabsQaBay==null) return;
        // Previous custom-window suites own their own UI until complete.
        if(!LogisticsQaHostDone || !AppraisalQaHostDone) return;
        var ui=LabelerBlueUIManager.Instance;
        if(!storageTabsQaHostUiStarted)
        {
            ui.OpenUI("Host independent draft",_=>storageTabsQaHostDraftTouched=true,()=>storageTabsQaHostDraftTouched=true,64);
            storageTabsQaHostUiStarted=true;
        }
        Require(ui.panel.activeSelf && ui.inputField.text=="Host independent draft" && !storageTabsQaHostDraftTouched,
            "partner tab editor replaced or committed original host text input");
        Require(storageTabsQaWrites<=4,"tab rename original mutation repeated");
        if(storageTabsQaWrites!=4) return;
        Require(StorageExpansionHelper.GetTabName(storageTabsQaBay,1)==null,"native clear did not remove custom page name");
        StorageTabsQaHostDone=true;
        // Retire this QA-only draft before CloseUI's ordinary deselect callback.
        AccessTools.Field(typeof(LabelerBlueUIManager),"onTextDone").SetValue(ui,null);
        AccessTools.Field(typeof(LabelerBlueUIManager),"onTextClear").SetValue(ui,null);
        ui.CloseUI();
        Record("PASS host-native-storage-tabs","original SetTabName ran exactly four times; independent host editor unchanged; final page name cleared");
    }

    private bool RunGuestStorageTabsQa(ReplicaLink link,double now)
    {
        if(storageTabsQaDeadline==0) storageTabsQaDeadline=now+30;
        Require(now<storageTabsQaDeadline,"native storage-tab rename timed out at "+storageTabsQaStep);
        var bay=NativeWorld.Guest.Objects.Values.OfType<GameItem>().SingleOrDefault(item=>item.IsTag(StorageTabsFixture));
        if(bay==null) return false;
        var tabs=NativeStorageTabs.Tabs(bay); var ui=LabelerBlueUIManager.Instance;
        switch(storageTabsQaStep)
        {
            case 0:
                Require(tabs?.count==3 && tabs.onRenameRequest!=null,"projected storage tabs lost original rename capability");
                tabs.Select(1,notify:true);
                var list=(IList)AccessTools.Field(typeof(MachineryTabs),"tabs").GetValue(tabs);
                var page=list[1];
                var plate=(TreeNodeRender)AccessTools.Field(page.GetType(),"plate").GetValue(page);
                plate.OnPointerDoubleClick(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current));
                Require(ui.panel.activeSelf && ui.inputField.characterLimit==12,"original tab double-click did not open local native editor immediately");
                ui.inputField.SetTextWithoutNotify("原料"); ui.OnEditEnd("原料");
                ui.inputField.SetTextWithoutNotify("半成品"); ui.OnEditEnd("半成品");
                Require(QaWaiting(link),"native tab rename did not issue an actual network request");
                ui.CloseUI();
                storageTabsQaStep=1; return false;
            case 1:
                if(QaWaiting(link) || StorageExpansionHelper.GetTabName(bay,1)!="半成品") return false;
                Require(tabs.GetCaption(1)=="半成品" && !ui.panel.activeSelf,
                    "close dropped the last explicit tab edit or confirmation reopened the editor");
                var pages=(IList)AccessTools.Field(typeof(MachineryTabs),"tabs").GetValue(tabs);
                var selectedPage=pages[1];
                var selectedPlate=(TreeNodeRender)AccessTools.Field(selectedPage.GetType(),"plate").GetValue(selectedPage);
                selectedPlate.OnPointerDoubleClick(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current));
                Require(ui.panel.activeSelf && ui.inputField.text=="半成品","reopened native tab editor did not read the confirmed name");
                Require(storageTabsQaFirstCommand!=null,"missing actual first rename request");
                Require(link.Submit(storageTabsQaFirstCommand,now),"could not replay obsolete tab request with new request id");
                storageTabsQaStep=2; return false;
            case 2:
                if(QaWaiting(link)) return false;
                Require(link.Result==(ushort)ShopResult.Stale && StorageExpansionHelper.GetTabName(bay,1)=="半成品",
                    "obsolete storage page name overwrote the committed name");
                NativeOffscreenCapture.Capture("qa-native-storage-tabs");
                ui.inputField.SetTextWithoutNotify("终稿"); storageTabsQaStep=5; return false;
            case 5:
                if(!ui.inputField.isFocused) return false;
                // Do not manually invoke OnEditEnd: TMP's actual disable /
                // deselect lifecycle must commit the last native input.
                ui.CloseUI(); storageTabsQaStep=6; return false;
            case 6:
                if(QaWaiting(link) || StorageExpansionHelper.GetTabName(bay,1)!="终稿") return false;
                Require(!ui.panel.activeSelf,"native close confirmation reopened the editor");
                var reopenPages=(IList)AccessTools.Field(typeof(MachineryTabs),"tabs").GetValue(tabs);
                var reopenPage=reopenPages[1];
                ((TreeNodeRender)AccessTools.Field(reopenPage.GetType(),"plate").GetValue(reopenPage))
                    .OnPointerDoubleClick(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current));
                Require(ui.inputField.text=="终稿","native last end-edit was lost on close");
                ui.OnClearButtonClick(); storageTabsQaStep=3; return false;
            case 3:
                if(QaWaiting(link) || StorageExpansionHelper.GetTabName(bay,1)!=null) return false;
                Require(tabs.selected==1 && tabs.GetCaption(1)!="半成品","storage snapshot changed selected page or retained stale caption");
                ui.CloseUI();
                Record("PASS native-storage-tabs","original page double-click/native text editor; rapid dependent edits survive immediate close without reopening; old request rejected; original clear; local page selection retained");
                storageTabsQaStep=4; return true;
            default: return true;
        }
    }

    [HarmonyPatch(typeof(StorageExpansionHelper),nameof(StorageExpansionHelper.SetTabName))]
    private static class CountStorageTabWrites
    {
        private static void Postfix(GameItem bay,bool __runOriginal)
        { if(Enabled && IsHostProduct && __runOriginal && bay?.IsTag(StorageTabsFixture)==true) storageTabsQaWrites++; }
    }
}
