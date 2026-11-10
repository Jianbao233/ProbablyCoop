using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private uint proposedBackpack;
    private string proposedBackpackProof="";
    internal uint ProposedBackpack=>proposedBackpack;

    internal void OpenTravelPreparation()
    {
        var entry=EmporiumEntry.Instance;
        if(MapUIManager.Instance.IsOutside) return;
        var choices=new List<NativeLocalItemChooser.Choice> {
            new NativeLocalItemChooser.Choice { Label=LocHelper.GetLocalizedMechanic("response_afterhours_no_backpack"),
                Select=()=>SelectTravelBackpack(null) } };
        foreach(var backpack in entry.GetAllOwnedItemsByTag("BACKPACK_TAG").OfType<GameItemElement>())
        {
            var selected=backpack;
            choices.Add(new NativeLocalItemChooser.Choice { Source=selected,
                Label=LocHelper.GetLocalizedMechanic("response_afterhours_select_backpack",selected.name),
                Select=()=>SelectTravelBackpack(selected) });
        }
        NativeLocalItemChooser.Show(this,LocHelper.GetLocalizedMechanic("response_afterhours_title"),choices);
    }

    private bool SelectTravelBackpack(GameItem backpack)
    {
        if(StoreClientData.IsButcherHunting() && (backpack==null || !GunHelper.HasGunInside(backpack)))
        {
            CommandNotice(LocHelper.GetLocalizedMechanic("response_afterhours_need_gun",LocHelper.GetLocalizedClientName("name_the_butcher")));
            return false;
        }
        if(backpack!=null && (backpack.parentInventory==null || !backpack.IsTag("BACKPACK_TAG") || !GeneralHelper.IsItemOwned(backpack) ||
            !host && model.ReplicaId(backpack)==0 || backpack is GameItemElement element && element.IsDestroyed()))
        { CommandNotice("所选背包已发生变化，请重新选择。"); return false; }
        EmporiumEntry.Instance.selectedBackpack=backpack;
        MapUIManager.Instance.OpenUI();
        return true;
    }

    private bool ValidTravelBackpack(uint id,string proof,out GameItem backpack)
    {
        backpack=null;
        if(id==0) return proof=="" && !StoreClientData.IsButcherHunting();
        if(!model.Objects.TryGetValue(id,out var value) || value is not GameItem item || item.parentInventory==null ||
            !item.IsTag("BACKPACK_TAG") || !GeneralHelper.IsItemOwned(item) || !Accessible(item.parentInventory) ||
            !item.MayRemove() || item.MaxNumRemove()<item.unitCount || ContentsProof(item)!=proof ||
            StoreClientData.IsButcherHunting() && !GunHelper.HasGunInside(item)) return false;
        backpack=item;
        return true;
    }
}

[HarmonyPatch(typeof(StoreResponse),nameof(StoreResponse.ShowAfterhourResponse))]
internal static class CoopTravelPreparation
{
    private static bool Prefix()
    {
        var world=NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host;
        if(world==null) return true;
        world.OpenTravelPreparation();
        return false;
    }
}
