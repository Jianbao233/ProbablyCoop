using System;
using System.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    internal bool QaWildHandoffPending => wildAwaitingClose;
}

internal sealed partial class NativeBackgroundQa
{
    private const string UiMarketOrder = "qa_requested_market", UiWildService = "BUY_REFERAL";
    private readonly bool requestedUiQa=Environment.GetCommandLineArgs().Contains("--probably-coop-qa-requested-ui");
    private readonly bool requestedShootingQa=Environment.GetCommandLineArgs().Contains("--probably-coop-qa-requested-shooting");
    private readonly bool requestedMarketAudioQa=Environment.GetCommandLineArgs().Contains("--probably-coop-qa-market-audio");
    private int requestedUiStep, requestedCash, requestedSpaceCount;
    private double requestedReputation;
    private ulong requestedSpaceBefore, requestedLine;
    private bool requestedAppraisalQueued;
    private double marketplacePurchaseAfter, marketplaceFinishAt;
    private bool audioSaleQueued;
    private int audioRound, audioRegisterBase, audioTradeCash;
    private double audioNext, audioDeadline;
    private GameItem audioTradeItem;
    private int audioHostSaleCash = -1;
    private string requestedFaction;
    private ReputationElement requestedRow;

    private void VerifyRequestedShowcase()
    {
        var entry=EmporiumEntry.Instance;
        var ui=StoreUIManager.Instance;
        var shape=entry.showcaseElement.inventoryShape;
        Require(NetworkUpgrade.IsShowcaseIIUnlocked() && shape.width==9 && shape.height==7 &&
            ui.showcaseRenderer.sprite==ui.showcaseLarge && Mathf.Approximately(entry.displayCaseX,5.63f) &&
            Mathf.Approximately(entry.displayCaseY,24f),"loaded large showcase must have native grid, artwork and anchor");
        Require(entry.showcaseElement.childItems.Count>0,"upgraded showcase fixture must retain saved objects");
        Record("PASS requested-showcase","loaded upgrade II; 9x7 grid, large native sprite and anchor, displayed items="+entry.showcaseElement.childItems.Count);
        NativeOffscreenCapture.Capture("qa-requested-showcase");
    }

