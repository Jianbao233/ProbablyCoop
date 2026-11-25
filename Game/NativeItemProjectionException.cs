using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

internal sealed class NativeItemProjectionException : IOException
{
    internal readonly string Identifier, Provider;

    internal NativeItemProjectionException(JToken identifier, JToken provider, Exception error)
        : base("An item could not be applied to the guest scene", error)
    {
        Identifier = ReadToken(identifier);
        Provider = ReadToken(provider);
    }

    private static string ReadToken(JToken value)
    {
        if (value?.Type != JTokenType.String) return "-";
        string token = (string)value;
        return ModExtensionContract.IsToken(token) ? token : "-";
    }
}
