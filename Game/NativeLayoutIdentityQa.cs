using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int mutableLayoutStep;
    private double mutableLayoutAt;
    private CustomUIWindow qaRoomPanel;
    private bool RunMutableLayoutsQa(double now)
    {
        if(mutableLayoutStep==2) return true;
        if(mutableLayoutStep==0)
        {
            NativeMirrorProbe.VerifyMutableLayoutIdentity();
            Record("PASS native-mutable-layouts","native tabs/grid/tag/indicator/push/advanced buttons retain render identities through changing values; local selection/hover, storage swaps and structural tab changes; no replica business callbacks");
            var runtime=UnityEngine.Object.FindObjectOfType<CoopRuntime>();
            var panel=(CoopPanel)AccessTools.Field(typeof(CoopRuntime),"panel").GetValue(runtime);
            AccessTools.Method(typeof(CoopPanel),"Open").Invoke(panel,null);
            qaRoomPanel=(CustomUIWindow)AccessTools.Field(typeof(CoopPanel),"window").GetValue(panel);
            Require(qaRoomPanel.IsVisible,"native room controls did not open");
            var controls=new[]{"create","join","copy","leave","lanHost","approve","newRun","resume","carry"}
                .Select(field=>((CustomUIElement)AccessTools.Field(typeof(CoopPanel),field).GetValue(panel)).button).ToArray();
            Require(qaRoomPanel.ContentRoot.GetComponentsInChildren<UnityEngine.UI.Button>(true).ToHashSet().SetEquals(controls),
                "room panel contains an input outside room/checkpoint management and the native carry shortcut");
            var carryControl=((CustomUIElement)AccessTools.Field(typeof(CoopPanel),"carry").GetValue(panel)).button;
            var originalCarryWindow=EmporiumEntry.Instance.afterhourWindow;
            bool carryWasOpen=NativeActorCarryUi.IsOpen;
            Require(carryControl.interactable && NativeActorCarryUi.Available,"native carry shortcut unavailable in the cooperative shop");
            carryControl.onClick.Invoke();
            Require(NativeActorCarryUi.IsOpen!=carryWasOpen && ReferenceEquals(EmporiumEntry.Instance.afterhourWindow,originalCarryWindow),
                "carry shortcut did not toggle the existing original window");
            carryControl.onClick.Invoke();
            Require(NativeActorCarryUi.IsOpen==carryWasOpen && ReferenceEquals(EmporiumEntry.Instance.afterhourWindow,originalCarryWindow),
                "carry shortcut could not restore original visibility without replacing the native window");
            Require(((CustomUIElement)AccessTools.Field(typeof(CoopPanel),"leave").GetValue(panel)).button!=null &&
                ((CustomUIElement)AccessTools.Field(typeof(CoopPanel),"copy").GetValue(panel)).button!=null,"room management controls missing");
            mutableLayoutAt=now+1; mutableLayoutStep=1; return false;
        }
        if(now<mutableLayoutAt) return false;
        if(SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)
            NativeOffscreenCapture.Capture("qa-native-room-controls");
        qaRoomPanel.Hide();
        Record("PASS native-room-controls","real room panel retains join/copy/leave; carry shortcut toggles the same original window twice; no legacy item, trade, ready, finish or test-grid inputs in native gameplay");
        mutableLayoutStep=2; return true;
    }
}
