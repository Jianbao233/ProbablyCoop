using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;

// No customer/offer factories, currency assignments or fixture injection here.
// The disposable opening queue is played through original UI and authenticated commands.
internal sealed partial class NativeBackgroundQa
{
    private int openingStep,openingClients,openingCash,openingStoredRewards;
    private double openingUntil,openingNext;
    private StoreClientInstance openingObserved;
    private bool openingSawFoodBarter,openingBarterDone,openingPrepared;
    private byte[] openingOldOffer,openingOldAccept;
    private uint[] openingInitialOffers,openingReturnedOffers,openingGainIds;
    private int[] openingGainCounts;
    private uint openingFoodId;
    private int openingFoodCount;
    private Dictionary<string,int> openingExpectedStock;

    private static uint[] OpeningOfferIds(NativeWorld world)=>EmporiumEntry.Instance.GetAllNonOwnedItem()
        .Select(world.PointerIdentity).OrderBy(id=>id).ToArray();
    private void OpeningClickArrow(ReplicaLink link,bool right)
    {
        var ui=NegociationUIManager.Instance; ui.OpenUI(); ui.RefreshUI();
        var button=(Button)AccessTools.Field(typeof(NegociationUIManager),right ? "selectorNext" : "selectorPrev").GetValue(ui);
        Require(button!=null && button.interactable,"native barter selector unavailable");
        button.onClick.Invoke();
        Require(QaWaiting(link),"native barter selector emitted no authenticated request");
    }
    private static void OpeningExpectStock(Dictionary<string,int> expected)
    {
        var actual=OwnedStock();
        Require(expected.Count==actual.Count && expected.All(pair=>actual.TryGetValue(pair.Key,out int count) && count==pair.Value),
            "opening barter changed unrelated owned stock or exchanged the wrong quantity");
    }
    private static void OpeningChangeStock(Dictionary<string,int> stock,GameItem item,int sign)
    {
        stock.TryGetValue(item.identifier,out int count); count+=sign*item.unitCount;
        if(count==0) stock.Remove(item.identifier); else stock[item.identifier]=count;
    }

