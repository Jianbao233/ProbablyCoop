using System;
using System.Linq;
using HarmonyLib;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    // Native Open changes its argument's sprite/shape. A nonregistered display
    // card lets it run locally without modifying the replicated inventory item.
    private sealed class StripDisplay : GameItem
    {
        internal StripDisplay(GameItem strip) : base("probably_coop_strip_display")
        {
            foreach(string tag in new[]{"TEST_WATER_QUALITY","ASSOCIATED_ITEM_ID"})
                ModifyTag(tag,value=>value.Enable().SetInt(strip.GetTagReadonly(tag).GetInt()));
        }
        public override void Destroy() { }
        public override void Validate() { }
    }
    private GameItem StripWater(GameItem strip)
    {
        if(strip==null || !strip.IsTag("IS_SPENT")) return null;
        int id=strip.GetTagReadonly("ASSOCIATED_ITEM_ID").GetInt();
        if(id==0) return null;
        return model.Objects.Values.OfType<GameItem>().FirstOrDefault(item=>item.uniqueId==id && item.parentInventory!=null &&
            item.IsTag("LIQUID_CONTAINER_TAG") && Accessible(item.parentInventory) &&
            (item.IsTag("WINE_CONTAINER_TAG") || item.FindItemFeatureByCategory("CATEGORY_WATER_PURITY")!=null));
    }
    private void OpenStrip(GameItem strip)
    {
        if(StripWater(strip)==null) return;
        WaterStripUIManager.Instance.Open(new StripDisplay(strip));
    }
    private static void CommitStripColor(GameItem strip)
    {
        string tested=WaterFeatureHelper.GetWaterDisplayName(strip.GetTagReadonly("TEST_WATER_QUALITY").GetInt());
        var colors=new[]{WaterFeatureHelper.PURE_WATER_DISPLAY,WaterFeatureHelper.HIGH_QUALITY_WATER_DISPLAY,
            WaterFeatureHelper.BASE_WATER_DISPLAY,WaterFeatureHelper.GHOSTWATER_DISPLAY,
            WaterFeatureHelper.RUST_WATER_DISPLAY,WaterFeatureHelper.GUTTERFLOW_WATER_DISPLAY};
        int index=Array.IndexOf(colors,tested);
        if(index>=0) strip.SetSpriteAndShape("Items/items_amenities","strip"+index);
    }
    private static void RefreshStripLabels(GameItem water)
    {
        var ui=WaterStripUIManager.Instance;
        ui.labelTMP.text=LocHelper.GetLocalizedMechanic("mech_tooltip_labeled")+"\n"+
            WaterFeatureHelper.GetWaterFeature(water).GetCurrentItemCondition().display;
        ui.matchButton.gameObject.SetActive(ui.CanMatch(water));
        ui.confrontButton.gameObject.SetActive(ui.CanConfront(water));
    }
    [HarmonyPatch(typeof(WaterStripUIManager),nameof(WaterStripUIManager.Open))]
    private static class RouteStripOpen
    {
        private static bool Prefix(GameItem testStrip)
        {
            var world=RemoteUiExecution; if(world==null) return true;
            int id=testStrip.GetTagReadonly("ASSOCIATED_ITEM_ID").GetInt();
            var water=EmporiumEntry.Instance.FindItemByUniqueID(id);
            if(water==null) return false;
            WaterFeatureHelper.GetWaterFeature(water);
            CommitStripColor(testStrip);
            world.CaptureFrontend(FrontendKind.WaterStrip,testStrip);
            return false;
        }
    }
    [HarmonyPatch(typeof(WaterStripUIManager),nameof(WaterStripUIManager.Open))]
    private static class GuardGuestStripArgument
    {
        private static bool Prefix(GameItem testStrip)=>!NativeGuest.Active || testStrip is StripDisplay;
    }
    [HarmonyPatch(typeof(WaterStripUIManager),nameof(WaterStripUIManager.OnMatch))]
    private static class RouteStripMatch
    {
        private static bool Prefix(WaterStripUIManager __instance)
        {
            if(!NativeGuest.Active) return true;
            Guest?.RequestScanner(FrontendKind.WaterStrip,__instance.currentWaterContainer,"match"); return false;
        }
    }
    [HarmonyPatch(typeof(WaterStripUIManager),nameof(WaterStripUIManager.OnConfront))]
    private static class RouteStripConfront
    {
        private static bool Prefix(WaterStripUIManager __instance)
        {
            if(!NativeGuest.Active) return true;
            Guest?.RequestScanner(FrontendKind.WaterStrip,__instance.currentWaterContainer,"confront"); return false;
        }
    }
}
