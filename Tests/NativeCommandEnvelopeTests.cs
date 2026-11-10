using System.Text;
using ProbablyCoop;

internal static class NativeCommandEnvelopeTests
{
    internal static void Run()
    {
        foreach(string bad in new[]{"", "[1]", "{", "{}", "null", "{\"kind\":{}}", "{\"kind\":true}",
            "{\"kind\":\"item\",\"kind\":\"travel\"}", "{\"kind\":\"item\"}{}",
            "{\"kind\":\"item\",\"nested\":"+new string('[',20)+"0"+new string(']',20)+"}"})
            if(NativeCommandEnvelope.TryRead(Encoding.UTF8.GetBytes(bad),out _)) throw new Exception("Invalid native command accepted: "+bad);
        if(NativeCommandEnvelope.TryRead(new byte[]{0xff,0xfe},out _) || NativeCommandEnvelope.TryRead(new byte[4097],out _))
            throw new Exception("Invalid encoding/size accepted");
        if(!NativeCommandEnvelope.TryRead(Encoding.UTF8.GetBytes("{\"kind\":\"partnerSetup\",\"ids\":[\"lucky\"]}"),out var command) ||
            (string)command["kind"]!="partnerSetup") throw new Exception("Valid native command lost");
        Console.WriteLine("PASS native command envelope: bounded strict UTF8/JSON, duplicate fields, kind type and depth; invalid input returns rejection (13 checks)");
    }
}
