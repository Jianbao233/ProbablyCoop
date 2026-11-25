using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;

namespace ProbablyCoop;

// Native helpers often use afterhourWindow as an inventory graph. Only these
// six audited graph consumers are redirected. UI identity/visibility/layout
// accesses and ordinary local input keep the real original PixelWindow.
[HarmonyPatch]
internal static class NativeActorCarryQueries
{
    private sealed class Target
    {
        internal readonly MethodInfo Method, Consumer;
        internal readonly OpCode[] Arguments;
        internal Target(MethodInfo method,MethodInfo consumer,params OpCode[] arguments)
        {
            Method=method ?? throw new InvalidDataException("Audited native carry query is missing.");
            Consumer=consumer ?? throw new ArgumentNullException(nameof(consumer));
            Arguments=arguments;
            if(Consumer.GetParameters().Length==0 || Consumer.GetParameters()[0].ParameterType!=typeof(GraphNodeStorage))
                throw new InvalidDataException("Carry query consumer no longer accepts a graph root.");
        }
    }
    private static readonly MethodInfo ResolveMethod=AccessTools.Method(typeof(NativeActorCarryQueries),nameof(SceneRoot));
    private static readonly Lazy<Target[]> Contract=new Lazy<Target[]>(CreateContract);
#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
    private static readonly Lazy<MethodInfo> AndroidAddItems = new(() => typeof(GraphUtils).GetMethods()
        .Where(method => method.Name == "AddAllChildrenType" && method.IsGenericMethodDefinition &&
            method.ReturnType == typeof(void) && method.GetGenericArguments().Length == 1)
        .Select(method => method.MakeGenericMethod(typeof(GameItem)))
        .SingleOrDefault(method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .SequenceEqual(new[] { typeof(GraphNodeStorage), typeof(List<GameItem>) })));
#endif

    private static Target[] CreateContract()
    {
        var enumerate=typeof(GraphUtils).GetMethods().Single(method=>method.Name==nameof(GraphUtils.FindAllChildrenType) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(GameItem));
        return new[] {
            new Target(AccessTools.Method(typeof(ToolboxHelper),nameof(ToolboxHelper.FindToolboxInReach),Type.EmptyTypes),
                AccessTools.Method(typeof(ToolboxHelper),"FindToolboxIn",new[]{typeof(GraphNodeStorage)})),
            new Target(AccessTools.Method(typeof(MachineStorageCoordinator),nameof(MachineStorageCoordinator.FindCoordinatorInReach),Type.EmptyTypes),
                AccessTools.Method(typeof(MachineStorageCoordinator),"FindCoordinatorIn",new[]{typeof(GraphNodeStorage)})),
            new Target(AccessTools.Method(typeof(GraphUtils),nameof(GraphUtils.EmporiumTryAdd),new[]{typeof(GraphNodeStorage),typeof(GameItem)}),
                AccessTools.Method(typeof(GraphUtils),"TryAddInto",new[]{typeof(GraphNodeStorage),typeof(GameItem)}),OpCodes.Ldarg_1),
            new Target(AccessTools.Method(typeof(PlayerStore),nameof(PlayerStore.FindAllItem),new[]{typeof(bool)}),enumerate,OpCodes.Ldnull,OpCodes.Ldnull),
            new Target(AccessTools.Method(typeof(EmporiumEntry),nameof(EmporiumEntry.GetAllOwnedItems),new[]{typeof(bool),typeof(bool)}),enumerate,OpCodes.Ldnull,OpCodes.Ldnull),
            new Target(AccessTools.Method(typeof(EmporiumEntry),nameof(EmporiumEntry.GetAllItems),Type.EmptyTypes),enumerate,OpCodes.Ldnull,OpCodes.Ldnull)
        };
    }

    [HarmonyPrepare]
    private static bool Prepare() => NativePlatform.SupportsAuthorityContract;

    private static IEnumerable<MethodBase> TargetMethods()
    { VerifyNativeContract(); return Contract.Value.Select(target=>(MethodBase)target.Method); }

