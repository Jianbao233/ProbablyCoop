using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RootKey = ProbablyCoop.NativeActorInventory.RootKey;
using ActorSlot = ProbablyCoop.NativeActorInventory.ActorSlot;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private NativeActorCarry guestActorCarry;
    private NativeActorCarry qaActorCarry;
    internal NativeActorCarry ActorCarry => host ? NativeActorSession.Current?.Carry ?? qaActorCarry : guestActorCarry;
    private NativeActorRun.Slot OperationActor => !host ? NativeActorRun.Slot.Partner :
        actorRun?.CurrentActor ?? NativeActorRun.Slot.Host;

    private IEnumerable<(RootKey Key,GameInventory Inventory)> PhysicalRoots(bool actors)
    {
        for(int place=1;place<=NativeRootProjection.NativePlaces;place++)
        {
            if(actors && place is 4 or 5 or 8)
            {
                foreach(var owner in new[]{ActorSlot.Host,ActorSlot.Partner})
                {
                    var key=RootKey.Carried(owner,(NativeActorInventory.CarrySlot)place);
                    var inventory=PhysicalRoot(key);
                    if(inventory==null) throw new InvalidOperationException("Missing actor carry inventory: "+key);
                    yield return (key,inventory);
                }
            }
            else
            {
                var key=RootKey.NativeFixture(place,ActorSlot.Host);
                var inventory=NativeFixtureRoot(place);
                if(inventory!=null) yield return (key,inventory);
            }
        }
    }

    private GameInventory PhysicalRoot(RootKey key) => key.Personal && ActorCarry!=null
        ? ActorCarry.Get((NativeActorRun.Slot)(byte)key.Owner.Value,(NativeActorInventory.CarrySlot)key.Place) : NativeFixtureRoot(key.Place);

    private IDisposable EnterPartnerOperation() => host ? actorRun?.Enter(NativeActorRun.Slot.Partner) : null;

    // Only follow physical ownership: inventory -> nearest owning item ->
    // item.parentInventory. Render windows are not alternative inventory roots.
    private bool TryPhysicalRoot(GameInventory inventory,out RootKey key)
    {
        key=default;
        if(inventory==null) return false;
        var roots=PhysicalRoots(HasActorProjection).ToDictionary(row=>row.Inventory,row=>row.Key);
        var visited=new HashSet<GameInventory>();
        for(int depth=0;depth<NativeActorInventory.MaximumOwnershipDepth && visited.Add(inventory);depth++)
        {
            if(roots.TryGetValue(inventory,out key)) return true;
            var owner=PhysicalOwner(inventory);
            if(owner?.parentInventory==null) return false;
            inventory=owner.parentInventory;
        }
        return false;
    }

    private static GameItem PhysicalOwner(GameInventory inventory)
    {
        var seen=new HashSet<GraphNodeStorage>();
        var queue=new Queue<GraphNodeStorage>(inventory.parents);
        GameItem owner=null;
        while(queue.Count>0)
        {
            var node=queue.Dequeue();
            if(node==null || !seen.Add(node)) continue;
            if(seen.Count>NativeActorInventory.MaximumOwnershipDepth) return null;
            if(node is GameItem item)
            {
                if(owner!=null && !ReferenceEquals(owner,item)) return null;
                owner=item; continue;
            }
            // Crossing another inventory would treat its UI ancestry as the
            // immediate owner of this one. Such an ambiguous path is not valid.
            if(node is GameInventory) return null;
            foreach(var parent in node.parents) queue.Enqueue(parent);
        }
        return owner;
    }

    private bool ActorCanAccess(GameInventory inventory,out int place)
    {
        place=0;
        if(!TryPhysicalRoot(inventory,out var key)) return false;
        if(key.Personal && (byte)key.Owner.Value!=(byte)OperationActor) return false;
        place=key.Place;
        return true;
    }

    private NativeActorInventory CaptureOwnershipGraph()
    {
        var bindings=PhysicalRoots(true).ToArray();
        var roots=bindings.Select(row=>new NativeActorInventory.RootBinding(row.Key,model.Id(row.Inventory),row.Inventory is GameSlotInventory)).ToArray();
        var rootSet=new HashSet<GameInventory>(bindings.Select(row=>row.Inventory));
        var inventories=new Dictionary<GameInventory,GameItem>();
        var items=new Dictionary<GameItem,GameInventory>();
        void Walk(GameInventory inventory,int depth)
        {
            if(depth>NativeActorInventory.MaximumOwnershipDepth) throw new InvalidDataException("Actor physical inventory depth exceeded");
            foreach(var item in inventory.childItems)
            {
                if(item==null || item.parentInventory!=inventory || items.ContainsKey(item))
                    throw new InvalidDataException("Actor physical inventory contains an aliased child");
                items.Add(item,inventory);
                if(item.children.Count==0) continue;
                foreach(var nested in PhysicalInventories(item,rootSet))
                {
                    if(rootSet.Contains(nested) || inventories.ContainsKey(nested)) throw new InvalidDataException("Actor nested inventory has multiple owners");
                    inventories.Add(nested,item); Walk(nested,depth+1);
                }
            }
        }
        foreach(var binding in bindings) Walk(binding.Inventory,0);
        return NativeActorInventory.Build(roots, inventories.Select(row=>new NativeActorInventory.OwnedInventory(model.Id(row.Key),model.Id(row.Value),row.Key is GameSlotInventory)),
            items.Select(row=>new NativeActorInventory.PlacedItem(model.Id(row.Key),model.Id(row.Value))));
    }

    // Scan only the item's presentation/layout shell. Stop at inventories:
    // their physical children are visited by Walk above, never through stolen
    // UI or another item's display window. Native GraphUtils has no visited
    // guard and rescans every descendant for every ancestor container.
    private static IEnumerable<GameInventory> PhysicalInventories(GameItem item,HashSet<GameInventory> rootSet)
    {
        var seen=new HashSet<GraphNodeStorage>();
        var queue=new Queue<(GraphNodeStorage Node,int Depth)>();
        foreach(var child in item.children) queue.Enqueue((child,1));
        while(queue.Count>0)
        {
            var (node,depth)=queue.Dequeue();
            if(node==null || !seen.Add(node)) continue;
            if(depth>NativeActorInventory.MaximumOwnershipDepth || seen.Count>NativeActorInventory.MaximumEntities)
                throw new InvalidDataException("Actor inventory layout traversal exceeded its bound");
            if(node is GameItem) continue; // a renderer alias is not ownership
            if(node is GameInventory inventory)
            {
                // Some UI can expose a known world root. Its items already
                // belong to that root; never reinterpret the window as owner.
                if(rootSet.Contains(inventory)) continue;
                var owner=PhysicalOwner(inventory);
                if(ReferenceEquals(owner,item)) yield return inventory;
                else if(owner==null && inventory.childItems.Count>0)
                    throw new InvalidDataException("Populated inventory has no unambiguous physical owner");
                // An empty ownerless read-only view, or another item's view,
                // contributes no ownership edge. In particular this permits
                // native layout aliases without silently dropping real cargo.
                continue;
            }
            foreach(var child in node.children) queue.Enqueue((child,depth+1));
        }
    }

    private void BindActorSession()
    {
        var session=NativeActorSession.Current;
        if(!host || session==null) return;
        if(!ReferenceEquals(session.Store,PlayerStore.Instance)) throw new InvalidOperationException("Actor session does not belong to this store");
        if(actorRun!=null && !ReferenceEquals(actorRun,session.Run)) throw new InvalidOperationException("Actor run changed during a native world session");
        actorRun=session.Run;
    }
}
