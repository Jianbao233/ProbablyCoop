using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;

namespace ProbablyCoop;

// Disposable Unity/Mono verification only. This does not enable actor gameplay
// or attach an actor run to persistence, transport, UI or overnight processing.
internal static class NativeActorQa
{
    private const string Argument = "--probably-coop-qa-actor-fields";
    private const string MarkerPerk = "probably_coop_actor_qa_partner_only";

    internal static string[] Run(PlayerStore existingStore)
    {
        if (!NativeBackgroundQa.Enabled || Array.IndexOf(Environment.GetCommandLineArgs(), Argument) < 0 ||
            !NativeBackgroundQa.IsHostProduct)
            throw new InvalidOperationException("Native actor QA requires its explicit isolated Host QA entry.");
        if (existingStore == null || !ReferenceEquals(
                AccessTools.Field(typeof(PlayerStore), "instance")?.GetValue(null), existingStore))
            throw new InvalidOperationException("Native actor QA requires the already loaded current store.");
        if (NativeActorFieldAccess.IsInstalled)
            throw new InvalidOperationException("Native actor QA refuses to replace an active actor resolver.");

        var loaded = ModLoader.Mods.SingleOrDefault(mod => mod.Assembly == typeof(NativeActorQa).Assembly &&
            mod.Manifest?.ID == "probably_coop");
        if (loaded == null || !loaded.IsEnabled || loaded.HarmonyInstance == null)
            throw new InvalidOperationException("Native actor QA requires its enabled framework LoadedMod Harmony instance.");
        var harmony = loaded.HarmonyInstance;
        Require(CountActorTranspilers(harmony.Id) == 0, "actor transpilers were already present");

        var evidence = new List<string>();
        string beforeHash = PersonalHash(existingStore);
        var originalHealth = existingStore.healthData;
        var originalPerks = existingStore.startingPerks;
        var originalBlacklist = existingStore.appraisalBlacklist;
        var originalServices = existingStore.storeService;
        int originalCash = existingStore.playerCash, originalVisits = existingStore.visitLeftTonight;
        int nativeLuck = HealthData.GetLuck();
        bool nativeWounded = HealthData.IsSeriouslyWounded();
        Require(!StartingPerk.IsPerkActive(MarkerPerk), "QA marker collides with a host perk");

        // Only HealthData is copied to the synthetic partner. Its selected
        // perks remain independent; the unique marker has no world effects.
        var partner = NativeActorState.CreateDefault();
        partner.healthData = NativeActorState.Capture(existingStore).healthData;
        partner.healthData.luck = checked(partner.healthData.luck + 17);
        partner.healthData.woundState = nativeWounded ? 0 : 6;
        partner.startingPerks.Add(new StartingPerk
        {
            id = MarkerPerk,
            type = StartingPerk.StartingPerkType.NEUTRAL
        });
        int partnerLuck = checked(nativeLuck + 17);
        var actorRun = new NativeActorRun(existingStore, partner);
        bool installAttempted = false;
        Exception failure = null;
        try
        {
            int expectedMethods = NativeActorFieldAccess.VerifyNativeContract();
            Require(expectedMethods == 82, "audited Rev13 native actor method count changed");
            installAttempted = true;
            NativeActorFieldAccess.Install(harmony, actorRun.Resolve);
            int installedMethods = CountActorTranspilers(harmony.Id);
            Require(NativeActorFieldAccess.IsInstalled && installedMethods == expectedMethods,
                "the full audited actor transpiler set did not install");
            evidence.Add("PASS actor-mono-install methods=" + installedMethods + " framework=" + harmony.Id);

            AssertNativeQueries(nativeLuck, nativeWounded, false, "implicit Host");
            Func<bool> partnerCallback = null;
            ProbablyCoop.Core.ActivityToken callbackToken = default;
            using (actorRun.Enter(NativeActorRun.Slot.Host))
            {
                AssertNativeQueries(nativeLuck, nativeWounded, false, "outer Host");
                using (actorRun.Enter(NativeActorRun.Slot.Partner))
                {
                    AssertNativeQueries(partnerLuck, !nativeWounded, true, "nested Partner");
                    Require(ReferenceEquals(NativeActorFieldAccess.Get_healthData(existingStore), actorRun.Partner.healthData),
                        "typed health accessor disagrees with native Partner query");
                    callbackToken = actorRun.RegisterActivity(1, NativeActorRun.Slot.Partner);
                    partnerCallback = actorRun.Capture(callbackToken,
                        () => AssertNativeQueries(partnerLuck, !nativeWounded, true, "captured Partner"));
                    using (actorRun.Enter(NativeActorRun.Slot.Host))
                        AssertNativeQueries(nativeLuck, nativeWounded, false, "inner Host");
                    AssertNativeQueries(partnerLuck, !nativeWounded, true, "restored Partner");
                }
                AssertNativeQueries(nativeLuck, nativeWounded, false, "restored outer Host");
                Require(partnerCallback != null && partnerCallback(), "captured Partner did not execute");
                AssertNativeQueries(nativeLuck, nativeWounded, false, "Host after callback");
            }
            actorRun.RetireActivity(callbackToken);
            Require(!partnerCallback(), "retired Partner callback executed");
            evidence.Add("PASS actor-mono-scope native=GetLuck/IsSeriouslyWounded/IsPerkActive luckDelta=17 nested=Host-Partner-Host callback=Partner retired=false");

            using (actorRun.Enter(NativeActorRun.Slot.Partner))
            {
                StartingPerk.RemovePerk(MarkerPerk);
                Require(!StartingPerk.IsPerkActive(MarkerPerk) && actorRun.Partner.startingPerks.Count == 0,
                    "native RemovePerk did not mutate only the detached Partner list");
            }
            Require(PersonalHash(existingStore) == beforeHash, "native Partner mutation changed Host fields");
            evidence.Add("PASS actor-mono-mutation native=StartingPerk.RemovePerk target=detached-Partner hostHash=" + beforeHash);

            var intentional = new InvalidOperationException("Actor QA intentional scope unwind");
            try { actorRun.Execute(NativeActorRun.Slot.Partner, () => throw intentional); }
            catch (InvalidOperationException exception) when (ReferenceEquals(exception, intentional)) { }
            Require(actorRun.CurrentActor == NativeActorRun.Slot.Host, "exception leaked Partner scope");
            AssertNativeQueries(nativeLuck, nativeWounded, false, "Host after exception");
            evidence.Add("PASS actor-mono-exception restored=Host");
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            // Uninstall removes this transpiler method only, preserving the
            // framework-owned Harmony instance and every unrelated mod patch.
            if (installAttempted)
                TryCleanup(() => NativeActorFieldAccess.Uninstall(harmony), ref failure);
            TryCleanup(actorRun.Dispose, ref failure);
            TryCleanup(() =>
            {
                Require(!NativeActorFieldAccess.IsInstalled && CountActorTranspilers(harmony.Id) == 0,
                    "actor transpiler cleanup incomplete");
                AssertNativeQueries(nativeLuck, nativeWounded, false, "Host after uninstall");
                Require(PersonalHash(existingStore) == beforeHash &&
                    ReferenceEquals(existingStore.healthData, originalHealth) &&
                    ReferenceEquals(existingStore.startingPerks, originalPerks) &&
                    ReferenceEquals(existingStore.appraisalBlacklist, originalBlacklist),
                    "Host personal field hash or reference identity changed");
                Require(existingStore.playerCash == originalCash && existingStore.visitLeftTonight == originalVisits &&
                    ReferenceEquals(existingStore.storeService, originalServices), "shared world sentinels changed");
                evidence.Add("PASS actor-mono-cleanup remaining=0 nativeQueries=Host hostHash=" + beforeHash +
                    " hostReferences=unchanged worldSentinels=unchanged");
            }, ref failure);
        }
        if (failure != null) throw new InvalidOperationException("Native actor Mono QA failed; cleanup attempted.", failure);
        evidence.Add("PASS actor-mono-complete scope=field-routing-only gameplay-adapter=NOT_RUN saveIO=none");
        return evidence.ToArray();
    }

