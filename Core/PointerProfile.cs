using System;
using System.Globalization;
using System.Text;

namespace ProbablyCoop.Core;

internal static class PointerProfile
{
    internal const int NameLimit=24;
    internal const int NameBytes=NameLimit*3;
    private static readonly Encoding Utf8=new UTF8Encoding(false,true);

    internal static string NormalizeName(string value)
    {
        value=(value ?? "").Trim();
        var name=new StringBuilder(Math.Min(value.Length,NameLimit));
        for(int i=0;i<value.Length && name.Length<NameLimit;i++)
        {
            char current=value[i];
            if(char.IsHighSurrogate(current) && i+1<value.Length && char.IsLowSurrogate(value[i+1]))
            {
                if(name.Length+2>NameLimit) break;
                name.Append(current).Append(value[++i]);
            }
            else if(current is not ('<' or '>') && CharUnicodeInfo.GetUnicodeCategory(current) is not
                (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or
                 UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate))
                name.Append(current);
        }
        return name.ToString().Trim();
    }

    internal static byte[] Encode(string name)=>Utf8.GetBytes(name);

    internal static bool TryRead(byte[] bytes,out string name)
    {
        name=null;
        if(bytes==null || bytes.Length>NameBytes) return false;
        try { name=Utf8.GetString(bytes); }
        catch(DecoderFallbackException) { return false; }
        return name==NormalizeName(name);
    }
}
