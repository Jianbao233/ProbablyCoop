using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// Pure data only. Kept separate from Unity so bounds, cancellation and wire
// compatibility can be tested without starting either game process.
internal static class NativeSnapshotCodec
{
    internal const int MaxPlainBytes=16*1024*1024, MaxPackedBytes=4*1024*1024;
    // Reflection contracts already restrict these values to primitive/string/
    // enum/decimal fields. Avoid creating a serializer and JTokenWriter for
    // every single scalar in every item tag and UI node.
    internal static JValue Scalar(object value)
    {
        if(value is char character) value=character.ToString();
        else if(value is Enum) value=Convert.ChangeType(value,Enum.GetUnderlyingType(value.GetType()),System.Globalization.CultureInfo.InvariantCulture);
        return new JValue(value);
    }
    internal static JToken FieldValue(object value)
    {
        if(value==null) return JValue.CreateNull();
        var type=value.GetType();
        return type.IsPrimitive || type.IsEnum || value is string || value is decimal
            ? Scalar(value) : JToken.FromObject(value);
    }
    internal static byte[] Pack(JObject data,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var output=new MemoryStream();
        using(var packed=new LimitedStream(output,MaxPackedBytes,cancellation))
        using(var gzip=new GZipStream(packed,CompressionLevel.Fastest,true))
        using(var plain=new LimitedStream(gzip,MaxPlainBytes,cancellation))
        using(var text=new StreamWriter(plain,new UTF8Encoding(false,true),8192))
        using(var writer=new JsonTextWriter(text) { Formatting=Formatting.None })
            data.WriteTo(writer);
        cancellation.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    internal static JObject Unpack(byte[] compressed,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(compressed==null || compressed.Length==0 || compressed.Length>MaxPackedBytes)
            throw new InvalidDataException("Native compressed snapshot exceeds bounds");
        if(NativeSnapshotBinary.Matches(compressed)) return NativeSnapshotBinary.Unpack(compressed,cancellation);
        using var input=new MemoryStream(compressed,false);
        using var gzip=new GZipStream(input,CompressionMode.Decompress);
        using var bounded=new LimitedStream(gzip,MaxPlainBytes,cancellation);
        using var text=new StreamReader(bounded,new UTF8Encoding(false,true),false,8192);
        using var reader=new JsonTextReader(text) { MaxDepth=64 };
        var data=JObject.Load(reader);
        // Drain to EOF as well: a complete JSON prefix must not hide trailing
        // data or evade the decompression limit.
        if(reader.Read()) throw new InvalidDataException("Trailing native snapshot data");
        cancellation.ThrowIfCancellationRequested();
        if(data.Descendants().Count()>500000) throw new InvalidDataException("Native graph exceeds node budget");
        return data;
    }

    internal sealed class LimitedStream : Stream
    {
        private readonly Stream inner;
        private readonly long limit;
        private readonly CancellationToken cancellation;
        private long count;
        internal LimitedStream(Stream inner,long limit,CancellationToken cancellation)
        { this.inner=inner; this.limit=limit; this.cancellation=cancellation; }
        public override bool CanRead=>inner.CanRead;
        public override bool CanWrite=>inner.CanWrite;
        public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException();
        public override long Position { get=>count; set=>throw new NotSupportedException(); }
        public override int Read(byte[] buffer,int offset,int length)
        {
            cancellation.ThrowIfCancellationRequested();
            // Read one byte beyond the remaining allowance to distinguish EOF
            // at the exact boundary from a larger stream.
            int read=inner.Read(buffer,offset,(int)Math.Min(length,limit-count+1));
            Count(read); return read;
        }
        public override void Write(byte[] buffer,int offset,int length)
        { cancellation.ThrowIfCancellationRequested(); Count(length); inner.Write(buffer,offset,length); }
        private void Count(int length)
        {
            if(length>limit-count) throw new InvalidDataException("Native snapshot exceeds byte budget");
            count+=length;
        }
        public override void Flush() { cancellation.ThrowIfCancellationRequested(); inner.Flush(); }
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        // Each surrounding using owns its stream; this wrapper leaves it open.
    }
}
