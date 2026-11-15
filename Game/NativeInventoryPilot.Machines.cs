using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    [HarmonyPatch]
    private static class MachinePress
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MachineryButton), "OnSelect");
            yield return AccessTools.Method(typeof(MachineryRadioButton), "OnSelect");
        }

        private static bool Prefix(SelectableElement __instance, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary ||
                !pilot.world.TryFindControl(__instance, out var owner, out int index)) return true;
            if (!pilot.CanMove(owner) || __instance.isDisabled || __instance.isHidden)
            {
                __instance.Deselect();
                return false;
            }
            if (NativeMachineControls.OpensRecipePanel(__instance)) return true;
            if (pilot.localHost) return BeginOriginalAuthorityAction(out __state);
            if (__instance is not MachineryRadioButton { group: not null, isOn: true })
                pilot.campaign.RequestMachine(owner, index, __instance,
                    __instance is MachineryRadioButton radio && !radio.isOn ? 1 : 0);
            __instance.Deselect();
            return false;
        }

        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch]
    private static class MachinePointer
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MachinerySlider), "SetFromPointer");
            yield return AccessTools.Method(typeof(MachineryRangeSlider), "SetFromPointer");
        }

        private static bool Prefix(PixelElement __instance, ref bool notify)
        {
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary || !notify ||
                !pilot.world.TryFindControl(__instance, out var owner, out _)) return true;
            if (!pilot.CanMove(owner)) return false;
            if (!pilot.localHost) notify = false;
            return true;
        }
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.ToggleSlotItem))]
    private static class MachineToggle
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(GameItem __instance, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary) return true;
            if (!pilot.CanMove(__instance) || !__instance.CanToggleSlotItem()) return false;
            if (pilot.localHost) return BeginOriginalAuthorityAction(out __state);
            pilot.campaign.RequestMachineOption(__instance, MachineControl.Toggle);
            return false;
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch(typeof(ChemFinisherUIManager), nameof(ChemFinisherUIManager.OnChemRecipeSelected))]
    private static class RecipeSelection
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(ChemFinisherUIManager __instance, string identifier, out NativeInventoryPilot __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary) return true;
            if (!pilot.CanMove(__instance.currentItem)) return false;
            if (pilot.localHost) return BeginOriginalAuthorityAction(out __state);
            pilot.campaign.RequestMachineOption(__instance.currentItem, MachineControl.Recipe, identifier);
            return false;
        }
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state) =>
            CompleteAuthorityAction(__state, __exception);
    }

    [HarmonyPatch]
    private static class MachineRelease
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MachinerySlider), "EndDrag");
            yield return AccessTools.Method(typeof(MachineryRangeSlider), "EndDrag");
        }

        private static bool Prefix(PixelElement __instance, out (NativeInventoryPilot host, Action restore) __state)
        {
            __state = default;
            var pilot = Current;
            if (pilot?.campaign == null || pilot.RunningNativeBoundary ||
                !NativeWorld.DraggingContinuous(__instance) ||
                !pilot.world.TryFindControl(__instance, out var owner, out int index)) return true;
            if (pilot.localHost) return BeginOriginalAuthorityAction(out __state.host);
            var (first, second) = NativeMachineControls.Values(__instance);
            if (pilot.CanMove(owner)) pilot.campaign.RequestMachine(owner, index, __instance, first, second);
            Action restoreCallback = null;
            switch (__instance)
            {
                case MachinerySlider slider:
                    var sliderReleased = slider.onReleased;
                    slider.onReleased = null;
                    restoreCallback = () => slider.onReleased = sliderReleased;
                    break;
                case MachineryRangeSlider range:
                    var rangeReleased = range.onReleased;
                    range.onReleased = null;
                    restoreCallback = () => range.onReleased = rangeReleased;
                    break;
            }
            __state.restore = () => { restoreCallback?.Invoke(); pilot.world.RestoreControlView(owner); };
            return true;
        }

        private static Exception Finalizer(Exception __exception, (NativeInventoryPilot host, Action restore) __state)
        {
            __state.restore?.Invoke();
            return CompleteAuthorityAction(__state.host, __exception);
        }
    }

    [HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.OneSecPrefixFunction))]
    private static class MachineClock
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static void Prefix()
        {
            var pilot = Current;
            if (pilot?.campaign != null && pilot.localHost && pilot.Ready)
                pilot.machineClock = pilot.campaign.BeginAuthorityAction();
        }
    }

    private bool machineClock;

    [HarmonyPatch(typeof(EmporiumEntry), "Update")]
    private static class MachineClockFinish
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static Exception Finalizer(Exception __exception)
        {
            var pilot = Current;
            if (pilot?.machineClock == true)
            {
                pilot.machineClock = false;
                return CompleteAuthorityAction(pilot, __exception);
            }
            return __exception;
        }
    }
}
