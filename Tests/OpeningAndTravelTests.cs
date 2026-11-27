using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop;
using ProbablyCoop.Api;
using ProbablyCoop.Core;

internal static class OpeningAndTravelTests
{
    internal static void Run()
    {
        VerifyOpeningQuotes();
        VerifyDeclaredCareers();
        VerifyDraftStream();
        VerifyIndependentSites();
        VerifyLeaveAfterOccupancyChanges();
        Console.WriteLine("PASS paired opening debt, draft replacement, independent visits, shared site lifetime and stale leave");
    }

    private static void VerifyLeaveAfterOccupancyChanges()
    {
        var travel = new TravelSiteLedger<object>();
        Check(travel.BeginNight(5), "start the return-confirmation night");
        Check(travel.TryEnter(0, travel.Where(0), "dump", () => new object(), out var ground),
            "first player creates the destination");
        Check(travel.TryPrepareLeave(0, travel.Where(0), out var oldPrompt) && oldPrompt.LastVisitor,
            "first player opens the final-visitor prompt");
        Check(travel.TryEnter(1, travel.Where(1), "dump", () => new object(), out var joined) && ReferenceEquals(ground, joined),
            "a late visitor shares the existing ground");
        Check(travel.TryPrepareLeave(1, travel.Where(1), out var leaving) && travel.TryLeave(1, leaving, false),
            "the late visitor leaves without clearing the ground");
        Check(!travel.TryLeave(0, oldPrompt, true) && ReferenceEquals(ground, travel.At("dump")),
            "the first prompt cannot authorize cleanup after another visit");
        Check(travel.TryPrepareLeave(0, travel.Where(0), out var newPrompt) && newPrompt.LastVisitor &&
            travel.TryLeave(0, newPrompt, true) && travel.At("dump") == null,
            "the fresh final-visitor prompt commits exactly once");
    }