    private static void AssertNativeQueries(int luck, bool wounded, bool marker, string context)
    {
        Require(HealthData.GetLuck() == luck, context + " native GetLuck mismatch");
        Require(HealthData.IsSeriouslyWounded() == wounded, context + " native IsSeriouslyWounded mismatch");
        Require(StartingPerk.IsPerkActive(MarkerPerk) == marker, context + " native IsPerkActive mismatch");
    }

    private static int CountActorTranspilers(string owner)
    {
        int count = 0;
        foreach (var original in Harmony.GetAllPatchedMethods())
        {
            var info = Harmony.GetPatchInfo(original);
            if (info == null) continue;
            foreach (var patch in info.Transpilers)
                if (patch.owner == owner && patch.PatchMethod?.DeclaringType == typeof(NativeActorFieldAccess)) count++;
        }
        return count;
    }

    private static string PersonalHash(PlayerStore store)
    {
        byte[] data = Encoding.UTF8.GetBytes(NativeActorState.Capture(store).Serialize().ToString(Formatting.None));
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
    }

    private static void TryCleanup(Action action, ref Exception failure)
    {
        try { action(); }
        catch (Exception cleanup) { failure = failure == null ? cleanup : new AggregateException(failure, cleanup); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Actor QA: " + message);
    }
}