    internal static int VerifyNativeContract()
    {
        var module=typeof(EmporiumEntry).Module;
        if(!NativePlatform.MatchesActorModule(module, NativeActorCarryAccess.ExpectedMvid)) throw new InvalidDataException("Carry query native MVID changed.");
        using(var stream=File.OpenRead(module.Assembly.Location))
        using(var sha=SHA256.Create())
            if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")!=NativeActorCarryAccess.ExpectedAssemblySha256)
                throw new InvalidDataException("Carry query native assembly changed.");
        // The port's Harmony translates native operands directly. Verify that
        // translated IL in the exact same Rewrite before activation.
        if (!NativePlatform.IsAuditedAndroidRuntime)
            foreach(var target in Contract.Value) Rewrite(ReadOriginal(target.Method),target.Method);
        return Contract.Value.Length;
    }

#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
    internal static bool HasPatchedContract()
    {
        var patch = AccessTools.Method(typeof(NativeActorCarryQueries), nameof(Transpiler));
        return Contract.Value.All(target =>
            Harmony.GetPatchInfo(target.Method)?.Transpilers.Any(entry => entry.PatchMethod == patch) == true);
    }
#endif
    // Metadata-only IL read. Avoid invoking Harmony's dynamic method factory
    // just to verify a contract (its old mscorlib ILGenerator type also differs
    // from the netstandard reference facade used by native Mods).
    private static IEnumerable<CodeInstruction> ReadOriginal(MethodBase method)
    {
        var bytes=method.GetMethodBody()?.GetILAsByteArray() ?? throw new InvalidDataException("Missing carry query body.");
        foreach(var instruction in NativeIl.Read(bytes))
        {
            var opcode=instruction.Opcode;
            object operand=null;
            if(opcode.OperandType is OperandType.InlineField or OperandType.InlineMethod)
                operand=method.Module.ResolveMember(BitConverter.ToInt32(bytes,instruction.OperandOffset),method.DeclaringType?.GetGenericArguments(),
                    (method as MethodInfo)?.GetGenericArguments());
            yield return new CodeInstruction(opcode,operand);
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
        => Rewrite(instructions,__originalMethod);

    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,MethodBase method)
    {
        var target=Contract.Value.SingleOrDefault(value=>Equals(value.Method,method))
            ?? throw new InvalidDataException("Unknown native carry query boundary.");
        var result=instructions.Select(instruction=>new CodeInstruction(instruction)).ToList();
        int found=0;
        for(int i=0;i<result.Count;i++)
        {
            if (result[i].operand is not FieldInfo field || field.DeclaringType != typeof(EmporiumEntry) ||
                field.Name != nameof(EmporiumEntry.afterhourWindow)) continue;
            if(result[i].opcode!=OpCodes.Ldfld || i>0 && result[i-1].opcode.OpCodeType==OpCodeType.Prefix)
                throw new InvalidDataException("Unsupported afterhour window access in carry query.");
            int call=i+1+target.Arguments.Length;
            bool matchesOriginal = call < result.Count && result[call].opcode == OpCodes.Call &&
                SameConsumer(result[call].operand as MethodInfo, target.Consumer);
            if (matchesOriginal)
            {
                for (int a = 0; a < target.Arguments.Length; a++)
                    if (result[i + 1 + a].opcode != target.Arguments[a])
                        throw new InvalidDataException("Carry graph consumer argument layout changed.");
            }
            else if (!MatchesAndroidListAppend(result, i))
            {
                string actual = string.Join(" | ", result.Skip(Math.Max(0, i - 1)).Take(7)
                    .Select(instruction => instruction.opcode + " " + instruction.operand));
                throw new InvalidDataException($"Afterhour graph consumer changed in {method.Name}: expected {target.Consumer}, found {actual}.");
            }
            // The original receiver is already on the stack. Copy preserves
            // branch labels and exception boundaries on the field instruction.
            result[i].opcode=OpCodes.Call; result[i].operand=ResolveMethod; found++;
        }
        if(found!=1) throw new InvalidDataException("Expected exactly one afterhour graph root in "+method.Name);
        return result;
    }

    private static bool SameConsumer(MethodInfo actual, MethodInfo expected)
    {
        if (actual == null || expected == null || actual.Name != expected.Name || actual.DeclaringType != expected.DeclaringType)
            return false;
        if (Equals(actual, expected)) return true;
#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
        // The port may hand Harmony a distinct reflection wrapper for the
        // same constructed generic method. Require the exact definition token
        // and argument types from this module in that case.
        if (NativePlatform.IsAuditedAndroidRuntime && actual?.IsGenericMethod == true && expected.IsGenericMethod &&
            actual.GetGenericArguments().SequenceEqual(expected.GetGenericArguments()))
        {
            var actualDefinition = actual.GetGenericMethodDefinition();
            var expectedDefinition = expected.GetGenericMethodDefinition();
            return actualDefinition.Module == expectedDefinition.Module &&
                actualDefinition.MetadataToken == expectedDefinition.MetadataToken;
        }
#endif
        return false;
    }

    private static bool MatchesAndroidListAppend(List<CodeInstruction> code, int field)
    {
#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
        // The port replaces append-from-temporary-list with an in-place
        // append. The receiver on the stack is still its first graph argument.
        if (!NativePlatform.IsAuditedAndroidRuntime || field + 2 >= code.Count ||
            code[field + 1].opcode != OpCodes.Ldloc_0 || code[field + 2].opcode != OpCodes.Call ||
            code[field + 2].operand is not MethodInfo call) return false;
        if (SameConsumer(call, AndroidAddItems.Value)) return true;
        return call.DeclaringType == typeof(EmporiumEntry) && call.Name == "PortAddOwned" &&
            call.IsStatic && !call.IsGenericMethod && call.ReturnType == typeof(void) &&
            call.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(new[] { typeof(GraphNodeStorage), typeof(List<GameItem>) });
#else
        return false;
#endif
    }

    internal static GraphNodeStorage SceneRoot(EmporiumEntry entry)
    {
        if(ReferenceEquals(entry,null)) throw new NullReferenceException();
        var session=NativeActorSession.Current;
        var bank=session?.Carry;
        // Default host queries (including automatic inspections) keep the
        // original range. Guest has no host Session; its real window is already
        // bound to the local partner. Only explicit partner scope needs a view.
        if(bank==null || bank.IsDisposed || !ReferenceEquals(bank.Source,entry) || bank.CurrentActor!=NativeActorRun.Slot.Partner)
            return entry.afterhourWindow;
        return new SceneGraph(entry,bank,NativeActorRun.Slot.Partner);
    }

    private sealed class SceneGraph : GraphNodeStorage
    {
        private readonly EmporiumEntry source;
        private readonly NativeActorCarry bank;
        private readonly NativeActorRun.Slot actor;
        internal SceneGraph(EmporiumEntry source,NativeActorCarry bank,NativeActorRun.Slot actor)
        { this.source=source; this.bank=bank; this.actor=actor; }
        public List<GraphNodeStorage> parents => new List<GraphNodeStorage>();
        public List<GraphNodeStorage> children => new List<GraphNodeStorage> {
            source.afterhourInventory,bank.Get(actor,NativeActorInventory.CarrySlot.LeftPocket),
            bank.Get(actor,NativeActorInventory.CarrySlot.Backpack),bank.Get(actor,NativeActorInventory.CarrySlot.RightPocket) };
        public void Validate() { } // This root has no frontend or physical parent.
        public bool AddParent(GraphNodeStorage node)=>false;
        public bool RemoveParent(GraphNodeStorage node)=>false;
        public bool MayHaveValidInventorySlot(GameItem item)=>children.Any(node=>node!=null && node.MayHaveValidInventorySlot(item));
        public SlotMarker TryFindOneValidInventorySlot(GameItem item)
        {
            // Same priority and last-equal-wins order as SetupAfterhourInv's
            // MachineryPanelRow: ground, left pocket, backpack, right pocket.
            SlotMarker best=null;
            foreach(var child in children)
            {
                var candidate=child?.TryFindOneValidInventorySlot(item);
                if(candidate!=null && candidate.numTransfer>0 && candidate.priority>=(best?.priority ?? int.MinValue)) best=candidate;
            }
            return best;
        }
    }
}
