using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

internal sealed class RoomModManifest
{
    internal const string Field = "joinMods";
    internal const int Limit = 128;
    internal const string OwnId = "probably_coop";

    internal enum Difference { Missing, Disabled, Version, Files, LocalOnly, Extension }

    internal sealed class Entry
    {
        internal readonly string Id, Name, Version, Digest;
        internal readonly ulong Workshop;
        internal readonly bool Enabled;
        internal Entry(string id, string name, string version, ulong workshop, bool enabled = true, string digest = "")
        { Id = id; Name = name; Version = version; Workshop = workshop; Enabled = enabled; Digest = digest; }
    }

    internal sealed class Row
    {
        internal readonly string Id, Name, HostVersion, LocalVersion, HostDigest, LocalDigest;
        internal readonly ulong Workshop;
        internal readonly Difference Kind;
        internal readonly string Blocker;
        internal Row(Entry host, Entry local, Difference kind, string blocker = null)
        {
            Id = (host ?? local).Id;
            Name = (host ?? local).Name;
            HostVersion = host?.Version;
            LocalVersion = local?.Version;
            HostDigest = host?.Digest;
            LocalDigest = local?.Digest;
            Workshop = host?.Workshop ?? 0;
            Kind = kind;
            Blocker = blocker;
        }

        internal bool Same(Row row) => Id == row.Id && Name == row.Name && HostVersion == row.HostVersion &&
            LocalVersion == row.LocalVersion && HostDigest == row.HostDigest && LocalDigest == row.LocalDigest &&
            Workshop == row.Workshop && Kind == row.Kind && Blocker == row.Blocker;
    }

    internal readonly Row[] Rows;
    internal bool Blocked => Rows.Any(row => row.Blocker != null);
    internal bool NeedsConsent => Rows.Any(row => row.Kind != Difference.LocalOnly);

    private RoomModManifest(IEnumerable<Row> rows) => Rows = rows.OrderByDescending(row => row.Blocker != null)
        .ThenBy(row => row.Kind).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();

    internal bool Same(RoomModManifest other) => other != null && other.Rows.Length == Rows.Length &&
        Rows.Where((row, index) => !row.Same(other.Rows[index])).Any() == false;

    internal static JArray Write(IEnumerable<Entry> mods) => new JArray(mods.Where(mod => mod.Enabled)
        .OrderBy(mod => mod.Id, StringComparer.Ordinal).Take(Limit)
        .Select(mod => new JArray(mod.Id, mod.Name, mod.Version, mod.Workshop, mod.Digest)));

    internal static Dictionary<string, Entry> Read(JToken value)
    {
        var found = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (value == null) return found;
        if (value is not JArray entries || entries.Count > Limit)
            throw new InvalidDataException("Invalid room mod list.");
        foreach (var valueRow in entries)
        {
            if (valueRow is not JArray row || row.Count != 5 || row[0].Type != JTokenType.String ||
                row[1].Type != JTokenType.String || row[2].Type != JTokenType.String || row[3].Type != JTokenType.Integer ||
                row[4].Type != JTokenType.String || !ModExtensionContract.IsToken((string)row[0]) ||
                !ulong.TryParse(row[3].ToString(), out ulong workshop))
                throw new InvalidDataException("Invalid room mod record.");
            string id = (string)row[0];
            string name = (string)row[1], version = (string)row[2], digest = (string)row[4];
            if (id == OwnId || name != Display(name, 96) || version != Display(version, 48) || digest != CanonicalDigest(digest) ||
                !found.TryAdd(id, new Entry(id, name, version, workshop, digest: digest)))
                throw new InvalidDataException("Duplicated or invalid room mod record.");
        }
        return found;
    }

    internal static RoomModManifest Compare(Dictionary<string, Entry> host, Dictionary<string, Entry> local,
        IReadOnlyDictionary<string, string> blockers)
    {
        var rows = new List<Row>();
        foreach (string id in host.Keys.Concat(local.Keys).Concat(blockers.Keys).Distinct(StringComparer.Ordinal))
        {
            host.TryGetValue(id, out var remote);
            local.TryGetValue(id, out var own);
            blockers.TryGetValue(id, out var blocker);
            if (remote != null && own == null) rows.Add(new Row(remote, null, Difference.Missing, blocker));
            else if (remote != null && !own.Enabled) rows.Add(new Row(remote, own, Difference.Disabled, blocker));
            else if (remote != null && remote.Version != own.Version) rows.Add(new Row(remote, own, Difference.Version, blocker));
            else if (remote != null && remote.Digest.Length > 0 && remote.Digest.Length == own.Digest.Length &&
                remote.Digest != own.Digest) rows.Add(new Row(remote, own, Difference.Files, blocker));
            else if (blocker != null)
                rows.Add(new Row(remote ?? (own == null ? new Entry(id, id, "", 0) : null), own, Difference.Extension, blocker));
            else if (remote == null && own?.Enabled == true) rows.Add(new Row(null, own, Difference.LocalOnly));
        }
        return new RoomModManifest(rows);
    }

    internal static string Display(string text, int max) => string.Concat((text ?? "").Where(c =>
        !char.IsControl(c) && !char.IsSurrogate(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)
        .Take(max)).Trim();

    internal static string CanonicalDigest(string hash) => hash?.Length is 64 or 128 &&
        hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F') ? hash.ToLowerInvariant() : "";
}
