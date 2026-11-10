using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;

namespace ProbablyCoop;

// Opt-in private QA only. Each case visits the same live shop twice,
// in mirrored order, while the guest continues ordinary production.
internal sealed class NativeCaptureComparison
{
    private static readonly int[] Order={0,3,1,2,2,1,3,0};
    private readonly string[] names;
    private readonly bool stock,itemRows,itemFields,plain,markers;
    private const int Warmup=10, BlockSeconds=10;
    private readonly Bucket[] blocks=new Bucket[Order.Length];
    private NativeCaptureComparison(bool stock,bool itemRows,bool itemFields,bool plain,bool markers)
    {
        this.stock=stock;
        this.itemRows=itemRows;
        this.itemFields=itemFields;
        this.plain=plain;
        this.markers=markers;
        names=markers ? new[]{"fresh_markers","reuse_markers","fresh_markers","reuse_markers"} :
            plain ? new[]{"assembled","elided","assembled","elided"} :
            itemFields ? new[]{"fresh_fields","reuse_fields","fresh_fields","reuse_fields"} :
            itemRows ? new[]{"main","worker","main","worker"} :
            stock ? new[]{"fresh","tags","shapes","tags_and_shapes"} : new[]{"reference","fill","rows","fill_and_rows"};
        for(int i=0;i<blocks.Length;i++) blocks[i]=new Bucket();
    }
    private double? start;
    internal static NativeCaptureComparison Current { get; private set; }
    internal bool UseFill { get; private set; }=true;
    internal bool UseRows { get; private set; }=true;
    internal bool ReuseTags { get; private set; }
    internal bool ReuseShapes { get; private set; }=true;
    internal bool WorkerItems { get; private set; }=true;
    internal bool ReuseItemFields { get; private set; }=true;
    internal Bucket Measuring { get; private set; }
    internal bool EarlyPlain { get; private set; }=true;
    internal bool PoolMarkers { get; private set; }=true;
    internal static void Arm(bool stock,bool itemRows,bool itemFields,bool plain,bool markers) => Current=new NativeCaptureComparison(stock,itemRows,itemFields,plain,markers);

    internal sealed class Measurements
    {
        public long samples,over33,over50;
        public double totalMs,maxMs;
        public double meanMs=>samples==0 ? 0 : totalMs/samples;
        internal void Add(double ms)
        {
            samples++; totalMs+=ms; maxMs=Math.Max(maxMs,ms);
            if(ms>1000d/30) over33++;
            if(ms>50) over50++;
        }
        internal void Include(Measurements other)
        {
            samples+=other.samples; totalMs+=other.totalMs; maxMs=Math.Max(maxMs,other.maxMs);
            over33+=other.over33; over50+=other.over50;
        }
    }
    internal sealed class Bucket
    {
        public long elided,reusedMarkers;
        public readonly Measurements capture=new Measurements(),worker=new Measurements(),
            captureFrames=new Measurements(),readFrames=new Measurements(),allFrames=new Measurements();
        internal void Include(Bucket other)
        {
            elided+=other.elided;
            reusedMarkers+=other.reusedMarkers;
            capture.Include(other.capture); worker.Include(other.worker); captureFrames.Include(other.captureFrames);
            readFrames.Include(other.readFrames); allFrames.Include(other.allFrames);
        }
    }
    internal readonly struct CaptureTimer : IDisposable
    {
        private readonly Bucket bucket;
        private readonly long start;
        internal CaptureTimer(Bucket bucket)
        {
            this.bucket=bucket;
            start=bucket==null ? 0 : Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if(bucket==null) return;
            bucket.capture.Add((Stopwatch.GetTimestamp()-start)*1000d/Stopwatch.Frequency);
        }
    }

    internal void Advance(double now)
    {
        start??=now;
        double elapsed=now-start.Value-Warmup;
        if(elapsed<0) return;
        int block=(int)(elapsed/BlockSeconds);
        if(block>=Order.Length)
        {
            var report=new Dictionary<string,Bucket>();
            var visits=new List<object>();
            for(int i=0;i<blocks.Length;i++)
            {
                var bucket=blocks[i];
                string name=names[Order[i]];
                if(bucket.capture.samples<12 || bucket.captureFrames.samples<12 || bucket.worker.samples<12)
                    throw new InvalidOperationException("Insufficient native captures in visit "+i+" for "+name);
                if(!report.TryGetValue(name,out var combined)) report.Add(name,combined=new Bucket());
                combined.Include(bucket);
                visits.Add(new {visit=i,mode=name,metrics=bucket});
            }
            ModHelper.WriteModDataFileText("probably_coop","qa-capture-comparison.json",JsonConvert.SerializeObject(new {cases=report,visits},Formatting.Indented));
            UnityEngine.Debug.Log($"[probably_coop QA] PASS paired-capture {report.Count} cases completed on one authority and connection");
            Current=null; Measuring=null;
            return;
        }
        int mode=Order[block];
        WorkerItems=!itemRows || (mode&1)!=0;
        ReuseItemFields=itemFields && (mode&1)!=0;
        EarlyPlain=!plain || (mode&1)!=0;
        PoolMarkers=!markers || (mode&1)!=0;
        UseFill=!markers && !plain && !itemFields && !itemRows && !stock && (mode&1)!=0; UseRows=markers || plain || itemFields || itemRows || stock || (mode&2)!=0;
        ReuseTags=stock && (mode&1)!=0; ReuseShapes=!stock || (mode&2)!=0;
        // Exclude the boundaries where speculative reads or the previous
        // frame's worker can still belong to the preceding case.
        double phase=elapsed-block*BlockSeconds;
        Measuring=phase>=1 && phase<BlockSeconds-1 ? blocks[block] : null;
    }
    internal void Frame(double interval,NativePerformance.FrameWork work)
    {
        if(Measuring==null || (work&NativePerformance.FrameWork.QaRender)!=0) return;
        Measuring.allFrames.Add(interval);
        if((work&NativePerformance.FrameWork.Capture)!=0) Measuring.captureFrames.Add(interval);
        else if((work&NativePerformance.FrameWork.Tooltip)!=0) Measuring.readFrames.Add(interval);
    }
}
