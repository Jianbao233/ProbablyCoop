using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProbablyCoop;

internal static class Program
{
    private static int checks;
    private static void Eq<T>(T expected,T actual) { checks++; if(!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception("Expected "+expected+", got "+actual); }
    private static void Throws<T>(Action action) where T:Exception { checks++; try { action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }
    private static GameItem Item(GameInventory root,int units=1,bool bag=false,bool owned=true)
    { var item=new GameItem { unitCount=units, Backpack=bag, Owned=owned }; root.Add(item); return item; }
    private static int Main()
    {
        try { Run(); Console.WriteLine("ActorCarry STUB LOGIC PASS checks="+checks+"; no Unity/native gameplay executed"); return 0; }
        catch(Exception e) { Console.WriteLine(e); return 1; }
    }
    private static void Run()
    {
        var source=new EmporiumEntry(); var localLeft=source.afterhourPocketSlotInvLeft;
        NativeActorRun.Slot actor=NativeActorRun.Slot.Host;
        var carry=new NativeActorCarry(source,()=>actor,NativeActorRun.Slot.Host);
        var partnerLeft=carry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.LeftPocket);
        var partnerRight=carry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.RightPocket);
        var partnerBag=carry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.Backpack);
        Eq(localLeft,carry.Get(NativeActorRun.Slot.Host,NativeActorInventory.CarrySlot.LeftPocket));
        Eq(source.afterhourPocketSlotInvBackpack,carry.Get(NativeActorRun.Slot.Host,NativeActorInventory.CarrySlot.Backpack));
        Eq(source.afterhourPocketSlotInvRight,carry.Get(NativeActorRun.Slot.Host,NativeActorInventory.CarrySlot.RightPocket));
        Eq(false,ReferenceEquals(partnerLeft,localLeft)); Eq(false,ReferenceEquals(partnerRight,partnerLeft));
        Eq(false,partnerLeft.isHidden); Eq(false,partnerBag.isHidden);
        Eq(false,partnerLeft.handler.parent.gameObject.Active); Eq(true,partnerLeft.handler.gameObject.Active);
        Eq(partnerLeft.handler.parent,partnerBag.handler.parent);
        Eq(3,partnerBag.Width); Eq(3,partnerBag.Height); Eq(true,partnerBag.StealUi);
        Eq("[item_expedition_box_label_pocket1]",partnerLeft.Tooltip); Eq("[item_expedition_box_label_pocket2]",partnerRight.Tooltip);
        Eq("pouch",partnerLeft.Sprite); Eq("simple_backpack_medium",partnerBag.Sprite);
        Eq(false,partnerLeft.Accept(new GameItem { Owned=false })); Eq(true,partnerLeft.Accept(new GameItem { Owned=true }));
        Eq(false,partnerBag.Accept(new GameItem { Owned=true,Backpack=false }));
        Eq(false,partnerBag.Accept(new GameItem { Owned=false,Backpack=true }));
        Eq(true,partnerBag.Accept(new GameItem { Owned=true,Backpack=true }));
        var hostSelected=new GameItem(); var partnerSelected=new GameItem();
        carry.SetSelected(NativeActorRun.Slot.Host,hostSelected); carry.SetSelected(NativeActorRun.Slot.Partner,partnerSelected);
        Eq(hostSelected,source.selectedBackpack); Eq(partnerSelected,carry.Selected(NativeActorRun.Slot.Partner));
        Eq(hostSelected,carry.Selected(NativeActorRun.Slot.Host));
        Throws<InvalidOperationException>(()=>carry.ReturnCarryToShop(NativeActorRun.Slot.Partner));
        actor=NativeActorRun.Slot.Partner;
        var bag=Item(source.invElement,1,bag:true); carry.SetSelected(actor,bag);
        var selectedResult=carry.TransferSelectedBackpack(actor);
        Eq(true,selectedResult.Complete); Eq(1,selectedResult.MovedUnits); Eq(bag,partnerBag.childItem); Eq(partnerBag,bag.parentInventory);
        Eq(null,carry.Selected(actor)); Eq(hostSelected,source.selectedBackpack);
        var otherBag=Item(source.invElement,1,bag:true); carry.SetSelected(actor,otherBag);
        var occupied=carry.TransferSelectedBackpack(actor);
        Eq(false,occupied.Complete); Eq("backpack-slot-occupied",occupied.Reason); Eq(otherBag,carry.Selected(actor)); Eq(source.invElement,otherBag.parentInventory);
        source.backInvinvElement.Capacity=0; source.invElement.Capacity=source.invElement.Units; source.showcaseElement.Capacity=0;
        var blocked=carry.ReturnCarryToShop(actor);
        Eq(false,blocked.Complete); Eq(0,blocked.MovedUnits); Eq(1,blocked.RemainingUnits); Eq(partnerBag,bag.parentInventory);
        source.backInvinvElement.Capacity=1;
        Eq(true,carry.ReturnCarryToShop(actor).Complete); Eq(source.backInvinvElement,bag.parentInventory);
        var stack=Item(partnerLeft,5);
        source.invElement.Capacity=source.invElement.Units+2; source.showcaseElement.Capacity=1;
        var partial=carry.ReturnCarryToShop(actor);
        Eq(false,partial.Complete); Eq(3,partial.MovedUnits); Eq(2,partial.RemainingUnits); Eq(2,stack.unitCount); Eq(partnerLeft,stack.parentInventory);
        source.showcaseElement.Capacity=5;
        Eq(true,carry.ReturnCarryToShop(actor).Complete); Eq(null,partnerLeft.childItem);
        var unowned=Item(source.invElement,1,bag:true,owned:false); carry.SetSelected(actor,unowned);
        Eq(false,carry.TransferSelectedBackpack(actor).Complete); Eq(unowned,carry.Selected(actor)); Eq(source.invElement,unowned.parentInventory);
        var hostBag=Item(source.afterhourPocketSlotInvLeft,1,bag:true); carry.SetSelected(actor,hostBag);
        Eq("other-actor-carry",carry.TransferSelectedBackpack(actor).Reason); Eq(source.afterhourPocketSlotInvLeft,hostBag.parentInventory);
        var cargo=Item(partnerRight); cargo.Nested.Add(new GameItem());
        Eq(2,carry.GetAllCarriedItems(actor).Count); Eq(3,carry.GetAllCarriedItems().Count);
        // Ground remains untouched even when every carry slot is returned.
        var ground=Item(source.afterhourInventory,2); source.invElement.Capacity=100;
        Eq(true,carry.ReturnCarryToShop(actor).Complete); Eq(source.afterhourInventory,ground.parentInventory); Eq(2,ground.unitCount);
        var live=Item(partnerRight);
        Throws<InvalidOperationException>(()=>carry.Dispose()); Eq(false,carry.IsDisposed); Eq(partnerRight,live.parentInventory);
        var remoteRender=partnerRight.handler; carry.DisposeForSceneUnload();
        Eq(true,carry.IsDisposed); Eq(true,remoteRender.gameObject.Destroyed); Eq(false,localLeft.handler.gameObject.Destroyed);
        Eq(partnerRight,live.parentInventory); Eq(localLeft,source.afterhourPocketSlotInvLeft);
        Throws<ObjectDisposedException>(()=>carry.Get(actor,NativeActorInventory.CarrySlot.LeftPocket));
        carry.DisposeForSceneUnload(); carry.Dispose();
        var guestSource=new EmporiumEntry(); var guest=new NativeActorCarry(guestSource,()=>NativeActorRun.Slot.Partner,NativeActorRun.Slot.Partner);
        Eq(guestSource.afterhourPocketSlotInvLeft,guest.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.LeftPocket));
        Eq(false,ReferenceEquals(guestSource.afterhourPocketSlotInvLeft,guest.Get(NativeActorRun.Slot.Host,NativeActorInventory.CarrySlot.LeftPocket)));
        guest.SetSelected(NativeActorRun.Slot.Partner,otherBag); Eq(otherBag,guestSource.selectedBackpack);
        var threadError=Task.Run(()=> { try { guest.Get(NativeActorRun.Slot.Host,NativeActorInventory.CarrySlot.LeftPocket); return false; } catch(InvalidOperationException) { return true; } }).Result;
        Eq(true,threadError);
        guestSource.afterhourPocketSlotInvLeft=new GameSlotInventory();
        Throws<InvalidOperationException>(()=>guest.Get(NativeActorRun.Slot.Host,NativeActorInventory.CarrySlot.LeftPocket));
        guest.DisposeForSceneUnload();
    }
}

