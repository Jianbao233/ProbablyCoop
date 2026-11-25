using System.IO;
using Newtonsoft.Json.Linq;
using ProbablyCoop;
using ProbablyCoop.Api;

internal static class ModExtensionTests
{
    internal static void Run()
    {
        CheckSessionLifetime();
        CheckWireContract();
        Console.WriteLine("PASS extension v1 handles, action bounds, UTF-8 limits, and revision validation");
    }

    private static void CheckSessionLifetime()
    {
        var state = new CoopExtensionState(1, "open");
        bool ready = false;
        int requests = 0;
        var session = new CoopExtensionSession(CoopExtensionActor.Partner, () => ready, () => state,
            (_, _, _, _) =>
            {
                requests++;
                return CoopActionSubmission.Queued;
            });

        Check(!session.IsClosed && !session.IsReady && session.CurrentState == null,
            "a handle can be attached before its first projection");
        Check(session.Request("accept", "", 1) == CoopActionSubmission.Unavailable,
            "an attached but unready handle cannot submit");
        ready = true;
        Check(session.IsReady && ReferenceEquals(session.CurrentState, state) &&
            session.Request("accept", "", state.Revision) == CoopActionSubmission.Queued,
            "the same handle resumes when the shop is usable");

        Task.Run(() =>
        {
            Check(!session.IsReady && session.CurrentState == null &&
                session.Request("accept", "", 1) == CoopActionSubmission.Unavailable,
                "worker threads cannot access current state or submit");
        }).GetAwaiter().GetResult();

        session.Close();
        session.Close();
        Check(session.IsClosed && !session.IsReady && session.CurrentState == null &&
            session.Request("accept", "", 1) == CoopActionSubmission.Unavailable && requests == 1,
            "retirement is permanent and does not reach the callbacks");
    }

    private static void CheckWireContract()
    {
        var contract = CreateContract(new[] { "refresh", "accept" });
        var first = contract.Freeze(null, "open", false);
        var duplicate = contract.Freeze(first, "open", false);
        var committed = contract.Freeze(first, "open", true);
        Check(ReferenceEquals(duplicate, first) && first.Revision == 1 && committed.Revision == 2,
            "successful actions advance the revision even when state is unchanged");
        var row = contract.Write(committed);
        Check(((JArray)row["actions"]).Values<string>().SequenceEqual(new[] { "accept", "refresh" }) &&
            ReferenceEquals(contract.Read(row, committed), committed), "canonical action order and repeat projection");
        Reject(() => contract.Read(contract.Write(first), committed));
        row["payload"] = "replaced";
        Reject(() => contract.Read(row, committed));
        Reject(() => CreateContract(new[] { "accept", "accept" }));
        Reject(() => CreateContract(Array.Empty<string>()));
        _ = CreateContract(Enumerable.Range(0, 32).Select(i => "item-" + i));
        Reject(() => CreateContract(Enumerable.Range(0, 33).Select(i => "item-" + i)));
        string full = new string('水', 1365) + "x";
        _ = contract.Freeze(first, full, false);
        Reject(() => contract.Freeze(first, full + "x", false));
        Check(ModExtensionContract.IsText(new string('水', 341) + "x", CoopExtensionApi.MaximumActionBytes) &&
            !ModExtensionContract.IsText(new string('水', 342), CoopExtensionApi.MaximumActionBytes) &&
            !ModExtensionContract.IsText("\uD800", CoopExtensionApi.MaximumActionBytes),
            "UTF-8 bytes and invalid surrogate rejection match the public limits");
    }

    private static ModExtensionContract CreateContract(IEnumerable<string> actions) =>
        new ModExtensionContract("test_mod", "1.0", new CoopExtensionRegistration("test-v1", actions,
            () => "open", _ => CoopActionResult.Rejected, _ => { }, _ => { }));

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("extension v1 unexpectedly accepted invalid input");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("extension v1: " + description);
    }
}
