using Newtonsoft.Json.Linq;
using ProbablyCoop;
using ProbablyCoop.Core;

internal static class RoomModManifestTests
{
    private static RoomModManifest.Entry Mod(string id, string version, bool enabled = true, ulong workshop = 0) =>
        new RoomModManifest.Entry(id, id, version, workshop, enabled);

    internal static void Run()
    {
        var host = RoomModManifest.Read(RoomModManifest.Write(new[]
        {
            Mod("chem", "1.4", workshop: 83), Mod("container", "2.1"), Mod("disabled", "1"),
            Mod("identical", "4"), Mod("ignored", "1", false)
        }));
        var local = new[] { Mod("container", "2.0"), Mod("local_sound", "3"), Mod("identical", "4"),
            Mod("disabled", "1", false) }.ToDictionary(mod => mod.Id);
        var optional = RoomModManifest.Compare(host, local, new Dictionary<string,string>());
        Require(optional.NeedsConsent && !optional.Blocked && host.Count == 4);
        Require(optional.Rows.Single(row => row.Id == "chem").Workshop == 83);
        Require(optional.Rows.Single(row => row.Id == "disabled").Kind == RoomModManifest.Difference.Disabled);
        Require(!RoomModManifest.Compare(RoomModManifest.Read(null), local, new Dictionary<string,string>()).NeedsConsent);
        var blocked = RoomModManifest.Compare(host, local, new Dictionary<string,string> { ["container"] = "sync_extension_mismatch" });
        Require(blocked.Blocked && blocked.Rows[0].Id == "container" && !blocked.Same(optional));
        Require(optional.Same(RoomModManifest.Compare(host, local, new Dictionary<string,string>())));
        Require(RoomModManifest.Display("bad\r\nname\u202E", 96) == "badname");
        Reject(new JArray(new JArray("dup","Name","v1",1,""), new JArray("dup","Another","v2",2,"")));
        Reject(new JArray(new JArray("fake\nlog","Name","1",0,"")));
        Reject(new JArray(new JArray("valid","A\nName","1",0,"")));
        Reject(new JArray(new JArray("valid","Name","1",0,"bad_digest")));
        var first = new RoomModManifest.Entry("files", "Files", "1", 0, digest: new string('a', 64));
        var edited = new RoomModManifest.Entry("files", "Files", "1", 0, digest: new string('b', 64));
        var files = RoomModManifest.Compare(RoomModManifest.Read(RoomModManifest.Write(new[] { first })),
            new[] { edited }.ToDictionary(mod => mod.Id), new Dictionary<string,string>());
        Require(files.NeedsConsent && files.Rows.Single().Kind == RoomModManifest.Difference.Files);
        VerifyLocalFileAndLoadIssues();
        VerifyOpeningRequirements(host, local);
        VerifyAvailableOpeningChoices();
        VerifyWorkshopProgress();
        VerifyCopiedJoin();
        VerifyModDiagnostics();
        var samePackage = new RoomModManifest.Entry("files", "Localized listing", "1", 123, digest: first.Digest);
        Require(RoomModManifest.ExactPackage(first, samePackage));
        Require(!RoomModManifest.ExactPackage(first, edited));
        Require(!RoomModManifest.ExactPackage(first, Mod("files", "1", enabled: false)));
        Console.WriteLine("PASS room mod preflight: opening choices, optional consent, required extensions, download progress and expiring join resume");
    }

