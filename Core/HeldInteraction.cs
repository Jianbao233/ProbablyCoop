using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ProbablyCoop.Core;

// Immutable graph footprint for one authoritative operation. Holding one item
// reserves its contents and owning items, but not every sibling in the store.
internal sealed class HeldInteraction<T> where T : class
{
    private sealed class Identity : IEqualityComparer<T>
    {
        internal static readonly Identity Instance=new Identity();
        public bool Equals(T x,T y)=>ReferenceEquals(x,y);
        public int GetHashCode(T value)=>RuntimeHelpers.GetHashCode(value);
    }
    private readonly HashSet<T> below=new HashSet<T>(Identity.Instance);
    private readonly HashSet<T> above=new HashSet<T>(Identity.Instance);

    internal HeldInteraction(IEnumerable<T> held,Func<T,IEnumerable<T>> children,Func<T,IEnumerable<T>> parents)
    {
        if(held==null || children==null || parents==null) throw new ArgumentNullException();
        var roots=new List<T>();
        foreach(var value in held) if(value!=null) roots.Add(value);
        Walk(roots,children,below); Walk(roots,parents,above);
    }
    private static void Walk(IEnumerable<T> roots,Func<T,IEnumerable<T>> edges,HashSet<T> visited)
    {
        var pending=new Stack<T>(roots);
        while(pending.Count>0)
        {
            var node=pending.Pop();
            if(node==null || !visited.Add(node)) continue;
            var next=edges(node);
            if(next!=null) foreach(var child in next) if(child!=null) pending.Push(child);
        }
    }
    // Mutating a held item, its contents or one of its owning containers.
    internal bool TouchesItem(T item)=>item!=null && (below.Contains(item) || above.Contains(item));
    // Inserting at an independent slot in a shared inventory is allowed. An
    // inventory *inside* a held container is not an independent destination.
    internal bool TouchesDestination(T inventory)=>inventory!=null && below.Contains(inventory);
    // Sort/automatic stacking may touch any child; include held descendants.
    internal bool TouchesContents(T inventory)=>TouchesItem(inventory);

    // Used by pending local operations as well as authoritative leases. Two
    // sibling items remain independent; moving an owner/descendant or inserting
    // inside a pending container depends on that earlier operation.
    internal bool TouchesOperation(IEnumerable<T> sources,T destination=null,T target=null)
    {
        if(TouchesDestination(destination) || TouchesItem(target)) return true;
        if(sources!=null) foreach(var source in sources) if(TouchesItem(source)) return true;
        return false;
    }
}
