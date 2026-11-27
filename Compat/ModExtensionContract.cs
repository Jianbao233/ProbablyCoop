using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Api;

namespace ProbablyCoop;

internal sealed class ModExtensionJoinException : IOException
{
    internal string MessageKey { get; }
    internal string ModId { get; }

    internal ModExtensionJoinException(string key,string id,string message,Exception inner=null)
        : base(message+": "+id,inner)
    {
        MessageKey=key;
        ModId=id;
    }
}

internal sealed class ModExtensionProjectionException : IOException
{
    internal string ModId { get; }

    internal ModExtensionProjectionException(string modId, Exception error)
        : base("A cooperative extension failed to apply the host state", error)
    {
        ModId = modId;
    }
}

internal sealed class ModExtensionActionFailure
{
    internal const string Code = "MOD-101";
    internal string Context { get; }

    internal ModExtensionActionFailure(string modId, string actionId, Exception error)
    {
        string errorType = error.GetType().Name;
        Context = $"mod={SafeToken(modId)} action={SafeToken(actionId)} error={SafeToken(errorType)}";
    }

    private static string SafeToken(string value) => ModExtensionContract.IsToken(value) ? value : "unknown";
}

internal sealed class ModExtensionContract
{
    internal const int MaxExtensions = 16;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false,true);
    internal readonly string Id, PackageVersion, Protocol;
    internal readonly string[] Actions;

    internal ModExtensionContract(string id,string version,CoopExtensionRegistration registration)
    {
        if(!IsToken(id) || !IsText(version,64) || string.IsNullOrWhiteSpace(version) || !IsToken(registration.Protocol))
            throw new InvalidDataException("Invalid cooperative extension identity: " + id);
        if(registration.Actions.Length is 0 or >32 || registration.Actions.Any(a => !IsToken(a)) ||
            registration.Actions.Distinct(StringComparer.Ordinal).Count()!=registration.Actions.Length)
            throw new InvalidDataException("Invalid cooperative extension actions: " + id);
        Id = id;
        PackageVersion = version;
        Protocol = registration.Protocol;
        Actions = registration.Actions.OrderBy(a => a,StringComparer.Ordinal).ToArray();
    }

    internal bool HasAction(string action) => Array.BinarySearch(Actions,action,StringComparer.Ordinal)>=0;
    internal static bool IsToken(string token) => token?.Length is >=1 and <=64 && token.All(c =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.');

    internal static bool IsText(string text,int limit)
    {
        if(text==null || text.Length>limit) return false;
        try { return Utf8.GetByteCount(text)<=limit; }
        catch(EncoderFallbackException) { return false; }
    }

    internal CoopExtensionState Freeze(CoopExtensionState previous,string data,bool forceRevision)
    {
        if(!IsText(data,CoopExtensionApi.MaximumStateBytes))
            throw new InvalidDataException("Invalid or too large cooperative extension state: " + Id);
        if(!forceRevision && previous?.Payload==data) return previous;
        return new CoopExtensionState(checked((previous?.Revision ?? 0)+1),data);
    }

    internal CoopExtensionState VerifyActionResult(CoopExtensionState before, string after, string action, CoopActionResult result)
    {
        if (result == CoopActionResult.Applied) return Freeze(before, after, true);
        if (result is not (CoopActionResult.Rejected or CoopActionResult.Stale or CoopActionResult.Busy))
            throw new InvalidOperationException($"Cooperative extension {Id} returned an invalid result for {action}.");
        if (!string.Equals(before.Payload, after, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cooperative extension {Id} changed shared state for {action} but returned {result}.");
        return before;
    }

    internal JObject Write(CoopExtensionState state) => new JObject {
        ["id"] = Id, ["version"] = PackageVersion, ["protocol"] = Protocol,
        ["actions"] = new JArray(Actions), ["revision"] = state.Revision, ["payload"] = state.Payload
    };

    internal CoopExtensionState Read(JObject row,CoopExtensionState previous)
    {
        if(row.Count!=6 || row["version"]?.Type!=JTokenType.String || row["protocol"]?.Type!=JTokenType.String ||
            (string)row["version"]!=PackageVersion || (string)row["protocol"]!=Protocol ||
            row["actions"] is not JArray actions || actions.Count!=Actions.Length ||
            actions.Where((a,i) => a.Type!=JTokenType.String || (string)a!=Actions[i]).Any())
            throw new InvalidDataException("Cooperative extension version or actions differ: " + Id +
                " (local " + PackageVersion + ", protocol " + Protocol + ")");
        if(row["revision"]?.Type!=JTokenType.Integer || !ulong.TryParse(row["revision"].ToString(),out ulong revision) ||
            revision==0 || (previous!=null && revision<previous.Revision) || row["payload"]?.Type!=JTokenType.String)
            throw new InvalidDataException("Invalid cooperative extension revision: " + Id);
        string data = (string)row["payload"];
        if(!IsText(data,CoopExtensionApi.MaximumStateBytes) ||
            (previous?.Revision==revision && data!=previous.Payload))
            throw new InvalidDataException("Invalid cooperative extension state: " + Id);
        return previous?.Revision==revision ? previous : new CoopExtensionState(revision,data);
    }

    internal static Dictionary<string,JObject> ReadRows(JToken token)
    {
        if(token is not JArray array || array.Count>MaxExtensions)
            throw new InvalidDataException("Invalid cooperative extension list");
        var rows = new Dictionary<string,JObject>(StringComparer.Ordinal);
        foreach(var value in array)
            if(value is not JObject row || row["id"]?.Type!=JTokenType.String || !IsToken((string)row["id"]) ||
                !rows.TryAdd((string)row["id"],row))
                throw new InvalidDataException("Invalid or duplicate cooperative extension identity");
        return rows;
    }
}
