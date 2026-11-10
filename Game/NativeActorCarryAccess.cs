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
// The session supplies a resolver restricted to its exact native EmporiumEntry.
// Returning null leaves unrelated stores, save previews and solo games native.
internal static class NativeActorCarryAccess
{
    internal const string ExpectedMvid = "911508a6-e0eb-4cc0-91e8-e14b16ea28d7";
    internal const string ExpectedAssemblySha256 = "725CB999D019DFF1FF31BED353C7F9BB8D81A7605A15DE1F80F5A3A6CF7B1D05";
    private static readonly object Gate = new object();
    private static readonly MethodInfo TranspilerMethod = typeof(NativeActorCarryAccess).GetMethod(nameof(Transpile), BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(op => op.Value);
    private static Func<EmporiumEntry, NativeActorCarry> resolver;
    private static Harmony owner;
    private static Dictionary<int, Target> installedTargets;
    private static readonly List<MethodBase> patched = new List<MethodBase>();

    internal static bool IsInstalled => Volatile.Read(ref resolver) != null;

    internal static void Install(Harmony frameworkHarmony, Func<EmporiumEntry, NativeActorCarry> resolveActor)
    {
        if (frameworkHarmony == null) throw new ArgumentNullException(nameof(frameworkHarmony));
        if (resolveActor == null) throw new ArgumentNullException(nameof(resolveActor));
        lock (Gate)
        {
            if (owner != null) throw new InvalidOperationException("Actor carry access already installed or awaiting cleanup");
            // Complete all native metadata verification before the first patch.
            var targets = ValidateNativeAssembly();
            owner = frameworkHarmony;
            installedTargets = targets.Where(target => target.Token != SetupToken).ToDictionary(target => target.Token);
            try
            {
                foreach (var target in targets.Where(target => target.Token != SetupToken))
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
                catch (Exception cleanupError) { throw new AggregateException("Actor carry install failed; cleanup incomplete, routing disabled", installError, cleanupError); }
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
            if (!ReferenceEquals(owner, frameworkHarmony)) throw new InvalidOperationException("Actor carry patches belong to another framework Harmony instance");
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
        if (failures.Count != 0) throw new AggregateException("Actor carry patch cleanup failed", failures);
        installedTargets = null;
        owner = null;
    }

    private static NativeActorCarry Resolve(EmporiumEntry entry)
    {
        if (ReferenceEquals(entry, null)) throw new NullReferenceException();
        var bank = Volatile.Read(ref resolver)?.Invoke(entry);
        // A caller mistake must not redirect a different save preview/scene.
        return bank != null && !bank.IsDisposed && ReferenceEquals(bank.Source, entry) ? bank : null;
    }
    internal static GameSlotInventory Get_afterhourPocketSlotInvLeft(EmporiumEntry entry)
    { var bank = Resolve(entry); return bank == null ? entry.afterhourPocketSlotInvLeft : bank.Get(bank.CurrentActor, NativeActorInventory.CarrySlot.LeftPocket); }
    internal static GameSlotInventory Get_afterhourPocketSlotInvRight(EmporiumEntry entry)
    { var bank = Resolve(entry); return bank == null ? entry.afterhourPocketSlotInvRight : bank.Get(bank.CurrentActor, NativeActorInventory.CarrySlot.RightPocket); }
    internal static GameSlotInventory Get_afterhourPocketSlotInvBackpack(EmporiumEntry entry)
    { var bank = Resolve(entry); return bank == null ? entry.afterhourPocketSlotInvBackpack : bank.Get(bank.CurrentActor, NativeActorInventory.CarrySlot.Backpack); }
    internal static GameItem Get_selectedBackpack(EmporiumEntry entry)
    { var bank = Resolve(entry); return bank == null ? NativeActorCarry.ReadNativeSelected(entry) : bank.Selected(bank.CurrentActor); }
    internal static void Set_selectedBackpack(EmporiumEntry entry, GameItem item)
    { var bank = Resolve(entry); if (bank == null) NativeActorCarry.WriteNativeSelected(entry, item); else bank.SetSelected(bank.CurrentActor, item); }

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
        var module = typeof(EmporiumEntry).Module;
        if (module.ModuleVersionId != new Guid(ExpectedMvid)) throw Invalid("Native actor carry module MVID changed");
        using (var stream = File.OpenRead(module.Assembly.Location))
        using (var sha = SHA256.Create())
            if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != ExpectedAssemblySha256)
                throw Invalid("Native actor carry assembly hash changed");
        foreach (string name in FieldNames)
        {
            var field = typeof(EmporiumEntry).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null || field.IsStatic || field.FieldType != (name == SelectedName ? typeof(GameItem) : typeof(GameSlotInventory)))
                throw Invalid("Native carry field contract changed: " + name);
            Accessor(field, false);
            if (name == SelectedName) Accessor(field, true);
        }
        var expected = Manifest().ToDictionary(target => target.Token);
        var found = new HashSet<int>();
        foreach (var type in module.Assembly.GetTypes())
        foreach (var method in Methods(type))
        {
            var sites = ReadNativeSites(method);
            if (sites.Length == 0) continue;
            if (!expected.TryGetValue(method.MetadataToken, out var target))
                throw Invalid("Unknown native actor carry access: " + Describe(method));
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
                        throw Invalid("Unsupported native actor carry opcode " + opcode + " in " + Describe(method));
                    result.Add(new Site(offset, field.MetadataToken, opcode == OpCodes.Stfld));
                }
            }
            position += length;
        }
        return result.ToArray();
    }

    private static void RequireBytes(byte[] bytes, int offset, int count)
    { if (count < 0 || offset > bytes.Length - count) throw Invalid("Truncated native IL operand"); }

    private static bool IsActorField(FieldInfo field) => field != null && field.DeclaringType == typeof(EmporiumEntry) && FieldNames.Contains(field.Name);

    private static MethodInfo Accessor(FieldInfo field, bool write)
    {
        var method = typeof(NativeActorCarryAccess).GetMethod((write ? "Set_" : "Get_") + (field.Name == SelectedName ? "selectedBackpack" : field.Name), BindingFlags.Static | BindingFlags.NonPublic);
        Type[] parameters = write ? new[] { typeof(EmporiumEntry), field.FieldType } : new[] { typeof(EmporiumEntry) };
        if (method == null || method.ReturnType != (write ? typeof(void) : field.FieldType) ||
            !method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters))
            throw Invalid("Actor accessor stack contract mismatch: " + field.Name);
        return method;
    }

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        if (__originalMethod.Module != typeof(EmporiumEntry).Module || installedTargets == null ||
            !installedTargets.TryGetValue(__originalMethod.MetadataToken, out var target))
            throw Invalid("Unexpected actor carry transpiler target");
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
                throw Invalid("Unsupported transformed actor carry instruction: " + diagnostic);
            var key = (field.MetadataToken, instruction.opcode == OpCodes.Stfld);
            actual.TryGetValue(key, out int count); actual[key] = count + 1;
            instruction.opcode = OpCodes.Call;
            instruction.operand = Accessor(field, key.Item2);
        }
        if (actual.Count != expected.Count || actual.Any(pair => !expected.TryGetValue(pair.Key, out int count) || count != pair.Value))
            throw Invalid("Actor carry operand count changed by an unknown patch: " + diagnostic);
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

    private const string SelectedName = "<selectedBackpack>k__BackingField";
    private const int SetupToken = 0x060038CF;
    private static readonly string[] FieldNames = { SelectedName, "afterhourPocketSlotInvLeft", "afterhourPocketSlotInvRight", "afterhourPocketSlotInvBackpack" };

    // All 29 native accesses in six methods are verified. Setup's fourteen
    // accesses stay native: initialization owns the original local UI slots.
    // Only the fifteen runtime accesses in the other five methods are routed.
    private static Target[] Manifest() => new[]
    {
        new Target(0x06003864, "GameItem EmporiumEntry::get_selectedBackpack()", new Site(1, 0x040023BB, false)),
        new Target(0x06003865, "System.Void EmporiumEntry::set_selectedBackpack(GameItem)", new Site(2, 0x040023BB, true)),
        new Target(0x060038CA, "System.Void EmporiumEntry::TransferAfterhourToInv(System.Boolean)", new Site(1, 0x040023BF, false), new Site(15, 0x040023BF, false), new Site(31, 0x040023C0, false), new Site(45, 0x040023C0, false), new Site(61, 0x040023BE, false), new Site(75, 0x040023BE, false)),
        new Target(0x060038CB, "System.Void EmporiumEntry::TransferBackpackToInv()", new Site(22, 0x040023BF, false)),
        new Target(0x060038CC, "System.Collections.Generic.List`1<GameItem> EmporiumEntry::GetAllAfterhourOwnedItems()", new Site(76, 0x040023BF, false), new Site(89, 0x040023BF, false), new Site(158, 0x040023C0, false), new Site(171, 0x040023C0, false), new Site(243, 0x040023BE, false), new Site(256, 0x040023BE, false)),
        new Target(SetupToken, "System.Void EmporiumEntry::SetupAfterhourInv()", new Site(49, 0x040023BE, true), new Site(55, 0x040023BE, false), new Site(97, 0x040023BE, false), new Site(138, 0x040023BF, true), new Site(144, 0x040023BF, false), new Site(181, 0x040023C0, true), new Site(187, 0x040023C0, false), new Site(229, 0x040023C0, false), new Site(272, 0x040023BE, false), new Site(283, 0x040023BF, false), new Site(301, 0x040023C0, false), new Site(362, 0x040023BE, false), new Site(397, 0x040023BF, false), new Site(432, 0x040023C0, false)),
    };
}
