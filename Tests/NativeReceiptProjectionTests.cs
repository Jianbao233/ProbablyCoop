using Newtonsoft.Json.Linq;
using ProbablyCoop;
using ProbablyCoop.Core;

internal static class NativeReceiptProjectionTests
{
    internal static void Run()
    {
        Check(NativeReceiptProjection.Read(new JArray(), 0, 0).Receipts.Length == 0, "initial window");
        foreach (ulong through in new ulong[] { 1, 31, 32, 33, 200, ulong.MaxValue })
        {
            var rows = Window(through);
            var read = NativeReceiptProjection.Read(rows, through, 1);
            Check(read.Receipts.Length == (int)Math.Min((ulong)ReplicaLink.RequestWindow, through) &&
                read.Receipts[^1].Serial == through, "bounded contiguous window");
            rows[0]!["result"] = 2;
            Check(read.Receipts[0].Result == 1, "prepared values cannot change through source JSON");
        }
        Reject(null!, 0, 0, "missing window");
        Reject(new JObject(), 0, 0, "non-array window");
        Reject(Window(1), 1, 0, "empty watermark result");
        Reject(new JArray(), 0, 1, "result without command");
        Reject(Window(1), 1, 7, "unknown watermark result");
        Reject(Window(1), 2, 1, "missing final receipt");
        Reject(Window(2), 1, 1, "extra receipt");
        Reject(Window(1), 1, 2, "contradictory result");
        foreach (var malformed in new JToken[] { JValue.CreateNull(), new JArray(), new JValue(1) })
        { var rows = Window(2); rows[1] = malformed; Reject(rows, 2, 1, "invalid row type"); }
        foreach (var serial in new JToken[] { new JValue("2"), new JValue(-1), new JValue(0), new JValue(1), new JValue(3), new JValue(2.0) })
        { var rows = Window(2); rows[1]!["serial"] = serial; Reject(rows, 2, 1, "invalid or noncontiguous serial"); }
        foreach (var result in new JToken[] { new JValue("1"), new JValue(-1), new JValue(0), new JValue(7), new JValue(65536), new JValue(1.0) })
        { var rows = Window(2); rows[1]!["result"] = result; Reject(rows, 2, 1, "invalid result"); }
        // A late bad row never exposes a partially accepted window to the
        // projection consumer; the previously committed instance is unchanged.
        var accepted = NativeReceiptProjection.Read(Window(1), 1, 1);
        var badFinal = Window(2); badFinal[1]!["result"] = "1";
        try { accepted = NativeReceiptProjection.Read(badFinal, 2, 1); throw new Exception("accepted late malformed row"); }
        catch (InvalidDataException) { }
        Check(accepted.Receipts.Length == 1 && accepted.Receipts[0].Serial == 1, "late failure is atomic");
        Console.WriteLine("PASS native receipt preflight: whole-window validation, bounded sequence, watermark agreement and late-row rejection");
    }

    private static JArray Window(ulong through)
    {
        int count = (int)Math.Min((ulong)ReplicaLink.RequestWindow, through);
        var result = new JArray();
        for (int i = 0; i < count; i++)
            result.Add(new JObject { ["serial"] = through - (ulong)count + (ulong)i + 1, ["result"] = 1 });
        return result;
    }
    private static void Reject(JToken rows, ulong through, ushort result, string name)
    {
        try { NativeReceiptProjection.Read(rows, through, result); }
        catch (InvalidDataException) { return; }
        throw new Exception("Receipt preflight accepted " + name);
    }
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("Receipt preflight: " + name); }
}
