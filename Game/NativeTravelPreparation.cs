using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private uint proposedBackpack;
    private string proposedBackpackProof="";
    internal uint ProposedBackpack=>proposedBackpack;
    private bool selectingTravelBackpack;

    internal void OpenTravelPreparation()
    {
        var entry=EmporiumEntry.Instance;
        if(MapUIManager.Instance.IsOutside || MapUIManager.Instance.IsInTransit) return;
        TransitUI.Close();
        tripUi?.Hide();
        selectingTravelBackpack=true;
        var choices=new List<NativeLocalItemChooser.Choice> {
            new NativeLocalItemChooser.Choice { Label=LocHelper.GetLocalizedMechanic("response_afterhours_no_backpack"),
                Select=()=>SelectTravelBackpack(null) } };
        foreach(var backpack in entry.GetAllOwnedItemsByTag("BACKPACK_TAG").OfType<GameItemElement>())
        {
            if(BackpackReservedByPeer(backpack)) continue;
            var selected=backpack;
            choices.Add(new NativeLocalItemChooser.Choice { Source=selected,
                Label=LocHelper.GetLocalizedMechanic("response_afterhours_select_backpack",selected.name),
                Select=()=>SelectTravelBackpack(selected) });
        }
        NativeLocalItemChooser.Show(this,LocHelper.GetLocalizedMechanic("response_afterhours_title"),choices,FinishTravelPreparation);
    }

    private bool BackpackReservedByPeer(GameItem backpack)
    {
        int peer=host ? 1 : 0;
        return backpack!=null && (tripReady & (1<<peer))!=0 && tripBags[peer]!=0 &&
            model.Objects.TryGetValue(tripBags[peer],out var reserved) && ReferenceEquals(reserved,backpack);
    }

    private bool HasDepartureProposal=>proposedTrip.Length>0 && proposedTrip!="return";

    private void FinishTravelPreparation()
    {
        selectingTravelBackpack=false;
        drawnTrip=null;
        TickTripUi();
    }

    private void CloseTravelPreparation()
    {
        if(!selectingTravelBackpack) return;
        selectingTravelBackpack=false;
        NativeLocalItemChooser.Close(this);
        drawnTrip=null;
    }

    internal void CancelDepartureForSleep()
    {
        if(MapUIManager.Instance==null || MapUIManager.Instance.IsOutside || MapUIManager.Instance.IsInTransit) return;
        bool abandon=HasDepartureProposal || TransitUI.IsOpen || selectingTravelBackpack;
        CloseTravelPreparation();
        TransitUI.Close();
        if(!abandon) return;
        tripUi?.Hide();
        drawnTrip=null;
        ProposeTrip("cancel");
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
        if(BackpackReservedByPeer(backpack))
        { CommandNotice("另一位玩家已经选了这个背包。请选择另一个，或选择不携带。"); return false; }

        EmporiumEntry.Instance.selectedBackpack=backpack;
        FinishTravelPreparation();
        if(!HasDepartureProposal) MapUIManager.Instance.OpenUI();
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
