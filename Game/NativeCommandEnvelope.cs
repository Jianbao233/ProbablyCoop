using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal static class NativeCommandEnvelope
{
    private static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
    internal static bool TryRead(byte[] payload,out JObject command)
    {
        command=null;
        if(payload==null || payload.Length==0 || payload.Length>ReplicaLink.MaxCommand) return false;
        try
        {
            using var input=new StringReader(Utf8.GetString(payload));
            using var reader=new JsonTextReader(input) { MaxDepth=16, DateParseHandling=DateParseHandling.None };
            var value=JObject.Load(reader,new JsonLoadSettings { DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error });
            if(reader.Read() || value["kind"]?.Type!=JTokenType.String || ((string)value["kind"]).Length is 0 or >64) return false;
            command=value; return true;
        }
        catch(JsonException) { return false; }
        catch(DecoderFallbackException) { return false; }
    }
}
