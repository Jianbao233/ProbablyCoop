using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Attribute only host replication's tooltip work. Normal player hovers and
// menus keep their original behavior and are not mixed into these samples.
internal static class NativeTooltipTiming
{
    [ThreadStatic] private static int depth;
    [ThreadStatic] private static bool useFill;
    internal static bool HasBenchmarkFillPatch() => Fill.IsInstalled();
    internal static Capture Begin()
    {
        if(!NativePerformance.Detailed) return default;
        if(depth==0)
        {
            useFill=NativeCaptureComparison.Current?.UseFill==true;
            if(useFill) Fill.UpdateCompatibility();
        }
        depth++;
        return new Capture(true);
    }
    internal readonly struct Capture : IDisposable
    {
        private readonly bool entered;
        internal Capture(bool entered) { this.entered=entered; }
        public void Dispose() { if(entered) depth--; }
    }
    [HarmonyPatch(typeof(GameItemElement),"GetTooltipBasic")]
    private static class Basic
    {
        [HarmonyPrepare] private static bool Install() => NativePerformance.Detailed;
        private static void Prefix(out NativePerformance.Scope __state)=>__state=depth>0 ? NativePerformance.Measure("host.tooltip_basic") : default;
        private static void Postfix(NativePerformance.Scope __state)=>__state.Dispose();
    }
    [HarmonyPatch(typeof(RichTextBuilder),nameof(RichTextBuilder.GetFillString))]
    private static class Fill
    {
        private static readonly MethodInfo Target=AccessTools.Method(typeof(RichTextBuilder),nameof(RichTextBuilder.GetFillString));
        private static readonly TooltipFill Rows=new TooltipFill();
        private static int checkedFrame=-1,verified;
        private static bool original;
        // Keep the alternative available only to the isolated four-case
        // benchmark. Normal rooms retain the game's own text helper.
        [HarmonyPrepare] private static bool Install() => NativeQaDisplay.IsIsolatedQa &&
            NativeBackgroundQa.IsHostProduct &&
            Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-compare-capture")>=0;
        internal static bool IsInstalled() => Harmony.GetPatchInfo(Target)?.Prefixes.Any(p=>p.PatchMethod.DeclaringType==typeof(Fill))==true;
        internal static void UpdateCompatibility()
        {
            int frame=UnityEngine.Time.frameCount;
            if(frame!=checkedFrame)
            {
                checkedFrame=frame;
                var patches=Harmony.GetPatchInfo(Target);
                original=patches!=null && patches.Prefixes.Concat(patches.Postfixes)
                    .Concat(patches.Transpilers).Concat(patches.Finalizers).All(p=>p.PatchMethod.DeclaringType==typeof(Fill));
            }
        }
        private static bool Prefix(string text,char replace,ref string __result,out string __state)
        {
            __state=null;
            if(depth==0 || !useFill || !original || !Rows.TryGet(text,replace,out string value)) return true;
            if(NativeBackgroundQa.Enabled && verified<512) { __state=value; return true; }
            __result=value; return false;
        }
        private static void Postfix(string __state,string __result)
        {
            if(__state==null) return;
            if(!string.Equals(__state,__result,StringComparison.Ordinal)) throw new InvalidOperationException("Native tooltip fill mismatch");
            if(++verified==512) UnityEngine.Debug.Log("[probably_coop QA] PASS native-tooltip-fill 512 original rich text fills matched exactly");
        }
    }
    [HarmonyPatch(typeof(RichTextBuilder),nameof(RichTextBuilder.Build))]
    private static class Build
    {
        [HarmonyPrepare] private static bool Install() => NativePerformance.Detailed;
        private static void Prefix(out NativePerformance.Scope __state)=>__state=depth>0 ? NativePerformance.Measure("host.tooltip_build") : default;
        private static void Postfix(NativePerformance.Scope __state)=>__state.Dispose();
    }
}
