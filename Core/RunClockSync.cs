using System;

namespace ProbablyCoop.Core;

// A run's display clock uses real elapsed time on both ends. Duplicate world
// snapshots must neither advance its sample serial nor restart the local timer.
internal sealed class RunClockSync
{
    private const double Interval=5,Correction=0.25;
    internal ulong Serial { get; private set; }
    internal float Seconds { get; private set; }
    private double sampleAt;
    internal float At(double now) => Seconds+(float)Math.Max(0,now-sampleAt);
    internal void Sample(float seconds,double now)
    {
        if(Serial!=0 && now>=sampleAt && now-sampleAt<Interval && Math.Abs(At(now)-seconds)<Correction) return;
        Receive(checked(Serial+1),seconds,now);
    }
    internal void Receive(ulong serial,float seconds,double now)
    {
        if(serial==0 || float.IsInfinity(seconds) || float.IsNaN(seconds) || double.IsInfinity(now) || double.IsNaN(now))
            throw new ArgumentException("Invalid run clock sample");
        if(serial<=Serial) return;
        Serial=serial; Seconds=seconds; sampleAt=now;
    }
    internal void Reset() { Serial=0; Seconds=0; sampleAt=0; }
}
