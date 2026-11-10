namespace ProbablyCoop.Core;

// Sample the surface even if the physical mouse is still: an item or window
// can move under it. Unchanged samples need only a short presence heartbeat.
internal sealed class CursorCadence
{
    private const double SampleSeconds=0.05, HeartbeatSeconds=0.30;
    private double nextSample,nextHeartbeat;
    private bool sent,visible;
    private ulong surface;
    private ushort x,y;
    private uint? viewport;
    internal bool ShouldSample(double now,bool shown,bool clicked) =>
        !sent || clicked || shown!=visible || now>=nextSample;

    internal bool Observe(double now,bool shown,bool clicked,ulong context,ushort localX,ushort localY,uint? viewportPosition=null)
    {
        nextSample=now+SampleSeconds;
        if(sent && !clicked && now<nextHeartbeat && shown==visible &&
            context==surface && localX==x && localY==y && viewportPosition==viewport) return false;
        sent=true; visible=shown; surface=context; x=localX; y=localY; viewport=viewportPosition;
        nextHeartbeat=now+HeartbeatSeconds;
        return true;
    }
    internal void Reset() { sent=false; nextSample=nextHeartbeat=0; }
}
