using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool waterMerchantQa=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-water-merchant")>=0;
    private double waterMerchantVisibleAt;
    private bool waterMerchantRecorded;

    // Creates a fresh native scenario and observes it. Nothing here fills,
    // replaces, or drags any of the starting items.
    private void TickWaterMerchant(RoomSelector room,NativeShop shop,bool inMenu,double now)
    {
        string role=host ? "Host" : "Guest";
        Require(Application.productName=="Probably Stolen Coop RegressionRev12Water "+role &&
            !Application.isBatchMode,"water merchant QA requires its fresh isolated GPU profile");
        Require(now-started<95,"water merchant QA did not reach the observation on "+role);

        if(host && !loaded && inMenu && room.IsHost)
        {
            Require(CoopRun.ListCheckpoints().Count==0,"water merchant QA expected an empty host profile");
            loaded=true;
            NewGameData.HardReset();
            CoopRun.Armed=true;
            var menu=MainMenuUIController.Instance;
            menu.OnNewGameClick();
            menu.OnScenarioContinue(StartingScenario.GetByStartType(NewGameData.StartType.WATER_MERCHANT));
            if(menu.confirmPanelUIManager.panel.activeSelf && menu.confirmPanelUIManager.yesAction!=null)
                menu.confirmPanelUIManager.OnYesClick();
            menu.OnPerkFinishedClick();
            return;
        }

        if(host && shop!=null && stage==0)
        {
            var source=PlayerStore.Instance;
            Require(source.saveSlotId>=0 && CoopRun.IsRegistered(source) && CoopRun.VerifyCheckpoint(source,1),
                "water merchant did not write the original day-one cooperative checkpoint");
            ModHelper.WriteModDataFileText("probably_coop","qa-room.txt",room.Code);
            stage=1;
        }
        if(room.Session?.State!=LinkState.Connected ||
            (host ? shop==null : NativeWorld.Guest?.Ready!=true)) return;
        if(waterMerchantVisibleAt==0)
        {
            StoreUIManager.Instance.CloseAllUI();
            waterMerchantVisibleAt=now+2;
            return;
        }
        if(now<waterMerchantVisibleAt) return;
        if(waterMerchantRecorded) return;

        var store=PlayerStore.Instance;
        Require(store.startType==NewGameData.StartType.WATER_MERCHANT &&
            StoreStation.Instance.dayCounter==1 && (host || store.saveSlotId==-1),
            "not a new water merchant run or guest loaded a native slot");
        Require(ItemSelectHandler.current.enabled && !ItemSelectHandler.current.auto,
            "original shop input handler policy is not active");
        var bottles=EmporiumEntry.Instance.invElement.childItems.OfType<GameItemElement>()
            .Where(item=>item.identifier is "large_bottled_water" or "water_jug")
            .OrderBy(item=>item.uniqueId).ToArray();
        Require(bottles.Length==3,"missing original water merchant containers");
        var items=new JArray();
        foreach(var item in bottles)
        {
            int volume=WaterHelper.GetTotalVolume(item);
            var layers=item.GetActiveInnateSpriteMods().ToArray();
            Require(item.background.gameObject.activeInHierarchy && layers.All(layer=>layer.gameObject.activeInHierarchy &&
                layer.image.enabled && layer.image.sprite!=null),"water item or liquid layer is not visibly rendered");
            Require(layers.Length==(volume>0 ? 2 : 0),"empty/full native liquid-layer mismatch on "+item.identifier);
            items.Add(new JObject {
                ["id"]=item.uniqueId, ["identifier"]=item.identifier, ["volume"]=volume,
                ["parts"]=new JArray(WaterHelper.GetLiquidParts(item)),
                ["bounds"]=JObject.FromObject(NativeOffscreenCapture.Bounds(item.background.rectTransform)),
                ["layers"]=new JArray(layers.Select(layer=>new JObject {
                    ["sprite"]=layer.image.sprite.name,
                    ["color"]=new JArray(layer.image.color.r,layer.image.color.g,layer.image.color.b,layer.image.color.a)
                }))
            });
        }
        Require(bottles.Count(item=>WaterHelper.GetTotalVolume(item)>0)==2 &&
            bottles.Any(item=>item.identifier=="water_jug" && WaterHelper.GetTotalVolume(item)>0),
            "expected filled large bottle, filled jug and an empty large bottle");
        NativeOffscreenCapture.Capture("rev12-water-merchant");
        var facts=new JObject {
            ["role"]=role, ["day"]=1, ["scenario"]="water_merchant", ["slot"]=store.saveSlotId,
            ["game"]=PlayerStore.VERSION, ["autoSelect"]=ItemSelectHandler.current.auto, ["items"]=items
        };
        ModHelper.WriteModDataFileText("probably_coop","qa-rev12-water.json",facts.ToString(Formatting.Indented));
        Record("PASS rev12-water-merchant-local","original filled and empty starter items; recorded quantities, liquid components and native visible layers");
        waterMerchantRecorded=true;
        if(!host) Finish("PASS guest water merchant observation");
    }
}
