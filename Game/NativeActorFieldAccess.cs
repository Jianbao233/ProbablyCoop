using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;

namespace ProbablyCoop;

// Explicit opt-in only: no HarmonyPatch attribute and no singleton replacement.
// The run owner supplies a resolver restricted to its exact active PlayerStore.
// Returning null leaves unrelated stores, save previews and solo games native.
internal static class NativeActorFieldAccess
{
    internal const string ExpectedMvid = "911508a6-e0eb-4cc0-91e8-e14b16ea28d7";
    internal const string ExpectedAssemblySha256 = "725CB999D019DFF1FF31BED353C7F9BB8D81A7605A15DE1F80F5A3A6CF7B1D05";
    private static readonly object Gate = new object();
    private static readonly MethodInfo TranspilerMethod = typeof(NativeActorFieldAccess).GetMethod(nameof(Transpile), BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(op => op.Value);
    private static Func<PlayerStore, NativeActorState> resolver;
    private static Harmony owner;
    private static Dictionary<int, Target> installedTargets;
    private static readonly List<MethodBase> patched = new List<MethodBase>();

    internal static bool IsInstalled => Volatile.Read(ref resolver) != null;

    internal static void Install(Harmony frameworkHarmony, Func<PlayerStore, NativeActorState> resolveActor)
    {
        if (frameworkHarmony == null) throw new ArgumentNullException(nameof(frameworkHarmony));
        if (resolveActor == null) throw new ArgumentNullException(nameof(resolveActor));
        lock (Gate)
        {
            if (owner != null) throw new InvalidOperationException("Actor field access already installed or awaiting cleanup");
            // Complete all native metadata verification before the first patch.
            var targets = ValidateNativeAssembly();
            owner = frameworkHarmony;
            installedTargets = targets.ToDictionary(target => target.Token);
            try
            {
                foreach (var target in targets)
                {
                    // Record before Patch so a partially installed failing patch
                    // is also removed by rollback. No actor routing is live yet.
                    patched.Add(target.Method);
                    frameworkHarmony.Patch(target.Method, transpiler: new HarmonyMethod(TranspilerMethod));
                }
                Volatile.Write(ref resolver, resolveActor);
            }
            catch (Exception installError)
            {
                Volatile.Write(ref resolver, null);
                try { RemovePatches(); }
                catch (Exception cleanupError) { throw new AggregateException("Actor field install failed; cleanup incomplete, routing disabled", installError, cleanupError); }
                throw;
            }
        }
    }

    internal static void Uninstall(Harmony frameworkHarmony)
    {
        if (frameworkHarmony == null) throw new ArgumentNullException(nameof(frameworkHarmony));
        lock (Gate)
        {
            if (owner == null) return;
            if (!ReferenceEquals(owner, frameworkHarmony)) throw new InvalidOperationException("Actor field patches belong to another framework Harmony instance");
            // Even if unpatching fails, all remaining wrappers fall back to the
            // original field; do not leave a stale actor callback running.
            Volatile.Write(ref resolver, null);
            RemovePatches();
        }
    }

    private static void RemovePatches()
    {
        var failures = new List<Exception>();
        foreach (var method in patched.ToArray())
        {
            try { owner.Unpatch(method, TranspilerMethod); patched.Remove(method); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count != 0) throw new AggregateException("Actor field patch cleanup failed", failures);
        installedTargets = null;
        owner = null;
    }

    private static NativeActorState Resolve(PlayerStore store)
    {
        // ldfld/stfld throw for a null receiver even when a resolver could
        // supply an actor. Keep that IL contract before invoking user policy.
        if (ReferenceEquals(store, null)) throw new NullReferenceException();
        return Volatile.Read(ref resolver)?.Invoke(store);
    }

    internal static string Get_playerName(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.playerName : actor.playerName; }
    internal static void Set_playerName(PlayerStore store, string value)
    { var actor = Resolve(store); if (actor == null) store.playerName = value; else actor.playerName = value; }
    internal static int Get_appraiserLevel(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.appraiserLevel : actor.appraiserLevel; }
    internal static void Set_appraiserLevel(PlayerStore store, int value)
    { var actor = Resolve(store); if (actor == null) store.appraiserLevel = value; else actor.appraiserLevel = value; }
    internal static double Get_appraisalProgress(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.appraisalProgress : actor.appraisalProgress; }
    internal static void Set_appraisalProgress(PlayerStore store, double value)
    { var actor = Resolve(store); if (actor == null) store.appraisalProgress = value; else actor.appraisalProgress = value; }
    internal static bool Get_appraisalPromotionDeclined(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.appraisalPromotionDeclined : actor.appraisalPromotionDeclined; }
    internal static void Set_appraisalPromotionDeclined(PlayerStore store, bool value)
    { var actor = Resolve(store); if (actor == null) store.appraisalPromotionDeclined = value; else actor.appraisalPromotionDeclined = value; }
    internal static int Get_appraisalPromotionDay(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.appraisalPromotionDay : actor.appraisalPromotionDay; }
    internal static void Set_appraisalPromotionDay(PlayerStore store, int value)
    { var actor = Resolve(store); if (actor == null) store.appraisalPromotionDay = value; else actor.appraisalPromotionDay = value; }
    internal static int Get_scavengingAttempts(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.scavengingAttempts : actor.scavengingAttempts; }
    internal static void Set_scavengingAttempts(PlayerStore store, int value)
    { var actor = Resolve(store); if (actor == null) store.scavengingAttempts = value; else actor.scavengingAttempts = value; }
    internal static int Get_dumpingGroundMajorWoundImmunitySearchesLeft(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.dumpingGroundMajorWoundImmunitySearchesLeft : actor.dumpingGroundMajorWoundImmunitySearchesLeft; }
    internal static void Set_dumpingGroundMajorWoundImmunitySearchesLeft(PlayerStore store, int value)
    { var actor = Resolve(store); if (actor == null) store.dumpingGroundMajorWoundImmunitySearchesLeft = value; else actor.dumpingGroundMajorWoundImmunitySearchesLeft = value; }
    internal static int Get_dumpingGroundMinorWoundImmunitySearchesLeft(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.dumpingGroundMinorWoundImmunitySearchesLeft : actor.dumpingGroundMinorWoundImmunitySearchesLeft; }
    internal static void Set_dumpingGroundMinorWoundImmunitySearchesLeft(PlayerStore store, int value)
    { var actor = Resolve(store); if (actor == null) store.dumpingGroundMinorWoundImmunitySearchesLeft = value; else actor.dumpingGroundMinorWoundImmunitySearchesLeft = value; }
    internal static HealthData Get_healthData(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.healthData : actor.healthData; }
    internal static void Set_healthData(PlayerStore store, HealthData value)
    { var actor = Resolve(store); if (actor == null) store.healthData = value; else actor.healthData = value; }
    internal static List<StartingPerk> Get_startingPerks(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.startingPerks : actor.startingPerks; }
    internal static void Set_startingPerks(PlayerStore store, List<StartingPerk> value)
    { var actor = Resolve(store); if (actor == null) store.startingPerks = value; else actor.startingPerks = value; }
    internal static List<string> Get_appraisalBlacklist(PlayerStore store)
    { var actor = Resolve(store); return actor == null ? store.appraisalBlacklist : actor.appraisalBlacklist; }
    internal static void Set_appraisalBlacklist(PlayerStore store, List<string> value)
    { var actor = Resolve(store); if (actor == null) store.appraisalBlacklist = value; else actor.appraisalBlacklist = value; }

    private sealed class Target
    {
        internal readonly int Token;
        internal readonly string Signature;
        internal readonly Site[] Sites;
        internal MethodBase Method;
        internal Target(int token, string signature, params Site[] sites)
        { Token = token; Signature = signature; Sites = sites; }
    }

    internal readonly struct Site
    {
        internal readonly int Offset, Field;
        internal readonly bool Write;
        internal Site(int offset, int field, bool write) { Offset = offset; Field = field; Write = write; }
    }

    // Public-to-this-assembly verification hook for offline checks. It performs
    // metadata reads only and neither patches methods nor invokes game code.
    internal static int VerifyNativeContract() => ValidateNativeAssembly().Length;

    private static Target[] ValidateNativeAssembly()
    {
        var module = typeof(PlayerStore).Module;
        if (module.ModuleVersionId != new Guid(ExpectedMvid)) throw Invalid("Native actor field module MVID changed");
        using (var stream = File.OpenRead(module.Assembly.Location))
        using (var sha = SHA256.Create())
            if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != ExpectedAssemblySha256)
                throw Invalid("Native actor field assembly hash changed");
        foreach (string name in FieldNames)
        {
            var field = typeof(PlayerStore).GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var actorField = typeof(NativeActorState).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null || field.IsStatic || actorField == null || field.FieldType != actorField.FieldType)
                throw Invalid("Native actor field contract changed: " + name);
            Accessor(field, false); Accessor(field, true);
        }
        var expected = Manifest().ToDictionary(target => target.Token);
        var found = new HashSet<int>();
        foreach (var type in module.Assembly.GetTypes())
        foreach (var method in Methods(type))
        {
            var sites = ReadNativeSites(method);
            if (sites.Length == 0) continue;
            if (!expected.TryGetValue(method.MetadataToken, out var target))
                throw Invalid("Unknown native actor field access: " + Describe(method));
            if (!found.Add(target.Token) || Describe(method) != target.Signature || sites.Length != target.Sites.Length)
                throw Invalid("Native actor method changed: " + target.Signature);
            for (int i = 0; i < sites.Length; i++)
                if (sites[i].Offset != target.Sites[i].Offset || sites[i].Field != target.Sites[i].Field || sites[i].Write != target.Sites[i].Write)
                    throw Invalid("Native actor operand changed: " + target.Signature + " IL_" + sites[i].Offset.ToString("X4"));
            target.Method = method;
        }
        if (found.Count != expected.Count) throw Invalid("Missing native actor methods from audited manifest");
        return expected.Values.OrderBy(target => target.Token).ToArray();
    }

