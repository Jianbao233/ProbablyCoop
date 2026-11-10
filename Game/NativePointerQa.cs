using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private bool guestPointerChecked,hostPointerChecked;
    private bool pointerProbePending;
    private ulong pointerProbeContext;
    private ushort pointerProbeX,pointerProbeY;
    private double pointerProbeDeadline,pointerProbeNext;
    private const ushort PointerFallbackX=20000,PointerFallbackY=35000;
    private const uint PointerFallback=PointerFallbackX|((uint)PointerFallbackY<<16);
    private const string PointerReceipt="PROBABLY_COOP_QA_POINTER_RECEIPT";

    private void CheckCheckpointPointer(CoopSession session,double now)
    {
        const ushort dumpingGround=2,viewport=32;
        var pointer=NativePointer.Current;
        if(host)
        {
            // Exercise the activity label shown for a remote sample from
            // another location, such as during a shared scene transition.
            if(now-profileReadyAt is >=2 and <=10)
                pointer.SendSample(session,now,true,false,new PointerContext(dumpingGround,viewport).Encode(),32000,32000);
        }
        else if(!profilePointerObserved && pointer.QaRenderedLocation(dumpingGround))
        {
            profilePointerObserved=true;
            Record("PASS checkpoint-pointer-glyphs","remote activity label rendered all localized location glyphs");
        }
    }

    private bool CheckGuestMachinePointer(CoopSession session)
    {
        if(guestPointerChecked) return true;
        if(pointerProbePending)
        {
            if(MachineReplica().IsTag(PointerReceipt))
            {
                guestPointerChecked=true;
                Record("PASS guest-native-pointer-context","actual native control raycast, entity and normalized point; independent window placement preserved; receipt witnessed by authority on real unreliable cursor channel");
                return true;
            }
            double current=Time.realtimeSinceStartupAsDouble;
            Require(current<pointerProbeDeadline,"authority did not witness native pointer probe within 8 seconds");
            // Cursor presence is deliberately unreliable. A single sample is
            // not an acceptance handshake, especially on an unfocused private
            // desktop. Retry this bounded QA probe until the host observes it.
            if(current>=pointerProbeNext)
            {
                NativePointer.Current.SendSample(session,current,true,true,pointerProbeContext,pointerProbeX,pointerProbeY,PointerFallback);
                pointerProbeNext=current+0.1;
            }
            return false;
        }
        // Private batch mode does not present frames. Render the original
        // canvas first, as for the other real native UI/geometry probes.
        NativeOffscreenCapture.Capture("qa-machine-pointer");
        var machine=MachineReplica();
        var control=Modes(machine).Get(2);
        var rect=control.rectTransform;
        var canvas=rect.GetComponentInParent<Canvas>()?.rootCanvas;
        var camera=canvas==null || canvas.renderMode==RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        // Native widget root transforms are intentionally zero-sized. Its
        // radio plate occupies 16x16 native pixels from the top-left origin.
        var point=new Vector2(8,-8);
        var center=RectTransformUtility.WorldToScreenPoint(camera,rect.TransformPoint(point));
        var pointer=NativePointer.Current;
        Require(pointer!=null,"native pointer absent");
        ushort surface=pointer.FindSurface(center,out var normalized,out uint entity);
        Require(surface==PointerContext.NativeElement && entity!=0,"native control hover fell back to viewport: center="+center+
            " rect="+rect.rect+" active="+rect.gameObject.activeInHierarchy+" hits="+pointer.QaRaycastDetails());
        var element=NativeWorld.Guest.PointerElement(entity);
        Require(element==control || element==Modes(machine) || element==machine.contentWindow ||
            element?.rectTransform.IsChildOf(machine.contentWindow.rectTransform)==true,
            "native control hover identified another window");
        var local=NativePointer.ResolveElement(entity);
        Require(local!=null,"visible native control could not resolve");
        var bounds=NativePointer.ElementBounds(element);
        var mapped=RectTransformUtility.WorldToScreenPoint(camera,local.TransformPoint(Rect.NormalizedToPoint(bounds,normalized)));
        Require(Vector2.Distance(center,mapped)<0.5f,"native control coordinates do not roundtrip");
        // Move only this disposable guest's window, with ordinary renderer
        // coordinates. The same logical point must follow it, not the viewport.
        var windowRect=machine.contentWindow.rectTransform; var before=windowRect.anchoredPosition;
        try
        {
            windowRect.anchoredPosition=before+new Vector2(37,19); Canvas.ForceUpdateCanvases();
            var moved=RectTransformUtility.WorldToScreenPoint(camera,local.TransformPoint(Rect.NormalizedToPoint(bounds,normalized)));
            var controlMoved=RectTransformUtility.WorldToScreenPoint(camera,rect.TransformPoint(point));
            Require(Vector2.Distance(moved,controlMoved)<0.5f && Vector2.Distance(mapped,moved)>10,"pointer did not follow independent window placement");
        }
        finally { windowRect.anchoredPosition=before; Canvas.ForceUpdateCanvases(); }
        var context=new PointerContext(NativePointer.CurrentLocation(),surface,entity);
        ushort x=(ushort)Mathf.RoundToInt(normalized.x*65535), y=(ushort)Mathf.RoundToInt(normalized.y*65535);
        double now=Time.realtimeSinceStartupAsDouble;
        Require(pointer.SendSample(session,now,true,true,context.Encode(),x,y,PointerFallback) &&
            !pointer.SendSample(session,now,true,false,context.Encode(),x,y,PointerFallback),"click or stationary cursor coalescing failed");
        pointerProbeContext=context.Encode(); pointerProbeX=x; pointerProbeY=y;
        pointerProbeDeadline=now+8; pointerProbeNext=now+0.1; pointerProbePending=true;
        return false;
    }
    private void CheckHostMachinePointer()
    {
        if(hostPointerChecked || NativePointer.Current==null) return;
        var context=NativePointer.Current.QaLastEntityContext;
        if(context.Entity==0 || context.Entity!=NativePointer.Current.QaLastClickedEntity) return;
        Require(context.Location==NativePointer.CurrentLocation(),"native pointer crossed a scene boundary");
        Require(NativeWorld.Host.PointerElement(context.Entity)!=null,"cursor entity not present on authority");
        Require(!qaMachine.contentWindow.handler.gameObject.activeInHierarchy,"remote cursor opened host machine window");
        Require(NativePointer.ResolveElement(context.Entity)==null,"hidden local control resolved to a misleading viewport position");
        Require(NativePointer.Current.QaHasViewportFallback(PointerFallbackX,PointerFallbackY),
            "global pointer fallback was lost when the local machine window was closed");
        hostPointerChecked=true;
        qaMachine.EnableTag(PointerReceipt);
        Record("PASS host-native-pointer-context","real guest cursor references the same native entity; closed local machine window stays closed; global pointer fallback and activity label available");
    }
}
