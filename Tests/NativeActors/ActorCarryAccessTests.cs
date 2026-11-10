using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProbablyCoop;

internal static class ActorCarryAccessTests
{
    private static int checks;
    internal static int RunAll()
    {
        try { Run(); Console.WriteLine("ActorCarryAccess PASS checks=" + checks + " (metadata and typed access; Unity not run)"); return 0; }
        catch (Exception e) { Console.WriteLine("ActorCarryAccess FAIL " + e); return 1; }
    }
    private static void Equal<T>(T expected, T actual)
    { checks++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
    private static void Throws<T>(Action action) where T : Exception
    { checks++; try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Set(object instance, string field, object value) => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Equal(false, NativeActorCarryAccess.IsInstalled);
        Equal(6, NativeActorCarryAccess.VerifyNativeContract());
        int total = 0;
        foreach (var method in typeof(EmporiumEntry).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            total += NativeActorCarryAccess.ReadNativeSites(method).Length;
        Equal(29, total);
        var left = typeof(EmporiumEntry).GetField("afterhourPocketSlotInvLeft");
        var selected = typeof(EmporiumEntry).GetField("<selectedBackpack>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        var read = new NativeActorCarryAccess.Site(0, left.MetadataToken, false);
        var write = new NativeActorCarryAccess.Site(0, selected.MetadataToken, true);
        var original = new CodeInstruction(OpCodes.Ldfld, left);
        original.labels.Add(new DynamicMethod("carryLabel", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel());
        original.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        var rewritten = NativeActorCarryAccess.Rewrite(new[] { original }, new[] { read }, "carry read").Single();
        Equal(OpCodes.Call, rewritten.opcode);
        Equal("Get_afterhourPocketSlotInvLeft", ((MethodInfo)rewritten.operand).Name);
        Equal(original.labels.Single(), rewritten.labels.Single());
        Equal(ExceptionBlockType.BeginExceptionBlock, rewritten.blocks.Single().blockType);
        Equal(OpCodes.Ldfld, original.opcode);
        Equal(false, ReferenceEquals(original.labels, rewritten.labels));
        Equal("Set_selectedBackpack", ((MethodInfo)NativeActorCarryAccess.Rewrite(new[] { new CodeInstruction(OpCodes.Stfld, selected) }, new[] { write }, "selected write").Single().operand).Name);
        Throws<InvalidDataException>(() => NativeActorCarryAccess.Rewrite(Array.Empty<CodeInstruction>(), new[] { read }, "missing"));
        Throws<InvalidDataException>(() => NativeActorCarryAccess.Rewrite(new[] { original, original }, new[] { read }, "extra"));
        Throws<InvalidDataException>(() => NativeActorCarryAccess.Rewrite(new[] { new CodeInstruction(OpCodes.Ldflda, left) }, new[] { read }, "address"));
        Throws<InvalidDataException>(() => NativeActorCarryAccess.Rewrite(new[] { new CodeInstruction(OpCodes.Volatile), original }, new[] { read }, "prefix"));
        var native = Raw<EmporiumEntry>();
        var other = Raw<EmporiumEntry>();
        native.afterhourPocketSlotInvLeft = Raw<GameSlotInventory>();
        native.afterhourPocketSlotInvRight = Raw<GameSlotInventory>();
        native.afterhourPocketSlotInvBackpack = Raw<GameSlotInventory>();
        other.afterhourPocketSlotInvLeft = Raw<GameSlotInventory>();
        var actor = NativeActorRun.Slot.Host;
        var bank = Raw<NativeActorCarry>();
        var slots = new GameSlotInventory[2,3];
        slots[0,0] = native.afterhourPocketSlotInvLeft; slots[0,1] = native.afterhourPocketSlotInvRight; slots[0,2] = native.afterhourPocketSlotInvBackpack;
        for (int i=0;i<3;i++) slots[1,i]=Raw<GameSlotInventory>();
        Set(bank,"source",native); Set(bank,"currentActor",new Func<NativeActorRun.Slot>(()=>actor)); Set(bank,"localActor",NativeActorRun.Slot.Host);
        Set(bank,"slots",slots); Set(bank,"selected",new GameItem[2]); Set(bank,"thread",Environment.CurrentManagedThreadId);
        var hostItem = Raw<GameItemElement>(); var partnerItem=Raw<GameItemElement>();
        Equal(native.afterhourPocketSlotInvLeft, NativeActorCarryAccess.Get_afterhourPocketSlotInvLeft(native));
        NativeActorCarryAccess.Set_selectedBackpack(native,hostItem);
        Equal(hostItem, NativeActorCarry.ReadNativeSelected(native));
        var resolver = typeof(NativeActorCarryAccess).GetField("resolver",BindingFlags.Static|BindingFlags.NonPublic);
        try
        {
            // Intentionally return the wrong bank for unrelated receivers. The
            // wrapper's own identity guard must still retain native semantics.
            resolver.SetValue(null,new Func<EmporiumEntry,NativeActorCarry>(_=>bank));
            Equal(slots[0,0],NativeActorCarryAccess.Get_afterhourPocketSlotInvLeft(native));
            actor=NativeActorRun.Slot.Partner;
            Equal(slots[1,0],NativeActorCarryAccess.Get_afterhourPocketSlotInvLeft(native));
            Equal(slots[1,1],NativeActorCarryAccess.Get_afterhourPocketSlotInvRight(native));
            Equal(slots[1,2],NativeActorCarryAccess.Get_afterhourPocketSlotInvBackpack(native));
            Equal(slots[0,0],native.afterhourPocketSlotInvLeft);
            NativeActorCarryAccess.Set_selectedBackpack(native,partnerItem);
            Equal(partnerItem,NativeActorCarryAccess.Get_selectedBackpack(native));
            Equal(hostItem,NativeActorCarry.ReadNativeSelected(native));
            actor=NativeActorRun.Slot.Host;
            Equal(hostItem,NativeActorCarryAccess.Get_selectedBackpack(native));
            Equal(other.afterhourPocketSlotInvLeft,NativeActorCarryAccess.Get_afterhourPocketSlotInvLeft(other));
            NativeActorCarryAccess.Set_selectedBackpack(other,partnerItem);
            Equal(partnerItem,NativeActorCarry.ReadNativeSelected(other));
            Equal(hostItem,NativeActorCarry.ReadNativeSelected(native));
            Throws<NullReferenceException>(()=>NativeActorCarryAccess.Get_afterhourPocketSlotInvLeft(null));
            Throws<NullReferenceException>(()=>NativeActorCarryAccess.Set_selectedBackpack(null,null));
            Set(bank,"disposed",true);
            Equal(native.afterhourPocketSlotInvLeft,NativeActorCarryAccess.Get_afterhourPocketSlotInvLeft(native));
        }
        finally { resolver.SetValue(null,null); }
        Equal(false,NativeActorCarryAccess.IsInstalled);
        Throws<ArgumentNullException>(()=>NativeActorCarryAccess.Install(null,_=>null));
        Throws<ArgumentNullException>(()=>NativeActorCarryAccess.Uninstall(null));
    }
}