    private static void VerifyModDiagnostics()
    {
        string samplePath = System.IO.Path.GetFullPath("sample-build");
        var host = Enumerable.Range(0, 36).Select(i => Mod($"expansion_{i:00}", "1.2.3")).ToDictionary(row => row.Id);
        var local = new Dictionary<string, RoomModManifest.Entry>
        {
            ["expansion_00"] = new("expansion_00", "User name <tag>", samplePath, 0, digest: new string('a', 64)),
            ["local_music"] = Mod("local_music", "2.0-beta+test")
        };
        var report = RoomModManifest.Compare(host, local,
            new Dictionary<string, string> { ["expansion_35"] = "sync_extension_missing_guest" });
        var lines = report.Rows.Select(row => row.Diagnostic()).ToArray();
        Require(lines.Length == 37 && lines.Any(line => line.StartsWith("id=expansion_34 ", StringComparison.Ordinal)));
        Require(lines[0] == "id=expansion_35 host=1.2.3 local=- difference=Missing blocker=sync_extension_missing_guest local_override=no");
        Require(lines.Any(line => line.Contains("id=expansion_00 host=1.2.3 local=(hidden) difference=Version")));
        Require(lines.Any(line => line.Contains("id=local_music host=- local=2.0-beta+test difference=LocalOnly")));
        Require(lines.All(line => !line.Contains(samplePath) && !line.Contains("<tag>") && !line.Contains(new string('a', 64))));
    }

    private static void VerifyLocalFileAndLoadIssues()
    {
        const ulong workshop = 90;
        var manifestHash = new string('a', 64);
        var folderHash = new string('b', 64);
        var differentHash = new string('c', 64);
        var host = new Dictionary<string, RoomModManifest.Entry>
        {
            ["chem"] = new("chem", "Chemistry", "1", workshop, digest: manifestHash + folderHash)
        };
        RoomModManifest Compare(RoomModManifest.Entry local) => RoomModManifest.Compare(host,
            new Dictionary<string, RoomModManifest.Entry> { ["chem"] = local }, new Dictionary<string, string>());

        var partial = new RoomModManifest.Entry("chem", "Chemistry", "1", 0, digest: manifestHash);
        Require(!Compare(partial).NeedsConsent);
        var shadowed = new RoomModManifest.Entry("chem", "Chemistry", "1", 0,
            digest: differentHash, shadowedWorkshop: workshop);
        var optional = Compare(shadowed);
        Require(optional.NeedsConsent && !optional.Blocked &&
            optional.Rows.Single().Kind == RoomModManifest.Difference.Files && optional.Rows.Single().LocalOverridesWorkshop);
        var shadowedHost = RoomModManifest.Read(RoomModManifest.Write(new[] { shadowed }));
        var published = shadowedHost["chem"];
        Require(published.Workshop == workshop && published.Digest == shadowed.Digest && published.Version == shadowed.Version);
        Require(published.ShadowedWorkshop == 0 && !published.LoadFailed && !optional.Same(Compare(published)));
        var unknown = RoomModManifest.Read(RoomModManifest.Write(new[] { partial }))["chem"];
        Require(unknown.Workshop == 0);
        var cleanInstall = new RoomModManifest.Entry("chem", "Chemistry", "1", workshop,
            digest: manifestHash + folderHash, shadowedWorkshop: workshop + 1);
        Require(RoomModManifest.Read(RoomModManifest.Write(new[] { cleanInstall }))["chem"].Workshop == workshop);
        var missingGuest = RoomModManifest.Compare(shadowedHost, new Dictionary<string, RoomModManifest.Entry>(),
            new Dictionary<string, string>());
        Require(missingGuest.Rows.Single().Workshop == workshop &&
            missingGuest.Rows.Single().Kind == RoomModManifest.Difference.Missing);
        var installedGuest = RoomModManifest.Compare(shadowedHost,
            new Dictionary<string, RoomModManifest.Entry> { ["chem"] = cleanInstall },
            new Dictionary<string, string>(), shadowedHost);
        Require(installedGuest.Blocked && installedGuest.Rows.Single().Kind == RoomModManifest.Difference.Files);
        var failed = new RoomModManifest.Entry("chem", "Chemistry", "1", workshop, enabled: false, loadFailed: true);
        var required = RoomModManifest.Compare(host, new Dictionary<string, RoomModManifest.Entry> { ["chem"] = failed },
            new Dictionary<string, string>(), host);
        Require(required.Blocked && required.Rows.Single().Kind == RoomModManifest.Difference.LoadFailed &&
            required.Rows.Single().Blocker == "sync_opening_mod_required");
        Console.WriteLine("PASS Mod check distinguishes partial fingerprints, startup errors and local Workshop overrides");
    }

