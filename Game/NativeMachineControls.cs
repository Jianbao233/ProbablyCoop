using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

internal static class NativeMachineControls
{
    private static readonly MethodInfo Press = AccessTools.Method(typeof(MachineryButton), "OnSelect");
    internal const int Limit = 256;

    internal static List<PixelElement> Read(GameItem owner)
    {
        var controls = new List<PixelElement>();
        if (owner.contentWindow?.childElement is PixelElement root)
            Collect(root, controls, 0);
        return controls;
    }

    private static void Collect(PixelElement node, List<PixelElement> controls, int depth)
    {
        if (node == null || depth > 24 || controls.Count >= Limit) return;
        switch (node)
        {
            case MachineryButton or MachineryRadioButton or MachinerySlider or MachineryRangeSlider:
                controls.Add(node);
                break;
            case MachineryPanelRow row:
                foreach (var panel in row.Panels) Collect(panel.content, controls, depth + 1);
                break;
            case MachineryPanel panel:
                Collect(panel.content, controls, depth + 1);
                break;
            case MachineryIntake intake:
                Collect(intake.content, controls, depth + 1);
                break;
            case MachineryStack stack:
                foreach (var element in stack.elements) Collect(element, controls, depth + 1);
                break;
            case MachineryTabs tabs:
                for (int i = 0; i < tabs.count; i++) Collect(tabs.GetPage(i), controls, depth + 1);
                break;
        }
    }

    internal static MachineControl Kind(PixelElement widget) => widget switch
    {
        MachineryButton => MachineControl.Button,
        MachineryRadioButton => MachineControl.Radio,
        MachinerySlider => MachineControl.Slider,
        MachineryRangeSlider => MachineControl.Range,
        _ => throw new ArgumentException("Unsupported native machine control.", nameof(widget)),
    };

    internal static bool OpensRecipePanel(PixelElement widget) =>
        NativeFrontendCapabilities.Kind(widget) == (int)NativeWorld.FrontendKind.ChemFinisher;

    internal static (float first, float second) Values(PixelElement widget) => widget switch
    {
        MachineryRadioButton radio => (radio.isOn ? 1 : 0, 0),
        MachinerySlider slider => (slider.value, 0),
        MachineryRangeSlider range => (range.low, range.high),
        _ => (0, 0),
    };

    internal static void Display(PixelElement widget, float first, float second)
    {
        switch (widget)
        {
            case MachineryRadioButton radio: radio.SetOn(first != 0); break;
            case MachinerySlider slider when !NativeWorld.DraggingContinuous(slider): slider.SetValue(first); break;
            case MachineryRangeSlider range when !NativeWorld.DraggingContinuous(range): range.SetValues(first, second); break;
        }
    }

    internal static bool Execute(PixelElement widget, MachineIntent command)
    {
        switch (widget)
        {
            case MachineryButton button when !button.isDisabled && !button.isHidden:
                Press.Invoke(button, null);
                return true;
            case MachineryRadioButton radio when !radio.isDisabled && !radio.isHidden &&
                (radio.group == null || command.First != 0):
                radio.SetOn(command.First != 0, notify: true);
                return true;
            case MachinerySlider slider when !slider.isDisabled &&
                command.First >= slider.min && command.First <= slider.max:
                slider.SetValue(command.First, notify: true);
                slider.onReleased?.Invoke(slider.value);
                return true;
            case MachineryRangeSlider range when !range.isDisabled &&
                command.First >= range.min && command.Second <= range.max:
                range.SetValues(command.First, command.Second, notify: true);
                range.onReleased?.Invoke(range.low, range.high);
                return true;
            default:
                return false;
        }
    }
}
