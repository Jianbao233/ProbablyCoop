using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using ProbablyCoop;

internal static class ActorFieldTests
{
    private static readonly List<object> Results = new List<object>();
    internal static int RunAll()
    {
        try { Run(); }
        catch (Exception error) { Results.Add(new { name = "unexpected", status = "FAIL", error = error.ToString() }); }
        Console.WriteLine(JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        return Results.Any(value => value.ToString().Contains("FAIL")) ? 1 : 0;
    }
    private static void Check(string name, Action action)
    {
        try { action(); Results.Add(new { name, status = "PASS" }); }
        catch (Exception error) { Results.Add(new { name, status = "FAIL", error = error.ToString() }); }
    }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Check("default disabled", () => Equal(false, NativeActorFieldAccess.IsInstalled));
        Check("Rev13 native assembly MVID SHA and all 82 method operands", () => Equal(82, NativeActorFieldAccess.VerifyNativeContract()));
        var field = typeof(PlayerStore).GetField("appraiserLevel");
        var setter = typeof(NativeActorFieldAccess).GetMethod("Set_appraiserLevel", BindingFlags.NonPublic | BindingFlags.Static);
        var getter = typeof(NativeActorFieldAccess).GetMethod("Get_appraiserLevel", BindingFlags.NonPublic | BindingFlags.Static);
        var read = new NativeActorFieldAccess.Site(0, field.MetadataToken, false);
        var write = new NativeActorFieldAccess.Site(0, field.MetadataToken, true);
        var label = new DynamicMethod("labels", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
        var original = new CodeInstruction(OpCodes.Ldfld, field);
        original.labels.Add(label);
        original.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        Check("rewrite copy preserves labels and exception blocks", () =>
        {
            var result = NativeActorFieldAccess.Rewrite(new[] { original }, new[] { read }, "read").Single();
            Equal(OpCodes.Call, result.opcode); Equal(getter, result.operand); Equal(label, result.labels.Single());
            Equal(ExceptionBlockType.BeginExceptionBlock, result.blocks.Single().blockType);
            Equal(OpCodes.Ldfld, original.opcode); Equal(field, original.operand);
            if (ReferenceEquals(result.labels, original.labels) || ReferenceEquals(result.blocks, original.blocks)) throw new Exception("metadata list alias");
        });
        Check("typed setter rewrite", () => Equal(setter, NativeActorFieldAccess.Rewrite(new[] { new CodeInstruction(OpCodes.Stfld, field) }, new[] { write }, "write").Single().operand));
        Check("missing access rejected", () => Throws<InvalidDataException>(() => NativeActorFieldAccess.Rewrite(Array.Empty<CodeInstruction>(), new[] { read }, "missing")));
        Check("extra access rejected", () => Throws<InvalidDataException>(() => NativeActorFieldAccess.Rewrite(new[] { original, original }, new[] { read }, "extra")));
        Check("write read mismatch rejected", () => Throws<InvalidDataException>(() => NativeActorFieldAccess.Rewrite(new[] { original }, new[] { write }, "wrong op")));
        Check("address taking rejected", () => Throws<InvalidDataException>(() => NativeActorFieldAccess.Rewrite(new[] { new CodeInstruction(OpCodes.Ldflda, field) }, new[] { read }, "ldflda")));
        Check("prefix rejected", () => Throws<InvalidDataException>(() => NativeActorFieldAccess.Rewrite(new[] { new CodeInstruction(OpCodes.Volatile), original }, new[] { read }, "volatile")));
        var raw = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        var unrelated = (PlayerStore)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStore));
        var actor1 = NativeActorState.CreateDefault();
        var actor2 = NativeActorState.CreateDefault();
        var resolverField = typeof(NativeActorFieldAccess).GetField("resolver", BindingFlags.NonPublic | BindingFlags.Static);
        NativeActorState active = actor1;
        Check("raw fallback", () => { NativeActorFieldAccess.Set_appraiserLevel(raw, 3); Equal(3, raw.appraiserLevel); Equal(3, NativeActorFieldAccess.Get_appraiserLevel(raw)); });
        try
        {
            resolverField.SetValue(null, new Func<PlayerStore, NativeActorState>(store => ReferenceEquals(store, raw) ? active : null));
            Check("two actor selection no raw field swap", () =>
            {
                NativeActorFieldAccess.Set_appraiserLevel(raw, 11); Equal(11, actor1.appraiserLevel); Equal(3, raw.appraiserLevel);
                active = actor2; NativeActorFieldAccess.Set_appraiserLevel(raw, 22); Equal(22, NativeActorFieldAccess.Get_appraiserLevel(raw));
                active = actor1; Equal(11, NativeActorFieldAccess.Get_appraiserLevel(raw)); Equal(22, actor2.appraiserLevel);
            });
            Check("unrelated store remains native", () => { NativeActorFieldAccess.Set_appraiserLevel(unrelated, 7); Equal(7, unrelated.appraiserLevel); Equal(7, NativeActorFieldAccess.Get_appraiserLevel(unrelated)); Equal(11, actor1.appraiserLevel); });
            Check("all 11 typed field roundtrips and fallback", () =>
            {
                var names = (string[])typeof(NativeActorFieldAccess).GetField("FieldNames", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                Equal(11, names.Length);
                foreach (string name in names)
                {
                    var nativeField = typeof(PlayerStore).GetField(name);
                    object value = nativeField.FieldType == typeof(int) ? 63 : nativeField.FieldType == typeof(bool) ? true : nativeField.FieldType == typeof(double) ? 7.125d : nativeField.FieldType == typeof(string) ? "actor" : Activator.CreateInstance(nativeField.FieldType);
                    var set = typeof(NativeActorFieldAccess).GetMethod("Set_" + name, BindingFlags.NonPublic | BindingFlags.Static);
                    var get = typeof(NativeActorFieldAccess).GetMethod("Get_" + name, BindingFlags.NonPublic | BindingFlags.Static);
                    object rawBefore = nativeField.GetValue(raw);
                    set.Invoke(null, new[] { (object)raw, value }); Equal(value, get.Invoke(null, new[] { (object)raw })); Equal(rawBefore, nativeField.GetValue(raw));
                    set.Invoke(null, new[] { (object)unrelated, value }); Equal(value, nativeField.GetValue(unrelated));
                }
            });
            Check("null getter setter preserve native null receiver", () => { Throws<NullReferenceException>(() => NativeActorFieldAccess.Get_appraiserLevel(null)); Throws<NullReferenceException>(() => NativeActorFieldAccess.Set_appraiserLevel(null, 5)); });
            Check("rewritten emitted IL executes typed read write", () =>
            {
                var method = new DynamicMethod("actorSetAndGet", typeof(int), new[] { typeof(PlayerStore), typeof(int) }, typeof(Program), true);
                var instructions = new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Stfld, field), new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, field), new CodeInstruction(OpCodes.Ret) };
                var il = method.GetILGenerator();
                foreach (var instruction in NativeActorFieldAccess.Rewrite(instructions, new[] { read, write }, "execute"))
                    if (instruction.operand is MethodInfo call) il.Emit(instruction.opcode, call); else il.Emit(instruction.opcode);
                var callDelegate = (Func<PlayerStore, int, int>)method.CreateDelegate(typeof(Func<PlayerStore, int, int>));
                Equal(97, callDelegate(raw, 97)); Equal(97, actor1.appraiserLevel); Equal(3, raw.appraiserLevel);
                Equal(98, callDelegate(unrelated, 98)); Equal(98, unrelated.appraiserLevel);
                Throws<NullReferenceException>(() => callDelegate(null, 0));
            });
        }
        finally { resolverField.SetValue(null, null); }
        Check("test routing cleanup", () => { Equal(false, NativeActorFieldAccess.IsInstalled); Equal(3, NativeActorFieldAccess.Get_appraiserLevel(raw)); });
        Check("install rejects missing inputs", () => { Throws<ArgumentNullException>(() => NativeActorFieldAccess.Install(null, _ => null)); Throws<ArgumentNullException>(() => NativeActorFieldAccess.Uninstall(null)); });
    }
}