    private void TickRequestedUiHost(RoomSelector room)
    {
        var store=PlayerStore.Instance;
        if(stage==0)
        {
            VerifyRequestedShowcase();
            NotebookUIManager.Instance.CloseUI();
            var rep=store.storeReputations.Where(row=>row.GetReputationExact()<170).OrderBy(row=>row.factionId).First();
            requestedFaction=rep.factionId; requestedReputation=rep.GetReputationExact(); requestedCash=store.playerCash;
            SeedFrontendItem("bandage_item","QA_REQUESTED_MARKET_GOOD");
            if (requestedMarketAudioQa)
            {
                store.networkUpgrade["MARKETPLACE_REQUESTS"].state = 1;
                StoreUIManager.Instance.pcScreenObject.SetActive(false);
            }
            StoreRequest.ActivateRequest(new StoreRequest {
                id=UiMarketOrder,title="QA marketplace request",description="Original marketplace request",
                factionId=requestedFaction,requiredItem="bandage_item",duration=3,creditReward=77,reputationReward=12
            },force:true);
            store.networkUpgrade[UiWildService].state=1;
            StoreService.FindServiceByID(UiWildService).unlocked=false;
            StorePhoneClient.GetWildPhoneClient().phoneState=StorePhoneClient.PhoneState.Regular;
            if (!requestedMarketAudioQa) SeedAppraisalQa();
            stage=1;
            return;
        }
        if (requestedMarketAudioQa && stage == 1 && room.Session?.State == LinkState.Connected &&
            !store.networkUpgrade["MARKETPLACE_REQUESTS"].IsUnlocked())
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (marketplacePurchaseAfter == 0) marketplacePurchaseAfter = now + 4;
            if (now >= marketplacePurchaseAfter) store.networkUpgrade["MARKETPLACE_REQUESTS"].Unlock();
        }
        if(stage==1 && !store.storeRequests.Any(row=>row.id==UiMarketOrder))
        {
            Require(store.playerCash==requestedCash+77 &&
                StoreReputation.GetStoreReputation(requestedFaction).GetReputationExact()>requestedReputation,
                "market delivery did not change authority cash and reputation");
            Record("PASS requested-market-host","original request removed; exactly 77 credits and reputation awarded");
            stage=2;
        }
        if(stage==2 && store.networkUpgrade[UiWildService].IsUnlocked())
        {
            Require(StoreService.FindServiceByID(UiWildService).unlocked,"original Wild service was not unlocked on the host");
            Record("PASS requested-wild-host","native Buyer Referral activated on host");
            stage=3;
        }
        if (requestedMarketAudioQa)
        {
            if (stage == 3 && !audioSaleQueued &&
                NativeWorld.Host is { QaWildHandoffPending: false, SharedPhoneBusy: false })
            {
                if (store.storeState == PlayerStore.StoreState.MORNING)
                {
                    if (store.CanStartDay()) StoreShutterButton.Instance.OnClick();
                    return;
                }
                var seller = new StoreClient { identifier="QA_AUDIO_SELLER", displayName="QA seller",
                    spriteName=SpriteDict.GetRandomScavSpriteName(), clientFaction=StoreClient.FACTION_LOWER,dismissable=true,
                    clientIntent=StoreClient.ClientIntent.SELL, mainDialogue=null, isIntroduced=true };
                seller.OnArrival = () =>
                {
                    var item = DirectoryMaster.Item("processed_meat");
                    item.EnableTag("QA_AUDIO_CUSTOMER_GOODS");
                    store.AddDirectToWeightedTable(item,false);
                    Require(item.parentInventory==EmporiumEntry.Instance.backInvinvElement,"native purchase fixture not on the trading counter");
                };
                var buyer = new StoreClient { identifier="QA_AUDIO_BUYER", displayName="QA buyer",
                    spriteName=SpriteDict.GetRandomScavSpriteName(), clientFaction=StoreClient.FACTION_LOWER,dismissable=true,
                    clientIntent=StoreClient.ClientIntent.BUY, mainDialogue=null, isIntroduced=true };
                buyer.clientBuyingTagList.Add("FOOD");
                buyer.SetBudget(500);
                buyer.OnArrival = () =>
                {
                    var item = DirectoryMaster.Item("processed_meat");
                    item.EnableTag("QA_AUDIO_SHOP_GOODS");
                    store.AddDirectToWeightedTable(item,true);
                    Require(item.parentInventory==EmporiumEntry.Instance.backInvinvElement,"native sale fixture not on the trading counter");
                    audioHostSaleCash=store.playerCash;
                };
                store.storeClientManager.clientStack.Clear();
                store.storeClientManager.AddNextClient(buyer);
                store.storeClientManager.AddNextClient(seller);
                audioSaleQueued=true;
            }
            if (audioHostSaleCash >= 0 && store.playerCash > audioHostSaleCash &&
                !EmporiumEntry.Instance.backInvinvElement.childItems.Any(item => item.IsTag("QA_AUDIO_SHOP_GOODS")) && marketplaceFinishAt == 0)
                marketplaceFinishAt = Time.realtimeSinceStartupAsDouble + 14;
            if (marketplaceFinishAt != 0 && Time.realtimeSinceStartupAsDouble >= marketplaceFinishAt)
                Finish("PASS requested-market-audio host");
            return;
        }
        CheckHostAppraisalQa();
        if(stage==3 && AppraisalQaHostDone && !WildNetworkUI.IsOpen && NativeWorld.Host?.SharedPhoneBusy==false)
        {
            if(store.storeState==PlayerStore.StoreState.MORNING)
            {
                if(store.CanStartDay()) StoreShutterButton.Instance.OnClick();
                return;
            }
            Require(store.storeState==PlayerStore.StoreState.OPEN,"fixture must be open for the appraisal customer");
            if(!requestedAppraisalQueued)
            {
                store.storeClientManager.clientStack.Clear();
                var customer=StoreClientListAppraisal.CreateAppraisal1();
                customer.mainDialogue.SetText("CLIENT",string.Concat(Enumerable.Repeat("Cooperative appraisal test. ",24)));
                store.storeClientManager.AddNextClient(customer);
                requestedAppraisalQueued=true;
                Record("TRACE requested-appraisal-queued","original appraisal customer");
            }
        }
    }

    private bool TickRequestedUiGuest(ReplicaLink link,double now)
    {
        var store=PlayerStore.Instance;
        var world=NativeWorld.Guest;
        if(requestedUiStep==0)
        {
            var order=store.storeRequests.SingleOrDefault(row=>row.id==UiMarketOrder);
            if(order==null) return false;
            if (requestedMarketAudioQa)
            {
                if (!NetworkUpgrade.IsMarketplaceRequestsUnlocked()) return false;
                var pc = PCUIManager.Instance.storePC;
                Require(StoreUIManager.Instance.pcScreenObject.activeInHierarchy && pc.isActiveAndEnabled,
                    "the original marketplace screen is missing despite the shared upgrade");
                var button = pc.GetComponent<Interactable>();
                Require(button != null && button.isAvailable, "original marketplace world button is unavailable");
                button.OnClick();
                Require(PCUIManager.Instance.panel.activeSelf,"guest original marketplace world click did not open the native orders");
                Record("PASS requested-market-world-object","unlock showed the original PC screen and its original world button opened the orders");
            }
            VerifyRequestedShowcase();
            NotebookUIManager.Instance.CloseUI();
            if (!requestedMarketAudioQa) PCUIManager.Instance.storePC.OnClick();
            var row=PCUIManager.Instance.panel.GetComponentsInChildren<StoreRequestElement>().Single(item=>item.storeRequest==order);
            Require(PCUIManager.Instance.panel.activeSelf && row.acceptButton.interactable,"native marketplace row not usable from guest");
            UnityEngine.Object.FindObjectOfType<FeedbackBox>().GetComponent<Interactable>().OnClick();
            requestedFaction=order.factionId;
            var rep=StoreReputation.GetStoreReputation(requestedFaction);
            requestedRow=ReputationUIManager.Instance.layout.GetComponentsInChildren<ReputationElement>()
                .Single(item=>item.reputationTitle.text==rep.factionDisplay);
            Require(requestedRow.reputationNumber.text==rep.GetReputationExact().ToString("F2")+"/200",
                "native reputation row disagrees with the shared state");
            requestedCash=store.playerCash;
            requestedReputation=rep.GetReputationExact();
            row.acceptButton.onClick.Invoke();
            requestedUiStep=1;
        }
        else if(requestedUiStep==1 && !QaWaiting(link) && !store.storeRequests.Any(row=>row.id==UiMarketOrder))
        {
            var amount=StoreReputation.GetStoreReputation(requestedFaction).GetReputationExact();
            Require(store.playerCash==requestedCash+77 && amount>requestedReputation &&
                requestedRow.isActiveAndEnabled && requestedRow.reputationNumber.text==amount.ToString("F2")+"/200",
                "guest original marketplace reward or already-open reputation row failed to refresh");
            Record("PASS requested-market-reputation","native guest computer delivery and live reputation row; cash="+store.playerCash+" reputation="+amount);
            NativeOffscreenCapture.Capture("qa-requested-reputation");
            ReputationUIManager.Instance.CloseUI();
            PCUIManager.Instance.HideUI();
            requestedUiStep=2;
        }
        else if(requestedUiStep==2 && RunWildQa(link,now,UiWildService))
        {
            Require(StoreService.FindServiceByID(UiWildService).unlocked,"native Wild service not projected back to guest");
            Record("PASS requested-wild-guest","original call, network card, purchase and shared service unlock");
            if (requestedMarketAudioQa)
            {
                Require(world.QaRegisterPlays==1,"original marketplace delivery cash register must play once");
                if (world.SharedPhoneBusy) PhoneUIManager.Instance.StopCall();
                audioDeadline=now+90;
                requestedUiStep=10;
                return false;
            }
            requestedUiStep=3;
        }
        else if(requestedUiStep==3 && RunGuestAppraisalQa(link,now))
        {
            Record("PASS requested-appraisal-guest","opened the original certificate; verified category changes in both directions");
            requestedUiStep=4;
        }
        else if(requestedUiStep==4 && store.storeState==PlayerStore.StoreState.OPEN && world.QueuedClients>0)
        {
            UnityEngine.Object.FindObjectOfType<StoreBell>().OnClick();
            requestedUiStep=5;
        }
        else if(requestedUiStep==5 && world.HasDialogue &&
            store.currentClientInstance?.GetClientBlueprint().clientIntent==StoreClient.ClientIntent.APPRAISAL_SERVICE)
        {
            Require(KeyBindHelper.Get("advance_dialogue")==KeyCode.Space,"QA profile does not bind native dialogue to Space");
            Require(NativeAdvanceInput.SelectTarget()==NativeAdvanceInput.Target.Customer,"ordinary guest appraisal dialogue did not take key focus");
            requestedSpaceBefore=world.LastSubmittedUiRequest;
            requestedLine=world.BubbleLineIdentity(DialogUIManager.Instance.clientBubble);
            requestedUiStep=6;
            Record("TRACE requested-space-ready","waiting for Windows Space key; focus="+Application.isFocused);
        }
        else if(requestedUiStep==6)
        {
            if(Input.GetKeyDown(KeyCode.Space)) requestedSpaceCount++;
            var item=EmporiumEntry.Instance.GetAllNonOwnedItem().FirstOrDefault(value=>value.IsTag("APPRAISAL_ITEM"));
            if(item==null || world.HasDialogue || QaWaiting(link)) return false;
            Require(requestedSpaceCount>0 && world.LastSubmittedUiRequest>requestedSpaceBefore &&
                world.BubbleLineIdentity(DialogUIManager.Instance.clientBubble)!=requestedLine,
                "appraisal dialogue did not advance from the physical Space input");
            Record("PASS requested-space","Windows Space -> Unity key bind -> original guest advance -> original host dialogue");
            return true;
        }
        else if(requestedUiStep==10) return TickTradeAndDialAudio(link,now);
        return false;
    }

    private bool TickTradeAndDialAudio(ReplicaLink link,double now)
    {
        Require(now<audioDeadline,"trade and telephone audio fixture timed out round="+audioRound);
        if (now<audioNext || QaWaiting(link)) return false;
        var world=NativeWorld.Guest;
        var store=PlayerStore.Instance;
        var client=store.currentClientInstance?.GetClientBlueprint();
        if (audioRound==0 || audioRound==3)
        {
            bool sale=audioRound==3;
            if(client?.identifier!=(sale ? "QA_AUDIO_BUYER" : "QA_AUDIO_SELLER"))
            {
                if(world.HasDialogue) DialogUIManager.Instance.OnAdvancePressed();
                else if(store.storeState==PlayerStore.StoreState.OPEN && world.QueuedClients>0 &&
                    store.lockNextBellFlag==0 && !StoreClientMono.Instance.IsMoving())
                    UnityEngine.Object.FindObjectOfType<StoreBell>().OnClick();
                audioNext=now+0.45;
                return false;
            }
            if(!store.isClientArrived || world.PendingMainDialogue) return false;
            audioTradeItem=world.Objects.Values.OfType<GameItem>().FirstOrDefault(i=>i.IsTag(sale ? "QA_AUDIO_SHOP_GOODS" : "QA_AUDIO_CUSTOMER_GOODS"));
            if(audioTradeItem==null) return false;
            audioRound++;
            audioNext=now+0.5;
            return false;
        }
        if (audioRound==1 || audioRound==4)
        {
            if(audioTradeItem.parentInventory!=EmporiumEntry.Instance.backInvinvElement) return false;
            var ui=NegociationUIManager.Instance;
            ui.UpdateUI(audioTradeItem);
            ui.OpenUI();
            ui.RefreshUI();
            var button=(TabletButton)HarmonyLib.AccessTools.Field(typeof(NegociationUIManager),"primaryButton").GetValue(ui);
            Require(button?.Interactable==true,"native buy/sell button unavailable in fixture");
            audioRegisterBase=world.QaRegisterPlays;
            audioTradeCash=store.playerCash;
            button.button.onClick.Invoke();
            Require(QaWaiting(link),"native buy/sell button did not submit");
            audioRound++;
            return false;
        }
        if (audioRound==2 || audioRound==5)
        {
            Require(QaResult(link)==(ushort)ShopResult.Applied,"native trade was rejected: "+(ShopResult)QaResult(link));
            bool sale=audioRound==5;
            if(sale ? store.playerCash<=audioTradeCash : store.playerCash>=audioTradeCash) return false;
            Require(world.QaRegisterPlays==audioRegisterBase+1,"native buy/sell register cue missing or repeated");
            Record(sale ? "PASS native-sale-register" : "PASS native-buy-register","guest native tablet traded and played the original register source once");
            NegociationUIManager.Instance.CloseUI();
            audioRound++;
            return false;
        }
        if (audioRound==6)
        {
            if(world.SharedPhoneBusy) return false;
            var phone=PhoneUIManager.Instance;
            phone.OpenUI();
            Require(!StorePhoneClient.IsNumberExist(987654321),"QA invalid telephone number exists");
            foreach(int digit in new[]{9,8,7,6,5,4,3,2,1}) phone.PressNumber(digit);
            audioRound++;
            return false;
        }
        if (world.QaInvalidNumbers==0) return false;
        Require(world.QaPhoneRings==1 && world.QaInvalidNumbers==1 && !world.SharedPhoneBusy,
            "original dial tone/invalid number did not finish exactly once");
        Record("PASS native-manual-dial-audio","guest manual dial played the original calling and invalid-number sources and stopped the ring");
        return true;
    }

    private void TickRequestedShootingHost(RoomSelector room,double now)
    {
        if(stage==0)
        {
            NotebookUIManager.Instance.CloseUI();
            SeedShootingQa();
            ExtendedLogisticsQaHostDone=true;
            stage=1;
        }
        bool connected=room.Session?.State==LinkState.Connected;
        observedConnection|=connected;
        if(observedConnection) CheckHostShootingQa();
        CheckShootingQaConnection(connected,now);
        if(ShootingQaHostDone && mechanismsHostFinishAt==0) mechanismsHostFinishAt=now+3;
        if(mechanismsHostFinishAt!=0 && now>=mechanismsHostFinishAt) Finish("PASS requested-shooting host");
    }

    private bool TickRequestedShootingGuest(ReplicaLink link,double now)
    {
        if(resumedQa && !ResumeShootingQa(link,now)) return false;
        return RunGuestShootingQa(link,now);
    }
}
