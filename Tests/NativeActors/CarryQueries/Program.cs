using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;

internal class Program
{
    private static int checks;
    private static int Main(string[] args)
    {
        if(args.Length!=1 || !File.Exists(args[0])) throw new ArgumentException("Pass one independently built ProbablyCoop.dll path.");
        string modPath=Path.GetFullPath(args[0]);
        string managed=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"native-managed-path.txt")).Trim();
        AssemblyLoadContext.Default.Resolving += (_,name)=>
        {
            var path=name.Name=="ProbablyCoop" ? modPath :
                Path.Combine(managed,name.Name+".dll");
            return File.Exists(path)?AssemblyLoadContext.Default.LoadFromAssemblyPath(path):null;
        };
        try{Run();Console.WriteLine("CarryQueries PASS checks="+checks+" (real metadata and raw graph access; Unity NOT_RUN)");return 0;}
        catch(Exception e){Console.WriteLine(e);return 1;}
    }
    private static T Raw<T>()=>(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static object Raw(Type type)=>RuntimeHelpers.GetUninitializedObject(type);
    private static void Set(object obj,string field,object value)=>(obj.GetType().GetField(field,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)??throw new Exception("Missing "+field)).SetValue(obj,value);
    private static void Eq<T>(T expected,T actual){checks++;if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception("Expected "+expected+" got "+actual);}
    private static void Throws<T>(Action action) where T:Exception {checks++;try{action();}catch(T){return;}catch(TargetInvocationException e) when(e.InnerException is T){return;}throw new Exception("Expected "+typeof(T).Name);}
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var asm=Assembly.Load("ProbablyCoop");
        var queries=asm.GetType("ProbablyCoop.NativeActorCarryQueries");
        var verify=queries.GetMethod("VerifyNativeContract",BindingFlags.NonPublic|BindingFlags.Static);
        Eq(6,verify.Invoke(null,null));
        var resolve=queries.GetMethod("SceneRoot",BindingFlags.NonPublic|BindingFlags.Static);
        var rewrite=queries.GetMethod("Rewrite",BindingFlags.NonPublic|BindingFlags.Static);
        var method=typeof(ToolboxHelper).GetMethod("FindToolboxInReach");
        var consumer=typeof(ToolboxHelper).GetMethod("FindToolboxIn",BindingFlags.Static|BindingFlags.NonPublic);
        var field=typeof(EmporiumEntry).GetField("afterhourWindow");
        var load=new CodeInstruction(OpCodes.Ldfld,field);
        load.labels.Add(new DynamicMethod("querylabels",typeof(void),Type.EmptyTypes).GetILGenerator().DefineLabel());
        load.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        List<CodeInstruction> Rewrite(params CodeInstruction[] input)=>(List<CodeInstruction>)rewrite.Invoke(null,new object[]{input,method});
        var code=Rewrite(load,new CodeInstruction(OpCodes.Call,consumer));
        Eq(OpCodes.Call,code[0].opcode);Eq(resolve,code[0].operand);Eq(load.labels.Single(),code[0].labels.Single());
        Eq(ExceptionBlockType.BeginExceptionBlock,code[0].blocks.Single().blockType);Eq(OpCodes.Ldfld,load.opcode);
        Throws<InvalidDataException>(()=>Rewrite());Throws<InvalidDataException>(()=>Rewrite(load,new CodeInstruction(OpCodes.Call,consumer),load,new CodeInstruction(OpCodes.Call,consumer)));
        Throws<InvalidDataException>(()=>Rewrite(load,new CodeInstruction(OpCodes.Call,typeof(object).GetMethod("ToString",Type.EmptyTypes))));
        Throws<InvalidDataException>(()=>Rewrite(new CodeInstruction(OpCodes.Volatile),load,new CodeInstruction(OpCodes.Call,consumer)));
        var source=Raw<EmporiumEntry>();var other=Raw<EmporiumEntry>();
        source.afterhourWindow=Raw<PixelWindow>();other.afterhourWindow=Raw<PixelWindow>();
        GraphNodeStorage Resolve(EmporiumEntry entry)=>(GraphNodeStorage)resolve.Invoke(null,new object[]{entry});
        Eq(source.afterhourWindow,Resolve(source));Throws<NullReferenceException>(()=>Resolve(null));
        var bankType=asm.GetType("ProbablyCoop.NativeActorCarry");var bank=Raw(bankType);
        var actorType=asm.GetType("ProbablyCoop.NativeActorRun+Slot");var host=Enum.ToObject(actorType,0);var partner=Enum.ToObject(actorType,1);
        Delegate Actor(object value)=>Expression.Lambda(typeof(Func<>).MakeGenericType(actorType),Expression.Constant(value,actorType)).Compile();
        var slots=new GameSlotInventory[2,3];for(int a=0;a<2;a++)for(int s=0;s<3;s++)slots[a,s]=Raw<FakeSlot>();
        source.afterhourPocketSlotInvLeft=slots[0,0];source.afterhourPocketSlotInvRight=slots[0,1];source.afterhourPocketSlotInvBackpack=slots[0,2];source.afterhourInventory=Raw<FakeGrid>();
        Set(bank,"source",source);Set(bank,"slots",slots);Set(bank,"localActor",host);Set(bank,"currentActor",Actor(partner));Set(bank,"thread",Environment.CurrentManagedThreadId);
        var sessionType=asm.GetType("ProbablyCoop.NativeActorSession");var session=Raw(sessionType);Set(session,"<Carry>k__BackingField",bank);
        var current=sessionType.GetField("<Current>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic);
        try
        {
            current.SetValue(null,session);
            var graph=Resolve(source);Eq(false,ReferenceEquals(source.afterhourWindow,graph));Eq(4,graph.children.Count);
            Eq(source.afterhourInventory,graph.children[0]);Eq(slots[1,0],graph.children[1]);Eq(slots[1,2],graph.children[2]);Eq(slots[1,1],graph.children[3]);
            Eq(0,graph.parents.Count);Eq(false,graph.AddParent(source.afterhourWindow));Eq(false,graph.RemoveParent(source.afterhourWindow));
            Eq(other.afterhourWindow,Resolve(other));Eq(false,graph.MayHaveValidInventorySlot(null));
            var ground=(FakeGrid)source.afterhourInventory;ground.Marker=new SlotMarker{priority=4,numTransfer=1};
            foreach(var slot in new[]{(FakeSlot)slots[1,0],(FakeSlot)slots[1,1],(FakeSlot)slots[1,2]})slot.Marker=new SlotMarker{priority=0,numTransfer=1};
            Eq(true,graph.MayHaveValidInventorySlot(null));Eq(ground.Marker,graph.TryFindOneValidInventorySlot(null));
            ground.Marker.priority=0;Eq(((FakeSlot)slots[1,1]).Marker,graph.TryFindOneValidInventorySlot(null));
            ((FakeSlot)slots[1,1]).Marker.numTransfer=0;Eq(((FakeSlot)slots[1,2]).Marker,graph.TryFindOneValidInventorySlot(null));
            Set(bank,"currentActor",Actor(host));Eq(source.afterhourWindow,Resolve(source));
            Set(bank,"currentActor",Actor(partner));Set(bank,"disposed",true);Eq(source.afterhourWindow,Resolve(source));
        }
        finally{current.SetValue(null,null);}
        Eq(source.afterhourWindow,Resolve(source));
    }
    private sealed class FakeSlot:GameSlotInventory
    {
        public SlotMarker Marker;
        public override bool MayHaveValidInventorySlot(GameItem item)=>Marker?.numTransfer>0;
        public override SlotMarker TryFindOneValidInventorySlot(GameItem item)=>Marker;
    }
    private sealed class FakeGrid:GameGridInventory
    {
        public FakeGrid():base(1,1){} public SlotMarker Marker;
        public override bool MayHaveValidInventorySlot(GameItem item)=>Marker?.numTransfer>0;
        public override SlotMarker TryFindOneValidInventorySlot(GameItem item)=>Marker;
    }
}
