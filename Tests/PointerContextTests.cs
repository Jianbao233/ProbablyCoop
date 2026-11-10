using ProbablyCoop.Core;

internal static class PointerContextTests
{
    internal static void Run()
    {
        foreach(uint entity in new[]{1u,65536u,0x80000000u,uint.MaxValue})
        foreach(ushort location in new ushort[]{0,1,2,3,4,5})
        {
            var context=new PointerContext(location,PointerContext.NativeElement,entity);
            var packet=new Packet(Kind.Cursor,Guid.NewGuid(),Guid.NewGuid(),1,revision:context.Encode(),x:65535,y:1234,flags:3);
            Check(Wire.TryDecode(Wire.Encode(packet),true,out var decoded),"wire rejected native cursor");
            Check(PointerContext.TryDecode(decoded.Revision,out var result) && result.Entity==entity && result.Location==location &&
                result.Surface==PointerContext.NativeElement && decoded.X==65535 && decoded.Y==1234,"entity/context truncated across wire");
        }
        foreach(ushort surface in new ushort[]{32,41,42,43,46,47,50,51,52})
            Check(PointerContext.TryDecode(new PointerContext(1,surface).Encode(),out _),"static context rejected");
        foreach(var bad in new[]{new PointerContext(6,32),new PointerContext(1,53),new PointerContext(1,32,1),new PointerContext(1,54),new PointerContext(1,0)})
            Check(!PointerContext.TryDecode(bad.Encode(),out _),"invalid context accepted");
        byte[] viewport={1,0,255,255};
        var fallbackPacket=new Packet(Kind.Cursor,serial:1,revision:new PointerContext(1,53,4).Encode(),
            x:7,y:9,flags:1,payload:viewport);
        var fallbackBytes=Wire.Encode(fallbackPacket);
        Check(Wire.TryDecode(fallbackBytes,true,out var fallback) && fallback.Payload.SequenceEqual(viewport),
            "cursor lost global coordinates");
        Check(!Wire.TryDecode(fallbackBytes.AsSpan(0,fallbackBytes.Length-1),true,out _),
            "truncated cursor fallback accepted");
        var cadence=new CursorCadence();
        var anchor=new PointerContext(1,53,4).Encode();
        Check(cadence.ShouldSample(0,true,false) && cadence.Observe(0,true,false,anchor,7,9),"missing first pointer");
        Check(!cadence.ShouldSample(.02,true,false),"unnecessary moving sample");
        Check(cadence.ShouldSample(.02,true,true) && cadence.Observe(.02,true,true,anchor,7,9),"click waited for next sample");
        Check(cadence.ShouldSample(.08,true,false) && !cadence.Observe(.08,true,false,anchor,7,9),"stationary pointer resent");
        Check(cadence.Observe(.14,true,false,new PointerContext(1,53,5).Encode(),7,9),"same coordinates over new item not sent");
        Check(cadence.Observe(.45,true,false,new PointerContext(1,53,5).Encode(),7,9),"stationary heartbeat missing");
        Check(cadence.ShouldSample(.46,false,false) && cadence.Observe(.46,false,false,32,0,0),"focus loss delayed");
        Check(cadence.Observe(.47,true,false,32,7,9,0x00010000) &&
            cadence.Observe(.53,true,false,32,7,9,0x00020000),"moving viewport fallback was suppressed");
        cadence.Reset(); Check(cadence.ShouldSample(.54,false,false) && cadence.Observe(.54,false,false,32,0,0),"new peer inherited old pointer");
        Console.WriteLine("PASS cursor context: full UInt32 entity/scene/surface across wire format, static compatibility and invalid contexts rejected");
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
}
