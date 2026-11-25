using System;

namespace ProbablyCoop.Core;

internal sealed class ConnectionMeasurements
{
    private const int ProbeWindow = 30;
    private const double LocalStallSeconds = 0.25;
    private const double LocalStallDisplaySeconds = 15;
    private enum ProbeOutcome : byte { Answered, TimedOut, LocalStall }
    private readonly ProbeOutcome[] outcomes = new ProbeOutcome[ProbeWindow];
    private int samples, misses, localStalls, nextSample;
    private ulong nextProbe, awaiting;
    private double sentAt, rateSince, stallAt;
    private double? lastPump;
    private bool probeCrossedStall;
    private long uploaded, downloaded;

    internal double? RoundTripMilliseconds { get; private set; }
    internal int CompletedProbes => samples - localStalls;
    internal double? TimeoutPercent => CompletedProbes > 0 ? 100.0 * misses / CompletedProbes : null;
    internal int LocallyDelayedProbes => localStalls;
    internal double? RecentLocalStallMilliseconds { get; private set; }
    internal double UploadBytesPerSecond { get; private set; }
    internal double DownloadBytesPerSecond { get; private set; }

    internal void Reset(double now)
    {
        samples = misses = localStalls = nextSample = 0;
        nextProbe = awaiting = 0;
        lastPump = null;
        probeCrossedStall = false;
        RoundTripMilliseconds = null;
        RecentLocalStallMilliseconds = null;
        rateSince = now;
        uploaded = downloaded = 0;
        UploadBytesPerSecond = DownloadBytesPerSecond = 0;
    }

    internal void AddSent(int bytes) => uploaded += bytes;
    internal void AddReceived(int bytes) => downloaded += bytes;

    internal void Tick(double now)
    {
        if (lastPump.HasValue) ObserveLocalPump(now);
        double elapsed = now - rateSince;
        if (elapsed < 2.0) return;
        UploadBytesPerSecond = uploaded / elapsed;
        DownloadBytesPerSecond = downloaded / elapsed;
        uploaded = downloaded = 0;
        rateSince = now;
    }

    internal void ObserveLocalPump(double now)
    {
        if (now - stallAt > LocalStallDisplaySeconds) RecentLocalStallMilliseconds = null;
        if (lastPump is double previous && now - previous >= LocalStallSeconds)
        {
            RecentLocalStallMilliseconds = (now - previous) * 1000;
            stallAt = now;
            if (awaiting != 0) probeCrossedStall = true;
        }
        lastPump = now;
    }

    internal void CancelPendingProbe()
    {
        awaiting = 0;
        probeCrossedStall = false;
    }

    internal ulong BeginProbe(double now)
    {
        if (awaiting != 0) RecordOutcome(probeCrossedStall ? ProbeOutcome.LocalStall : ProbeOutcome.TimedOut);
        probeCrossedStall = false;
        sentAt = now;
        awaiting = ++nextProbe;
        return awaiting;
    }

    internal void AcceptPong(ulong serial, double now)
    {
        if (awaiting == 0 || serial != awaiting) return;
        // The reply may have been queued while the local game could not poll it.
        if (lastPump.HasValue) ObserveLocalPump(now);
        bool delayed = probeCrossedStall;
        CancelPendingProbe();
        if (delayed)
        {
            RecordOutcome(ProbeOutcome.LocalStall);
            return;
        }
        double sample = Math.Max(0, (now - sentAt) * 1000);
        RoundTripMilliseconds = RoundTripMilliseconds is double previous ? previous * 0.75 + sample * 0.25 : sample;
        RecordOutcome(ProbeOutcome.Answered);
    }

    private void RecordOutcome(ProbeOutcome outcome)
    {
        if (samples < ProbeWindow) samples++;
        else if (outcomes[nextSample] == ProbeOutcome.TimedOut) misses--;
        else if (outcomes[nextSample] == ProbeOutcome.LocalStall) localStalls--;
        outcomes[nextSample] = outcome;
        if (outcome == ProbeOutcome.TimedOut) misses++;
        else if (outcome == ProbeOutcome.LocalStall) localStalls++;
        nextSample = (nextSample + 1) % ProbeWindow;
    }
}
