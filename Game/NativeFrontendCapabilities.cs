using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ProbablyCoop;

// Capability annotations follow reviewed native API factories, not item ids.
// Replacing a callback invalidates its annotation and falls back to authority
// discovery. Factories are never executed on a guest to discover capabilities.
[HarmonyPatch]
internal static class NativeFrontendCapabilities
{
    private sealed class Witness
    { internal NativeWorld.FrontendKind Kind; internal Delegate Callback; }
    private static readonly ConditionalWeakTable<object,Witness> Known=new ConditionalWeakTable<object,Witness>();
    private static readonly FieldInfo ButtonCallback=AccessTools.Field(typeof(MachineryButton),"callback");
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MachineChemFinisher),nameof(MachineChemFinisher.ChemFinisher));
        yield return AccessTools.Method(typeof(HydroponicDirectory),"PlantAnalyser");
        yield return AccessTools.Method(typeof(MiscItemDirectory),nameof(MiscItemDirectory.ExpeditionLog));
        yield return AccessTools.Method(typeof(MiscItemDirectory),nameof(MiscItemDirectory.ExchangeDirective));
        yield return AccessTools.Method(typeof(AmenitiesItemDirectory),"AquaScan");
        yield return AccessTools.Method(typeof(MedsItemDirectory),"ChemScanner");
        yield return AccessTools.Method(typeof(AmenitiesItemDirectory),"WaterTestStrip");
        yield return AccessTools.Method(typeof(MiscItemDirectory),"ScratcherTicket");
        yield return AccessTools.Method(typeof(HopperHelper),nameof(HopperHelper.CreateIntake));
        yield return AccessTools.Method(typeof(MachineStorageCoordinator),nameof(MachineStorageCoordinator.StorageCoordinator));
    }
    private static Delegate Current(object value,NativeWorld.FrontendKind kind) => value is MachineryButton button
        ? (Action)ButtonCallback.GetValue(button) : value is GameItem item ? kind switch {
            NativeWorld.FrontendKind.PlantAnalyser or NativeWorld.FrontendKind.WaterScanner or NativeWorld.FrontendKind.ChemScanner=>item.onItemTargetItemFunc,
            NativeWorld.FrontendKind.Log or NativeWorld.FrontendKind.Exchange or NativeWorld.FrontendKind.WaterStrip or NativeWorld.FrontendKind.Scratcher or NativeWorld.FrontendKind.Hopper or NativeWorld.FrontendKind.StorageCoordinator or NativeWorld.FrontendKind.MindReprogrammer or NativeWorld.FrontendKind.Appraisal=>item.onActivateSlotItemFunc,
            _=>null } : null;
    internal static int Kind(object value)=>Known.TryGetValue(value,out var witness) &&
        ReferenceEquals(witness.Callback,Current(value,witness.Kind)) ? (int)witness.Kind : 0;
    internal static void Bind(object value,NativeWorld.FrontendKind kind)
    {
        var callback=Current(value,kind);
        if(callback==null || callback.Method.Module.Assembly!=typeof(GameItem).Assembly) return;
        Known.Remove(value); Known.Add(value,new Witness { Kind=kind,Callback=callback });
    }
    private static void Postfix(MethodBase __originalMethod,GameItem __result)
    {
        if(__result==null) return;
        if(__originalMethod.DeclaringType==typeof(MachineChemFinisher))
        {
            var control=((MachineryStack)((MachineryPanelRow)__result.contentWindow.childElement).Panels.Single(panel=>panel.role=="control").content)
                .elements.OfType<MachineryButton>().First();
            Bind(control,NativeWorld.FrontendKind.ChemFinisher);
        }
        else Bind(__result,__originalMethod.DeclaringType==typeof(HopperHelper) ? NativeWorld.FrontendKind.Hopper :
            __originalMethod.DeclaringType==typeof(MachineStorageCoordinator) ? NativeWorld.FrontendKind.StorageCoordinator :
            __originalMethod.Name=="ScratcherTicket" ? NativeWorld.FrontendKind.Scratcher :
            __originalMethod.Name=="WaterTestStrip" ? NativeWorld.FrontendKind.WaterStrip :
            __originalMethod.DeclaringType==typeof(AmenitiesItemDirectory) ? NativeWorld.FrontendKind.WaterScanner :
            __originalMethod.DeclaringType==typeof(MedsItemDirectory) ? NativeWorld.FrontendKind.ChemScanner :
            __originalMethod.DeclaringType==typeof(HydroponicDirectory) ? NativeWorld.FrontendKind.PlantAnalyser :
            __originalMethod.Name==nameof(MiscItemDirectory.ExchangeDirective) ? NativeWorld.FrontendKind.Exchange : NativeWorld.FrontendKind.Log);
    }
}
