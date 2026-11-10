using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    // Native hover still draws the local highlight. On release the item hit
    // declares one operation even when a custom slot rule has no replica.
    internal bool RequestItemDrop(GameItem item,GameItem target,int count)
    {
        if(gesture==null || !ReferenceEquals(gesture,releasing) || gesture.Group || count<1 ||
            !ReferenceEquals(item,gesture.Items[0]) || ReferenceEquals(item,target) ||
            !TryData(item,out var source) || !TryData(target,out var container)) return false;
        bool insert=target.child!=null && GeneralHelper.MayPlayerInsertInto(target);
        bool stack=target.MayStack(item);
        int tool=(int?)targetQueries[model.ReplicaId(item).ToString()]?[model.ReplicaId(target).ToString()] ?? 0;
        if(!insert && !stack && TargetEditor(item)!=null)
        {
            if((tool&2)!=0) RequestItem(item,"target",target);
            else if(targetQueries[model.ReplicaId(item).ToString()]==null) RequestItem(item,"target",target);
            return true;
        }
        if(insert || stack || (tool&2)!=0 || targetQueries[model.ReplicaId(item).ToString()]==null &&
            GeneralHelper.IsItemEffectivelyOwned(item) &&
            (GeneralHelper.IsItemEffectivelyOwned(target) || item.IsTag("CAN_USE_ON_NON_OWNED_TAG")))
        {
            Submit(new JObject { ["kind"]="containerDrop",["id"]=(uint)source["entity"],["token"]=(string)source["moveToken"],
                ["target"]=(uint)container["entity"],["contents"]=ContentsProof(target,true),["count"]=count });
        }
        return true;
    }

    private ushort ExecuteContainerDrop(JObject command)
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        if(!CurrentItem(command,out var item) || !model.Objects.TryGetValue((uint)command["target"],out var obj) ||
            obj is not GameItem target || ContentsProof(target)!=(string)command["contents"]) return (ushort)ShopResult.Stale;
        if(HandTouches(item) || HandTouches(target)) return (ushort)ShopResult.Busy;
        int count=(int)command["count"];
        if(count<1 || count>item.unitCount || count>item.MaxNumRemove() || !item.MayRemove() ||
            !Accessible(item.parentInventory) || !Accessible(target.parentInventory) || item==target ||
            GraphUtils.FindAllChildrenType<GameItem>(item).Contains(target))
            return (ushort)ShopResult.Unavailable;

        int remaining=count;
        bool insert=GeneralHelper.MayPlayerInsertInto(target) && target.MayHaveValidInventorySlot(item);
        if(insert) remaining=GraphUtils.TryAcceptAll(target,item,count);
        if(remaining>0 && item.parentInventory!=null && target.MayStack(item))
            remaining-=target.GetStackSlot(item)?.TryAcceptOnce(remaining) ?? 0;
        if(remaining>0 && item.parentInventory!=null && item.MayTarget(target) && item.CanTarget(target))
        {
            if(TargetEditor(item)==null)
            {
                ItemBehaviourManager.current.Target(item,target);
                if(item.MayTarget(target) && item.CanTarget(target)) item.Target(target);
            }
            remaining=0;
        }
        if(remaining==count) return (ushort)(insert ? ShopResult.NoSpace : ShopResult.Unavailable);
        nextCapture=0;
        return (ushort)ShopResult.Applied;
    }
}
