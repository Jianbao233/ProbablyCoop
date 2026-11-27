using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

internal static class PartnerSetupTests
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static void Reject<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { checks++; return; } throw new Exception("Expected rejection: " + name); }
    private static PlayerStore Store()
    {
        var store = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        store.saveSlotId = 3; store.runID = Guid.NewGuid().ToString("D"); store.playerCash = 900;
        var state = NativeActorState.CreateDefault();
        foreach (var field in typeof(NativeActorState).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            typeof(PlayerStore).GetField(field.Name)?.SetValue(store, field.GetValue(state));
        return store;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run()
    {
        CheckDraftOwnership();
        var native = StartingPerkList.AllPerkDefinitions();
        Check(native.Count == 48, "exact native selectable definition count");
        Check(native.All(perk => NativeActorPartnerSetup.Classify(perk.id) is not (NativeActorPartnerSetup.PerkScope.Unknown or NativeActorPartnerSetup.PerkScope.RetiredDefinition)), "all selectable definitions classified");
        Check(native.Count(perk => NativeActorPartnerSetup.Classify(perk.id) == NativeActorPartnerSetup.PerkScope.Personal) == 15, "personal query categories");
        Check(native.Count(perk => NativeActorPartnerSetup.Classify(perk.id) == NativeActorPartnerSetup.PerkScope.Mixed) == 12, "mixed categories");
        Check(native.Count(perk => NativeActorPartnerSetup.Classify(perk.id) == NativeActorPartnerSetup.PerkScope.SharedShop) == 21, "shared shop categories");
        foreach (string id in new[] { "loan", "debt", "unsubscribed", "speedy", "expedition_organizer" })
            Check(NativeActorPartnerSetup.Classify(id) == NativeActorPartnerSetup.PerkScope.RetiredDefinition, "retired constant: " + id);
        Check(NativeActorPartnerSetup.Catalog().Count == 27, "only supported personal and mixed choices");
        Check(NativeActorPartnerSetup.ValidateSelection(Array.Empty<string>()).Length == 0, "empty choice permitted");
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.ValidateSelection(new[] { "saving" }), "shared startup cannot be bought twice");
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.ValidateSelection(new[] { "other_mod" }), "unknown mod perk unsupported");
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.ValidateSelection(new[] { "lucky", "lucky" }), "duplicate ID");
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.ValidateSelection(new[] { "lucky", "unlucky" }), "native mutual exclusion");
        var openingPerks = NativeActorPartnerSetup.Catalog(fullOpening: true);
        void CheckConflict(string first, string second, bool expected)
        {
            Check(NativeActorPartnerSetup.ConflictsAcrossActors(openingPerks[first], openingPerks[second]) == expected &&
                NativeActorPartnerSetup.ConflictsAcrossActors(openingPerks[second], openingPerks[first]) == expected,
                first + " and " + second + " cross-player conflict");
        }
        CheckConflict("convict", "model_citizen", true);
        CheckConflict("side_street", "prime_real_estate", true);
        CheckConflict("lucky", "unlucky", false);
        CheckConflict("maimed", "careful_scavenger", false);
        CheckConflict("convict", "convict", false);
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.ValidateSelection(new[] { "lucky", "careful_scavenger", "tinkerer", "diplomatic" }), "native point budget");
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.ValidateSelection(new[] { "nicotine_addiction", "alcohol_addiction", "narcotic_addiction", "maimed", "illiterate" }), "native slot budget");
        var registry = (List<StartingPerk>)typeof(StartingPerkList).GetField("modPerks", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var replacement = new StartingPerk { id = "lucky", source = "test_mod", cost = -999 };
        registry.Add(replacement);
        try { Reject<InvalidDataException>(() => NativeActorPartnerSetup.ValidateSelection(new[] { "lucky" }), "registered mod override is not native definition"); }
        finally { registry.Remove(replacement); }
        var undeclared = new StartingPerk { id = "fixture_no_provider", incompatiblePerks = null };
        registry.Add(undeclared);
        try
        {
            Check(!NativeActorPartnerSetup.Catalog(fullOpening: true).ContainsKey(undeclared.id),
                "a registered perk without a provider does not affect the cooperative picker");
        }
        finally { registry.Remove(undeclared); }
        CheckSavedModPerkReceipt();

        var store = Store(); using var run = new NativeActorRun(store, NativeActorState.CreateDefault());
        var originalHost = NativeActorState.Capture(store).Serialize();
        var health = run.Partner.healthData; var perks = run.Partner.startingPerks;
        var draft = NativeActorPartnerSetup.CreateDraft(run.Run, new[] { "lucky", "nicotine_addiction" });
        var prepared = NativeActorPartnerSetup.Prepare(run, draft, null);
        Check(!prepared.AlreadyComplete && run.Partner.startingPerks.Count == 0 && health.luck == 0, "prepare has no live effects");
        string receipt = prepared.Commit();
        Check(run.Partner.startingPerks.Count == 2 && health.luck == 15 && health.daysSinceNicotineConsumption == 3, "match original Lucky and withdrawal initializers");
        Check(ReferenceEquals(health, run.Partner.healthData) && ReferenceEquals(perks, run.Partner.startingPerks), "personal field identities stable");
        Check(JToken.DeepEquals(originalHost, NativeActorState.Capture(store).Serialize()) && store.playerCash == 900, "host personal state and shared cash unchanged");
        Check(prepared.Commit() == receipt && health.luck == 15, "repeat prepared commit is idempotent");
        Check(NativeActorPartnerSetup.IsComplete(receipt, run.Run), "explicit persisted completion verifies");
        run.Partner.startingPerks.RemoveAt(0); // A native cure/removal after creation is valid.
        var again = NativeActorPartnerSetup.Prepare(run, draft, receipt);
        Check(again.AlreadyComplete && again.Commit() == receipt && perks.Count == 1 && health.luck == 15, "reconnect does not replay creation or cure perks");
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.Prepare(run, NativeActorPartnerSetup.CreateDraft(run.Run, Array.Empty<string>()), receipt), "different second selection refused");
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.IsComplete(receipt, Guid.NewGuid()), "receipt belongs to exact run");
        var invalid = (JObject)draft.DeepClone(); invalid["schema"] = "1";
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.Prepare(run, invalid, null), "schema requires integer");
        invalid = (JObject)draft.DeepClone(); invalid["slot"] = 0;
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.Prepare(run, invalid, null), "host cannot be partner target");
        var badReceipt = JObject.Parse(receipt); badReceipt["selection"] = "bad";
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.IsComplete(badReceipt.ToString(), run.Run), "receipt selection hash checked");
        using var pendingRun = new NativeActorRun(Store(), NativeActorState.CreateDefault());
        var pending = NativeActorPartnerSetup.Prepare(pendingRun, NativeActorPartnerSetup.CreateDraft(pendingRun.Run, new[] { "unlucky" }), null);
        pendingRun.Partner.healthData.luck = 2;
        Reject<InvalidDataException>(() => pending.Commit(), "stale prepared target refused");
        Check(pendingRun.Partner.startingPerks.Count == 0 && pendingRun.Partner.healthData.luck == 2, "stale target failure has no write");
        var unluckyDraft = NativeActorPartnerSetup.CreateDraft(pendingRun.Run, new[] { "unlucky", "gambling_addiction" });
        var unlucky = NativeActorPartnerSetup.Prepare(pendingRun, unluckyDraft, null);
        string unluckyReceipt = unlucky.Commit();
        Check(pendingRun.Partner.healthData.luck == -13, "original Unlucky applies minus fifteen to the current baseline");
        pendingRun.Partner.startingPerks.RemoveAll(perk => perk.id == "gambling_addiction");
        var restored = NativeActorPartnerSetup.Prepare(pendingRun, unluckyDraft, unluckyReceipt);
        Check(restored.AlreadyComplete && restored.Commit() == unluckyReceipt &&
            pendingRun.Partner.startingPerks.All(perk => perk.id != "gambling_addiction") &&
            pendingRun.Partner.healthData.luck == -13, "persisted creation does not re-add cured gambling addiction");
        using var defaults = new NativeActorRun(Store(), NativeActorState.CreateDefault());
        var empty = NativeActorPartnerSetup.Prepare(defaults, NativeActorPartnerSetup.CreateDraft(defaults.Run, Array.Empty<string>()), null);
        Check(NativeActorPartnerSetup.IsComplete(empty.Commit(), defaults.Run) && defaults.Partner.startingPerks.Count == 0, "empty draft is a persistent completed choice");
        using var opening = new NativeActorRun(Store(), NativeActorState.CreateDefault());
        string[] personal = { "lucky", "tinkerer", "diplomatic" };
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.PrepareOpening(opening, personal), "full opening validates the actual budget");
        var paired = NativeActorPartnerSetup.PrepareOpening(opening, personal.Append("bad_lease"));
        Check(NativeActorPartnerSetup.IsComplete(paired.Commit(), opening.Run) &&
            opening.Partner.startingPerks.Select(perk => perk.id).SequenceEqual(personal),
            "the shared drawback funds the complete opening draft and is not a personal perk");
        Check(typeof(NewGamePerksUI).GetMethod("OnContinueClicked", BindingFlags.NonPublic | BindingFlags.Static) != null && typeof(NewGamePerksUI).GetMethod("OnWindowClosed", BindingFlags.NonPublic | BindingFlags.Static) != null, "native picker interception metadata exists");
        var session = new NativeActorSession(run) { Store = store }; NativeActorSession.Current = session;
        var shared = StartingPerkList.WorkingPipes(); store.startingPerks.Add(shared);
        var saved = new Dictionary<string, StartingPerk> { ["bad_lease"] = StartingPerkList.BadLease(), [shared.id] = shared };
        NativeOpening.SavedShopPerks = saved;
        store.startingPerks.Add(StartingPerkList.Unlucky());
        Check(NativeActorPerkQueries.MissingStatusPerks(store).Select(perk => perk.id).SequenceEqual(new[] { "bad_lease" }),
            "host status only adds the other player's missing shop perks");
        using (run.Enter(NativeActorRun.Slot.Partner))
        {
            Check(NativeActorPerkQueries.TryShared("working_pipes", out var found) && ReferenceEquals(found, shared), "partner operation reads host shared perk configuration");
            Check(!NativeActorPerkQueries.TryShared("lucky", out _), "personal luck keeps native actor query");
            Check(!NativeActorPerkQueries.TryShared("short_sleeper", out _), "mixed short sleeper keeps actor-specific night query");
            Check(NativeActorPerkQueries.TryShared("saving", out found) && found == null, "absent shared perk never falls back to partner list");
            Check(NativeActorPerkQueries.MissingStatusPerks(store).Select(perk => perk.id).SequenceEqual(new[] { "bad_lease", "working_pipes" }),
                "partner status includes each shared perk once and leaves host-only personal traits out");
        }
        NativeActorSession.Current = null;
        NativeOpening.SavedShopPerks = null;
        NativeWorld.Guest = new NativeWorld
        {
            HasActorProjection = true, RemoteHostActor = NativeActorState.Capture(store),
            OpeningSharedPerks = saved.ToDictionary(pair => pair.Key, pair => NativeActorState.ClonePerk(pair.Value))
        };
        Check(NativeActorPerkQueries.TryShared("working_pipes", out var replica) && replica.id == "working_pipes" && !ReferenceEquals(replica, shared), "guest reads host shared configuration replica");
        var guestStore = Store();
        var localPerks = guestStore.startingPerks;
        localPerks.Add(StartingPerkList.Lucky());
        Check(NativeActorPerkQueries.MissingStatusPerks(guestStore).Select(perk => perk.id).SequenceEqual(new[] { "bad_lease", "working_pipes" }) &&
            ReferenceEquals(guestStore.startingPerks, localPerks) && localPerks.Single().id == "lucky",
            "guest status reads both shared sources without changing saved actor state");
        NativeWorld.Guest = null;
        NativeInventoryPilot.RemoteHostActor = NativeActorState.Capture(store);
        Check(NativeActorPerkQueries.TryShared("working_pipes", out replica) && replica.id == "working_pipes" &&
            !NativeActorPerkQueries.TryShared("gambling_addiction", out _), "effect guest reads shared host perks and keeps personal addiction queries");
        NativeInventoryPilot.RemoteHostActor = null;
        Check(!NativeActorPerkQueries.TryShared("working_pipes", out _), "single player shared query remains original");
        Check(!NativeActorPerkQueries.MissingStatusPerks(store).Any(), "solo status needs no additional shared rows");
        session.Activities.Dispose(); session.PersonalUi.Dispose();
        Console.WriteLine($"PASS {checks} partner perk classification, budget, typed initialization and receipt checks; native menu rendering and click flow NOT_RUN.");
        return 0;
    }

    private static void CheckSavedModPerkReceipt()
    {
        using var run = new NativeActorRun(Store(), NativeActorState.CreateDefault());
        const string id = "fixture_personal_perk";
        const string provider = "fixture_author";
        var definition = NativeActorState.ClonePerk(new StartingPerk { id = id, source = provider, incompatiblePerks = null });
        Check(definition.incompatiblePerks.Count == 0, "optional native incompatibility list normalized");
        // The fixture reads the receipt after selection admission, with no
        // loader or installed third-party Mod available.
        var prepare = typeof(NativeActorPartnerSetup).GetMethod("PreparePersonal", BindingFlags.Static | BindingFlags.NonPublic);
        var admitted = (NativeActorPartnerSetup.Prepared)prepare.Invoke(null, new object[] { run, new[] { definition } });
        var receipt = JObject.Parse(admitted.Commit());
        Check((string)receipt["externalPerks"][id] == provider &&
            NativeActorPartnerSetup.IsComplete(receipt.ToString(), run.Run),
            "recorded personal mod perk can be validated while provider is unavailable");
        var differentOwner = (JObject)receipt.DeepClone();
        differentOwner["externalPerks"][id] = "other_provider";
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.IsComplete(differentOwner.ToString(), run.Run),
            "mod perk provider is bound to its selection receipt");
        var strippedOwner = (JObject)receipt.DeepClone();
        strippedOwner.Remove("externalPerks");
        Reject<InvalidDataException>(() => NativeActorPartnerSetup.IsComplete(strippedOwner.ToString(), run.Run),
            "unknown saved perk requires provider provenance");
    }

    private static void CheckDraftOwnership()
    {
        // Only field shells: no GameObject, scene, UI close or save operation.
        var data = (NewGameData)RuntimeHelpers.GetUninitializedObject(typeof(NewGameData));
        var original = new List<StartingPerk>(); data.startingPerks = original;
        var owner = new NativeActorPartnerSetup.DraftHandle(data); data.startingPerks = owner.Draft;
        Check(owner.RestoreTo(data) && ReferenceEquals(data.startingPerks, original), "owned draft restores original menu list");
        Check(!owner.RestoreTo(data), "restoration is idempotent");
        var newer = new List<StartingPerk>(); data.startingPerks = newer;
        Check(!owner.RestoreTo(data) && ReferenceEquals(data.startingPerks, newer), "newer same-descriptor native config is preserved");
        var otherData = (NewGameData)RuntimeHelpers.GetUninitializedObject(typeof(NewGameData));
        otherData.startingPerks = new List<StartingPerk>();
        data.startingPerks = owner.Draft;
        Check(!owner.RestoreTo(otherData) && ReferenceEquals(data.startingPerks, owner.Draft), "replacement NewGameData instance is not overwritten");

        var pendingField = typeof(NativeActorPartnerSetup).GetField("pending", BindingFlags.Static | BindingFlags.NonPublic);
        var instanceField = typeof(NewGameData).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        var savedPending = pendingField.GetValue(null); var savedData = instanceField.GetValue(null);
        object Pending(NativeActorPartnerSetup.DraftHandle handle)
        {
            var value = Activator.CreateInstance(pendingField.FieldType, nonPublic: true);
            pendingField.FieldType.GetField("Handle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, handle);
            return value;
        }
        try
        {
            instanceField.SetValue(null, data);
            var pending = Pending(owner); pendingField.SetValue(null, pending);
            var wrongOwner = new NativeActorPartnerSetup.DraftHandle(data);
            Check(!NativeActorPartnerSetup.CancelDraft(wrongOwner) && ReferenceEquals(pendingField.GetValue(null), pending), "other owner cannot cancel active picker");
            Check(NativeActorPartnerSetup.BeforeClosed() == null, "unbound or already-destroyed window cannot intercept another native close");
            Check(NativeActorPartnerSetup.CancelDraft(owner) && pendingField.GetValue(null) == null && ReferenceEquals(data.startingPerks, original),
                "owner cancel clears pending and restores data even with no living window");
            Check(!NativeActorPartnerSetup.CancelDraft(owner), "repeated owner disposal is harmless");

            var next = new NativeActorPartnerSetup.DraftHandle(data); data.startingPerks = next.Draft;
            pending = Pending(next); pendingField.SetValue(null, pending);
            Check(!NativeActorPartnerSetup.CancelDraft(owner) && ReferenceEquals(pendingField.GetValue(null), pending), "stale join owner cannot cancel a replacement picker");
            NativeActorPartnerSetup.AfterClosed(owner);
            Check(ReferenceEquals(pendingField.GetValue(null), pending), "late old-window close cannot retire a replacement picker");
            data.startingPerks = newer;
            Check(NativeActorPartnerSetup.CancelDraft(next) && ReferenceEquals(data.startingPerks, newer), "disposing old picker preserves newer native menu choice");
            next = new NativeActorPartnerSetup.DraftHandle(data); data.startingPerks = next.Draft;
            pendingField.SetValue(null, Pending(next)); instanceField.SetValue(null, otherData);
            Check(NativeActorPartnerSetup.CancelDraft(next) && pendingField.GetValue(null) == null && ReferenceEquals(data.startingPerks, next.Draft),
                "scene replacement clears stale pending without modifying detached descriptor");
        }
        finally { pendingField.SetValue(null, savedPending); instanceField.SetValue(null, savedData); }
    }
}
