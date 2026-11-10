using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

internal static partial class NativeMirrorProbe
{
    // Root inventories are native scene fixtures, while nested inventories are
    // item layouts. Both must receive the same locks/tags and slot mode contract.
    internal static JObject CaptureInventoryState(GameInventory inventory)
    {
        var state=new JObject { ["fields"]=FieldData(inventory,InventoryFields),["tags"]=Tags(inventory) };
        if(inventory is GameSlotInventory slot)
        {
            state["steal"]=slot.stealUI; state["down"]=slot.isUIDown; state["hidden"]=slot.isHidden;
        }
        return state;
    }

    internal static void ApplyInventoryState(GameInventory inventory,JObject data)
    {
        ApplyFields(inventory,InventoryFields,(JObject)data["fields"]);
        var tags=ReadTags((JObject)data["tags"]);
        inventory.dict.Clear();
        foreach(var tag in tags.dict) inventory.dict.Add(tag.Key,tag.Value);
        if(inventory is GameSlotInventory slot)
        {
            bool layoutChanged=slot.stealUI!=(bool)data["steal"] || slot.isUIDown!=(bool)data["down"] || slot.isHidden!=(bool)data["hidden"];
            slot.stealUI=(bool)data["steal"]; slot.isUIDown=(bool)data["down"];
            if(slot.isHidden!=(bool)data["hidden"]) { if((bool)data["hidden"]) slot.Hide(); else slot.Show(); }
            if(layoutChanged)
            {
                NativeReplicaSlotPresentation.Refresh(slot);
                NativeReplicaGraph.Validate(slot);
                NativeReplicaGraph.NotifySizeChanged(slot);
            }
        }
    }
}
