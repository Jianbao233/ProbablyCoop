using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

// Tests the supplied built Mod, rather than another linked copy of its source.
// Only the Mod-owned NativeShop shell and pure DaySettlement are instantiated.
// NativeWorld is inspected as IL; its sizeable game-reflection initializer and
// its Unity-dependent execution path are deliberately never run.
internal static class DaySettlementBoundaryTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null))
        .GroupBy(op => op.Value).ToDictionary(g => g.Key, g => g.First());

    internal static void Run(string modAssemblyPath, string gameManagedPath)
    {
        string path = Path.GetFullPath(modAssemblyPath);
        if (!File.Exists(path)) throw new FileNotFoundException("Build the current Mod before running boundary checks.", path);
        var context = new ReferenceContext(Path.GetFullPath(gameManagedPath));
        int checks = 0;
        void Require(bool condition, string label)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("FAIL compiled day boundary: " + label);
        }
        try
        {
            Assembly mod = context.LoadFromAssemblyPath(path);
            Type shopType = mod.GetType("ProbablyCoop.NativeShop", true);
            Type attemptType = mod.GetType("ProbablyCoop.Core.DaySettlement", true);
            Type worldType = mod.GetType("ProbablyCoop.NativeWorld", true);
            Type phaseType = mod.GetType("ProbablyCoop.Core.ShopDay", true);
            Require(shopType.TypeInitializer == null, "NativeShop shell must have no static initializer");
            Require(attemptType.TypeInitializer == null, "DaySettlement must have no static initializer");
            object shop = RuntimeHelpers.GetUninitializedObject(shopType);
            FieldInfo phase = shopType.GetField("phase", Instance);
            FieldInfo settlement = shopType.GetField("settlement", Instance);
            FieldInfo failed = shopType.GetField("failedMutation", Instance);
            PropertyInfo blocked = shopType.GetProperty("BlocksWorldCommands", Instance);
            PropertyInfo pending = shopType.GetProperty("PendingSettlement", Instance);
            Require(phase != null && settlement != null && failed != null && blocked != null && pending != null,
                "compiled NativeShop boundary members exist");
            bool IsBlocked() => (bool)blocked.GetValue(shop);
            void Phase(string value) => phase.SetValue(shop, Enum.Parse(phaseType, value));
            object Attempt() => Activator.CreateInstance(attemptType, Instance, null,
                new object[] { new object(), "offline-boundary-run", 5, new object(), 125, 20d }, null);
            object Call(object attempt, string name, params object[] args) =>
                attemptType.GetMethod(name, Instance).Invoke(attempt, args);

            foreach (string value in new[] { "Playing", "Ready", "Saved" })
            { Phase(value); Require(!IsBlocked(), value + " without an attempt stays admitted"); }
            foreach (string value in new[] { "Settling", "SaveUnverified", "Ended" })
            { Phase(value); Require(IsBlocked(), value + " freezes world commands"); }
            Phase("Playing"); failed.SetValue(shop, true);
            Require(IsBlocked(), "failed native mutation freezes commands independently of phase");
            failed.SetValue(shop, false);

            object attempt = Attempt(); settlement.SetValue(shop, attempt);
            Require((bool)pending.GetValue(shop) && IsBlocked(), "pending attempt blocks even before presentation catches up");
            Require((bool)Call(attempt, "Expire", 21d), "same attempt reaches its timeout");
            Phase("SaveUnverified");
            Require((bool)pending.GetValue(shop) && IsBlocked(), "timeout keeps the in-flight attempt frozen");
            Call(attempt, "ConfirmMorning", true);
            Require(IsBlocked() && !(bool)Call(attempt, "TryComplete"), "morning alone cannot unlock commands");
            Call(attempt, "ConfirmSave", true);
            Require(IsBlocked(), "two receipts do not unlock before completion tick");
            Require((bool)Call(attempt, "TryComplete"), "late matching receipts finish the original attempt");
            Phase("Saved");
            Require(!(bool)pending.GetValue(shop) && !IsBlocked(), "completed attempt and Saved phase unlock together");

            attempt = Attempt(); settlement.SetValue(shop, attempt); Phase("SaveUnverified");
            Call(attempt, "ConfirmSave", false);
            Call(attempt, "ConfirmSave", true); Call(attempt, "ConfirmMorning", true);
            Require(!(bool)pending.GetValue(shop) && !(bool)Call(attempt, "TryComplete") && IsBlocked(),
                "failed attempt cannot revive or reopen world admission");

            MethodInfo execute = worldType.GetMethod("Execute", Instance, null, new[] { typeof(byte[]) }, null);
            Require(execute != null, "compiled NativeWorld Execute(byte[]) exists");
            List<Instruction> body = Read(execute);
            MethodInfo blockedGetter = blocked.GetGetMethod(true);
            int CallIndex(MethodBase method) => IndexOfCall(body, method);
            int gate = CallIndex(blockedGetter);
            Require(gate >= 0, "Execute calls the production BlocksWorldCommands getter");
            string[] downstream = { "ExecutePartnerSetup", "ValidOperationContext", "AcceptFrontendPrediction",
                "CaptureHostHand", "EnterPartnerOperation", "ExecuteHand", "ExecuteCore" };
            int firstDownstream = body.Count;
            foreach (string name in downstream)
            {
                MethodInfo method = worldType.GetMethods(Instance).Single(m => m.Name == name);
                int index = CallIndex(method);
                Require(index > gate, "global gate precedes " + name);
                firstDownstream = Math.Min(firstDownstream, index);
            }
            List<Instruction> guard = body.Skip(gate + 1).Take(firstDownstream - gate - 1).ToList();
            Require(guard.Any(i => Equals(i.Operand, "ending")) && guard.Any(i => Equals(i.Operand, "handRelease")),
                "ending and handRelease exceptions are evaluated inside the global guard");
            int busy = Convert.ToInt32(mod.GetType("ProbablyCoop.Core.ShopResult", true).GetField("Busy").GetRawConstantValue());
            int busyIndex = guard.FindIndex(i => Integer(i) == busy);
            Require(busyIndex >= 0 && guard.Skip(busyIndex + 1).Any(i =>
                i.Opcode == OpCodes.Leave || i.Opcode == OpCodes.Leave_S || i.Opcode == OpCodes.Ret),
                "guard emits Busy and exits before mutable command paths");

            // Host UI has native paths that do not pass through Execute. Read
            // their compiled IL without constructing NativeWorld or triggering
            // its initializer. These assertions check admission order, not Unity
            // behavior or a full control-flow proof.
            MethodInfo WorldMethod(string name) => worldType.GetMethods(Instance).Single(m => m.Name == name);
            void GateBeforeCalls(MethodInfo entry, params MethodBase[] guardedCalls)
            {
                List<Instruction> instructions = Read(entry);
                int boundary = IndexOfCall(instructions, blockedGetter);
                Require(boundary >= 0, entry.DeclaringType.Name + "." + entry.Name + " calls the shared world boundary");
                foreach (MethodBase guardedCall in guardedCalls)
                    Require(IndexOfCall(instructions, guardedCall) > boundary, entry.Name + " gate precedes " + guardedCall.Name);
            }
            GateBeforeCalls(WorldMethod("TravelRequest"), WorldMethod("ResetActorTrip"), WorldMethod("TravelProof"));
            GateBeforeCalls(WorldMethod("ActorTravelRequest"), WorldMethod("ResetActorTrip"), WorldMethod("TravelProof"));
            GateBeforeCalls(WorldMethod("RunShootingAction"), WorldMethod("CaptureHostHand"), WorldMethod("ChangeShooting"));
            GateBeforeCalls(WorldMethod("CanPickupShooting"), WorldMethod("get_ShootingOwnsCustomer"), WorldMethod("CanUseShootingInput"));
            GateBeforeCalls(worldType.GetProperty("HandBusinessOpen", Instance).GetGetMethod(true), WorldMethod("get_EndingActive"));
            Type scavenger = mod.GetType("ProbablyCoop.ReplicaScavengeRequest", true);
            GateBeforeCalls(scavenger.GetMethod("Prefix", Static), WorldMethod("RequestScavenge"));
            Type runtime = mod.GetType("ProbablyCoop.CoopRuntime", true);
            GateBeforeCalls(runtime.GetMethod("Update", Instance), shopType.GetProperty("Gate", Static).GetSetMethod(true));

            Console.WriteLine("PASS " + checks + " compiled Mod settlement/accessor and command IL assertions; sha256=" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) +
                "; NativeWorld not instantiated, no Unity/native methods or save IO executed");
        }
        finally { context.Unload(); }
    }

    private static int IndexOfCall(List<Instruction> body, MethodBase method) => body.FindIndex(i =>
        (i.Opcode == OpCodes.Call || i.Opcode == OpCodes.Callvirt) && i.Operand is int token && token == method.MetadataToken);

    private sealed class ReferenceContext : AssemblyLoadContext
    {
        private readonly string managed;
        internal ReferenceContext(string managed) : base("DaySettlementBoundary-" + Guid.NewGuid(), true)
        { this.managed = managed; }
        protected override Assembly Load(AssemblyName name)
        {
            // Resolve framework facades from this .NET test host. Loading the
            // game's Mono mscorlib would create a second System.Object universe.
            if (name.Name == "mscorlib" || name.Name == "netstandard" || name.Name == "System" ||
                name.Name.StartsWith("System.", StringComparison.Ordinal) || name.Name == "Microsoft.CSharp") return null;
            string path = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class Instruction
    {
        internal OpCode Opcode;
        internal object Operand;
    }

    private static int? Integer(Instruction instruction)
    {
        if (instruction.Opcode == OpCodes.Ldc_I4 || instruction.Opcode == OpCodes.Ldc_I4_S) return (int)instruction.Operand;
        if (instruction.Opcode == OpCodes.Ldc_I4_M1) return -1;
        string name = instruction.Opcode.Name;
        return name != null && name.Length == 8 && name.StartsWith("ldc.i4.", StringComparison.Ordinal) &&
            name[7] >= '0' && name[7] <= '8' ? name[7] - '0' : (int?)null;
    }

    private static List<Instruction> Read(MethodBase method)
    {
        byte[] bytes = method.GetMethodBody().GetILAsByteArray();
        var result = new List<Instruction>();
        int position = 0;
        while (position < bytes.Length)
        {
            short code = bytes[position++];
            if (code == 0xfe) code = (short)(0xfe00 | bytes[position++]);
            OpCode opcode = Opcodes[code];
            object operand = null;
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI: operand = (int)(sbyte)bytes[position++]; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineVar: operand = (int)bytes[position++]; break;
                case OperandType.InlineVar: operand = (int)BitConverter.ToUInt16(bytes, position); position += 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: position += 8; break;
                case OperandType.InlineSwitch:
                    int count = BitConverter.ToInt32(bytes, position); position += 4 + count * 4; break;
                case OperandType.InlineString:
                    operand = method.Module.ResolveString(BitConverter.ToInt32(bytes, position)); position += 4; break;
                default: operand = BitConverter.ToInt32(bytes, position); position += 4; break;
            }
            result.Add(new Instruction { Opcode = opcode, Operand = operand });
        }
        return result;
    }
}
