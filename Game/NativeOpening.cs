using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.SceneManagement;

namespace ProbablyCoop;

internal static class NativeOpening
{
    private const string PartnerCareerKey = "probably_coop.opening.partner.v1";
    private const string SharedPerksKey = "probably_coop.opening.shared_perks.v1";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayerStore, SavedPerks> CachedSharedPerks = new();
    private sealed class SavedPerks
    {
        internal string Serialized;
        internal Dictionary<string, StartingPerk> Perks;
    }
    private static readonly Lazy<Dictionary<string, StartingPerk>> SharedDefinitions = new(() =>
        NativeActorPartnerSetup.Catalog(fullOpening: true).Values
            .Where(perk => string.IsNullOrEmpty(perk.source) &&
                NativeActorPartnerSetup.Classify(perk.id) == NativeActorPartnerSetup.PerkScope.SharedShop)
            .ToDictionary(perk => perk.id, StringComparer.Ordinal));
    private static CoopOpening pending;
    private static bool careerOnly, mixedBenefits;
    private static readonly System.Reflection.FieldInfo LabelsField = AccessTools.Field(typeof(CodeInstruction), "labels");
    private static System.Collections.IList Labels(CodeInstruction instruction) =>
        (System.Collections.IList)LabelsField.GetValue(instruction);
    private static bool CanInstallHostHooks => NativePlatform.SupportsAuthorityContract &&
        NativePlatform.MatchesActorModule(typeof(NewGameData).Module, NativeActorCarryAccess.ExpectedMvid);
    internal static bool CanHost => NativePlatform.SupportsHosting && CanInstallHostHooks;

    internal static CoopOpening Current => CoopRun.Armed && pending?.Request == NewGameData.Instance ? pending : null;

    internal static void Arm(CoopOpening opening)
    {
        opening.ApplyMenuChoices();
        pending = opening;
        CoopRun.Armed = true;
    }

    internal static void Clear() => pending = null;

    internal static void InitializePartner(NativeActorSession session)
    {
        if (pending?.Request != NewGameData.Instance) return;
        pending?.ApplyPartner(session);
    }

    internal static bool UsesCareer(PlayerStore store, NewGameData.StartType career)
    {
        if (store.startType == career) return true;
        // A registered onStart temporarily gives the native shop that career's type.
        if (!NativeGuest.Active && Current is { } opening && PlayerStore.HasInstance && ReferenceEquals(store, PlayerStore.Instance))
            return opening.HostScenario.startType == career || opening.PartnerScenario.startType == career;
        if (!NativeGuest.Active && NativeActorSession.Current?.Store != store && NativeActorPersistence.LoadedOpeningStore != store)
            return false;
        string partnerId;
        if (NativeGuest.Active) partnerId = NativeWorld.Guest?.OpeningCareer;
        else store.modData.TryGetValue(PartnerCareerKey, out partnerId);
        return !string.IsNullOrEmpty(partnerId) && StartingScenario.Get(partnerId)?.startType == career;
    }

    internal static string PartnerCareer(PlayerStore store) =>
        store.modData.TryGetValue(PartnerCareerKey, out string id) ? id : null;

    internal static void RestorePartnerCareerReputation(PlayerStore store)
    {
        if (store.isCartelExited || store.startType == NewGameData.StartType.DRUG_DEALER ||
            StartingScenario.Get(PartnerCareer(store) ?? "")?.startType != NewGameData.StartType.DRUG_DEALER ||
            store.storeReputations == null || store.storeReputations.Any(rep => rep.factionId == StoreReputation.FACTION_CARTEL)) return;
        store.storeReputations.Add(new StoreReputation
        {
            factionDisplay = LocHelper.GetLocalizedClientName("faction_name_cartel"),
            factionId = StoreReputation.FACTION_CARTEL,
            factionColor1 = "515b66",
            factionColor2 = "44464c",
            factionColor3 = "737064",
            perks = StoreReputation.CreatePerks(StoreReputation.FACTION_CARTEL)
        });
    }

