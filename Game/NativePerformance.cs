using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace ProbablyCoop;

// Opt-in, bounded timing evidence. Never logs player data or writes a save.
internal static class NativePerformance
{
    private static readonly bool Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-perf") >= 0;
    private static readonly bool Coarse = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-perf-coarse") >= 0;
    internal static bool Profiling=>Enabled;
    internal static bool Detailed=>Enabled && !Coarse;
    private sealed class Samples
    {
        internal readonly long[] Histogram = new long[4096];
        internal long Count;
        internal long Over33,Over50,Over100;
        internal readonly int TargetMs;
        internal long AboveTarget;
        internal Samples(string name)
        { TargetMs=name=="host.capture" ? 4 : name=="guest.command_to_projection" ? 100 : 0; }
        internal double Total, Maximum;
        internal double Next;
    }
    private static readonly Dictionary<string, Samples> Timings = new Dictionary<string, Samples>();
    private static long previousFrame;
    [Flags] internal enum FrameWork { None=0, Capture=1, Apply=2, QaRender=4, Tooltip=8 }
    private static FrameWork lastWork;
    private static bool previouslyConnected;
    private static bool Keep(string name) => Enabled && (!Coarse || name is
        "host.capture" or "host.pack_worker" or "guest.apply" or "guest.unpack_worker" or
        "guest.command_to_projection" or "guest.command_reply_applied" or "runtime.update" or "qa.render");
    internal static Scope Measure(string name) => new Scope(Keep(name) ? name : null);
    internal static Scope Measure(string name,FrameWork work)
    {
        if(Enabled) lastWork|=work;
        return Measure(name);
    }
    // Worker timings are reported by the owning main-thread consumer only.
    internal static void Sample(string name, double milliseconds) { if(Keep(name)) Record(name,milliseconds); }
    internal static void SlowProjection(string kind,ulong serial,ulong start,ulong finish,double reply,double projection)
    {
        if(Enabled) UnityEngine.Debug.Log(FormattableString.Invariant(
            $"[probably_coop PERF] slow_projection kind={kind} serial={serial} from={start} through={finish} reply_ms={reply:F3} applied_ms={projection:F3}"));
    }
    internal static void Frame(bool connected)
    {
        if (!Enabled) return;
        long now = Stopwatch.GetTimestamp();
        if(previousFrame!=0)
        {
            double interval=(now-previousFrame)*1000d/Stopwatch.Frequency;
            Record("frame.interval",interval,true);
            if(connected && previouslyConnected)
            {
                NativeCaptureComparison.Current?.Frame(interval,lastWork);
                string category=(lastWork&FrameWork.QaRender)!=0 ? "frame.qa_render" :
                    (lastWork&FrameWork.Capture)!=0 ? "frame.with_capture" :
                    (lastWork&FrameWork.Apply)!=0 ? "frame.with_apply" :
                    (lastWork&FrameWork.Tooltip)!=0 ? "frame.with_tooltip_reads" : "frame.without_snapshot";
                Record(category,interval,true);
            }
        }
        previouslyConnected=connected;
        lastWork=FrameWork.None;
        previousFrame = now;
    }
    private static void Record(string name, double milliseconds,bool frame=false)
    {
        if (!Timings.TryGetValue(name, out var row)) Timings[name] = row = new Samples(name);
        row.Count++; row.Total += milliseconds; row.Maximum = Math.Max(row.Maximum, milliseconds);
        row.Histogram[Math.Min(4095, (int)(milliseconds * 4))]++;
        if(row.TargetMs!=0 && milliseconds>row.TargetMs) row.AboveTarget++;
        if(frame)
        {
            if(milliseconds>1000d/30) row.Over33++;
            if(milliseconds>50) row.Over50++;
            if(milliseconds>100) row.Over100++;
        }
        double now = Time.realtimeSinceStartupAsDouble;
        if (row.Next == 0) row.Next = now + 10;
        if (now < row.Next) return;
        long cumulative = 0; int percentile = 0;
        for (; percentile < row.Histogram.Length; percentile++)
        { cumulative += row.Histogram[percentile]; if (cumulative >= Math.Ceiling(row.Count * .95)) break; }
        double p95Upper = percentile == 4095 ? row.Maximum : (percentile + 1) / 4d;
        string stalls=frame ? $" over33={row.Over33} over50={row.Over50} over100={row.Over100}" : "";
        string target=row.TargetMs==0 ? "" : $" target_ms={row.TargetMs} above_target={row.AboveTarget}";
        UnityEngine.Debug.Log(FormattableString.Invariant($"[probably_coop PERF] {name} n={row.Count} mean_ms={row.Total/row.Count:F3} p95_upper_ms={p95Upper:F3} max_ms={row.Maximum:F3} managed_bytes={GC.GetTotalMemory(false)} gc0={GC.CollectionCount(0)}{stalls}{target}"));
        Array.Clear(row.Histogram, 0, row.Histogram.Length);
        row.Count = row.Over33 = row.Over50 = row.Over100 = row.AboveTarget = 0; row.Total = row.Maximum = 0; row.Next = now + 10;
    }
    internal readonly struct Scope : IDisposable
    {
        private readonly string name;
        private readonly long start;
        internal Scope(string name) { this.name = name; start = name == null ? 0 : Stopwatch.GetTimestamp(); }
        public void Dispose() { if (name != null) Record(name, (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency); }
    }
}