    private static void VerifyAvailableOpeningChoices()
    {
        var provider = new RoomModManifest.Entry("brewer", "Brewing tools", "2.0", 27, digest: new string('a', 64));
        var host = new Dictionary<string, RoomModManifest.Entry> { [provider.Id] = provider };
        var local = new Dictionary<string, RoomModManifest.Entry>
        {
            [provider.Id] = Mod(provider.Id, provider.Version),
            ["local_sound"] = Mod("local_sound", "1")
        };
        var blockers = new Dictionary<string, string>();
        var report = RoomModManifest.Compare(host, local, blockers, openingChoices: true);
        Require(!report.Blocked && report.Rows.Single().Kind == RoomModManifest.Difference.Files &&
            report.Rows.Single().Workshop == 27);
        local[provider.Id] = Mod(provider.Id, provider.Version, enabled: false);
        Require(RoomModManifest.Compare(host, local, blockers, openingChoices: true).Rows.Single().Kind ==
            RoomModManifest.Difference.Disabled);
        local[provider.Id] = provider;
        Require(RoomModManifest.Compare(host, local, blockers, openingChoices: true).Rows.Length == 0);
        Require(RoomModManifest.Compare(host, local, blockers).Rows.Single().Kind == RoomModManifest.Difference.LocalOnly);
    }

    private static void VerifyOpeningRequirements(Dictionary<string, RoomModManifest.Entry> host,
        Dictionary<string, RoomModManifest.Entry> local)
    {
        var requirements = RoomModManifest.ReadOpeningPackages(RoomModManifest.Write(new[] {
            host["chem"], new RoomModManifest.Entry("brewer", "Brewing tools", "2.0", 27, digest: new string('a', 64))
        }));
        local["brewer"] = new RoomModManifest.Entry("brewer", "Brewing tools", "2.0", 27, digest: new string('b', 64));
        var blockers = new Dictionary<string, string>();
        var denied = RoomModManifest.Compare(host, local, blockers, requirements);
        Require(denied.Blocked && denied.Rows.Single(row => row.Id == "chem").Blocker == "sync_opening_mod_required" &&
            denied.Rows.Single(row => row.Id == "brewer").Workshop == 27 &&
            denied.Rows.Single(row => row.Id == "brewer").Kind == RoomModManifest.Difference.Files);
        var fixedLocal = new Dictionary<string, RoomModManifest.Entry>(local)
        {
            ["brewer"] = requirements["brewer"], ["chem"] = requirements["chem"]
        };
        var permitted = RoomModManifest.Compare(host, fixedLocal, blockers, requirements);
        Require(!permitted.Blocked && permitted.NeedsConsent &&
            permitted.Rows.All(row => row.Id != "brewer" && row.Id != "chem"));
        local.Remove("brewer");
    }