    internal static Dictionary<string, StartingPerk> ReadSharedPerks(JToken token)
    {
        if (token == null) return null;
        if (token is not JArray ids || ids.Count > 16) throw new InvalidDataException("Invalid cooperative shop perks.");
        var perks = new Dictionary<string, StartingPerk>(StringComparer.Ordinal);
        foreach (var row in ids)
        {
            if (row.Type != JTokenType.String || string.IsNullOrEmpty((string)row))
                throw new InvalidDataException("Invalid cooperative shop perk ID.");
            string id = (string)row;
            if (!SharedDefinitions.Value.TryGetValue(id, out var perk))
                perk = NativeActorPartnerSetup.FindDeclaredSharedPerk(id);
            if (perk == null || !perks.TryAdd(id, perk))
                throw new InvalidDataException("Unknown or duplicate cooperative shop perk: " + id);
        }
        return perks;
    }

    internal static Dictionary<string, StartingPerk> SharedPerks(PlayerStore store)
    {
        if (!store.modData.TryGetValue(SharedPerksKey, out var serialized)) return null;
        var cached = CachedSharedPerks.GetValue(store, _ => new SavedPerks());
        if (cached.Serialized != serialized)
        {
            cached.Perks = ReadSharedPerks(JArray.Parse(serialized));
            cached.Serialized = serialized;
        }
        return cached.Perks;
    }

    internal static StartingPerk SharedStartingPerk(string id) => Current?.PartnerPerks.FirstOrDefault(perk => perk.id == id);

    internal static bool HasMixedBenefit(string id) => mixedBenefits && Current is CoopOpening opening &&
        NativeActorPartnerSetup.Classify(id) == NativeActorPartnerSetup.PerkScope.Mixed &&
        opening.PartnerPerks.Any(perk => perk.id == id);

    private static bool CareerOnly() => careerOnly;
    private static void ApplySecondCareer()
    {
        var opening = Current;
        if (opening == null || careerOnly || opening.HostScenario.id == opening.PartnerScenario.id) return;
        var request = opening.Request;
        var previousType = request.startType;
        var previousId = request.startScenarioId;
        var store = PlayerStore.Instance;
        int cash = store.playerCash;
        try
        {
            careerOnly = true;
            request.startType = opening.PartnerScenario.startType;
            request.startScenarioId = opening.PartnerScenario.id;
            NewGameData.HandleInitialItem();
            if (store.playerCash != checked(cash - opening.Cost(opening.PartnerScenario.id)))
                throw new InvalidDataException("The partner career charged a different start cost.");
        }
        finally
        {
            request.startType = previousType;
            request.startScenarioId = previousId;
            careerOnly = false;
        }
    }

    private static bool ApplyRegisteredCareer()
    {
        var opening = Current;
        if (opening == null) return false;
        var scenario = careerOnly ? opening.PartnerScenario : opening.HostScenario;
        if (!scenario.IsMod) return false;

        // The game's normal modded branch catches exceptions and proceeds.
        // A paired opening must finish both careers before the initial save.
        var store = PlayerStore.Instance;
        var oldType = store.startType;
        string oldId = store.startScenarioId;
        int expectedCash = checked(store.playerCash - opening.Cost(scenario.id));
        try
        {
            store.startType = scenario.startType;
            store.startScenarioId = scenario.id;
            scenario.onStart?.Invoke();
            if (store.playerCash != expectedCash)
                throw new InvalidDataException("Career does not match its declared starting cash cost: " + scenario.id);
            return true;
        }
        finally
        {
            store.startType = oldType;
            store.startScenarioId = oldId;
        }
    }

