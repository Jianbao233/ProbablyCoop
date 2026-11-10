using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int independentUiStep,independentUiMode;
    private double independentUiUntil;
    private bool independentUiToggle;
    private ActivityRequests.Entry independentMode,independentToggle,independentPhone;
    private bool RunIndependentUiQa(ReplicaLink link,double now)
    {
        if(independentUiStep==3) return true;
        if(independentUiUntil==0) independentUiUntil=now+40;
        Require(now<independentUiUntil,"independent native activities timed out at "+independentUiStep);
        var world=NativeWorld.Guest;
        var machine=MachineReplica();
        var toggle=FindButcherToggle();
        var target=world.Objects.Values.OfType<GameItem>().Single(item=>item.identifier=="auto_butcher");
        if(independentUiStep==0)
        {
            if(world.HasPendingWork) return false;
            independentUiMode=Modes(machine).selected; independentUiToggle=toggle.isOn;
            Modes(machine).Select(independentUiMode==1 ? 2 : 1,true);
            independentMode=world.UiRequestState(world.LastSubmittedUiRequest);
            AccessTools.Method(typeof(MachineryRadioButton),"OnSelect").Invoke(toggle,null);
            independentToggle=world.UiRequestState(world.LastSubmittedUiRequest);
            // Fault injection is distinct from the two original input paths.
            // A failing phone request must not consume either machine result.
            Require(world.RequestPhone("quick",0),"independent phone fault injection was blocked by a machine request");
            independentPhone=world.UiRequestState(world.LastSubmittedUiRequest);
            ulong beforeTrip=world.LastSubmittedUiRequest;
            world.ProposeTrip(AfterhourLocation.DUMPING_GROUND);
            Require(world.LastSubmittedUiRequest==beforeTrip,"global travel bypassed pending native activities");
            world.RequestShutter(false);
            Require(world.LastSubmittedUiRequest==beforeTrip,"global shutter phase change bypassed pending native activities");
            Require(independentMode!=null && independentToggle!=null && independentPhone!=null &&
                independentMode.Serial<independentToggle.Serial && independentToggle.Serial<independentPhone.Serial &&
                link.IndependentInFlight>=3 && !independentMode.Result.HasValue && !independentToggle.Result.HasValue &&
                machine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()==independentUiMode &&
                target.GetTagReadonly(ButcherTag).GetBool()==independentUiToggle,
                "different native activities did not dispatch immediately with separate ids/local-only previews");
            independentUiStep=1; return false;
        }
        if(world.HasPendingWork) return false;
        if(independentUiStep==1)
        {
            Require(independentMode.Result==(ushort)ShopResult.Applied && independentToggle.Result==(ushort)ShopResult.Applied &&
                independentPhone.Result==(ushort)ShopResult.Busy && !world.SharedPhoneBusy &&
                machine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()!=independentUiMode &&
                target.GetTagReadonly(ButcherTag).GetBool()!=independentUiToggle,
                "independent outcomes or host machine changes were attributed to the wrong activity");
            Modes(machine).Select(independentUiMode,true);
            independentMode=world.UiRequestState(world.LastSubmittedUiRequest);
            AccessTools.Method(typeof(MachineryRadioButton),"OnSelect").Invoke(toggle,null);
            independentToggle=world.UiRequestState(world.LastSubmittedUiRequest);
            Require(independentMode.Serial<independentToggle.Serial && link.IndependentInFlight>=2,
                "independent restoration was serialized behind the first machine");
            independentUiStep=2; return false;
        }
        Require(independentMode.Result==(ushort)ShopResult.Applied && independentToggle.Result==(ushort)ShopResult.Applied &&
            machine.GetTagReadonly("HEATING_PLATE_STATE").GetInt()==independentUiMode &&
            target.GetTagReadonly(ButcherTag).GetBool()==independentUiToggle,
            "independent native machine settings did not restore");
        Record("PASS native-independent-ui","two original machine inputs dispatched before any ACK; third phone fault rejected only itself; distinct per-request results and original machine settings restored");
        independentUiStep=3; return true;
    }
}