    private static void VerifyWorkshopProgress()
    {
        var missing = RoomModManifest.Difference.Missing;
        var version = RoomModManifest.Difference.Version;
        var files = RoomModManifest.Difference.Files;
        Require(RoomModManifest.ReadWorkshopProgress(missing, false, false, false) == RoomModManifest.WorkshopProgress.NotSubscribed);
        Require(RoomModManifest.ReadWorkshopProgress(missing, true, false, false) == RoomModManifest.WorkshopProgress.WaitingForDownload);
        Require(RoomModManifest.ReadWorkshopProgress(missing, true, false, true) == RoomModManifest.WorkshopProgress.PendingDownload);
        Require(RoomModManifest.ReadWorkshopProgress(missing, true, true, true) == RoomModManifest.WorkshopProgress.PendingDownload);
        Require(RoomModManifest.ReadWorkshopProgress(missing, true, true, false) == RoomModManifest.WorkshopProgress.RestartRequired);
        Require(RoomModManifest.ReadWorkshopProgress(version, true, true, true) == RoomModManifest.WorkshopProgress.PendingDownload);
        Require(RoomModManifest.ReadWorkshopProgress(version, true, true, false, needsUpdate: true) ==
            RoomModManifest.WorkshopProgress.WaitingForDownload);
        Require(RoomModManifest.ReadWorkshopProgress(version, true, true, true, needsUpdate: true) ==
            RoomModManifest.WorkshopProgress.PendingDownload);
        Require(RoomModManifest.ReadWorkshopProgress(version, true, true, false, downloading: true) ==
            RoomModManifest.WorkshopProgress.Downloading);
        Require(RoomModManifest.ReadWorkshopProgress(version, true, true, true, needsUpdate: true, downloading: true) ==
            RoomModManifest.WorkshopProgress.Downloading);
        Require(RoomModManifest.ReadWorkshopProgress(files, true, true, false) == null);
        Require(RoomModManifest.ReadWorkshopProgress(RoomModManifest.Difference.Disabled, true, true, true) == null);
        Require(RoomModManifest.WorkshopDownloadPercent(0, 0) == null);
        Require(RoomModManifest.WorkshopDownloadPercent(2, 3) == 66);
        Require(RoomModManifest.WorkshopDownloadPercent(ulong.MaxValue - 1, ulong.MaxValue) == 99);
        Require(RoomModManifest.WorkshopDownloadPercent(11, 10) == 100);
    }

    private static void VerifyCopiedJoin()
    {
        string code = "PSI-127.0.0.1-28015-" + Guid.NewGuid().ToString("N");
        string endpoint = "203.0.113.10:34670";
        Require(DirectRoomEndpoint.TryReadMappedJoin(code + "\r\n" + endpoint, out var restored, out var mapping) &&
            restored == code && mapping == endpoint);
        Require(!DirectRoomEndpoint.TryReadMappedJoin(code, out _, out _));
        Require(!DirectRoomEndpoint.TryReadMappedJoin(code + "\n127.0.0.1:28015", out _, out _));
        Require(!DirectRoomEndpoint.TryReadMappedJoin(code + "\n" + endpoint + "\nextra", out _, out _));
        Require(!DirectRoomEndpoint.TryReadMappedJoin("PSL-28015-" + Guid.NewGuid().ToString("N") + "\n" + endpoint, out _, out _));
        VerifyResumeRecord(code, endpoint);
    }

    private static void VerifyResumeRecord(string code, string endpoint)
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var stored = RoomJoinResume.Create(code + "\n" + endpoint, now).Write();
        var restored = RoomJoinResume.Read(stored, now.AddMinutes(59));
        Require(restored.Code == code && restored.Mapping == endpoint && restored.IsCurrent(now.AddMinutes(59)));
        Require(!restored.IsCurrent(now.AddHours(1)) && RoomJoinResume.Read(stored, now.AddHours(1)) == null);
        Require(RoomJoinResume.Read(stored, now.AddSeconds(-1)) == null);
        Require(RoomJoinResume.Read(stored + "\nextra", now) == null);
        Require(RoomJoinResume.Read("-1\n" + code, now) == null);
        Require(RoomJoinResume.Read(new string('9', 129), now) == null);
        string steam = RoomCode.Encode(1UL << 56);
        var steamResume = RoomJoinResume.Read(RoomJoinResume.Create(steam.ToLowerInvariant(), now).Write(), now);
        Require(steamResume.Code == steam && steamResume.Mapping == null);
        string local = "PSL-28015-" + Guid.NewGuid().ToString("N");
        Require(RoomJoinResume.Read(RoomJoinResume.Create(local, now).Write(), now)?.Code == local);
        Require(RoomJoinResume.Create(code + "\n127.0.0.1:28015", now) == null);
    }

    private static void Reject(JArray rows)
    {
        try { RoomModManifest.Read(rows); }
        catch (InvalidDataException) { return; }
        throw new Exception("Accepted malformed room mod list");
    }

    private static void Require(bool success)
    {
        if (!success) throw new Exception("Unexpected room mod review");
    }
}