    // Call at full-flow stage 13 after inspection and before the mandatory money sale.
    private bool PrepareOpeningSale(CoopSession session,NativeGuest guest,double now)
    {
        if(openingPrepared) return true;
        if(openingUntil==0) { openingUntil=now+115; Record("TRACE opening-business","bounded native opening queue: 115 seconds, at most 12 customers"); }
        var world=NativeWorld.Guest; var store=PlayerStore.Instance; var e=EmporiumEntry.Instance;
        Require(now<openingUntil,"opening business timed out step="+openingStep+" client="+store.currentClientInstance?.GetClientBlueprint()?.identifier);
        if(now<openingNext || QaWaiting(session.Native) || session.Shop.Waiting || world?.Ready!=true) return false;
        if(openingStep is not (10 or 11) && world.HasPendingWork) return false;
        var client=store.currentClientInstance?.GetClientBlueprint();
        if(openingStep==10)
        {
            if(!world.HasConfirmedGesture) return false;
            OpeningClickArrow(session.Native,true); openingStep=11; openingNext=now+0.5; return false;
        }
        if(openingStep==11)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Busy && client?.barterOffer?.currentSetIndex==0 &&
                OpeningOfferIds(world).SequenceEqual(openingInitialOffers),"barter selector destroyed or changed held offer goods");
            ReleaseNativeDrag(); openingStep=12; openingNext=now+0.5;
            Record("PASS barter-held-offer","original guest held a front-counter offer; original selector refused Busy without replacing entities");
            return false;
        }
        if(openingStep==12)
        {
            OpeningClickArrow(session.Native,true); openingStep=1; openingNext=now+0.5; return false;
        }
        if(openingStep==1)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Applied && client?.barterOffer?.currentSetIndex==1,
                "native next barter set did not reach host");
            Require(!OpeningOfferIds(world).Intersect(openingInitialOffers).Any() && store.playerCash==openingCash,
                "native offer replacement reused removed goods or charged currency");
            OpeningClickArrow(session.Native,false); openingStep=2; openingNext=now+0.5; return false;
        }
        if(openingStep==2)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Applied && client?.barterOffer?.currentSetIndex==0,
                "native previous barter set did not restore the default selection");
            openingReturnedOffers=OpeningOfferIds(world);
            Require(openingReturnedOffers.Length==2 && !openingReturnedOffers.Intersect(openingInitialOffers).Any(),
                "native offer factory did not replace default blood bag identities after the round trip");
            Require(session.Native.Submit(openingOldOffer,now),"old barter selection proof could not be replayed");
            openingStep=3; openingNext=now+0.5; return false;
        }
        if(openingStep==3)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Stale && client?.barterOffer?.currentSetIndex==0 &&
                OpeningOfferIds(world).SequenceEqual(openingReturnedOffers) && store.playerCash==openingCash,
                "old offer proof rebuilt goods after index returned to zero");
            Record("PASS barter-offer-roundtrip","original right/left buttons; fresh native offer entity identities; old index-0 proof refused without a third factory call");
            Require(session.Native.Submit(openingOldAccept,now),"old normal barter accept proof could not be replayed");
            openingStep=21; openingNext=now+0.5; return false;
        }
        if(openingStep==20)
        {
            Require(QaLatestResult(session.Native)==(ushort)ShopResult.Applied,"food placement on barter counter was rejected");
            openingStep=0;
        }
        if(openingStep==21)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Stale && client?.identifier=="storyFoodBarter" &&
                !client.isBusinessComplete && client.barterOffer.currentSetIndex==0 &&
                world.Objects.TryGetValue(openingFoodId,out var input) && input is GameItem food &&
                food.parentInventory==e.backInvinvElement && food.unitCount==openingFoodCount && GeneralHelper.IsItemOwned(food) &&
                OpeningOfferIds(world).SequenceEqual(openingReturnedOffers) && store.playerCash==openingCash,
                "old normal accept proof traded food or changed restored offer entities after offer-set ABA");
            Record("PASS barter-accept-aba","ordinary trade/accept captured with valid food before switching; after right/left old proof Stale, food still owned on back counter and all current reward identities unchanged");
            openingStep=4;
        }
        if(openingStep==6)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Applied,"original barter accept was rejected");
            Require(client?.identifier=="storyFoodBarter" && client.isBusinessComplete && store.playerCash==openingCash,
                "native barter did not complete business at unchanged cash");
            Require(world.Objects.TryGetValue(openingFoodId,out var foodObject) && foodObject is GameItem food &&
                food.parentInventory==e.soldElement && food.unitCount==openingFoodCount,
                "barter input lost its native identity/count or did not enter sold inventory");
            for(int i=0;i<openingGainIds.Length;i++)
                Require(world.Objects.TryGetValue(openingGainIds[i],out var value) && value is GameItem gain &&
                    GeneralHelper.IsItemOwned(gain) && gain.unitCount==openingGainCounts[i] &&
                    (gain.parentInventory==e.backInvinvElement || gain.parentInventory==e.frontInvinvElement || gain.parentInventory==e.invElement),
                    "barter reward identity/count/ownership differed on guest");
            OpeningExpectStock(openingExpectedStock);
            NativeOffscreenCapture.Capture("qa-opening-barter-complete");
            Record("PASS opening-native-barter","storyFoodBarter: one food to native sold root; two original blood bags now owned; all entity identities/counts preserved; currency="+openingCash+" unchanged");
            openingBarterDone=true; openingStep=7;
        }
        if(world.HasDialogue)
        {
            var dialog=DialogUIManager.Instance;
            var choice=dialog.dialogChoiceBubbles.FirstOrDefault(b=>b.gameObject.activeSelf && b.IsInteractable());
            if(choice!=null) choice.OnDialogChoiceClick(); else dialog.OnAdvancePressed();
            openingNext=now+0.65; return false;
        }
        if(world.PendingMainDialogue) { openingNext=now+0.2; return false; }
        if(openingStep==8)
        {
            Require(QaLatestResult(session.Native)==(ushort)ShopResult.Applied,"barter reward storage move was rejected");
            openingStep=7;
        }
        if(openingStep==7)
        {
            while(openingStoredRewards<openingGainIds.Length)
            {
                Require(world.Objects.TryGetValue(openingGainIds[openingStoredRewards],out var reward) && reward is GameItem,
                    "barter reward entity disappeared before storage");
                var item=(GameItem)reward;
                Require(GeneralHelper.IsItemOwned(item) && item.unitCount==openingGainCounts[openingStoredRewards],
                    "barter reward ownership/quantity changed while storing");
                if(item.parentInventory==e.invElement) { openingStoredRewards++; continue; }
                Require(item.parentInventory==e.backInvinvElement || item.parentInventory==e.frontInvinvElement,
                    "barter reward left the native counter before storage");
                var slot=e.invElement.TryFindOneValidInventorySlot(item,keepOrientation:true);
                Require(slot?.IsValid()==true && slot.targetItem==null && slot.numTransfer==item.unitCount,
                    "main stock has no native whole-item storage slot for barter reward "+item.identifier);
                slot.TryAcceptOnce(); Require(QaWaiting(session.Native),"native reward storage emitted no request");
                openingStep=8; openingNext=now+0.5; return false;
            }
            OpeningExpectStock(openingExpectedStock);
            Require(store.playerCash==openingCash,"storing barter rewards changed currency");
            Record("PASS barter-rewards-to-stock","both original reward entities reached main inventory by native auto-stash or original slot placement; identity/quantity/ownership and full owned-stock counts unchanged");
            openingStep=9;
        }
        if(!ReferenceEquals(openingObserved,store.currentClientInstance))
        {
            openingObserved=store.currentClientInstance;
            if(client!=null)
            {
                Require(++openingClients<=12,"opening queue exceeded twelve real customers without a matching monetary buyer");
                Record("TRACE opening-client",client.identifier+" intent="+client.clientIntent+" number="+openingClients);
            }
        }
        if(client==null)
        {
            Require(world.QueuedClients>0,"opening queue exhausted before mandatory monetary sale");
            OpeningRingBell(session.Native,now); return false;
        }
        if(!store.isClientArrived || store.lockNextBellFlag>0 || NegociationUIManager.IsTradeLocked())
        { openingNext=now+0.25; return false; }
        if(client.identifier=="storyFoodBarter" && !openingBarterDone)
        {
            openingSawFoodBarter=true;
            Require(!client.isBusinessComplete,"opening tutorial barter was completed before its assertions");
            if(!client.isIntroduced) { openingNext=now+0.25; return false; }
            Require(client.barterOffer!=null && client.barterOffer.conditions.Count==1 &&
                client.barterOffer.conditions[0].requiredItemTag=="FOOD","native food barter rule differs from the audited source");
            if(openingStep==0)
            {
                var condition=client.barterOffer.conditions[0];
                var food=e.GetOwnedItemsBack().FirstOrDefault(condition.IsItemValid);
                if(food==null)
                {
                    food=e.invElement.childItems.FirstOrDefault(i=>GeneralHelper.IsItemOwned(i) && condition.IsItemValid(i));
                    Require(food!=null,"existing store stock contains no food for the actual tutorial barter");
                    var slot=e.backInvinvElement.TryFindOneValidInventorySlot(food);
                    Require(slot?.IsValid()==true && slot.numTransfer==food.unitCount,"native barter counter cannot accept one whole food item");
                    slot.TryAcceptOnce(); Require(QaWaiting(session.Native),"native food placement emitted no request");
                    openingStep=20; openingNext=now+0.5; return false;
                }
                if(!client.barterOffer.IsBarterAcceptable()) { openingNext=now+0.2; return false; }
                openingFoodId=world.PointerIdentity(food); openingFoodCount=food.unitCount;
                Require(openingFoodId!=0,"native barter input lacks shared identity");
                var ui=NegociationUIManager.Instance; ui.OpenUI(); ui.RefreshUI();
                Require(client.barterOffer.currentSetIndex==0 && client.barterOffer.OfferSetCount()==3,
                    "opening tutorial barter does not expose its three native offers");
                var command=(JObject)AccessTools.Method(typeof(NativeWorld),"BarterOfferCommand").Invoke(world,new object[]{1});
                Require(command!=null,"cannot capture original barter selection proof");
                openingOldOffer=Encoding.UTF8.GetBytes(command.ToString(Formatting.None));
                Require(ui.currentNegociatedItem==null || ui.currentNegociatedItem.parentInventory==e.backInvinvElement &&
                    world.PointerIdentity(ui.currentNegociatedItem)!=0,"old accept proof would already have an invalid native selection before switching");
                var accept=new JObject { ["kind"]="trade",["action"]="accept",["token"]=command["token"].DeepClone(),
                    ["context"]=command["context"].DeepClone(),["selected"]=world.PointerIdentity(ui.currentNegociatedItem) };
                openingOldAccept=Encoding.UTF8.GetBytes(accept.ToString(Formatting.None));
                openingInitialOffers=OpeningOfferIds(world); openingCash=store.playerCash;
                Require(openingInitialOffers.Length==2 && openingInitialOffers.All(id=>id!=0),"default native barter requires two identifiable blood bags");
                var held=e.frontInvinvElement.childItems.OfType<GameItemElement>().First(i=>!GeneralHelper.IsItemOwned(i));
                var origin=e.frontInvinvElement.TryInventorySlot(held,held.unitCount,held.modifiedShape.Clone(),held.modifiedState);
                Require(origin?.IsValid()==true,"native offer has no valid source placement for held protection check");
                BeginAtNativeSlot(held,origin,holdAcrossFrames:true); openingStep=10; openingNext=now+0.5; return false;
            }
            if(openingStep==4)
            {
                var condition=client.barterOffer.conditions[0];
                var food=e.GetOwnedItemsBack().FirstOrDefault(condition.IsItemValid);
                Require(food!=null && world.PointerIdentity(food)==openingFoodId && food.unitCount==openingFoodCount,
                    "native barter input changed after rejected stale accept");
                if(!client.barterOffer.IsBarterAcceptable()) { openingNext=now+0.2; return false; }
                openingFoodId=world.PointerIdentity(food); openingFoodCount=food.unitCount;
                var gains=e.GetAllNonOwnedItem();
                Require(openingFoodId!=0 && gains.Count==2 && gains.All(i=>i.identifier=="blood_bag"),"native default barter items differ from original offer");
                openingGainIds=gains.Select(world.PointerIdentity).ToArray(); openingGainCounts=gains.Select(i=>i.unitCount).ToArray();
                Require(openingGainIds.All(id=>id!=0),"barter reward lacks shared identity");
                openingExpectedStock=OwnedStock(); OpeningChangeStock(openingExpectedStock,food,-1);
                foreach(var item in gains) OpeningChangeStock(openingExpectedStock,item,1);
                var ui=NegociationUIManager.Instance; ui.OpenUI(); ui.RefreshUI();
                var button=(TabletButton)AccessTools.Field(typeof(NegociationUIManager),"primaryButton").GetValue(ui);
                Require(button?.Interactable==true,"native barter accept button is disabled despite fulfilled conditions");
                button.button.onClick.Invoke(); Require(QaWaiting(session.Native),"native barter accept emitted no request");
                openingStep=6; openingNext=now+0.6; return false;
            }
        }
        if(!client.isBusinessComplete && client.clientIntent is StoreClient.ClientIntent.BUY or StoreClient.ClientIntent.SELLNBUY)
        {
            var match=session.Shop.View.Items.FirstOrDefault(row=>row.Owned && row.Movable && row.Place==1 &&
                guest.Replica(row.Id) is GameItem item && client.IsClientBuyingThisItem(item) && !client.IsClientRefusingItem(item) && client.HasBudgetLeftToBuy(item));
            if(match!=null)
            {
                Require(!openingSawFoodBarter || openingBarterDone,"opening preparation skipped required tutorial barter");
                NegociationUIManager.Instance.CloseUI(); openingPrepared=true;
                Record("PASS opening-monetary-buyer","native queue reached "+client.identifier+" with eligible owned entity="+match.Id+"; mandatory cash sale remains the next independent assertion");
                return true;
            }
            Record("TRACE opening-buyer-candidates",client.identifier+" "+string.Join("; ",session.Shop.View.Items.Where(row=>row.Owned && row.Place is 1 or 2)
                .Select(row=>guest.Replica(row.Id) is GameItem item
                    ? row.Id+":"+item.identifier+" place="+row.Place+" size="+row.Width+"x"+row.Height+" movable="+row.Movable+
                        " buying="+client.IsClientBuyingThisItem(item)+" refusing="+client.IsClientRefusingItem(item)+" budget="+client.HasBudgetLeftToBuy(item)
                    : row.Id+":replica-missing")));
        }
        Require(store.IsClientDimissable(),"opening customer needs an unhandled mandatory native interaction: "+client.identifier);
        OpeningRingBell(session.Native,now); return false;
    }
    private void OpeningRingBell(ReplicaLink link,double now)
    {
        NegociationUIManager.Instance.CloseUI();
        UnityEngine.Object.FindObjectOfType<StoreBell>().GetComponent<Interactable>().OnClick();
        Require(QaWaiting(link),"opening original bell emitted no request"); openingNext=now+1.5;
    }

    private sealed class OpeningBarterCounter { internal int Cycles,Accepts; }
    private sealed class OpeningBarterBefore
    {
        internal StoreClient Client;
        internal GameItem Food;
        internal GameItem[] Gains;
        internal int[] Counts;
        internal int FoodCount,Cash,SoldCount;
    }
    private static readonly ConditionalWeakTable<StoreClient,OpeningBarterCounter> openingSourceCounts=new ConditionalWeakTable<StoreClient,OpeningBarterCounter>();
    [HarmonyPatch(typeof(BarterOffer),nameof(BarterOffer.ShowSetByOffset))]
    private static class OpeningCycleSourceOracle
    {
        private static void Postfix(BarterOffer __instance,bool __runOriginal)
        {
            if(__runOriginal && Enabled && IsHostProduct && __instance.ownerClient?.identifier=="storyFoodBarter")
                openingSourceCounts.GetValue(__instance.ownerClient,_=>new OpeningBarterCounter()).Cycles++;
        }
    }
    [HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.AcceptBarter))]
    private static class OpeningBarterSourceOracle
    {
        private static void Prefix(PlayerStore __instance,out OpeningBarterBefore __state)
        {
            __state=null;
            var client=Enabled && IsHostProduct ? __instance.currentClientInstance?.GetClientBlueprint() : null;
            if(client?.identifier!="storyFoodBarter" || NativeWorld.Host==null) return;
            var food=EmporiumEntry.Instance.GetOwnedItemsBack().FirstOrDefault(client.barterOffer.conditions.Single().IsItemValid);
            Require(food!=null,"source tutorial barter missing native input");
            var gains=EmporiumEntry.Instance.GetAllNonOwnedItem().ToArray();
            __state=new OpeningBarterBefore { Client=client,Food=food,FoodCount=food.unitCount,Gains=gains,
                Counts=gains.Select(i=>i.unitCount).ToArray(),Cash=__instance.playerCash,SoldCount=__instance.soldItemsTracker.Count };
        }
        private static void Postfix(PlayerStore __instance,OpeningBarterBefore __state,bool __runOriginal)
        {
            if(!__runOriginal || __state==null) return;
            var count=openingSourceCounts.GetValue(__state.Client,_=>new OpeningBarterCounter());
            Require(++count.Accepts==1 && count.Cycles==2,"source barter ran more than once or old selection replay reran a factory");
            Require(__instance.playerCash==__state.Cash && __state.Client.isBusinessComplete &&
                __state.Food.parentInventory==EmporiumEntry.Instance.soldElement && __state.Food.unitCount==__state.FoodCount &&
                __instance.soldItemsTracker.Count==__state.SoldCount+1 && __state.Gains.Length==2 &&
                __state.Gains.Select((item,i)=>GeneralHelper.IsItemOwned(item) && item.unitCount==__state.Counts[i]).All(value=>value),
                "source native barter failed cash/identity/count/sold-record conservation");
            string proof="PASS opening-barter-host-once cycles=2 accepts=1 input="+__state.Food.uniqueId+" rewards="+
                string.Join(",",__state.Gains.Select(item=>item.uniqueId))+" cash="+__state.Cash+" unchanged; native sold-record +1";
            Debug.Log("[probably_coop QA] "+proof);
            ModHelper.WriteModDataFileText("probably_coop","qa-opening-barter-host.txt",proof);
        }
    }
}
