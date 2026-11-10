using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

internal static class NativeSnapshotBinaryTests
{
    internal static void Run()
    {
        var scalars=new JObject {
            ["null"]=JValue.CreateNull(),["true"]=true,["false"]=false,["signed"]=long.MinValue,["unsigned"]=ulong.MaxValue,
            ["single"]=float.Epsilon,["double"]=double.MaxValue,["decimal"]=decimal.MaxValue,["negativeDecimal"]=decimal.MinValue,
            ["nan"]=double.NaN,["infinity"]=double.PositiveInfinity,["negativeInfinity"]=float.NegativeInfinity,
            ["text"]="中文😀\0\n\"\\",["literalDate"]="2026-11-06T06:00:00Z",["empty"]=new JArray(),["object"]=new JObject(),
            ["nullString"]=new JValue((string)null)
        };
        var encoded=NativeSnapshotBinary.Pack(scalars);
        var decoded=NativeSnapshotCodec.Unpack(encoded);
        Check(JToken.DeepEquals(scalars,decoded),"exact scalar roundtrip");
        Check(decoded["literalDate"].Type==Newtonsoft.Json.Linq.JTokenType.String,"literal text became a date");
        var shared=new JObject { ["a"]=new string('中',4000),["b"]=new string('中',4000) };
        var roundtrip=NativeSnapshotCodec.Unpack(NativeSnapshotBinary.Pack(shared));
        Check(ReferenceEquals(((JValue)roundtrip["a"]).Value,((JValue)roundtrip["b"]).Value),"repeated strings not shared");
        for(int i=0;i<40;i++)
        {
            var graph=new JObject { ["rows"]=new JArray(Enumerable.Range(0,i).Select(n=>new JObject {
                ["n"]=n,["label"]="机器",["nested"]=new JArray(scalars.DeepClone(),n*.125,shared.DeepClone()) })) };
            Check(JToken.DeepEquals(graph,NativeSnapshotCodec.Unpack(NativeSnapshotBinary.Pack(graph))),"nested tree roundtrip");
        }
        Reject(()=>NativeSnapshotBinary.Unpack(new byte[]{0x50,0x53,0x42,0x32}),"header/version");
        Reject(()=>NativeSnapshotBinary.Unpack(new byte[NativeSnapshotCodec.MaxPackedBytes+1]),"compressed bound");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=>w.Write((byte)255))),"unknown token");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { w.Write((byte)1); w.Write(-1); })),"negative object count");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { w.Write((byte)1); w.Write(int.MaxValue); })),"oversized object count");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { w.Write((byte)1); w.Write(1); w.Write(~0); })),"unknown name reference");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { w.Write((byte)1); w.Write(1); w.Write(int.MaxValue); })),"oversized string length");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { w.Write((byte)1); w.Write(1); w.Write(2); w.Write((byte)65); })),"truncated name");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { w.Write((byte)1); w.Write(1); w.Write(1); w.Write((byte)255); })),"invalid UTF8");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { w.Write((byte)1); w.Write(2); Name(w,"a"); w.Write((byte)0); w.Write(~0); w.Write((byte)0); })),"duplicate key");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { w.Write((byte)1); w.Write(0); w.Write((byte)0); })),"trailing token");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { w.Write((byte)2); w.Write(0); })),"array root");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { w.Write((byte)1); w.Write(1); Name(w,"x"); w.Write((byte)2); w.Write(500001); })),"node budget");
        var deep=new JObject(); var cursor=deep;
        for(int i=0;i<65;i++) { var next=new JObject(); cursor["x"]=next; cursor=next; }
        Reject(()=>NativeSnapshotBinary.Pack(deep),"outgoing depth");
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> { for(int i=0;i<66;i++) { w.Write((byte)1); w.Write(1); Name(w,"x"); } w.Write((byte)0); })),"incoming depth");
        var tooBig=new JObject { ["text"]=new string('中',NativeSnapshotCodec.MaxPlainBytes/3+1) };
        Reject(()=>NativeSnapshotBinary.Pack(tooBig),"outgoing bytes");
        // A small compressed dictionary/ref stream cannot evade the logical
        // expanded budget by referring to one large string thousands of times.
        Reject(()=>NativeSnapshotCodec.Unpack(Frame(w=> {
            w.Write((byte)1); w.Write(1); Name(w,"x"); w.Write((byte)2); w.Write(6000);
            w.Write((byte)3); Name(w,new string('x',4096));
            for(int i=1;i<6000;i++) { w.Write((byte)3); w.Write(~1); }
        })),"shared string expansion");
        Reject(()=>NativeSnapshotBinary.Pack(new JObject { ["unsupported"]=DateTime.UtcNow }),"unsupported scalar must not silently stringify");
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();
            Cancelled(()=>NativeSnapshotBinary.Pack(scalars,cancelled.Token));
            Cancelled(()=>NativeSnapshotCodec.Unpack(encoded,cancelled.Token));
        }
        Benchmark();
        Console.WriteLine("PASS binary snapshots: exact scalar/Unicode/tree parity, per-packet string sharing, byte/node/depth/ref bounds, expansion, malformed/trailing rejection and cancellation");
    }
    private static void Name(BinaryWriter writer,string value)
    { var bytes=Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    private static byte[] Frame(Action<BinaryWriter> content)
    {
        using var output=new MemoryStream(); output.Write(new byte[]{0x50,0x53,0x42,0x31});
        using(var gzip=new GZipStream(output,CompressionLevel.Fastest,true))
        using(var writer=new BinaryWriter(gzip,Encoding.UTF8,true)) content(writer);
        return output.ToArray();
    }
    private static void Benchmark()
    {
        var graph=new JObject { ["items"]=new JArray(Enumerable.Range(0,100).Select(i=>new JObject {
            ["entity"]=i,["text"]="机器生产/顾客商品/夫妻店",["tooltip"]=new string('中',400),
            ["tags"]=new JArray(Enumerable.Range(0,60).Select(j=>new JObject {
                ["label"]="原版标签",["value"]=j*.25,["active"]=true,["flags"]=3,["counter"]=i })) })) };
        var json=NativeSnapshotCodec.Pack(graph); var binary=NativeSnapshotBinary.Pack(graph);
        Check(JToken.DeepEquals(NativeSnapshotCodec.Unpack(json),NativeSnapshotCodec.Unpack(binary)),"representative legacy graph parity");
        (double ms,long bytes) Measure(Action action)
        {
            for(int i=0;i<3;i++) action();
            long allocated=GC.GetAllocatedBytesForCurrentThread(); var timer=System.Diagnostics.Stopwatch.StartNew();
            for(int i=0;i<10;i++) action();
            return (timer.Elapsed.TotalMilliseconds/10,(GC.GetAllocatedBytesForCurrentThread()-allocated)/10);
        }
        var old=Measure(()=>NativeSnapshotCodec.Unpack(json)); var next=Measure(()=>NativeSnapshotCodec.Unpack(binary));
        Console.WriteLine($"BENCH snapshot decode .NET9 only: JSON={old.ms:F3}ms/{old.bytes}B binary={next.ms:F3}ms/{next.bytes}B packed={json.Length}/{binary.Length}B; Unity timing required");
    }
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    private static void Reject(Action action,string message)
    { try { action(); } catch(InvalidDataException) { return; } throw new Exception("Accepted "+message); }
    private static void Cancelled(Action action)
    { try { action(); } catch(OperationCanceledException) { return; } throw new Exception("Cancellation ignored"); }
}
