using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

internal sealed class RoomModManifest
{
    internal const string Field = "joinMods";
    internal const string OpeningField = "openingModsRequired";
    internal const int OpeningLimit = 16;
    internal const int Limit = 128;
    internal const string OwnId = "probably_coop";

    internal enum Difference { Missing, Disabled, LoadFailed, Version, Files, LocalOnly, Extension }
    internal enum WorkshopProgress { NotSubscribed, PendingDownload, Downloading, WaitingForDownload, RestartRequired }

    internal static WorkshopProgress? ReadWorkshopProgress(Difference difference, bool subscribed, bool installed,
        bool pending, bool needsUpdate = false, bool downloading = false)
    {
        if (difference is not (Difference.Missing or Difference.Version or Difference.Files)) return null;
        if (!subscribed) return difference == Difference.Missing ? WorkshopProgress.NotSubscribed : null;
        if (downloading) return WorkshopProgress.Downloading;
        if (pending) return WorkshopProgress.PendingDownload;
        if (!installed || needsUpdate) return WorkshopProgress.WaitingForDownload;
        // An installed version or file mismatch still needs the correct package.
        return difference == Difference.Missing ? WorkshopProgress.RestartRequired : null;
    }

    internal static int? WorkshopDownloadPercent(ulong received, ulong total) =>
        total == 0 ? null : received >= total ? 100 : Math.Min(99, (int)(100.0 * received / total));

    internal sealed class Entry
    {
        internal readonly string Id, Name, Version, Digest;
        internal readonly ulong Workshop;
        internal readonly bool Enabled;
        internal readonly bool LoadFailed;
        internal readonly ulong ShadowedWorkshop;
        internal Entry(string id, string name, string version, ulong workshop, bool enabled = true, string digest = "",
            bool loadFailed = false, ulong shadowedWorkshop = 0)
        {
            Id = id; Name = name; Version = version; Workshop = workshop; Enabled = enabled; Digest = digest;
            LoadFailed = loadFailed; ShadowedWorkshop = shadowedWorkshop;
        }
    }

    internal sealed class Row
    {
        internal readonly string Id, Name, HostVersion, LocalVersion, HostDigest, LocalDigest;
        internal readonly ulong Workshop;
        internal readonly Difference Kind;
        internal readonly string Blocker;
        internal readonly bool LocalOverridesWorkshop;
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
            LocalOverridesWorkshop = Workshop != 0 && local?.ShadowedWorkshop == Workshop;
        }

        internal bool Same(Row row) => Id == row.Id && Name == row.Name && HostVersion == row.HostVersion &&
            LocalVersion == row.LocalVersion && HostDigest == row.HostDigest && LocalDigest == row.LocalDigest &&
            Workshop == row.Workshop && Kind == row.Kind && Blocker == row.Blocker &&
            LocalOverridesWorkshop == row.LocalOverridesWorkshop;