    // The new shop has no slot yet. Fail a bounded new-run attempt before
    // InitialSave if a native starting package or other initialization fails.
    [HarmonyPatch(typeof(EmporiumEntry), "Start")]
    private static class SceneStart
    {
        [HarmonyPrepare] private static bool Prepare() => CanInstallHostHooks;
        private static void Finalizer(Exception __exception)
        {
            if (pending == null) return;
            Clear();
            CoopRun.Armed = false;
            if (__exception != null) SceneManager.LoadScene("EmporiumMenu", LoadSceneMode.Single);
        }
    }

    [HarmonyPatch(typeof(NewGameData), nameof(NewGameData.HandleInitialItem))]
    private static class NativeItems
    {
        [HarmonyPrepare] private static bool Prepare() => CanInstallHostHooks;
        private static void Prefix(out bool __state)
        {
            __state = mixedBenefits;
            if (Current != null && !careerOnly) mixedBenefits = true;
        }
        private static void Finalizer(bool __state) => mixedBenefits = __state;

        // Keep the original career branches, and enter only that region on
        // the second career's call. The native declaration of local 0 and
        // the null check still execute before that jump.
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source, ILGenerator il)
        {
            var code = source.ToList();
            var startType = AccessTools.Field(typeof(NewGameData), nameof(NewGameData.startType));
            var getter = AccessTools.PropertyGetter(typeof(NewGameData), nameof(NewGameData.Instance));
            var blackscreen = AccessTools.Method(typeof(StoreUIManager), "FinishBlackscreenSequence", new[] { typeof(bool) });
            var sentinel = AccessTools.Method(typeof(StartingPerk), nameof(StartingPerk.IsSentinel));

            int first = FindOnly(code, instruction => instruction.Calls(blackscreen)) - 2;
            int careers = Enumerable.Range(first + 1, code.Count - first - 3)
                .Single(i => code[i].Calls(getter) && code[i + 1].LoadsField(startType) &&
                    (code[i + 2].opcode == OpCodes.Brfalse || code[i + 2].opcode == OpCodes.Brfalse_S));
            int after = FindOnly(code, instruction => instruction.Calls(sentinel));
            if (first < 0 || careers >= after || !Labels(code[after]).Contains(code[careers + 2].operand))
                throw new InvalidDataException("Native career initialization region changed.");

            var only = AccessTools.Method(typeof(NativeOpening), nameof(CareerOnly));
            var second = AccessTools.Method(typeof(NativeOpening), nameof(ApplySecondCareer));
            var custom = AccessTools.Method(typeof(NativeOpening), nameof(ApplyRegisteredCareer));
            var returnLabel = il.DefineLabel();
            var careerLabel = il.DefineLabel();
            var afterCareerLabel = il.DefineLabel();
            var done = code.Last();
            if (done.opcode != OpCodes.Ret) throw new InvalidDataException("Native career return changed.");
            Labels(done).Add(returnLabel);
            var startCareer = new CodeInstruction(OpCodes.Call, custom);
            foreach (object label in Labels(code[careers])) Labels(startCareer).Add(label);
            Labels(code[careers]).Clear();
            Labels(startCareer).Add(careerLabel);
            var stopReplay = new CodeInstruction(OpCodes.Call, only);
            foreach (object label in Labels(code[after])) Labels(stopReplay).Add(label);
            Labels(stopReplay).Add(afterCareerLabel);
            stopReplay.blocks.AddRange(code[after].blocks);
            Labels(code[after]).Clear();
            code[after].blocks.Clear();
            code.InsertRange(after, new[] { stopReplay, new CodeInstruction(OpCodes.Brtrue, returnLabel), new CodeInstruction(OpCodes.Call, second) });
            code.InsertRange(careers, new[] { startCareer, new CodeInstruction(OpCodes.Brtrue, afterCareerLabel) });
            var skipShared = new CodeInstruction(OpCodes.Call, only);
            foreach (object label in Labels(code[first])) Labels(skipShared).Add(label);
            Labels(code[first]).Clear();
            code.InsertRange(first, new[] { skipShared, new CodeInstruction(OpCodes.Brtrue, careerLabel) });
            return code;
        }
    }

    private static int FindOnly(List<CodeInstruction> code, Predicate<CodeInstruction> match) =>
        Enumerable.Range(0, code.Count).Single(i => match(code[i]));

    [HarmonyPatch(typeof(StartingPerkContent), nameof(StartingPerkContent.HandleNewGamePerk))]
    private static class FinishPerkInitialization
    {
        [HarmonyPrepare] private static bool Prepare() => CanInstallHostHooks;
        private static void Prefix(out bool __state)
        {
            __state = mixedBenefits;
            if (Current != null) mixedBenefits = true;
        }
        private static void Postfix()
        {
            if (Current is not CoopOpening opening) return;
            var store = PlayerStore.Instance;
            if (opening.ModProviders.Length != 0)
            {
                var packages = NativeRoomModManifest.Local();
                var required = opening.ModProviders.Select(id => packages.TryGetValue(id, out var mod) &&
                    RoomModManifest.ExactPackage(mod, mod) ? mod :
                    throw new InvalidDataException("Required opening Mod is no longer enabled: " + id));
                var encoded = RoomModManifest.Write(required);
                RoomModManifest.ReadOpeningPackages(encoded);
                store.modData[NativeActorCheckpoint.OpeningModsDataKey] = encoded.ToString(Formatting.None);
            }
            store.modData[PartnerCareerKey] = opening.PartnerScenario.id;
            var shared = opening.PartnerPerks.Where(perk =>
                NativeActorPartnerSetup.Classify(perk.id) == NativeActorPartnerSetup.PerkScope.SharedShop);
            store.modData[SharedPerksKey] = new JArray(shared.Select(perk => perk.id)).ToString(Formatting.None);
            OpeningRent.Initialize(store, opening);
        }
        private static void Finalizer(bool __state) => mixedBenefits = __state;
    }

    [HarmonyPatch(typeof(GraphUtils), nameof(GraphUtils.DestroyIfUnplaced))]
    private static class StartingSpace
    {
        [HarmonyPrepare] private static bool Prepare() => CanInstallHostHooks;
        private static void Prefix(GameItem item, string context)
        {
            if (Current != null && item?.unitCount > 0 && item.parentInventory == null &&
                context == "EmporiumTryAdd: every store inventory is full")
                throw new InvalidDataException("The two starting packages do not fit in the shop.");
        }
    }

    [HarmonyPatch(typeof(ModHelper), nameof(ModHelper.IsStartingScenario))]
    private static class ScenarioMembership
    {
        private static void Postfix(string id, ref bool __result)
        {
            if (__result || string.IsNullOrEmpty(id)) return;
            if (Current is { } opening)
                __result = opening.HostScenario.id == id || opening.PartnerScenario.id == id;
            else if ((NativeActorSession.Current?.Store ?? NativeActorPersistence.LoadedOpeningStore) is { } store)
                __result = PartnerCareer(store) == id;
            else
                __result = NativeWorld.Guest?.OpeningCareer == id;
        }
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.IsStartType))]
    private static class CareerMembership
    {
        private static void Postfix(PlayerStore __instance, NewGameData.StartType startType, ref bool __result)
        {
            if (!__result) __result = UsesCareer(__instance, startType);
        }
    }

    [HarmonyPatch]
    private static class NamedCareerMembership
    {
        private static readonly Dictionary<string, NewGameData.StartType> CareerByMethod = new()
        {
            [nameof(PlayerStore.IsFenceStart)] = NewGameData.StartType.FENCE,
            [nameof(PlayerStore.IsWaterMerchantStart)] = NewGameData.StartType.WATER_MERCHANT,
            [nameof(PlayerStore.IsFarmerStart)] = NewGameData.StartType.FARMER,
            [nameof(PlayerStore.IsGunsmithStart)] = NewGameData.StartType.GUNSMITH,
            [nameof(PlayerStore.IsChemistStart)] = NewGameData.StartType.DRUG_DEALER,
            [nameof(PlayerStore.IsAquafarmerStart)] = NewGameData.StartType.AQUAFARMER,
            [nameof(PlayerStore.IsRancherStart)] = NewGameData.StartType.RANCHER
        };

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            CareerByMethod.Keys.Select(name => (System.Reflection.MethodBase)AccessTools.Method(typeof(PlayerStore), name));

        private static void Postfix(PlayerStore __instance, System.Reflection.MethodBase __originalMethod, ref bool __result)
        {
            if (!__result) __result = UsesCareer(__instance, CareerByMethod[__originalMethod.Name]);
        }
    }

    [HarmonyPatch(typeof(StoreClientListStory), nameof(StoreClientListStory.CreateMentorDisplayCase))]
    private static class MentorDisplayCaseGifts
    {
        [HarmonyPrepare] private static bool Prepare() => CanInstallHostHooks;

        private static void Postfix(StoreClient __result)
        {
            var store = NativeActorSession.Current?.Store;
            if (store == null || NativeGuest.Active || store.skipIntro || store.isDisplayCaseIntroduced ||
                StartingScenario.Get(PartnerCareer(store) ?? "") is not { } partner) return;

            var missing = DisplayCaseSigns(partner.startType).Except(DisplayCaseSigns(store.startType)).ToArray();
            if (missing.Length == 0) return;
            // The original third dialogue line gives the selected career's
            // signs and marks the common lesson complete.
            var lesson = __result.mainDialogue?.nextDialogue?.nextDialogue;
            var nativeGift = lesson?.endAction;
            if (nativeGift == null) return;
            lesson.endAction = () =>
            {
                bool firstLesson = !store.isDisplayCaseIntroduced;
                nativeGift();
                if (!firstLesson || !store.isDisplayCaseIntroduced || !ReferenceEquals(NativeActorSession.Current?.Store, store)) return;
                foreach (string sign in missing) store.AddDirectSellingItemToTable(sign, isOwend: true);
            };
        }

        private static string[] DisplayCaseSigns(NewGameData.StartType career) => career switch
        {
            NewGameData.StartType.WATER_MERCHANT => new[] { "sign_water" },
            NewGameData.StartType.SCAV => new[] { "sign_material" },
            _ => new[] { "sign_food", "sign_household" }
        };
    }

    [HarmonyPatch]
    private static class CartelCareerGate
    {
        [HarmonyPrepare] private static bool Prepare() => CanInstallHostHooks;
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(StoreClientManager), nameof(StoreClientManager.HandleCartelClient));
            yield return AccessTools.Method(typeof(StoreReputation), nameof(StoreReputation.InitReputation));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
        {
            var code = source.ToList();
            var field = AccessTools.Field(typeof(PlayerStore), nameof(PlayerStore.startType));
            int load = FindOnly(code, instruction => instruction.LoadsField(field));
            // Keep the store on the stack, then ask it about the union of careers.
            var helper = AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.IsStartType));
            var constant = code[load + 1];
            var branch = code[load + 2];
            bool branchesOnMatch = branch.opcode == OpCodes.Beq || branch.opcode == OpCodes.Beq_S;
            if (!constant.LoadsConstant((long)NewGameData.StartType.DRUG_DEALER) ||
                (!branchesOnMatch && branch.opcode != OpCodes.Bne_Un && branch.opcode != OpCodes.Bne_Un_S))
                throw new InvalidDataException("Native cartel career condition changed.");
            code[load].opcode = OpCodes.Ldc_I4;
            code[load].operand = (int)NewGameData.StartType.DRUG_DEALER;
            constant.opcode = OpCodes.Call;
            constant.operand = helper;
            branch.opcode = branchesOnMatch ? OpCodes.Brtrue : OpCodes.Brfalse;
            return code;
        }
    }
}
