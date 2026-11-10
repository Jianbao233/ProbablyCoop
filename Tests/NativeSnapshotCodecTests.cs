using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

internal static class NativeSnapshotCodecTests
{
    internal static void Run()
    {
        CheckScalarContract();
        var graph=new JObject {
            ["schema"]=1,["text"]="中文\n<color=#abc>夫妻店</color>😀\"\\",
            ["empty"]=new JArray(),["null"]=JValue.CreateNull(),
            ["roots"]=new JArray(Enumerable.Range(0,160).Select(i=>new JObject {
                ["entity"]=(uint)i,["cash"]=i*1.25,["enabled"]=i%2==0,
                ["children"]=new JArray(Enumerable.Range(0,12).Select(j=>new JObject {
                    ["entity"]=j,["label"]="机器生产/库存/委托/顾客商品",["value"]=(long)i*j,
                    ["shape"]=new JArray(1,2,0,1),["description"]=new string('x',200) })) }))
        };
        var oldText=graph.ToString(Formatting.None);
        var packed=NativeSnapshotCodec.Pack(graph);
        using(var stream=new StreamReader(new GZipStream(new MemoryStream(packed),CompressionMode.Decompress),Encoding.UTF8))
            Check(stream.ReadToEnd()==oldText,"wire JSON differs from old serializer");
        Check(JToken.DeepEquals(graph,NativeSnapshotCodec.Unpack(packed)),"Unicode/tree round trip");
        Check(JToken.DeepEquals(graph,NativeSnapshotCodec.Unpack(OldPack(oldText))),"legacy gzip compatibility");
        Reject(()=>NativeSnapshotCodec.Unpack(Array.Empty<byte>()),"empty");
        Reject(()=>NativeSnapshotCodec.Unpack(new byte[NativeSnapshotCodec.MaxPackedBytes+1]),"compressed limit");
        Reject(()=>NativeSnapshotCodec.Unpack(OldPack("{} {}")),"trailing document");
        Reject(()=>NativeSnapshotCodec.Unpack(OldPack("{\"x\":")),"incomplete JSON");
        Reject(()=>NativeSnapshotCodec.Unpack(OldPack("{\"x\":"+new string('[',70)+"0"+new string(']',70)+"}")),"depth limit");
        Reject(()=>NativeSnapshotCodec.Unpack(OldPack("{\"x\":["+string.Join(',',Enumerable.Repeat("0",500001))+"]}")),"node limit");
        // Multibyte input makes this a byte-bound test, not a character-count test.
        var tooBig=new JObject { ["text"]=new string('中',NativeSnapshotCodec.MaxPlainBytes/3+1) };
        Reject(()=>NativeSnapshotCodec.Pack(tooBig),"outgoing expanded limit");
        Reject(()=>NativeSnapshotCodec.Unpack(OldPack(tooBig.ToString(Formatting.None))),"incoming expansion bomb");
        Reject(()=>NativeSnapshotCodec.Unpack(OldPack("{}"+new string(' ',NativeSnapshotCodec.MaxPlainBytes))),"trailing whitespace expansion limit");
        using(var stop=new CancellationTokenSource())
        {
            stop.Cancel();
            Cancelled(()=>NativeSnapshotCodec.Pack(graph,stop.Token));
            Cancelled(()=>NativeSnapshotCodec.Unpack(packed,stop.Token));
        }
        // A representative graph measures this pure serialization phase only;
        // .NET 9 numbers are not Unity Mono frame-time evidence.
        NativeSnapshotCodec.Pack(graph); OldPack(oldText);
        var old=Measure(()=>OldPack(graph.ToString(Formatting.None)));
        var current=Measure(()=>NativeSnapshotCodec.Pack(graph));
        Console.WriteLine($"PASS native snapshot codec: legacy/Unicode parity, byte/depth/node limits, trailing data and cancellation; .NET9 pack allocated bytes old={old.Bytes} stream={current.Bytes}; mean ms old={old.Ms:F3} stream={current.Ms:F3}");
    }
    private enum SignedState : short { Negative=-31, Positive=12 }
    private enum UnsignedState : ulong { Last=ulong.MaxValue }
    private static void CheckScalarContract()
    {
        object[] scalars={null,true,false,(byte)255,(sbyte)-128,(short)-32768,(ushort)65535,
            int.MinValue,uint.MaxValue,long.MinValue,ulong.MaxValue,0.125f,float.Epsilon,
            double.MaxValue,double.NaN,float.PositiveInfinity,decimal.MaxValue,'中',"中\n😀\\\"",
            SignedState.Negative,UnsignedState.Last};
        var serializer=JsonSerializer.Create(new JsonSerializerSettings { TypeNameHandling=TypeNameHandling.None,MaxDepth=64 });
        foreach(var value in scalars)
        {
            var before=value==null ? JValue.CreateNull() : JToken.FromObject(value,serializer);
            var after=NativeSnapshotCodec.Scalar(value);
            Check(before.Type==after.Type && before.ToString(Formatting.None)==after.ToString(Formatting.None),"scalar wire mismatch "+value?.GetType().Name);
        }
        foreach(var value in new object[]{new List<string>{"共同库存",null},new List<int>{-1,2},new Dictionary<string,int>{{"资金",25}}})
            Check(JToken.DeepEquals(JToken.FromObject(value),NativeSnapshotCodec.FieldValue(value)),"collection field changed");
        var samples=Enumerable.Range(0,10000).Select(i=>scalars[i%scalars.Length]).ToArray();
        var beforeCost=Measure(()=> { foreach(var value in samples) if(value!=null) JToken.FromObject(value,serializer); });
        var afterCost=Measure(()=> { foreach(var value in samples) NativeSnapshotCodec.Scalar(value); });
        Console.WriteLine($"PASS native scalar contract: numeric extremes, enums, floating special values, Unicode, null and collection fallback; .NET9 10000 scalars allocated old={beforeCost.Bytes} direct={afterCost.Bytes}; mean ms old={beforeCost.Ms:F3} direct={afterCost.Ms:F3}");
    }
    private static byte[] OldPack(string text)
    {
        var bytes=Encoding.UTF8.GetBytes(text); using var output=new MemoryStream();
        using(var gzip=new GZipStream(output,CompressionLevel.Fastest,true)) gzip.Write(bytes,0,bytes.Length);
        return output.ToArray();
    }
    private static (long Bytes,double Ms) Measure(Action action)
    {
        var bytes=GC.GetAllocatedBytesForCurrentThread(); var timer=System.Diagnostics.Stopwatch.StartNew();
        for(int i=0;i<10;i++) action();
        return ((GC.GetAllocatedBytesForCurrentThread()-bytes)/10,timer.Elapsed.TotalMilliseconds/10);
    }
    private static void Cancelled(Action action)
    { try { action(); } catch(OperationCanceledException) { return; } throw new Exception("cancellation ignored"); }
    private static void Reject(Action action,string name)
    { try { action(); } catch(Exception e) when(e is InvalidDataException or JsonException) { return; } throw new Exception("accepted invalid snapshot: "+name); }
    private static void Check(bool value,string name) { if(!value) throw new Exception(name); }
}
