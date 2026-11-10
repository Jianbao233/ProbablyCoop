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
        internal Target(Type type,string name,Type[] parameters,MethodInfo consumer,params OpCode[] arguments)
        {
            Method=AccessTools.Method(type,name,parameters) ?? throw new MissingMethodException(type.FullName,name);
            Consumer=consumer ?? throw new ArgumentNullException(nameof(consumer));
            Arguments=arguments;
            if(Consumer.GetParameters().Length==0 || Consumer.GetParameters()[0].ParameterType!=typeof(GraphNodeStorage))
                throw new InvalidDataException("Carry query consumer no longer accepts a graph root.");
        }
    }
    private static readonly FieldInfo WindowField=AccessTools.Field(typeof(EmporiumEntry),nameof(EmporiumEntry.afterhourWindow));
    private static readonly MethodInfo ResolveMethod=AccessTools.Method(typeof(NativeActorCarryQueries),nameof(SceneRoot));
    private static readonly Lazy<Target[]> Contract=new Lazy<Target[]>(CreateContract);
    private static readonly Dictionary<short,OpCode> Opcodes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
        .Where(field=>field.FieldType==typeof(OpCode)).Select(field=>(OpCode)field.GetValue(null)).ToDictionary(opcode=>opcode.Value);

    private static Target[] CreateContract()
    {
        var enumerate=typeof(GraphUtils).GetMethods().Single(method=>method.Name==nameof(GraphUtils.FindAllChildrenType) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(GameItem));
        return new[] {
            new Target(typeof(ToolboxHelper),nameof(ToolboxHelper.FindToolboxInReach),Type.EmptyTypes,
                AccessTools.Method(typeof(ToolboxHelper),"FindToolboxIn",new[]{typeof(GraphNodeStorage)})),
            new Target(typeof(MachineStorageCoordinator),nameof(MachineStorageCoordinator.FindCoordinatorInReach),Type.EmptyTypes,
                AccessTools.Method(typeof(MachineStorageCoordinator),"FindCoordinatorIn",new[]{typeof(GraphNodeStorage)})),
            new Target(typeof(GraphUtils),nameof(GraphUtils.EmporiumTryAdd),new[]{typeof(GraphNodeStorage),typeof(GameItem)},
                AccessTools.Method(typeof(GraphUtils),"TryAddInto",new[]{typeof(GraphNodeStorage),typeof(GameItem)}),OpCodes.Ldarg_1),
            new Target(typeof(PlayerStore),nameof(PlayerStore.FindAllItem),new[]{typeof(bool)},enumerate,OpCodes.Ldnull,OpCodes.Ldnull),
            new Target(typeof(EmporiumEntry),nameof(EmporiumEntry.GetAllOwnedItems),new[]{typeof(bool),typeof(bool)},enumerate,OpCodes.Ldnull,OpCodes.Ldnull),
            new Target(typeof(EmporiumEntry),nameof(EmporiumEntry.GetAllItems),Type.EmptyTypes,enumerate,OpCodes.Ldnull,OpCodes.Ldnull)
        };
    }

    private static IEnumerable<MethodBase> TargetMethods()
    { VerifyNativeContract(); return Contract.Value.Select(target=>(MethodBase)target.Method); }

    internal static int VerifyNativeContract()
    {
        var module=typeof(EmporiumEntry).Module;
        if(module.ModuleVersionId!=new Guid(NativeActorCarryAccess.ExpectedMvid)) throw new InvalidDataException("Carry query native MVID changed.");
        using(var stream=File.OpenRead(module.Assembly.Location))
        using(var sha=SHA256.Create())
            if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")!=NativeActorCarryAccess.ExpectedAssemblySha256)
                throw new InvalidDataException("Carry query native assembly changed.");
        foreach(var target in Contract.Value) Rewrite(ReadOriginal(target.Method),target.Method);
        return Contract.Value.Length;
    }

    // Metadata-only IL read. Avoid invoking Harmony's dynamic method factory
    // just to verify a contract (its old mscorlib ILGenerator type also differs
    // from the netstandard reference facade used by native Mods).
    private static IEnumerable<CodeInstruction> ReadOriginal(MethodBase method)
    {
        var bytes=method.GetMethodBody()?.GetILAsByteArray() ?? throw new InvalidDataException("Missing carry query body.");
        int at=0;
        while(at<bytes.Length)
        {
            short code=bytes[at++];
            if(code==0xfe) { RequireBytes(bytes,at,1); code=unchecked((short)(0xfe00|bytes[at++])); }
            if(!Opcodes.TryGetValue(code,out var opcode)) throw new InvalidDataException("Unknown carry query IL.");
            int size;
            switch(opcode.OperandType)
            {
                case OperandType.InlineNone: size=0;break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: size=1;break;
                case OperandType.InlineVar: size=2;break;
                case OperandType.InlineI8: case OperandType.InlineR: size=8;break;
                case OperandType.InlineSwitch:
                    RequireBytes(bytes,at,4);int count=BitConverter.ToInt32(bytes,at);
                    if(count<0 || count>(bytes.Length-at-4)/4) throw new InvalidDataException("Invalid carry query switch.");
                    size=4+count*4;break;
                default:size=4;break;
            }
            RequireBytes(bytes,at,size);object operand=null;
            if(opcode.OperandType is OperandType.InlineField or OperandType.InlineMethod)
                operand=method.Module.ResolveMember(BitConverter.ToInt32(bytes,at),method.DeclaringType?.GetGenericArguments(),
                    (method as MethodInfo)?.GetGenericArguments());
            yield return new CodeInstruction(opcode,operand);at+=size;
        }
    }
    private static void RequireBytes(byte[] bytes,int offset,int count)
    { if(count<0 || offset>bytes.Length-count) throw new InvalidDataException("Truncated carry query IL."); }

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
            if(!Equals(result[i].operand,WindowField)) continue;
            if(result[i].opcode!=OpCodes.Ldfld || i>0 && result[i-1].opcode.OpCodeType==OpCodeType.Prefix)
                throw new InvalidDataException("Unsupported afterhour window access in carry query.");
            int call=i+1+target.Arguments.Length;
            if(call>=result.Count || result[call].opcode!=OpCodes.Call || !Equals(result[call].operand,target.Consumer))
                throw new InvalidDataException("Afterhour window is no longer passed directly to its audited graph consumer.");
            for(int a=0;a<target.Arguments.Length;a++)
                if(result[i+1+a].opcode!=target.Arguments[a])
                    throw new InvalidDataException("Carry graph consumer argument layout changed.");
            // The original receiver is already on the stack. Copy preserves
            // branch labels and exception boundaries on the field instruction.
            result[i].opcode=OpCodes.Call; result[i].operand=ResolveMethod; found++;
        }
        if(found!=1) throw new InvalidDataException("Expected exactly one afterhour graph root in "+method.Name);
        return result;
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
