using ProbablyCoop.Core;

internal static class HeldInteractionTests
{
    private sealed class Node
    {
        internal readonly List<Node> Children=new(),Parents=new();
        internal Node Add(Node child) { Children.Add(child); child.Parents.Add(this); return this; }
        // Different items may compare equal in an external object's value
        // equality implementation; interaction claims must use identity.
        public override bool Equals(object value)=>value is Node;
        public override int GetHashCode()=>1;
    }
    private static HeldInteraction<Node> Held(params Node[] nodes)=>new(nodes,n=>n.Children,n=>n.Parents);
    internal static void Run()
    {
        var stock=new Node(); var shelf=new Node(); var counter=new Node();
        var bottle=new Node(); var meat=new Node(); var book=new Node();
        stock.Add(bottle).Add(meat); shelf.Add(book);
        var hand=Held(bottle);
        Check(hand.TouchesItem(bottle),"held source unlocked");
        Check(!hand.TouchesItem(meat) && !hand.TouchesItem(book),"unrelated sibling or store area blocked");
        Check(!hand.TouchesDestination(stock) && hand.TouchesContents(stock),"precise insertion and sorting conflated");
        Check(!hand.TouchesContents(counter) && !hand.TouchesDestination(shelf),"unrelated inventory blocked");

        var machine=new Node(); var window=new Node(); var slot=new Node(); var water=new Node();
        stock.Add(machine); machine.Add(window); window.Add(slot); slot.Add(water);
        var containerHand=Held(machine);
        Check(containerHand.TouchesItem(water) && containerHand.TouchesDestination(slot) &&
            !containerHand.TouchesItem(meat),"held machine contents not scoped");
        var waterHand=Held(water);
        Check(waterHand.TouchesItem(machine) && waterHand.TouchesContents(slot) &&
            !waterHand.TouchesDestination(stock) && !waterHand.TouchesItem(bottle),"held child did not reserve its machine only");
        Check(containerHand.TouchesOperation(new[]{water}) && waterHand.TouchesOperation(new[]{machine}),
            "container/contents local gestures were treated as independent sources");
        Check(containerHand.TouchesOperation(new[]{meat},slot) &&
            !waterHand.TouchesOperation(new[]{meat},slot) && waterHand.TouchesOperation(new[]{meat},target:machine),
            "precise sibling insertion and whole-container mutation dependencies were conflated");
        Check(!containerHand.TouchesOperation(new[]{bottle},counter) &&
            !containerHand.TouchesOperation(new[]{bottle},stock),"unrelated precise placement acquired a container dependency");

        var pending=new PendingActions();
        var movingContainer=pending.Reserve(1,new uint[]{1});
        var movingContents=pending.Reserve(2,new uint[]{2});
        var otherWork=pending.Reserve(3,new uint[]{3});
        if(containerHand.TouchesOperation(new[]{water})) movingContents.AddDependency(movingContainer);
        if(containerHand.TouchesOperation(new[]{meat},counter)) otherWork.AddDependency(movingContainer);
        Check(!movingContents.CanSubmit && otherWork.CanSubmit,"container dependency blocked independent work");
        pending.Sent(movingContainer,1);
        pending.Result(movingContainer,false);
        Check(movingContents.State==PendingActions.Status.Rejected && otherWork.CanSubmit,
            "container rejection did not retract only related contents actions");

        var group=Held(bottle,book,bottle,null);
        Check(group.TouchesItem(bottle) && group.TouchesItem(book) && !group.TouchesItem(meat),"group selection leaked to siblings");
        Check(group.TouchesContents(stock) && group.TouchesContents(shelf) && !group.TouchesContents(counter),"group inventory membership");
        // Capture precedes temporary native selection/parent changes. Existing
        // footprint remains stable throughout this authoritative operation.
        stock.Children.Remove(bottle); bottle.Parents.Remove(stock); counter.Add(bottle);
        Check(group.TouchesContents(stock) && !group.TouchesContents(counter),"temporary native binding mutated captured footprint");
        Check(Held(bottle).TouchesContents(counter) && !Held(bottle).TouchesContents(stock),"next operation did not recapture current parents");

        // Shared windows and cycles are legal graph shapes; traversal must
        // terminate, retain aliases and avoid flooding adjacent sibling nodes.
        var secondOwner=new Node(); secondOwner.Add(window); window.Add(machine);
        var cyclic=Held(water);
        Check(cyclic.TouchesItem(secondOwner) && cyclic.TouchesItem(machine) && !cyclic.TouchesItem(meat),"shared/cyclic ownership");
        var empty=Held();
        Check(!empty.TouchesItem(null) && !empty.TouchesItem(bottle) && !empty.TouchesContents(stock),"empty hand locked world");
        Console.WriteLine("PASS held interaction footprint: unrelated siblings/areas, precise destination vs sort, nested containers, group capture, identity equality, shared/cyclic graph and immutable operation snapshot");
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
}
