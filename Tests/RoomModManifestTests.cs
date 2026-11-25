using Newtonsoft.Json.Linq;
using ProbablyCoop;

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
        Console.WriteLine("PASS room mod preflight: version differences, optional consent, required extension, disabled mod, bounded metadata and local extras");
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