    private static IEnumerable<MethodBase> Methods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
    }

    // Inspect all field operands, not source text. This detects new reads in
    // compiler-generated closures and unsupported address-taking operations.
    internal static Site[] ReadNativeSites(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes == null) return Array.Empty<Site>();
        var result = new List<Site>();
        int position = 0;
        while (position < bytes.Length)
        {
            int offset = position;
            short code = bytes[position++];
            if (code == 0xfe)
            {
                RequireBytes(bytes, position, 1);
                code = unchecked((short)(0xfe00 | bytes[position++]));
            }
            if (!Opcodes.TryGetValue(code, out var opcode)) throw Invalid("Unknown native IL opcode");
            int length;
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone: length = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: length = 1; break;
                case OperandType.InlineVar: length = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: length = 8; break;
                case OperandType.InlineSwitch:
                    RequireBytes(bytes, position, 4);
                    int count = BitConverter.ToInt32(bytes, position);
                    if (count < 0 || count > (bytes.Length - position - 4) / 4) throw Invalid("Invalid native IL switch");
                    length = 4 + count * 4; break;
                default: length = 4; break;
            }
            RequireBytes(bytes, position, length);
            if (opcode.OperandType == OperandType.InlineField)
            {
                var field = method.Module.ResolveField(BitConverter.ToInt32(bytes, position),
                    method.DeclaringType?.GetGenericArguments(), (method as MethodInfo)?.GetGenericArguments());
                if (IsActorField(field))
                {
                    if (opcode != OpCodes.Ldfld && opcode != OpCodes.Stfld)
                        throw Invalid("Unsupported native actor field opcode " + opcode + " in " + Describe(method));
                    result.Add(new Site(offset, field.MetadataToken, opcode == OpCodes.Stfld));
                }
            }
            position += length;
        }
        return result.ToArray();
    }

    private static void RequireBytes(byte[] bytes, int offset, int count)
    { if (count < 0 || offset > bytes.Length - count) throw Invalid("Truncated native IL operand"); }

    private static bool IsActorField(FieldInfo field) => field != null && field.DeclaringType == typeof(PlayerStore) && FieldNames.Contains(field.Name);

    private static MethodInfo Accessor(FieldInfo field, bool write)
    {
        var method = typeof(NativeActorFieldAccess).GetMethod((write ? "Set_" : "Get_") + field.Name, BindingFlags.Static | BindingFlags.NonPublic);
        Type[] parameters = write ? new[] { typeof(PlayerStore), field.FieldType } : new[] { typeof(PlayerStore) };
        if (method == null || method.ReturnType != (write ? typeof(void) : field.FieldType) ||
            !method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters))
            throw Invalid("Actor accessor stack contract mismatch: " + field.Name);
        return method;
    }

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        if (__originalMethod.Module != typeof(PlayerStore).Module || installedTargets == null ||
            !installedTargets.TryGetValue(__originalMethod.MetadataToken, out var target))
            throw Invalid("Unexpected actor field transpiler target");
        return Rewrite(instructions, target.Sites, target.Signature);
    }

    // Copies each instruction, including its labels and exception blocks.
    // Do not use CodeInstruction.Clone(), which intentionally drops those lists.
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, Site[] sites, string diagnostic)
    {
        var result = instructions.Select(instruction => new CodeInstruction(instruction)).ToList();
        var expected = sites.GroupBy(site => (site.Field, site.Write)).ToDictionary(group => group.Key, group => group.Count());
        var actual = new Dictionary<(int Field, bool Write), int>();
        for (int i = 0; i < result.Count; i++)
        {
            var instruction = result[i];
            if (instruction.operand is not FieldInfo field || !IsActorField(field)) continue;
            if (instruction.opcode != OpCodes.Ldfld && instruction.opcode != OpCodes.Stfld ||
                i > 0 && result[i - 1].opcode.OpCodeType == OpCodeType.Prefix)
                throw Invalid("Unsupported transformed actor field instruction: " + diagnostic);
            var key = (field.MetadataToken, instruction.opcode == OpCodes.Stfld);
            actual.TryGetValue(key, out int count); actual[key] = count + 1;
            instruction.opcode = OpCodes.Call;
            instruction.operand = Accessor(field, key.Item2);
        }
        if (actual.Count != expected.Count || actual.Any(pair => !expected.TryGetValue(pair.Key, out int count) || count != pair.Value))
            throw Invalid("Actor field operand count changed by an unknown patch: " + diagnostic);
        return result;
    }

    private static string TypeName(Type type)
    {
        if (type.IsByRef) return TypeName(type.GetElementType()) + "&";
        if (type.IsArray) return TypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericType) return type.GetGenericTypeDefinition().FullName.Replace('+', '/') + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        return type.FullName?.Replace('+', '/') ?? type.Name;
    }
    private static string Describe(MethodBase method) => TypeName((method as MethodInfo)?.ReturnType ?? typeof(void)) + " " +
        TypeName(method.DeclaringType) + "::" + method.Name + "(" + string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) + ")";
    private static InvalidDataException Invalid(string message) => new InvalidDataException(message);

    private static readonly string[] FieldNames = { "playerName", "appraiserLevel", "appraisalProgress", "appraisalPromotionDeclined", "appraisalPromotionDay", "scavengingAttempts", "dumpingGroundMajorWoundImmunitySearchesLeft", "dumpingGroundMinorWoundImmunitySearchesLeft", "healthData", "startingPerks", "appraisalBlacklist" };

    // Audited against VERSION 100-Rev13 / Steam build 26525552; 82 methods / 161 operands.
    private static Target[] Manifest() => new[]
    {
        new Target(0x060000C6, "System.Void EmporiumAnalytics::TrackStartingPerks()", new Site(86, 0x04000EF3, false)),
        new Target(0x06000151, "System.Void DumpingGroundLocation::RefreshLabels()", new Site(27, 0x04000EDF, false)),
        new Target(0x06000171, "System.Int32 HealthData::GetLuck()", new Site(12, 0x04000EDF, false)),
        new Target(0x06000174, "System.Boolean HealthData::IsSeriouslyWounded()", new Site(5, 0x04000EDF, false)),
        new Target(0x06000175, "System.Void HealthData::HandleNightlyWound()", new Site(5, 0x04000EDF, false), new Site(21, 0x04000EDF, false), new Site(38, 0x04000EDF, false), new Site(55, 0x04000EDF, false), new Site(74, 0x04000EDF, false), new Site(91, 0x04000EDF, false), new Site(108, 0x04000EDF, false), new Site(154, 0x04000EDF, false), new Site(178, 0x04000EDF, false), new Site(198, 0x04000EDF, false), new Site(214, 0x04000EDF, false), new Site(237, 0x04000EDF, false), new Site(255, 0x04000EDF, false), new Site(271, 0x04000EDF, false), new Site(288, 0x04000EDF, false), new Site(339, 0x04000EDF, false), new Site(431, 0x04000EDF, false), new Site(454, 0x04000EDF, false), new Site(503, 0x04000EDF, false), new Site(592, 0x04000EDF, false)),
        new Target(0x06000176, "System.Void HealthData::ReceiveMinorWound()", new Site(5, 0x04000EDF, false), new Site(21, 0x04000EDF, false), new Site(44, 0x04000EDF, false)),
        new Target(0x06000177, "System.Void HealthData::ReceiveMajorWound()", new Site(5, 0x04000EDF, false), new Site(21, 0x04000EDF, false), new Site(44, 0x04000EDF, false)),
        new Target(0x06000178, "System.Boolean HealthData::IsNicotineAddicted()", new Site(5, 0x04000EF3, false)),
        new Target(0x06000179, "System.Boolean HealthData::IsAlcoholAddicted()", new Site(5, 0x04000EF3, false)),
        new Target(0x0600017A, "System.Boolean HealthData::IsNarcoticAddicted()", new Site(5, 0x04000EF3, false)),
        new Target(0x0600017B, "System.Boolean HealthData::IsGamblingAddicted()", new Site(5, 0x04000EF3, false)),
        new Target(0x0600017C, "System.Boolean HealthData::IsSentinel()", new Site(5, 0x04000EF3, false)),
        new Target(0x06000331, "System.Boolean AquacultureItems::<InitMedicalRoe>g__CanUse|54_0(GameItem)", new Site(26, 0x04000EDF, false)),
        new Target(0x060008DB, "System.String AppraisalDebug::Block(System.String,System.Boolean)", new Site(258, 0x04000E7F, false), new Site(280, 0x04000E7F, true)),
        new Target(0x060008F4, "System.Boolean AppraisalItemRegistry::IsBlocked(AppraisalItemType,System.Int32)", new Site(5, 0x04000E7F, false)),
        new Target(0x060008FA, "System.Void AppraisalItemsWindow::SetAllowed(AppraisalItemType,System.Boolean)", new Site(34, 0x04000E7F, false), new Site(55, 0x04000E7F, true)),
        new Target(0x060008FB, "System.Int32 AppraisalRankHelper::GetRank()", new Site(5, 0x04000E7B, false)),
        new Target(0x06000908, "System.Boolean AppraisalRankHelper::CanProgress()", new Site(13, 0x04000E7D, false)),
        new Target(0x0600090A, "System.Boolean AppraisalRankHelper::IsPromotionReady()", new Site(12, 0x04000E7C, false)),
        new Target(0x0600090B, "System.Int32 AppraisalRankHelper::GetProgressPercent()", new Site(22, 0x04000E7C, false)),
        new Target(0x0600090C, "System.Void AppraisalRankHelper::AddProgress(System.Single)", new Site(85, 0x04000E7C, false), new Site(103, 0x04000E7C, true), new Site(113, 0x04000E7C, false), new Site(138, 0x04000E7E, true)),
        new Target(0x06000910, "System.Void AppraisalRankHelper::HandlePromotionClient(StoreClientManager)", new Site(25, 0x04000E7E, false)),
        new Target(0x06000911, "System.Void AppraisalRankHelper::Promote()", new Site(20, 0x04000E7B, true), new Site(39, 0x04000E7C, true), new Site(50, 0x04000E7E, true)),
        new Target(0x06000913, "System.Void AppraisalRankHelper::SetRank(System.Int32)", new Site(27, 0x04000E7B, true), new Site(46, 0x04000E7C, true), new Site(57, 0x04000E7E, true), new Site(68, 0x04000E7D, true)),
        new Target(0x06000914, "System.Void AppraisalRankHelper::DeclinePromotionForNow()", new Site(17, 0x04000E7E, true)),
        new Target(0x06000915, "System.Void AppraisalRankHelper::DeclinePromotionForGood()", new Site(6, 0x04000E7D, true), new Site(17, 0x04000E7E, true)),
        new Target(0x06000D29, "System.Void LiquidLuckHelper::Drink(GameItem,GameInventory)", new Site(24, 0x04000EDF, false), new Site(37, 0x04000EDF, false)),
        new Target(0x06000DB0, "System.String LouOperativeHelper::PlayerName()", new Site(5, 0x04000E1D, false)),
        new Target(0x06000E5A, "StartingPerk MindReprogrammerHelper::FindIncompatibleActivePerk(StartingPerk)", new Site(5, 0x04000EF3, false)),
        new Target(0x06000E5D, "System.Void MindReprogrammerHelper::Apply(GameItem,StartingPerk)", new Site(70, 0x04000EF3, false)),
        new Target(0x06000E5E, "System.Void MindReprogrammerHelper::OnPerkAdded(System.String)", new Site(18, 0x04000EDF, false), new Site(47, 0x04000EDF, false), new Site(76, 0x04000EDF, false), new Site(92, 0x04000EDF, false)),
        new Target(0x06000E5F, "System.Void MindReprogrammerHelper::OnPerkRemoved(System.String)", new Site(18, 0x04000EDF, false), new Site(47, 0x04000EDF, false), new Site(76, 0x04000EDF, false), new Site(92, 0x04000EDF, false)),
        new Target(0x06001037, "System.Void ScavHelper::ScavengeDumpingGrounds()", new Site(13, 0x04000EB2, false), new Site(27, 0x04000EB1, false), new Site(305, 0x04000EB2, false), new Site(312, 0x04000EB2, true), new Site(326, 0x04000EB1, false), new Site(333, 0x04000EB1, true), new Site(344, 0x04000EB0, false), new Site(351, 0x04000EB0, true)),
        new Target(0x06001040, "System.Void ScavHelper::ResetScavenging()", new Site(6, 0x04000EB0, true)),
        new Target(0x06001041, "System.Boolean ScavHelper::CanScavenge()", new Site(19, 0x04000EDF, false)),
        new Target(0x06001043, "System.Int32 ScavHelper::GetScavTimeLeft()", new Site(10, 0x04000EB0, false)),
        new Target(0x06001044, "System.Single ScavHelper::GetMinorWoundChance()", new Site(46, 0x04000EB0, false)),
        new Target(0x06001045, "System.Single ScavHelper::GetMajorWoundChance()", new Site(46, 0x04000EB0, false)),
        new Target(0x060015D4, "System.Boolean StartingPerk::IsPerkActive(System.String)", new Site(9, 0x04000EF3, false), new Site(45, 0x04000EF3, false)),
        new Target(0x060015D5, "System.Void StartingPerk::RemovePerk(System.String)", new Site(9, 0x04000EF3, false), new Site(38, 0x04000EF3, false), new Site(60, 0x04000EF3, false)),
        new Target(0x060015D6, "StartingPerk StartingPerk::GetPerkById(System.String)", new Site(9, 0x04000EF3, false), new Site(38, 0x04000EF3, false), new Site(60, 0x04000EF3, false)),
        new Target(0x06001647, "System.Void StartingPerkContent::HandleNewGamePerk()", new Site(391, 0x04000EDF, false), new Site(422, 0x04000EDF, false)),
        new Target(0x0600164E, "System.Void StartingPerkContent::HandleSecretAdmirerOnOpenShutter()", new Site(232, 0x04000EDF, false)),
        new Target(0x060017C3, "System.Void PlayerStore::StartNewGame()", new Site(194, 0x04000EDF, true), new Site(534, 0x04000EB1, true), new Site(542, 0x04000EB2, true), new Site(558, 0x04000EF3, true), new Site(588, 0x04000EDF, false)),
        new Target(0x060017EF, "System.Void PlayerStore::EndNight()", new Site(516, 0x04000EDF, false)),
        new Target(0x060017FC, "System.Void PlayerStore::HandleAfterhourDebuff()", new Site(1, 0x04000EDF, false)),
        new Target(0x060017C2, "System.Void PlayerStore::.ctor()", new Site(205, 0x04000E7E, true), new Site(216, 0x04000E7F, true), new Site(576, 0x04000EDF, true), new Site(609, 0x04000EF3, true)),
        new Target(0x06001D05, "System.Int32 StoreClientManager::GetNormalCustomerCount()", new Site(160, 0x04000EDF, false)),
        new Target(0x06001ECC, "ItemFeature ItemFeatureList::AddictionBuyPenalty()", new Site(80, 0x04000EDF, false)),
        new Target(0x06001ECD, "ItemFeature ItemFeatureList::AddictionSellPenalty()", new Site(80, 0x04000EDF, false)),
        new Target(0x06002037, "System.Void StoreShutterButton::EndAndClose()", new Site(78, 0x04000EDF, false)),
        new Target(0x06002428, "System.Void AdvCalendarUIManager::OnStatusButtonClicked()", new Site(272, 0x04000EF3, false)),
        new Target(0x060024D6, "System.Void DebugPanel::SetActiveAlcoholAddiction()", new Site(12, 0x04000EF3, false), new Site(32, 0x04000EDF, false), new Site(53, 0x04000EDF, false)),
        new Target(0x060026BD, "System.Void MapUIManager::OnGoingOutside(AfterhourLocation)", new Site(89, 0x04000EDF, false)),
        new Target(0x060026C9, "System.Void MapUIManager::UpdateScavUI()", new Site(20, 0x04000EDF, false)),
        new Target(0x06002825, "System.Void RunSummaryUI::BuildOverview(CustomUIBuilder,PlayerStore,RunStats)", new Site(1, 0x04000EF3, false), new Site(12, 0x04000EF3, false), new Site(87, 0x04000EF3, false)),
        new Target(0x060030D0, "System.Void GameItem::OnAddedToClientArea()", new Site(69, 0x04000EDF, false)),
        new Target(0x060030D2, "System.Void GameItem::OnAddedToWeightedArea(System.Boolean)", new Site(159, 0x04000EDF, false), new Site(290, 0x04000EDF, false)),
        new Target(0x06003BB9, "System.Boolean ModHelper::IsStartingPerkActive(System.String)", new Site(12, 0x04000EF3, false)),
        new Target(0x06003BBA, "System.Collections.Generic.List`1<System.String> ModHelper::GetStartingPerkIds()", new Site(18, 0x04000EF3, false), new Site(32, 0x04000EF3, false)),
        new Target(0x060040C7, "System.Void AquacultureItems/<>c::<InitMedicalRoe>b__54_3(GameItem,GameInventory,SlotMarker)", new Site(14, 0x04000EDF, false), new Site(30, 0x04000EDF, false), new Site(41, 0x04000EDF, false), new Site(74, 0x04000EDF, false)),
        new Target(0x060040C8, "System.Boolean AquacultureItems/<>c::<TilapiaSkinGraft>b__59_0(GameItem,GameInventory,SlotMarker)", new Site(13, 0x04000EDF, false)),
        new Target(0x060040C9, "System.Boolean AquacultureItems/<>c::<TilapiaSkinGraft>b__59_1(GameItem,GameInventory,SlotMarker)", new Site(13, 0x04000EDF, false)),
        new Target(0x060040CA, "System.Void AquacultureItems/<>c::<TilapiaSkinGraft>b__59_2(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false), new Site(21, 0x04000EDF, false), new Site(32, 0x04000EDF, false), new Site(65, 0x04000EDF, false)),
        new Target(0x06004100, "System.Void FoodItemDirectory/<>c__DisplayClass9_0::<RedBeer>b__0(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false)),
        new Target(0x060041D9, "System.Boolean MedsItemDirectory/<>c::<Bandage>b__15_0(GameItem,GameInventory,SlotMarker)", new Site(13, 0x04000EDF, false)),
        new Target(0x060041DA, "System.Boolean MedsItemDirectory/<>c::<Bandage>b__15_1(GameItem,GameInventory,SlotMarker)", new Site(13, 0x04000EDF, false)),
        new Target(0x060041DB, "System.Void MedsItemDirectory/<>c::<Bandage>b__15_2(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false), new Site(27, 0x04000EDF, false)),
        new Target(0x060041DC, "System.Boolean MedsItemDirectory/<>c::<HemostaticBandage>b__16_0(GameItem,GameInventory,SlotMarker)", new Site(13, 0x04000EDF, false)),
        new Target(0x060041DD, "System.Boolean MedsItemDirectory/<>c::<HemostaticBandage>b__16_1(GameItem,GameInventory,SlotMarker)", new Site(13, 0x04000EDF, false)),
        new Target(0x060041DE, "System.Void MedsItemDirectory/<>c::<HemostaticBandage>b__16_2(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false), new Site(21, 0x04000EDF, false), new Site(50, 0x04000EDF, false)),
        new Target(0x060041DF, "System.Boolean MedsItemDirectory/<>c::<TopicalBandage>b__17_0(GameItem,GameInventory,SlotMarker)", new Site(13, 0x04000EDF, false)),
        new Target(0x060041E0, "System.Boolean MedsItemDirectory/<>c::<TopicalBandage>b__17_1(GameItem,GameInventory,SlotMarker)", new Site(13, 0x04000EDF, false)),
        new Target(0x060041E1, "System.Void MedsItemDirectory/<>c::<TopicalBandage>b__17_2(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false), new Site(21, 0x04000EDF, false), new Site(50, 0x04000EDF, false)),
        new Target(0x060047A8, "System.Void ConsumableHelper/<>c::<InitNarcotic>b__3_3(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false)),
        new Target(0x060047AD, "System.Void ConsumableHelper/<>c::<InitAlcohol>b__5_3(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false)),
        new Target(0x060047B0, "System.Void ConsumableHelper/<>c::<InitNicotine>b__6_3(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false)),
        new Target(0x060047B3, "System.Void ConsumableHelper/<>c::<InitSynaprest>b__7_2(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false)),
        new Target(0x060047B6, "System.Void ConsumableHelper/<>c::<InitImmunivax>b__8_2(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false)),
        new Target(0x060047B8, "System.Void ConsumableHelper/<>c__DisplayClass4_0::<InitNarcoticRelief>b__2(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false)),
        new Target(0x06004F72, "System.Void WineHelper/<>c::<InitDrinkableWine>b__39_0(GameItem,GameInventory,SlotMarker)", new Site(5, 0x04000EDF, false)),
        new Target(0x06005C2A, "System.Void DebugPanel/<>c::<AddProceduralDebugFunction>b__115_15()", new Site(10, 0x04000E7B, false), new Site(21, 0x04000E7B, true)),
    };
}
