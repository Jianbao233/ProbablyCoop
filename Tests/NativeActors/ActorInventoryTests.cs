using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProbablyCoop;
using Inv = ProbablyCoop.NativeActorInventory;
using Root = ProbablyCoop.NativeActorInventory.RootKey;
using Slot = ProbablyCoop.NativeActorInventory.CarrySlot;
using Actor = ProbablyCoop.NativeActorInventory.ActorSlot;
using Decision = ProbablyCoop.NativeActorInventory.Decision;
internal static class ActorInventoryTests
{
    internal static int RunAll()
    {
        var results = new List<object>(); int failures = 0;
        void Test(string name, Action action) { try { action(); results.Add(new { name, status = "PASS" }); } catch (Exception e) { failures++; results.Add(new { name, status = "FAIL", error = e.ToString() }); } }
        void Equal<T>(T expected,T actual) { if(!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception($"expected {expected}, actual {actual}"); }
        void Throws<T>(Action action) where T:Exception { try { action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }
        Inv.RootBinding[] Roots() => new[] {
         new Inv.RootBinding(Root.Shared(1),1,false), new Inv.RootBinding(Root.Shared(3),3,false),
         new Inv.RootBinding(Root.Carried(Actor.Host,Slot.LeftPocket),14,true),new Inv.RootBinding(Root.Carried(Actor.Host,Slot.RightPocket),15,true),new Inv.RootBinding(Root.Carried(Actor.Host,Slot.Backpack),18,true),
         new Inv.RootBinding(Root.Carried(Actor.Partner,Slot.LeftPocket),24,true),new Inv.RootBinding(Root.Carried(Actor.Partner,Slot.RightPocket),25,true),new Inv.RootBinding(Root.Carried(Actor.Partner,Slot.Backpack),28,true)};
        Inv.PlacedItem[] Items() => new[] {new Inv.PlacedItem(101,1),new Inv.PlacedItem(102,1),new Inv.PlacedItem(103,3),new Inv.PlacedItem(104,14),new Inv.PlacedItem(105,28),new Inv.PlacedItem(106,201),new Inv.PlacedItem(107,202)};
        Inv.OwnedInventory[] Nested() => new[] {new Inv.OwnedInventory(201,101,false),new Inv.OwnedInventory(202,105,true)};
        Inv Graph() => Inv.Build(Roots(),Nested(),Items());
        var noHands=Array.Empty<Inv.HandView>();
        Test("six stable carry root keys and shared ground roundtrip",()=>{
         var keys = Roots().Select(r=>r.Key).ToArray();
         foreach(var key in keys) Equal(key,Root.Parse(key.ToString()));
         Equal("actor/0/pocket-left",Root.Carried(Actor.Host,Slot.LeftPocket).ToString());
         Equal("actor/1/backpack",Root.Carried(Actor.Partner,Slot.Backpack).ToString());
         Equal(Root.Shared(3),Root.NativeFixture(3,Actor.Partner)); Equal(Root.Carried(Actor.Partner,Slot.RightPocket),Root.NativeFixture(5,Actor.Partner));
         Equal(8,keys.Distinct().Count());
         Equal(Actor.Host,Inv.FromProtocolActor(1)); Equal(Actor.Partner,Inv.FromProtocolActor(2));
         Equal((byte)1,Inv.ToProtocolActor(Actor.Host)); Equal((byte)2,Inv.ToProtocolActor(Actor.Partner));
         Throws<ArgumentOutOfRangeException>(()=>Inv.FromProtocolActor(0));
         Throws<InvalidDataException>(()=>Root.Parse("actor/2/backpack"));
        });
        Test("legacy personal roots cannot be shared or defaulted",()=>{
         foreach(var place in new[]{4,5,8}) Throws<ArgumentOutOfRangeException>(()=>Root.Shared(place));
         Throws<ArgumentOutOfRangeException>(()=>Root.NativeFixture(4,(Actor)3));
         Throws<InvalidDataException>(()=>Root.Parse("world/4")); Throws<InvalidDataException>(()=>Root.Parse("actor/3/backpack"));
         Throws<InvalidDataException>(()=>Root.Parse("actor/1/hand")); Throws<InvalidDataException>(()=>Root.Parse("world/03"));
        });
        Test("empty personal roots required",()=>Throws<InvalidDataException>(()=>Inv.Build(Roots().Where(r=>r.Inventory!=25),Nested(),Items())));
        Test("duplicate root key rejected",()=>Throws<InvalidDataException>(()=>Inv.Build(Roots().Append(new Inv.RootBinding(Root.Shared(1),99,false)),Nested(),Items())));
        Test("same native root object cannot serve both actors even if empty",()=>{
         var roots=Roots(); roots[6]=new Inv.RootBinding(Root.Carried(Actor.Partner,Slot.RightPocket),15,true);
         Throws<InvalidDataException>(()=>Inv.Build(roots,Nested(),Items()));
        });
        Test("all personal roots must use native slot semantics",()=>{
         var roots=Roots(); roots[6]=new Inv.RootBinding(Root.Carried(Actor.Partner,Slot.RightPocket),25,false);
         Throws<InvalidDataException>(()=>Inv.Build(roots,Nested(),Items()));
        });
        Test("zero and cross kind entity ids rejected",()=>{
         Throws<InvalidDataException>(()=>Inv.Build(Roots(),Nested(),Items().Append(new Inv.PlacedItem(0,1))));
         Throws<InvalidDataException>(()=>Inv.Build(Roots(),Nested(),Items().Append(new Inv.PlacedItem(1,3))));
        });
        Test("duplicate owned item and two parent edges rejected",()=>Throws<InvalidDataException>(()=>Inv.Build(Roots(),Nested(),Items().Append(new Inv.PlacedItem(101,3)))));
        Test("root also used as nested inventory rejected",()=>Throws<InvalidDataException>(()=>Inv.Build(Roots(),Nested().Append(new Inv.OwnedInventory(14,102,true)),Items())));
        Test("missing parent detached item and wrong parent type rejected",()=>{
         Throws<InvalidDataException>(()=>Inv.Build(Roots(),Nested(),Items().Append(new Inv.PlacedItem(108,0))));
         Throws<InvalidDataException>(()=>Inv.Build(Roots(),Nested(),Items().Append(new Inv.PlacedItem(108,999))));
         Throws<InvalidDataException>(()=>Inv.Build(Roots(),Nested(),Items().Append(new Inv.PlacedItem(108,101))));
        });
        Test("cyclic container ownership rejected",()=>Throws<InvalidDataException>(()=>Inv.Build(Roots(),Nested().Append(new Inv.OwnedInventory(203,108,false)),Items().Append(new Inv.PlacedItem(108,203)))));
        Test("root slot and nested slot cannot own two items",()=>{
         Throws<InvalidDataException>(()=>Inv.Build(Roots(),Nested(),Items().Append(new Inv.PlacedItem(108,14))));
         Throws<InvalidDataException>(()=>Inv.Build(Roots(),Nested(),Items().Append(new Inv.PlacedItem(108,202))));
        });
        Test("nested contents inherit actual owner not renderer",()=>{
         var graph=Graph(); Equal(true,graph.TryRoot(106,out var shared)); Equal(Root.Shared(1),shared);
         Equal(true,graph.TryRoot(107,out var personal)); Equal(Root.Carried(Actor.Partner,Slot.Backpack),personal);
         Equal(28u,graph.RootEntity(personal)); Equal(202u,graph.Parent(107)); Equal(105u,graph.Parent(202));
         Equal(false,graph.TryRoot(999,out _));
        });
        Test("ownership graph detached from caller arrays",()=>{
         var roots=Roots(); var items=Items(); var graph=Inv.Build(roots,Nested(),items);
         roots[0]=default; items[0]=default;
         Equal(1u,graph.RootEntity(Root.Shared(1))); Equal(1u,graph.Parent(101));
        });
        Test("both actors can manipulate shared store and shared scene ground",()=>{
         foreach(var actor in new[]{Actor.Host,Actor.Partner}) {
          Equal(Decision.Allowed,Graph().CheckOperation(actor,new uint[]{102},3,0,noHands));
          Equal(Decision.Allowed,Graph().CheckOperation(actor,new uint[]{103},1,0,noHands));
         }
        });
        Test("personal root operations select actor rather than host slot",()=>{
         var graph=Graph(); Equal(Decision.Allowed,graph.CheckOperation(Actor.Partner,new uint[]{107},25,0,noHands));
         Equal(Decision.ForeignActorRoot,graph.CheckOperation(Actor.Host,new uint[]{107},15,0,noHands));
         Equal(Decision.ForeignActorRoot,graph.CheckOperation(Actor.Partner,new uint[]{102},15,0,noHands));
         Equal(Decision.Allowed,graph.CheckOperation(Actor.Host,new uint[]{104},1,0,noHands));
        });
        Test("moving a bag into own descendants rejected",()=>Equal(Decision.ContainmentCycle,Graph().CheckOperation(Actor.Host,new uint[]{101},201,0,noHands)));
        Test("moving parent and child together rejected",()=>Equal(Decision.InvalidSources,Graph().CheckOperation(Actor.Host,new uint[]{101,106},3,0,noHands)));
        Test("unknown actor entity invalid destination rejected",()=>{
         Equal(Decision.InvalidActor,Graph().CheckOperation((Actor)3,new uint[]{101},3,0,noHands));
         Equal(Decision.UnknownEntity,Graph().CheckOperation(Actor.Host,new uint[]{999},3,0,noHands));
         Equal(Decision.InvalidDestination,Graph().CheckOperation(Actor.Host,new uint[]{101},102,0,noHands));
        });
        var firstToken=new object(); var secondToken=new object();
        Inv.HandView Hold(Actor actor,object token,params uint[] ids)=>new Inv.HandView(actor,token,ids);
        Test("held root is a reservation and leaves physical parent unchanged",()=>{
         var graph=Graph(); var hand=Hold(Actor.Partner,secondToken,102);
         Equal(Decision.Allowed,graph.CheckHands(new[]{hand})); Equal(1u,graph.Parent(102));
         Equal(Root.Shared(1),graph.Roots.Single(r=>r.Inventory==1).Key);
        });
        Test("same item cannot be held by both actors",()=>Equal(Decision.HandConflict,Graph().CheckHands(new[]{Hold(Actor.Host,firstToken,102),Hold(Actor.Partner,secondToken,102)})));
        Test("held contents prevent another actor holding or moving their parent bag",()=>{
         var graph=Graph(); var other=Hold(Actor.Partner,secondToken,106);
         Equal(Decision.HandConflict,graph.CheckOperation(Actor.Host,new uint[]{101},3,0,new[]{other}));
         Equal(Decision.HandConflict,graph.CheckHands(new[]{Hold(Actor.Host,firstToken,101),other}));
        });
        Test("held bag protects contents and descendant destination but not siblings",()=>{
         var graph=Graph(); var hand=Hold(Actor.Partner,secondToken,101);
         Equal(Decision.HandConflict,graph.CheckOperation(Actor.Host,new uint[]{106},3,0,new[]{hand}));
         Equal(Decision.HandConflict,graph.CheckOperation(Actor.Host,new uint[]{102},201,0,new[]{hand}));
         Equal(Decision.Allowed,graph.CheckOperation(Actor.Host,new uint[]{102},3,0,new[]{hand}));
         Equal(Decision.Allowed,graph.CheckOperation(Actor.Host,new uint[]{103},1,0,new[]{hand}));
        });
        Test("sort whole inventory blocked by contained hold",()=>Equal(Decision.HandConflict,Graph().CheckOperation(Actor.Host,Array.Empty<uint>(),0,1,new[]{Hold(Actor.Partner,secondToken,106)})));
        Test("own drop consumes exact existing hand identity and items",()=>{
         var graph=Graph(); var hand=Hold(Actor.Partner,secondToken,102);
         Equal(Decision.HandConflict,graph.CheckOperation(Actor.Partner,new uint[]{102},3,0,new[]{hand}));
         Equal(Decision.Allowed,graph.CheckOperation(Actor.Partner,new uint[]{102},3,0,new[]{hand},secondToken));
         Equal(Decision.StaleHand,graph.CheckOperation(Actor.Partner,new uint[]{102},3,0,new[]{hand},new object()));
         Equal(Decision.StaleHand,graph.CheckOperation(Actor.Partner,new uint[]{101},3,0,new[]{hand},secondToken));
        });
        Test("stale foreign or malformed hand views rejected without locks",()=>{
         var graph=Graph();
         Equal(Decision.StaleHand,graph.CheckHands(new[]{Hold(Actor.Host,firstToken,107)}));
         Equal(Decision.StaleHand,graph.CheckHands(new[]{Hold(Actor.Host,firstToken,999)}));
         Equal(Decision.InvalidHands,graph.CheckHands(new[]{Hold(Actor.Host,firstToken,102),Hold(Actor.Host,secondToken,103)}));
         Equal(Decision.InvalidHands,graph.CheckHands(new[]{Hold(Actor.Host,firstToken,102,102)}));
         Equal(Decision.InvalidHands,graph.CheckHands(new[]{Hold(Actor.Host,null,102)}));
        });
        Test("hold arrays copied and bounded",()=>{
         var ids=new uint[]{102}; var hand=new Inv.HandView(Actor.Host,firstToken,ids); ids[0]=999; Equal(102u,hand.Items[0]);
         Equal(Decision.InvalidHands,Graph().CheckHands(new[]{new Inv.HandView(Actor.Host,firstToken,Enumerable.Range(1,40).Select(i=>(uint)i))}));
        });
        Test("cached ancestry still enforces total depth",()=>{
         var inventories=Nested().ToList(); var items=Items().ToList(); uint parent=1;
         for(uint i=0;i<34;i++) { uint item=1000+i, inv=2000+i; items.Add(new Inv.PlacedItem(item,parent)); inventories.Add(new Inv.OwnedInventory(inv,item,false)); parent=inv; }
         Throws<InvalidDataException>(()=>Inv.Build(Roots(),inventories,items));
        });
        Console.WriteLine(JsonSerializer.Serialize(new { component="NativeActorInventory",tests=results.Count,failures,results },new JsonSerializerOptions { WriteIndented=true }));
        return failures==0?0:1;
    }
}
