using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Validate the entire receipt window before publishing any graph or completing
// any request. A malformed final row must not acknowledge earlier operations.
internal sealed class NativeReceiptProjection
{
    internal readonly struct Receipt
    {
        internal readonly ulong Serial;
        internal readonly ushort Result;
        internal Receipt(ulong serial, ushort result) { Serial = serial; Result = result; }
    }

    internal readonly Receipt[] Receipts;
    private NativeReceiptProjection(Receipt[] receipts) { Receipts = receipts; }

    internal static NativeReceiptProjection Read(JToken value, ulong through, ushort result)
    {
        if (result > (ushort)ShopResult.Failed || (through == 0) != (result == (ushort)ShopResult.None))
            throw new InvalidDataException("Invalid native receipt watermark");
        int count = (int)Math.Min((ulong)ReplicaLink.RequestWindow, through);
        if (value is not JArray rows || rows.Count != count)
            throw new InvalidDataException("Invalid native receipt window");
        var receipts = new Receipt[count];
        ulong first = through - (ulong)count;
        for (int index = 0; index < count; index++)
        {
            if (rows[index] is not JObject row ||
                row["serial"]?.Type != JTokenType.Integer ||
                !ulong.TryParse(row["serial"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong serial) ||
                serial != first + (ulong)index + 1 ||
                row["result"]?.Type != JTokenType.Integer ||
                !ushort.TryParse(row["result"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort rowResult) ||
                rowResult is 0 or > (ushort)ShopResult.Failed)
                throw new InvalidDataException("Invalid native request receipt");
            receipts[index] = new Receipt(serial, rowResult);
        }
        if (count != 0 && receipts[count - 1].Result != result)
            throw new InvalidDataException("Native receipt watermark disagrees with final result");
        return new NativeReceiptProjection(receipts);
    }
}