// Explicit behavior doubles: constructors, rule callbacks and move semantics
// are modeled here. Native IL/accessor checks live in ActorCarryAccessTests.
namespace ProbablyCoop
{
    internal class NativeActorRun { internal enum Slot:byte {Host,Partner} }
    internal class NativeActorInventory { internal enum CarrySlot:byte {LeftPocket=4,RightPocket=5,Backpack=8} }
}
namespace UnityEngine
{
    public class Object { public static void Destroy(Object obj) { if(obj is GameObject go) go.Destroyed=true; } }
    public sealed class GameObject:Object { public bool Active=true,Destroyed; public void SetActive(bool active)=>Active=active; }
}
public interface GraphNodeStorage { }
public sealed class TreeNodeRender
{
    public UnityEngine.GameObject gameObject=new(); public TreeNodeRender parent;
    public static TreeNodeRender Instantiate()=>new(); public TreeNodeRender SetParent(TreeNodeRender node) {parent=node;return this;}
}
public class GameItem
{
    public int unitCount=1; public bool Owned=true,Backpack; public GameInventory parentInventory;
    public List<GameItem> Nested=new();
}
public class GameInventory:GraphNodeStorage
{
    public List<GameItem> Items=new(); public int Capacity=int.MaxValue;
    public int Units=>Items.Sum(i=>i.unitCount);
    public virtual bool Accept(GameItem item)=>true;
    public void Add(GameItem item) {item.parentInventory?.Items.Remove(item);Items.Add(item);item.parentInventory=this;}
}
public sealed class GameSlotInventory:GameInventory
{
    public TreeNodeRender handler=TreeNodeRender.Instantiate(); public bool isHidden,StealUi; public int Width,Height; public string Tooltip,Sprite;
    public Func<GameItem,bool> Rule=_=>true; public GameItem childItem=>Items.FirstOrDefault();
    public GameSlotInventory(int width=1,int height=1,bool stealUI=false) {Width=width;Height=height;StealUi=stealUI;}
    public void Hide(){isHidden=true;handler.gameObject.SetActive(false);}
    public GameSlotInventory SetTooltipName(string text){Tooltip=text;return this;}
    public GameSlotInventory SetBackgroundFadeSprite(string atlas,string sprite){Sprite=sprite;return this;}
    public override bool Accept(GameItem item)=>childItem==null&&Rule(item);
}
public sealed class EmporiumEntry
{
    public GameSlotInventory afterhourPocketSlotInvLeft=new(),afterhourPocketSlotInvRight=new(),afterhourPocketSlotInvBackpack=new(3,3,true);
    public GameInventory backInvinvElement=new(),invElement=new(),showcaseElement=new(),afterhourInventory=new();
    public GameItem selectedBackpack {get;set;}
}
public static class ContainerHelper
{
    public static void AllowOnlyOwnedItems(GameSlotInventory slot)=>slot.Rule=item=>item.Owned;
    public static void AllowOnlyTaggedItem(GameSlotInventory slot,string tag,bool ownedOnly)=>slot.Rule=item=>item.Backpack&&(!ownedOnly||item.Owned);
}
public static class LocHelper {public static string GetLocalizedItem(string value)=>value;}
public static class GraphUtils
{
    public static List<T> FindAllChildrenType<T>(GraphNodeStorage root) where T:class
    {
        var result=new List<T>();
        void Add(GameItem item){if(item is T typed)result.Add(typed);foreach(var nested in item.Nested)Add(nested);}
        foreach(var item in ((GameInventory)root).Items)Add(item);
        return result;
    }
    public static int TryAcceptAll(GraphNodeStorage destination,GameItem item,int amount=-1)
    {
        var target=(GameInventory)destination; if(amount<0)amount=item.unitCount;
        if(!target.Accept(item))return amount;
        int move=Math.Min(amount,Math.Max(0,target.Capacity-target.Units));
        if(move==item.unitCount){target.Add(item);}
        else if(move>0){item.unitCount-=move;target.Add(new GameItem{unitCount=move,Owned=item.Owned,Backpack=item.Backpack});}
        return amount-move;
    }
}
