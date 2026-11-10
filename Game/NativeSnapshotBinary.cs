using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// Versioned data-only snapshot encoding. No type names, object activation or
// native references. Per-packet string sharing removes repeated property names
// and rich text; a separate expanded budget also counts every shared reference.
internal static class NativeSnapshotBinary
{
    private const uint Magic=0x31425350; // PSB1, little endian
    private const int MaxNodes=500001,MaxDepth=64;
    private static readonly Encoding Utf8=new UTF8Encoding(false,true);
    private enum Kind : byte { Null,Object,Array,String,Integer,Unsigned,Double,Decimal,False,True,Single,NullString }
    internal static bool Matches(byte[] data)=>data.Length>=4 && data[0]==0x50 && data[1]==0x53 && data[2]==0x42 && data[3]==0x31;

    internal static byte[] Pack(JObject data,CancellationToken cancellation=default,
        IReadOnlyDictionary<JToken,NativeDeferredFields.Block> fields=null)
    {
        cancellation.ThrowIfCancellationRequested();
        using var output=new MemoryStream();
        using(var packed=new NativeSnapshotCodec.LimitedStream(output,NativeSnapshotCodec.MaxPackedBytes,cancellation))
        {
            using(var header=new BinaryWriter(packed,Utf8,true)) header.Write(Magic);
            using var gzip=new GZipStream(packed,CompressionLevel.Fastest,true);
            using var plain=new NativeSnapshotCodec.LimitedStream(gzip,NativeSnapshotCodec.MaxPlainBytes,cancellation);
            using var buffered=new BufferedStream(plain,8192);
            using var writer=new BinaryWriter(buffered,Utf8,true);
            new Encoder(writer,cancellation,fields).Write(data,0);
        }
        cancellation.ThrowIfCancellationRequested();
        return output.ToArray();
    }
    internal static JObject Unpack(byte[] bytes,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(bytes==null || bytes.Length>NativeSnapshotCodec.MaxPackedBytes || !Matches(bytes)) throw new InvalidDataException("Invalid binary snapshot header");
        try
        {
            using var input=new MemoryStream(bytes,4,bytes.Length-4,false);
            using var gzip=new GZipStream(input,CompressionMode.Decompress);
            using var plain=new NativeSnapshotCodec.LimitedStream(gzip,NativeSnapshotCodec.MaxPlainBytes,cancellation);
            using var buffered=new BufferedStream(plain,8192);
            using var reader=new BinaryReader(buffered,Utf8,true);
            var value=new Decoder(reader,cancellation).Read(0);
            if(value is not JObject data || buffered.ReadByte()!=-1) throw new InvalidDataException("Invalid binary snapshot root or trailing data");
            cancellation.ThrowIfCancellationRequested();
            return data;
        }
        catch(Exception error) when(error is EndOfStreamException || error is DecoderFallbackException || error is ArgumentException || error is OverflowException)
        { throw new InvalidDataException("Malformed binary snapshot",error); }
    }
    private abstract class Budget
    {
        private readonly CancellationToken cancellation;
        protected int Nodes;
        private int expanded;
        protected Budget(CancellationToken cancellation) { this.cancellation=cancellation; }
        protected void Node(int depth)
        {
            if(depth>MaxDepth || ++Nodes>MaxNodes) throw new InvalidDataException("Binary snapshot depth/node budget exceeded");
            if((Nodes & 127)==0) cancellation.ThrowIfCancellationRequested();
            Charge(1);
        }
        protected void Charge(int bytes)
        {
            if(bytes<0 || bytes>NativeSnapshotCodec.MaxPlainBytes-expanded) throw new InvalidDataException("Expanded binary snapshot exceeds byte budget");
            expanded+=bytes;
        }
        protected void Count(int count,int multiplier=1)
        {
            if(count<0 || count>(MaxNodes-Nodes)/multiplier) throw new InvalidDataException("Invalid binary snapshot container count");
            Charge(4);
        }
    }
    private sealed class Encoder : Budget
    {
        private readonly BinaryWriter writer;
        private readonly IReadOnlyDictionary<JToken,NativeDeferredFields.Block> fields;
        private readonly Dictionary<string,(int Id,int Bytes)> strings=new Dictionary<string,(int,int)>(StringComparer.Ordinal);
        internal Encoder(BinaryWriter writer,CancellationToken cancellation,IReadOnlyDictionary<JToken,NativeDeferredFields.Block> fields):base(cancellation)
        { this.writer=writer; this.fields=fields; }
        private void Text(string value)
        {
            if(strings.TryGetValue(value,out var old)) { Charge(4+old.Bytes); writer.Write(~old.Id); return; }
            int length=Utf8.GetByteCount(value); Charge(4+length);
            strings.Add(value,(strings.Count,length));
            writer.Write(length); writer.Write(Utf8.GetBytes(value));
        }
        internal void Write(JToken value,int depth)
        {
            Node(depth);
            switch(value.Type)
            {
                case JTokenType.Object:
                    if(fields!=null && fields.TryGetValue((JObject)value,out var block)) { Frozen(block,depth); break; }
                    var obj=(JObject)value; Count(obj.Count,2); writer.Write((byte)Kind.Object); writer.Write(obj.Count);
                    foreach(var p in obj.Properties()) { Node(depth+1); Text(p.Name); Write(p.Value,depth+1); }
                    break;
                case JTokenType.Array:
                    if(fields!=null && fields.TryGetValue(value,out var arrayBlock))
                    {
                        if(arrayBlock.Bytes!=null) CellArray(arrayBlock.Bytes,depth);
                        else if(arrayBlock.Strings!=null) StringArray(arrayBlock.Strings,depth);
                        else throw new InvalidDataException("Invalid frozen array block");
                        break;
                    }
                    var array=(JArray)value; Count(array.Count); writer.Write((byte)Kind.Array); writer.Write(array.Count);
                    foreach(var item in array) Write(item,depth+1);
                    break;
                case JTokenType.Null: writer.Write((byte)Kind.Null); break;
                case JTokenType.String:
                    string text=(string)value;
                    writer.Write((byte)(text==null ? Kind.NullString : Kind.String));
                    if(text!=null) Text(text);
                    break;
                case JTokenType.Boolean: writer.Write((byte)((bool)value ? Kind.True : Kind.False)); break;
                case JTokenType.Integer:
                    Charge(8);
                    if(((JValue)value).Value is ulong u) { writer.Write((byte)Kind.Unsigned); writer.Write(u); }
                    else { writer.Write((byte)Kind.Integer); writer.Write(Convert.ToInt64(((JValue)value).Value,CultureInfo.InvariantCulture)); }
                    break;
                case JTokenType.Float:
                    var number=((JValue)value).Value;
                    if(number is decimal dec) { Charge(16); writer.Write((byte)Kind.Decimal); writer.Write(dec); }
                    else if(number is float single) { Charge(4); writer.Write((byte)Kind.Single); writer.Write(single); }
                    else { Charge(8); writer.Write((byte)Kind.Double); writer.Write(Convert.ToDouble(number,CultureInfo.InvariantCulture)); }
                    break;
                default: throw new InvalidDataException("Unsupported binary snapshot scalar: "+value.Type);
            }
        }
        private void Frozen(NativeDeferredFields.Block block,int depth)
        {
            if(block.Keys==null) { FrozenRow(block,0,depth); return; }
            Count(block.Keys.Length,2); writer.Write((byte)Kind.Object); writer.Write(block.Keys.Length);
            for(int row=0;row<block.Keys.Length;row++)
            {
                Node(depth+1); Text(block.Keys[row]); Node(depth+1);
                if(block.InheritsRow(row)) writer.Write((byte)Kind.False);
                else FrozenRow(block,row*block.Names.Length,depth+1);
            }
        }
        private void FrozenRow(NativeDeferredFields.Block block,int offset,int depth)
        {
            Count(block.Names.Length,2); writer.Write((byte)Kind.Object); writer.Write(block.Names.Length);
            for(int i=0;i<block.Names.Length;i++)
            {
                Node(depth+1); Text(block.Names[i]); Node(depth+1);
                if(block.TagRows==null) FrozenValue(block.Values[offset+i],depth+1);
                else FrozenTag(block.TagRows[offset/block.Names.Length],block.TagColumns[i]);
            }
        }
        private void FrozenValue(object value,int depth)
        {
            if(value is byte[] cells) CellArray(cells,depth);
            else if(value is string[] strings) StringArray(strings,depth);
            else if(value is int[] ints)
            {
                Count(ints.Length); writer.Write((byte)Kind.Array); writer.Write(ints.Length);
                foreach(int number in ints) { Node(depth+1); Charge(8); writer.Write((byte)Kind.Integer); writer.Write((long)number); }
            }
            else if(value is KeyValuePair<string,int>[] rows)
            {
                Count(rows.Length,2); writer.Write((byte)Kind.Object); writer.Write(rows.Length);
                foreach(var row in rows)
                { Node(depth+1); Text(row.Key); Node(depth+1); Charge(8); writer.Write((byte)Kind.Integer); writer.Write((long)row.Value); }
            }
            else FrozenScalar(value);
        }
        private void FrozenTag(NativeTagRow row,int column)
        {
            switch(column)
            {
                case 0: writer.Write((byte)(row.Enabled ? Kind.True : Kind.False)); break;
                case 1: FrozenText(row.Text); break;
                case 2: Charge(8); writer.Write((byte)Kind.Integer); writer.Write((long)row.Integer); break;
                case 3: Charge(4); writer.Write((byte)Kind.Single); writer.Write(row.Single); break;
                case 4: Charge(8); writer.Write((byte)Kind.Integer); writer.Write(row.Long); break;
                case 5: Charge(8); writer.Write((byte)Kind.Double); writer.Write(row.Double); break;
                case 6: writer.Write((byte)(row.Boolean ? Kind.True : Kind.False)); break;
                case 7: FrozenText(row.Identifier); break;
                case 8: FrozenText(row.DisplayName); break;
                default: throw new InvalidDataException("Invalid frozen tag column");
            }
        }
        private void CellArray(byte[] cells,int depth)
        {
            Count(cells.Length); writer.Write((byte)Kind.Array); writer.Write(cells.Length);
            foreach(byte cell in cells) { Node(depth+1); Charge(8); writer.Write((byte)Kind.Integer); writer.Write((long)cell); }
        }
        private void StringArray(string[] strings,int depth)
        {
            Count(strings.Length); writer.Write((byte)Kind.Array); writer.Write(strings.Length);
            foreach(var text in strings) { Node(depth+1); FrozenText(text); }
        }
        private void FrozenText(string value)
        {
            writer.Write((byte)(value==null ? Kind.Null : Kind.String));
            if(value!=null) Text(value);
        }
        private void FrozenScalar(object value)
        {
            // Match NativeSnapshotCodec.Scalar + Write exactly, including enum
            // underlying signedness and null object (distinct from null string).
            if(value is Enum) value=Convert.ChangeType(value,Enum.GetUnderlyingType(value.GetType()),CultureInfo.InvariantCulture);
            if(value is char character) value=character.ToString();
            if(value==null) { writer.Write((byte)Kind.Null); return; }
            if(value is string text) { writer.Write((byte)Kind.String); Text(text); return; }
            if(value is bool boolean) { writer.Write((byte)(boolean ? Kind.True : Kind.False)); return; }
            if(value is decimal dec) { Charge(16); writer.Write((byte)Kind.Decimal); writer.Write(dec); return; }
            if(value is float single) { Charge(4); writer.Write((byte)Kind.Single); writer.Write(single); return; }
            if(value is double number) { Charge(8); writer.Write((byte)Kind.Double); writer.Write(number); return; }
            if(value is ulong unsigned) { Charge(8); writer.Write((byte)Kind.Unsigned); writer.Write(unsigned); return; }
            if(value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long)
            { Charge(8); writer.Write((byte)Kind.Integer); writer.Write(Convert.ToInt64(value,CultureInfo.InvariantCulture)); return; }
            throw new InvalidDataException("Unsupported frozen snapshot scalar");
        }
    }
    private sealed class Decoder : Budget
    {
        private readonly BinaryReader reader;
        private readonly List<(string Value,int Bytes)> strings=new List<(string,int)>();
        internal Decoder(BinaryReader reader,CancellationToken cancellation):base(cancellation) { this.reader=reader; }
        private string Text()
        {
            int code=reader.ReadInt32();
            if(code<0)
            {
                int id=~code;
                if(id>=strings.Count) throw new InvalidDataException("Invalid binary snapshot string reference");
                var old=strings[id]; Charge(4+old.Bytes); return old.Value;
            }
            if(code>NativeSnapshotCodec.MaxPlainBytes-4) throw new InvalidDataException("Invalid binary snapshot string length");
            Charge(4+code);
            var bytes=reader.ReadBytes(code);
            if(bytes.Length!=code) throw new EndOfStreamException();
            string value=Utf8.GetString(bytes); strings.Add((value,code)); return value;
        }
        internal JToken Read(int depth)
        {
            Node(depth);
            switch((Kind)reader.ReadByte())
            {
                case Kind.Object:
                    int properties=reader.ReadInt32(); Count(properties,2); var obj=new JObject();
                    for(int i=0;i<properties;i++)
                    {
                        Node(depth+1); string name=Text();
                        if(obj.Property(name)!=null) throw new InvalidDataException("Duplicate binary snapshot property");
                        obj.Add(name,Read(depth+1));
                    }
                    return obj;
                case Kind.Array:
                    int count=reader.ReadInt32(); Count(count); var array=new JArray();
                    for(int i=0;i<count;i++) array.Add(Read(depth+1));
                    return array;
                case Kind.Null: return JValue.CreateNull();
                case Kind.NullString: return new JValue((string)null);
                case Kind.String: return new JValue(Text());
                case Kind.False: return new JValue(false);
                case Kind.True: return new JValue(true);
                case Kind.Integer: Charge(8); return new JValue(reader.ReadInt64());
                case Kind.Unsigned: Charge(8); return new JValue(reader.ReadUInt64());
                case Kind.Double: Charge(8); return new JValue(reader.ReadDouble());
                case Kind.Single: Charge(4); return new JValue(reader.ReadSingle());
                case Kind.Decimal: Charge(16); return new JValue(reader.ReadDecimal());
                default: throw new InvalidDataException("Unknown binary snapshot token");
            }
        }
    }
}
