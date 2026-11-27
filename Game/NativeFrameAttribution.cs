#if PROBABLY_COOP_GRID_QA
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace ProbablyCoop;

// Both boundaries are sampled at the start of CoopRuntime.Update. Drag input
// can run on either side of that Update, so use the stopwatch, not frameCount
// as a key for accumulating work. Worker overlap is wall time, not main-thread work.
internal static class NativeFrameAttribution
{
    internal static readonly bool Enabled = NativePerformance.Profiling &&
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-perf-frame-trace") >= 0;
    private const int MinimumFrameMilliseconds = 50;
    private const int MaximumReports = 512;
    private static readonly object PackLock = new object();
    private static readonly long[] MainTicks = new long[(int)Stage.Publish + 1];
    private static long previousBoundary, packStart, packTicks;
    private static long mainWorkStart, mainCoveredTicks;
    private static int packs, previousCollections, reports, activeStages;
    private static bool tracingHost, previousPeerConnected;

    internal enum Stage { Runtime, LocalDrag, LocalDrop, RemoteDrag, Capture, Inventory, StockAudit, Publish }

    internal static Scope Measure(Stage stage)
    {
        int bit = 1 << (int)stage;
        if (!tracingHost || (activeStages & bit) != 0) return default;
        long started = Stopwatch.GetTimestamp();
        if (activeStages == 0) mainWorkStart = started;
        activeStages |= bit;
        return new Scope(stage, started);
    }

    internal static void Boundary(bool hostActive, bool peerConnected)
    {
        if (!Enabled) return;
        long end, overlap;
        lock (PackLock)
        {
            end = Stopwatch.GetTimestamp();
            overlap = packTicks;
            if (packs != 0)
            {
                overlap += end - packStart;
                packStart = end;
            }
            packTicks = 0;
        }
        int collections = GC.CollectionCount(0);
        double elapsed = Milliseconds(end - previousBoundary);
        if (tracingHost && hostActive && elapsed >= MinimumFrameMilliseconds && reports < MaximumReports)
        {
            reports++;
            double covered = Milliseconds(mainCoveredTicks);
            double outside = Math.Max(0, elapsed - covered);
            UnityEngine.Debug.Log(FormattableString.Invariant(
                $"[probably_coop PERF] slow_host_frame end_frame={UnityEngine.Time.frameCount} elapsed_ms={elapsed:F3} peer_before={(previousPeerConnected ? 1 : 0)} peer_after={(peerConnected ? 1 : 0)} runtime_ms={Spent(Stage.Runtime):F3} local_drag_ms={Spent(Stage.LocalDrag):F3} local_drop_ms={Spent(Stage.LocalDrop):F3} remote_drag_ms={Spent(Stage.RemoteDrag):F3} capture_ms={Spent(Stage.Capture):F3} inventory_ms={Spent(Stage.Inventory):F3} stock_audit_ms={Spent(Stage.StockAudit):F3} publish_ms={Spent(Stage.Publish):F3} profiled_main_ms={covered:F3} unattributed_ms={outside:F3} pack_overlap_ms={Milliseconds(overlap):F3} gc0_delta={collections - previousCollections}"));
            if (reports == MaximumReports)
                UnityEngine.Debug.Log($"[probably_coop PERF] host_frame_trace_limit end_frame={UnityEngine.Time.frameCount}");
        }
        if (tracingHost != hostActive)
            UnityEngine.Debug.Log($"[probably_coop PERF] host_frame_trace {(hostActive ? "on" : "off")} end_frame={UnityEngine.Time.frameCount} threshold_ms={MinimumFrameMilliseconds}");
        previousCollections = collections;
        previousBoundary = end;
        // Polling can reconnect the peer midway through this interval. Keep tracing
        // the existing host so the first checkpoint is included as well.
        previousPeerConnected = peerConnected;
        tracingHost = hostActive;
        mainCoveredTicks = 0;
        Array.Clear(MainTicks, 0, MainTicks.Length);
    }

    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    private static double Spent(Stage stage) => Milliseconds(MainTicks[(int)stage]);

    internal static PackScope MeasurePack()
    {
        if (!Enabled) return default;
        lock (PackLock)
        {
            if (packs++ == 0) packStart = Stopwatch.GetTimestamp();
        }
        return new PackScope(true);
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly Stage stage;
        private readonly long start;

        internal Scope(Stage stage, long start) { this.stage = stage; this.start = start; }

        public void Dispose()
        {
            if (start == 0) return;
            long end = Stopwatch.GetTimestamp();
            MainTicks[(int)stage] += end - start;
            activeStages &= ~(1 << (int)stage);
            // Stage totals are inclusive. The union of main-thread scopes
            // counts a drag inside Runtime only once.
            if (activeStages == 0) mainCoveredTicks += end - mainWorkStart;
        }
    }

    internal readonly struct PackScope : IDisposable
    {
        private readonly bool active;

        internal PackScope(bool active) => this.active = active;

        public void Dispose()
        {
            if (!active) return;
            lock (PackLock)
            {
                if (--packs == 0) packTicks += Stopwatch.GetTimestamp() - packStart;
            }
        }
    }
}

[HarmonyPatch]
internal static class NativeFrameDragAttribution
{
    private static bool Prepare() => NativeFrameAttribution.Enabled;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ItemMouseDragHandler), "OnEventPress");
        yield return AccessTools.Method(typeof(ItemMouseDragHandler), "OnEventMouseMove");
        yield return AccessTools.Method(typeof(ItemMouseDragHandler), "OnEventMouseScroll");
        yield return AccessTools.Method(typeof(ItemMouseDragHandler), "OnEventRelease");
        yield return AccessTools.Method(typeof(ItemMouseDragHandler), "OnEventUpdate");
    }

    [HarmonyPriority(Priority.First)]
    private static void Prefix(out NativeFrameAttribution.Scope __state) =>
        __state = NativeFrameAttribution.Measure(NativeFrameAttribution.Stage.LocalDrag);

    private static void Finalizer(NativeFrameAttribution.Scope __state) => __state.Dispose();
}

[HarmonyPatch(typeof(ItemMouseDragHandler), "EndDrag")]
internal static class NativeFrameDropAttribution
{
    private static bool Prepare() => NativeFrameAttribution.Enabled;

    [HarmonyPriority(Priority.First)]
    private static void Prefix(out NativeFrameAttribution.Scope __state) =>
        __state = NativeFrameAttribution.Measure(NativeFrameAttribution.Stage.LocalDrop);

    private static void Finalizer(NativeFrameAttribution.Scope __state) => __state.Dispose();
}
#endif
