using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string ButcherTag="AUTO_BUTCHER_DROP_TO_BUTCHER_BOOL";
    private const string ButcherDescription="合作设置已更新";
    private GameItem butcher;
    private MachineryRadioButton butcherToggle;
    private MachineryButton butcherButton;
    private MachineryTabs butcherTabs;
    private int butcherLocalTab;
    private string originalButtonDescription;
    private bool originalButtonFill,guestButtonFill;
    private bool butcherInitial, hostButcherEdited;
    private int butcherStep;
    private double butcherUntil;
    private double butcherStableSince;

    private void SeedButcher()
    {
        Require(DirectoryMaster.Has<GameItem>("auto_butcher"),"missing native auto butcher");
        butcher=DirectoryMaster.Item("auto_butcher");
        Require(TryPlaceInEmptyCell(butcher,EmporiumEntry.Instance.invElement),"no native space for auto butcher");
        butcherInitial=butcher.GetTagReadonly(ButcherTag).GetBool();
        Record("FIXTURE auto-butcher","registered native machine with independent toggle callbacks");
    }

    private void CheckHostButcher()
    {
        if(butcher==null) return;
        bool current=butcher.GetTagReadonly(ButcherTag).GetBool();
        if(current==butcherInitial && !hostButcherEdited) return;
        if(current!=butcherInitial && hostButcherEdited) return;
        // The inventory graph only enumerates storage. The world registry
        // also contains the original visual controls from the machine panels.
        var button=NativeWorld.Host.Objects.Values.OfType<MachineryButton>()
            .Single(b=>b.text==LocHelper.GetLocalizedMechanic("mech_auto_butcher_btn_butcher_now"));
        if(!hostButcherEdited)
        {
            originalButtonDescription=button.description;
            originalButtonFill=button.fillWidth;
            button.SetDescription(ButcherDescription);
            button.SetFillWidth(!originalButtonFill);
            hostButcherEdited=true;
        }
        else
        {
            button.SetDescription(originalButtonDescription);
            button.SetFillWidth(originalButtonFill);
            hostButcherEdited=false;
        }
    }

    private bool RunGuestButcher(ReplicaLink link,double now)
    {
        if(butcherStep==3) return true;
        if(butcherStep==0)
        {
            butcher=NativeWorld.Guest.Objects.Values.OfType<GameItem>().Single(i=>i.identifier=="auto_butcher");
            butcherToggle=FindButcherToggle();
            butcherButton=FindButcherButton();
            var world=NativeWorld.Guest;
            world.TryData(butcher,out var butcherData);
            butcherTabs=world.Objects.Values.OfType<MachineryTabs>().Single(tabs=>world.TryData(tabs,out var data) &&
                (uint)data["owner"]==(uint)butcherData["entity"]);
            butcherLocalTab=butcherTabs.count-1;
            butcherTabs.Select(butcherLocalTab,false);
            guestButtonFill=butcherButton.fillWidth;
            butcherInitial=butcher.GetTagReadonly(ButcherTag).GetBool();
            ItemMouseDoubleClickHandler.current.OpenContentAction(butcher,new Vector2(320,250));
            Require(butcher.contentWindow.IsVisible() && butcherToggle.isOn==butcherInitial,"wrong original auto butcher UI state");
            ClickButcherToggle(link);
            butcherUntil=now+20; butcherStep=1; return false;
        }
        Require(now<butcherUntil,"native auto butcher toggle response timed out");
        if(QaWaiting(link)) return false;
        Require(QaResult(link)==(ushort)ShopResult.Applied,"native auto butcher action rejected: "+QaResult(link));
        bool expected=butcherStep==1 ? !butcherInitial : butcherInitial;
        if(butcher.GetTagReadonly(ButcherTag).GetBool()!=expected || butcherToggle.isOn!=expected ||
            (butcherButton.description==ButcherDescription)!=(butcherStep==1)) return false;
        Require(butcherButton.fillWidth==(butcherStep==1 ? !guestButtonFill : guestButtonFill) &&
            ReferenceEquals(butcherToggle,FindButcherToggle()) && ReferenceEquals(butcherButton,FindButcherButton()) &&
            ReferenceEquals(butcherTabs,NativeWorld.Guest.PointerElement(NativeWorld.Guest.PointerIdentity(butcherTabs))) &&
            butcherTabs.selected==butcherLocalTab &&
            butcher.contentWindow.IsVisible(),"auto butcher controls or open window were replaced");
        if(butcherStep==1)
        {
            if(butcherStableSince==0) butcherStableSince=now;
            if(now-butcherStableSince<4) return false;
            NativeOffscreenCapture.Capture("qa-auto-butcher");
            ClickButcherToggle(link); butcherStep=2; return false;
        }
        butcher.contentWindow.Hide(); butcherStep=3;
        Record("PASS native-auto-butcher-toggle","original machine OnSelect updated host equipment tag and restored it; tabs/local page, independent toggle and changing button retained identity");
        return true;
    }

    private static MachineryRadioButton FindButcherToggle() => NativeWorld.Guest.Objects.Values.OfType<MachineryRadioButton>()
        .Single(b=>b.text==LocHelper.GetLocalizedMechanic("mech_auto_butcher_toggle_drop"));
    private static MachineryButton FindButcherButton() => NativeWorld.Guest.Objects.Values.OfType<MachineryButton>()
        .Single(b=>b.text==LocHelper.GetLocalizedMechanic("mech_auto_butcher_btn_butcher_now"));
    private void ClickButcherToggle(ReplicaLink link)
    {
        bool before=butcherToggle.isOn;
        AccessTools.Method(typeof(MachineryRadioButton),"OnSelect").Invoke(butcherToggle,null);
        Require(QaWaiting(link) && butcherToggle.isOn!=before && butcher.GetTagReadonly(ButcherTag).GetBool()==before,
            "standalone switch failed to preview while awaiting host confirmation");
    }
}