    private static void VerifyOpeningQuotes()
    {
        Check(OpeningRentPlan.QuotedShortfall("water_merchant", "chemist", false) == 0, "both starting packages fit a base budget");
        Check(OpeningRentPlan.QuotedShortfall("gunsmith", "aquafarmer", false) == 300, "both expensive career fees count");
        Check(OpeningRentPlan.QuotedShortfall("gunsmith", "aquafarmer", true) == 150, "the saving perk credits once");
        Check(!OpeningRentPlan.CanCombine("rock_bottom", "chemist") &&
            OpeningRentPlan.CanCombine("rock_bottom", "rock_bottom"), "exclusive career");
        Check(OpeningRentPlan.Create(300, 4).Amounts.SequenceEqual(new[] { 30, 60, 90, 120 }), "default schedule");
        Check(OpeningRentPlan.Create(50, 6).Amounts.SequenceEqual(new[] { 2, 4, 7, 10, 12, 15 }), "rounding at small values");
        Check(OpeningRentPlan.Create(300, 0).Complete, "waiver");
        for (int missing = 0; missing <= 1200; missing++)
        for (int weeks = 1; weeks <= 6; weeks++)
        {
            var plan = OpeningRentPlan.Create(missing, weeks);
            Check(plan.Amounts.Sum() == missing && plan.Amounts.SequenceEqual(plan.Amounts.Order()), "retains amount and increasing order");
            int charged = 0;
            while (!plan.Complete)
            {
                plan = OpeningRentPlan.Restore(plan.Amounts, plan.Index);
                charged += plan.Active;
                plan = plan.Advance();
            }
            Check(charged == missing && plan.Active == 0 && plan.Future == 0, "reload and consume each installment exactly once");
        }
        foreach (var bad in new[] { new[] { 60, 30 }, new[] { -1, 1 }, new[] { 1, 1, 1, 1, 1, 1, 1 } })
        {
            try { _ = OpeningRentPlan.Restore(bad, 0); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Noncanonical rent schedule was accepted.");
        }
    }

    private static void VerifyDeclaredCareers()
    {
        var conflicts = new[] { "chemist" };
        var career = new CoopOpeningCareer("example_brewer", 375, conflicts);
        conflicts[0] = "water_merchant";
        var chemist = new CoopOpeningCareer("chemist", 300);
        var water = new CoopOpeningCareer("water_merchant", 200);
        Check(career.ConflictsWith(chemist) && chemist.ConflictsWith(career) && !career.ConflictsWith(water),
            "either catalog can declare an exclusion and the declaration is immutable");
        Check(!OpeningRentPlan.CanCombineIds("rock_bottom", career.Id) &&
            OpeningRentPlan.CanCombineIds(career.Id, water.Id), "native exclusive opening keeps its rules");
        Check(OpeningRentPlan.QuotedShortfall(career.CashCost, chemist.CashCost, false) == 75 &&
            OpeningRentPlan.QuotedShortfall(career.CashCost, 0, false) == 0,
            "declared costs participate in the existing rent formula");
        foreach (Action invalid in new Action[]
        {
            () => _ = new CoopOpeningCareer("invalid career", 0),
            () => _ = new CoopOpeningCareer("career", 1_000_001),
            () => _ = new CoopOpeningCareer("career", 0, new[] { "career" })
        })
        {
            try { invalid(); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Accepted an invalid opening career declaration.");
        }
    }

    private static void VerifyDraftStream()
    {
        var id = Guid.NewGuid();
        var menu = new NativeSnapshotFrame(new[] { NativeSnapshotCodec.Pack(new JObject
        {
            ["openingDraftV1"] = new JObject { ["id"] = id.ToString("D"), ["career"] = "water_merchant", ["weeks"] = 4 }
        }) });
        var menuContract = NativeJoinSnapshot.Read(menu, null, 0);
        Check(menuContract.Run == Guid.Empty && !menuContract.Required, "store cannot open from a menu draft");
        var store = new NativeSnapshotFrame(new[] { NativeSnapshotCodec.Pack(new JObject
        {
            ["actors"] = new JObject { ["run"] = id.ToString("D") }, ["partnerSetupRequired"] = false
        }) });
        var nativeContract = NativeJoinSnapshot.Read(store, menu, 1);
        Check(nativeContract.Run == id && !nativeContract.Required, "first store checkpoint replaces a completed menu draft");
    }

    private static void VerifyIndependentSites()
    {
        var travel = new TravelSiteLedger<object>();
        int created = 0;
        object NewSite() { created++; return new object(); }

        Check(travel.BeginNight(5), "start a night");
        var start0 = travel.Where(0);
        var start1 = travel.Where(1);
        Check(travel.TryEnter(0, start0, "dump", NewSite, out var ground), "first actor enters");
        Check(travel.VisitsLeft(0) == 0 && travel.VisitsLeft(1) == 1, "only the first actor is charged");
        Check(travel.TryEnter(1, start1, "engineer", NewSite, out var store), "second actor may go elsewhere");
        Check(!ReferenceEquals(ground, store) && created == 2 && !travel.BeginNight(6), "distinct sites block rollover");
        Check(travel.TryPrepareLeave(0, travel.Where(0), out var soloExit) &&
            !travel.TryLeave(0, soloExit, false) && travel.TryLeave(0, soloExit, true), "final exit waits for native confirmation");
        Check(!travel.TryEnter(0, start0, "dump", NewSite, out _) &&
            !travel.TryEnter(0, travel.Where(0), "dump", NewSite, out _), "stale epoch and second visit are rejected");
        Check(travel.TryPrepareLeave(1, travel.Where(1), out var secondExit) && travel.TryLeave(1, secondExit, true) &&
            travel.BeginNight(6), "next night after both return");

        Check(travel.TryEnter(0, travel.Where(0), "food", NewSite, out var food), "first actor generates food stock");
        Check(travel.TryPrepareLeave(0, travel.Where(0), out var wasLast), "snapshot of first leave");
        Check(travel.TryEnter(1, travel.Where(1), "food", NewSite, out var shared) &&
            ReferenceEquals(food, shared) && created == 3, "second actor joins existing stock");
        Check(!travel.TryLeave(0, wasLast, true) &&
            travel.TryPrepareLeave(0, travel.Where(0), out var noLongerLast) && !noLongerLast.LastVisitor &&
            travel.TryLeave(0, noLongerLast, false) && ReferenceEquals(food, travel.At("food")),
            "arrival invalidates cleanup authorization and keeps stock");
        Check(travel.TryPrepareLeave(1, travel.Where(1), out var last) && last.LastVisitor &&
            travel.TryLeave(1, last, true) && travel.At("food") == null, "only final departure ends the stock lifetime");

        var paired = travel.ObservePaired(7, "dump", NewSite);
        Check(ReferenceEquals(paired, travel.ObservePaired(7, "dump", NewSite)) && created == 4, "paired map observes the same stock");
        ulong generation = travel.Where(0).Generation;
        travel.ObservePaired(7, "", NewSite);
        Check(travel.EveryoneHome && travel.Where(0).Generation != generation, "paired return retires location requests");

        var reloaded = new TravelSiteLedger<object>();
        Check(reloaded.BeginNight(7, partnerAlreadyVisited: true) && reloaded.EveryoneHome &&
            reloaded.VisitsLeft(0) == 1 && reloaded.VisitsLeft(1) == 0 &&
            !reloaded.TryEnter(1, reloaded.Where(1), "dump", NewSite, out _),
            "a same-day reload restores only the recorded partner visit");
        Check(reloaded.BeginNight(7, hostAlreadyVisited: true) && reloaded.BeginNight(7) &&
            reloaded.VisitsLeft(0) == 0 && reloaded.VisitsLeft(1) == 0,
            "reinitializing the same night never refunds a recorded visit");
        Check(reloaded.BeginNight(8) && reloaded.VisitsLeft(0) == 1 && reloaded.VisitsLeft(1) == 1,
            "the next night restores both independent allowances");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Opening and travel: " + message);
    }
}
