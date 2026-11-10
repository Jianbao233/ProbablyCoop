using System;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int controlStep;
    private double controlUntil,controlNext;
    private MachinerySlider qaSlider;
    private ActivityRequests.Entry rejectedSlider;
    private bool RunContinuousControlQa(ReplicaLink link,double now)
    {
        if(controlStep==7) return true;
        var world=NativeWorld.Guest; var machine=ToolItem("livestock_feed_dispenser");
        if(controlStep==0)
        {
            world.TryData(machine,out var itemData); uint owner=(uint)itemData["entity"];
            qaSlider=world.Objects.Values.OfType<MachinerySlider>().Single(s=>world.TryData(s,out var data) && (uint)data["owner"]==owner);
            ItemMouseDoubleClickHandler.current.OpenContentAction(machine,new Vector2(500,250));
            Require(machine.contentWindow.IsVisible(),"feed dispenser original window did not open");
            AccessTools.Field(typeof(MachinerySlider),"dragging").SetValue(qaSlider,true);
            qaSlider.SetValue(40,true); controlStep=1; controlUntil=now+15; controlNext=now+1;
            return false;
        }
        Require(now<controlUntil,"native slider timed out step="+controlStep+" result="+QaResult(link));
        if(now<controlNext) return false;
        if(controlStep==1)
        {
            Require(!QaWaiting(link) && qaSlider.value==40 && NativeWorld.DraggingContinuous(qaSlider),"replication interrupted local slider editing");
            Require(machine.GetTagReadonly("FEED_DISPENSER_TARGET_PERCENT_INT").GetInt()==85,"guest edited authoritative machine before commit");
            AccessTools.Method(typeof(MachinerySlider),"EndDrag").Invoke(qaSlider,null);
            controlStep=2; return false;
        }
        if(controlStep is 2 or 5)
        {
            if(!QaWaiting(link)) return false;
            if(controlStep==5) rejectedSlider=world.UiRequestState(world.LastSubmittedUiRequest);
            Require(qaSlider.value==(controlStep==2 ? 40 : 60),"released native slider snapped back before its own confirmation");
            controlStep++; return false;
        }
        if(QaWaiting(link))
        {
            float expected=controlStep==3 || rejectedSlider?.Result==(ushort)ShopResult.Stale ? 40 : 60;
            Require(qaSlider.value==expected,"snapshot erased a pending preview or a late reply affected the wrong control");
            return false;
        }
        if(controlStep==6)
        {
            Require(QaResult(link)==(ushort)ShopResult.Stale && qaSlider.value==40 &&
                machine.GetTagReadonly("FEED_DISPENSER_TARGET_PERCENT_INT").GetInt()==40 && machine.contentWindow.IsVisible(),
                "rejected slider preview did not restore its own confirmed model/window");
            Record("PASS native-slider-rejection","typed stale-proof fault through original slider callbacks; pending preview retained then only its own value restored, machine state unchanged");
            controlStep=7; return true;
        }
        if(controlStep==4)
        {
            world.TryData(qaSlider,out var data); var proof=data["controlToken"];
            try
            {
                // Deliberate protocol fault, not a claimed normal user action.
                // All visual editing still goes through the original callbacks.
                data["controlToken"]="QA_INVALID_CONTROL_PROOF";
                AccessTools.Field(typeof(MachinerySlider),"dragging").SetValue(qaSlider,true);
                qaSlider.SetValue(60,true);
                AccessTools.Method(typeof(MachinerySlider),"EndDrag").Invoke(qaSlider,null);
            }
            finally { data["controlToken"]=proof; }
            controlUntil=now+15; controlStep=5; return false;
        }
        Require(QaResult(link)==(ushort)ShopResult.Applied,"native slider command was rejected");
        if(machine.GetTagReadonly("FEED_DISPENSER_TARGET_PERCENT_INT").GetInt()!=40) return false;
        Require(qaSlider.value==40 && machine.contentWindow.IsVisible(),"host-confirmed machine control lost native local window/value");
        Record("PASS native-machine-slider","original slider drag/end callbacks; snapshots preserve draft and in-flight preview without snapback; host native tag 85->40; independent UI retained");
        controlStep=4; return false;
    }
}