        internal string Diagnostic() => $"id={Id} host={DiagnosticVersion(HostVersion)} local={DiagnosticVersion(LocalVersion)} " +
            $"difference={Kind} blocker={Blocker ?? "-"} local_override={(LocalOverridesWorkshop ? "yes" : "no")}";
    }

    internal readonly Row[] Rows;
    internal bool Blocked => Rows.Any(row => row.Blocker != null);
    internal bool NeedsConsent => Rows.Any(row => row.Kind != Difference.LocalOnly);

    private RoomModManifest(IEnumerable<Row> rows) => Rows = rows.OrderByDescending(row => row.Blocker != null)
        .ThenBy(row => row.Kind).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();

    internal bool Same(RoomModManifest other) => other != null && other.Rows.Length == Rows.Length &&
        Rows.Where((row, index) => !row.Same(other.Rows[index])).Any() == false;

    // The loader keeps the associated Workshop ID when a local copy wins.
    // It is a link for discovery; the version and digest describe the active copy.
    internal static JArray Write(IEnumerable<Entry> mods) => new JArray(mods.Where(mod => mod.Enabled)
        .OrderBy(mod => mod.Id, StringComparer.Ordinal).Take(Limit)
        .Select(mod => new JArray(mod.Id, mod.Name, mod.Version,
            mod.Workshop != 0 ? mod.Workshop : mod.ShadowedWorkshop, mod.Digest)));

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

    internal static Dictionary<string, Entry> ReadOpeningPackages(JToken value)
    {
        if (value == null) return null;
        if (value is not JArray rows || rows.Count is < 1 or > OpeningLimit)
            throw new InvalidDataException("Invalid required opening Mod list.");
        var result = Read(rows);
        if (result.Values.Any(mod => mod.Version.Length == 0))
            throw new InvalidDataException("Required opening Mod is missing its version.");
        return result;
    }

    internal static bool ExactPackage(Entry host, Entry local) =>
        host != null && local != null && host.Enabled && local.Enabled && host.Id == local.Id &&
        host.Version.Length > 0 && host.Version == local.Version && host.Digest == local.Digest;

    internal static RoomModManifest Compare(Dictionary<string, Entry> host, Dictionary<string, Entry> local,
        IReadOnlyDictionary<string, string> blockers, IReadOnlyDictionary<string, Entry> requiredOpening = null,
        bool openingChoices = false)
    {
        var rows = new List<Row>();
        foreach (string id in host.Keys.Concat(local.Keys).Concat(blockers.Keys)
            .Concat(requiredOpening?.Keys ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal))
        {
            host.TryGetValue(id, out var remote);
            local.TryGetValue(id, out var own);
            blockers.TryGetValue(id, out var blocker);
            if (requiredOpening != null && requiredOpening.TryGetValue(id, out var required))
            {
                remote = required; // Also covers a required provider beyond the general room list limit.
                if (!ExactPackage(required, own)) blocker ??= "sync_opening_mod_required";
            }
            if (remote != null && own == null) rows.Add(new Row(remote, null, Difference.Missing, blocker));
            else if (remote != null && own.LoadFailed) rows.Add(new Row(remote, own, Difference.LoadFailed, blocker));
            else if (remote != null && !own.Enabled) rows.Add(new Row(remote, own, Difference.Disabled, blocker));
            else if (remote != null && remote.Version != own.Version) rows.Add(new Row(remote, own, Difference.Version, blocker));
            else if (remote != null && remote.Digest != own.Digest &&
                (openingChoices || DifferentKnownFiles(remote.Digest, own.Digest)))
                rows.Add(new Row(remote, own, Difference.Files, blocker));
            else if (blocker != null)
                rows.Add(new Row(remote ?? (own == null ? new Entry(id, id, "", 0) : null), own, Difference.Extension, blocker));
            else if (!openingChoices && remote == null && own?.Enabled == true) rows.Add(new Row(null, own, Difference.LocalOnly));
        }
        return new RoomModManifest(rows);
    }

    private static bool DifferentKnownFiles(string host, string local)
    {
        // The second hash covers extra files and can be absent if the native
        // loader could only fingerprint the manifest and assemblies.
        int common = Math.Min(host.Length, local.Length);
        return common != 0 && !host.AsSpan(0, common).SequenceEqual(local.AsSpan(0, common));
    }

    internal static string Display(string text, int max) => string.Concat((text ?? "").Where(c =>
        !char.IsControl(c) && !char.IsSurrogate(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)
        .Take(max)).Trim();

    internal static string DiagnosticVersion(string text)
    {
        if (string.IsNullOrEmpty(text)) return "-";
        return text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '.' or '-' or '_' or '+')
            ? text : "(hidden)";
    }

    internal static string CanonicalDigest(string hash) => hash?.Length is 64 or 128 &&
        hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F') ? hash.ToLowerInvariant() : "";
}
