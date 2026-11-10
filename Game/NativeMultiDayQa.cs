using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int completedDays,multiStep,multiTrades,multiCash,multiDay,multiTradeRetries;
    private double multiNext,multiUntil;
    private bool multiTradePending,multiClientTraded,multiMovePending;
    private double multiTradeUntil;
    private uint[] zeroBudgetItems;
    private int[] zeroBudgetCounts;
    private Dictionary<string,int> freePurchaseStock;
    private uint[] freePurchaseIds;
    private static IEnumerable<GameItem> QaOwnedIncludingParked()
        => EmporiumEntry.Instance.GetAllOwnedItems().Concat(GraphUtils.FindAllChildrenType<GameItem>(EmporiumEntry.Instance.hirelingInv))
            .Where(GeneralHelper.IsItemOwned).Distinct();
    private static Dictionary<string,int> OwnedStock()
        => QaOwnedIncludingParked().GroupBy(i=>i.identifier)
            .ToDictionary(group=>group.Key,group=>group.Sum(i=>i.unitCount));

    private void ObserveFreePurchase(NegociationUIManager ui,StoreClient client,bool wholesale,NativeWorld world)
    {
        freePurchaseStock=null; freePurchaseIds=null;
        if(wholesale || AccessTools.Field(typeof(NegociationUIManager),"mode").GetValue(ui).ToString()!="Selling") return;
        var offered=(List<GameItem>)AccessTools.Method(typeof(NegociationUIManager),"GetNonOwnedBackInvItems").Invoke(null,null);
        if(offered.Count==0) return;
        if(client.isMultiBuyDisabled && offered.Count>1)
        {
            var selected=AccessTools.Field(typeof(NegociationUIManager),"selectedItem").GetValue(ui) as GameItem;
            offered=new List<GameItem>{selected!=null && offered.Contains(selected) ? selected : offered[0]};
        }
        if(offered.Any(i=>i.GetNegociatedValue()!=0)) return;
        var owned=OwnedStock();
        freePurchaseStock=offered.GroupBy(i=>i.identifier).ToDictionary(group=>group.Key,
            group=>(owned.TryGetValue(group.Key,out int prior) ? prior : 0)+group.Sum(i=>i.unitCount));
        freePurchaseIds=offered.Select(world.PointerIdentity).ToArray();
        Require(freePurchaseIds.All(id=>id!=0),"free purchase missing native entity identity");
    }
    private bool zeroBudgetVerified;
    private bool budgetFixture,budgetVerified;
    private bool preferenceBuyerSeeded,preferenceBuyerVerified;
    private const string ExposureFixtureId="probably_coop_qa_exposure";
    private GameItem exposureFixture;
    private bool exposureBuyerVerified;
    private byte[] obsoleteBellCommand;
    private bool bellReplayPending,bellReplayVerified;
    private const string SupplierOfferTag="QA_NATIVE_SUPPLIER_OFFER";
    private bool supplierSourceObserved,supplierOfferVerified,supplierBought;
    private int supplierQuote;
    private uint[] supplierGoods;
    private string SupplierOfferEvidence(NativeWorld world)
    {
        var store=PlayerStore.Instance;
        var client=store.currentClientInstance?.GetClientBlueprint();
        Require(client?.identifier=="courrierRedImp" && client.isOfferWholesale && client.isRegularSellDisable,
            "supplier QA is not the native wholesale delivery");
        int stock=StorePhoneClient.GetBrewerPhoneClient().stockType;
        Require(stock==0 || stock==1,"supplier order has an unexpected native stock selection");
        var goods=EmporiumEntry.Instance.GetAllNonOwnedItem();
        Require(goods.Count==stock+1 && goods.All(item=>item.identifier=="beerCase" && item.unitCount==1),
            "native courier arrived without its complete ordered cases; front="+
            string.Join(",",EmporiumEntry.Instance.frontInvinvElement.childItems.Select(item=>item.identifier+":"+item.unitCount+":"+GeneralHelper.IsItemOwned(item))));
        foreach(var item in goods)
        {
            var contents=GraphUtils.FindAllChildrenType<GameItem>(item);
            Require(contents.Count>0 && contents.All(child=>child.identifier=="red_beer") && contents.Sum(child=>child.unitCount)==8,
                "native courier case contents were lost or synthesized incompletely");
        }
        int quote=store.GetWholesalePrice();
        Require(quote>0,"native courier has an empty/zero wholesale offer");
        var rows=goods.OrderBy(world.PointerIdentity).Select(item=>world.PointerIdentity(item)+":"+item.identifier+":"+item.unitCount+":"+
            string.Join(",",GraphUtils.FindAllChildrenType<GameItem>(item).OrderBy(world.PointerIdentity)
                .Select(child=>world.PointerIdentity(child)+":"+child.identifier+":"+child.unitCount))).ToArray();
        Require(goods.Concat(goods.SelectMany(item=>GraphUtils.FindAllChildrenType<GameItem>(item))).All(item=>world.PointerIdentity(item)!=0),
            "supplier goods have no stable network identities");
        return quote+"|"+string.Join(";",rows);
    }

    private void StageBusinessCounter()
    {
        // Only the disposable multi-day QA fixture is rearranged. A real
        // wholesale purchase can leave owned goods on the front when its back
        // counter is full; the next customer must not lose an offer because
        // this suite left its earlier test cargo there.
        var store=PlayerStore.Instance; var actors=NativeActorSession.Current;
        if(!Enabled || !host || !IsHostProduct || !saveRoundTrip || store.currentClientInstance!=null ||
            MapUIManager.Instance.IsOutside || actors?.CanCommit!=true ||
            NativeShop.Gate?.HeldForInteraction!=null || NegociationUIManager.IsTradeLocked()) return;
        var entry=EmporiumEntry.Instance;
        foreach(var root in entry.frontInvinvElement.childItems.Where(GeneralHelper.IsItemOwned).ToArray())
        {
            var subtree=GraphUtils.FindAllChildrenType<GameItem>(root).Concat(new[]{root}).Distinct()
                .ToDictionary(item=>item,item=>(Parent:item.parentInventory,Count:item.unitCount,Owned:GeneralHelper.IsItemOwned(item)));
            var shape=new GridShapeBuilder(root.modifiedShape); SlotMarker target=null;
            for(int flip=0;flip<2 && target==null;flip++)
            for(int turn=0;turn<4 && target==null;turn++)
            for(int y=0;y<entry.hirelingInv.inventoryShape.height && target==null;y++)
            for(int x=0;x<entry.hirelingInv.inventoryShape.width && target==null;x++)
            {
                shape.SetTransform(x,y,flip!=0,turn);
                var candidate=entry.hirelingInv.TryInventorySlot(root,root.unitCount,shape);
                if(candidate?.IsValid()==true && candidate.targetItem==null && candidate.numTransfer>=root.unitCount) target=candidate;
            }
            Require(target!=null,"QA setup cannot park complete owned front cargo in original hireling capacity: "+root.identifier);
            Require(target.TryAcceptOnce(root.unitCount)==subtree[root].Count && root.parentInventory==entry.hirelingInv,
                "QA original complete-slot staging failed: "+root.identifier);
            var after=GraphUtils.FindAllChildrenType<GameItem>(root).Concat(new[]{root}).Distinct().ToArray();
            Require(after.Length==subtree.Count && after.All(item=>subtree.TryGetValue(item,out var prior) &&
                item.unitCount==prior.Count && GeneralHelper.IsItemOwned(item)==prior.Owned &&
                (ReferenceEquals(item,root) || ReferenceEquals(item.parentInventory,prior.Parent))),
                "QA staging changed a cargo identity, quantity, ownership or nested parent");
            Record("FIXTURE business-counter-staged","native full slot front -> shared hireling; item="+root.identifier+
                " nativeId="+root.uniqueId+" units="+root.unitCount+" subtree="+subtree.Count+"; no deletion, split, merge or capacity change");
        }
    }

    private void SeedBudgetConfirmation()
    {
        if(StoreStation.Instance.dayCounter<7) return;
        StageBusinessCounter();
        var client=PlayerStore.Instance.currentClientInstance?.GetClientBlueprint();
        if(!supplierSourceObserved && client?.identifier=="courrierRedImp" && PlayerStore.Instance.isClientArrived &&
            DialogUIManager.Instance.currentDialog==null && !NativeWorld.Host.PendingMainDialogue &&
            EmporiumEntry.Instance.GetAllNonOwnedItem().Count>0 &&
            EmporiumEntry.Instance.GetAllNonOwnedItem().SelectMany(item=>GraphUtils.FindAllChildrenType<GameItem>(item).Concat(new[]{item}))
                .All(item=>NativeWorld.Host.PointerIdentity(item)!=0))
        {
            using var actor=NativeActorSession.Current?.Enter(NativeActorRun.Slot.Partner);
            string evidence=SupplierOfferEvidence(NativeWorld.Host);
            qaMachine.ModifyTag(SupplierOfferTag,tag=>tag.Enable().SetString(evidence));
            supplierSourceObserved=true;
            Record("PASS native-supplier-source-offer",evidence+"; original courier endAction created cases and all contained beer");
        }
        if(exposureFixture!=null && client?.identifier!="annoyedEngineer")
        {
            exposureFixture.Destroy(); exposureFixture=null;
            Record("FIXTURE exposure-cleanup","removed disposable inspection item after the buyer departed");
        }
        if(!preferenceBuyerSeeded && client?.identifier=="annoyedEngineer" && !client.isBusinessComplete && PlayerStore.Instance.isClientArrived)
        {
            // Deterministic coverage of a vanilla delegate-backed buyer. Add a
            // real eligible item in this disposable host, not a fake query result.
            var item=DirectoryMaster.Item("processed_meat"); GeneralHelper.SetItemOwned(item,true);
            Require(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.backInvinvElement,item)==0 && item.parentInventory!=null,"preference buyer fixture does not fit");
            var preferenceField=AccessTools.Field(typeof(StoreClient),"willAcceptItemPreference");
            var originalPreference=preferenceField.GetValue(client);
            var proofMethod=AccessTools.Method(typeof(NativeWorld),"TradeToken");
            string originalProof=(string)proofMethod.Invoke(NativeWorld.Host,null);
            try
            {
                preferenceField.SetValue(client,new Func<GameItem,bool>(_=>false));
                Require((string)proofMethod.Invoke(NativeWorld.Host,null)!=originalProof,"callback-only purchase eligibility did not invalidate quote");
            }
            finally { preferenceField.SetValue(client,originalPreference); }
            Require((string)proofMethod.Invoke(NativeWorld.Host,null)==originalProof,"customer preference proof restoration differed");
            Record("PASS callback-quote-invalidation","same customer fields/prices with changed purchase callback rejected the previous quote proof; original callback restored");
            var feature=new ItemFeature {identifier=ExposureFixtureId,isExposable=true};
            feature.SetPublicDisplay("QA inspection").SetActualDisplay("QA inspection");
            var originalExposure=client.canClientExposeFeature;
            item.itemFeatures.Add(feature);
            try
            {
                client.canClientExposeFeature=(f,c)=>false;
                string beforeExposure=(string)proofMethod.Invoke(NativeWorld.Host,null);
                client.canClientExposeFeature=(f,c)=>ReferenceEquals(f,feature);
                Require(client.CanClientExposeAnyFeatureDeep(item) && (string)proofMethod.Invoke(NativeWorld.Host,null)!=beforeExposure,
                    "callback-only inspection eligibility did not invalidate native quote");
            }
            finally {item.itemFeatures.Remove(feature); client.canClientExposeFeature=originalExposure;}
            Record("PASS exposure-quote-invalidation","same customer/item fields and price; only inspection callback changes quote proof");
            // A distinct owned food remains off the counter. Its native
            // inspection decision must reach the real guest, without running
            // the factory or customer business delegate there.
            exposureFixture=DirectoryMaster.Item("processed_meat"); GeneralHelper.SetItemOwned(exposureFixture,true);
            exposureFixture.itemFeatures.Add(feature);
            Require(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.invElement,exposureFixture)==0 && exposureFixture.parentInventory!=null,"inspection fixture does not fit");
            client.canClientExposeFeature=(f,c)=>ReferenceEquals(f,feature) || originalExposure?.Invoke(f,c)==true;
            Require(client.IsClientBuyingThisItem(exposureFixture) && client.CanClientExposeAnyFeature(exposureFixture) &&
                client.CanClientExposeAnyFeatureDeep(exposureFixture) && !PlayerStore.Instance.CanSellThisItem(exposureFixture,true),"native inspection fixture does not gate sale");
            preferenceBuyerSeeded=true;
            Record("FIXTURE preference-buyer","native annoyedEngineer callback + owned processed_meat on counter; QA only");
        }
        if(budgetFixture) return;
        if(client==null || client.clientIntent!=StoreClient.ClientIntent.BUY || client.isBusinessComplete ||
            !PlayerStore.Instance.isClientArrived || NegociationUIManager.Instance.GetMultiSellValue()<=1) return;
        // Disposable host only: exercise the vanilla over-budget confirmation,
        // independent of which randomized buyer happens to arrive this run.
        client.SetClientBudget(0); budgetFixture=true;
        Record("FIXTURE budget-sale","native buyer="+client.identifier+" budget=0 after owned goods are offered; isolated QA only");
    }
    private string multiClient="";
    private readonly HashSet<string> multiObserved=new HashSet<string>();
    private bool rentPending,rentVerified;
    private int rentCash,rentPrice;
    private byte[] rentCommand;
    private bool rentReplayPending;
    private bool storyClosePending,storyClosed;
    private string storyClosingClient;
    private bool storyContinuationPending;
    private int storyCardsBefore;
    private bool reportSent,reportAck,reportVerified,reportActors,reportReplay;
    private int reportCash,reportReward;
    private byte[] oldReportCommand;
    private bool RunAdditionalDays(CoopSession session,double now)
    {
        if(!saveRoundTrip || completedDays>=3) return true;
        if(now<multiNext || QaWaiting(session.Native) || session.Shop.Waiting) return false;
        var store=PlayerStore.Instance; var world=NativeWorld.Guest; var ui=NegociationUIManager.Instance;
        var activeId=store.currentClientInstance?.GetClientBlueprint()?.identifier;
        if(activeId!=null) multiObserved.Add(activeId);
        if(bellReplayPending)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Stale,"old bell context acted on the next customer");
            bellReplayPending=false; bellReplayVerified=true;
            Record("PASS stale-native-bell","a new request using the previous empty-counter proof was rejected after customer arrival");
        }
        if(reportAck)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Applied,"native security report rejected"); reportAck=false;
        }
        if(reportReplay)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Stale && store.playerCash==reportCash+reportReward,"obsolete report paid reward twice");
            reportReplay=false; reportVerified=true;
            Record("PASS native-security-report","original report button, glass closure, officer sprites, guest-advanced native arrest dialogue, cash reward exactly once and wanted poster state; obsolete report rejected");
        }
        if(reportSent && !reportVerified)
        {
            if(!reportActors && StoreUIManager.Instance.secGameObject.gameObject.activeInHierarchy && StoreUIManager.Instance.StoreGlass.IsClosed)
            { reportActors=true; NativeOffscreenCapture.Capture("qa-security-arrest"); }
            if(store.wantedStatus.TryGetValue("wanted8",out int caught) && caught==1 && !world.HasDialogue && activeId!="wanted8")
            {
                Require(reportActors && store.playerCash==reportCash+reportReward,"security sequence lost actor presentation or reward");
                Require(session.Native.Submit(oldReportCommand,now),"obsolete report replay failed to send");
                reportReplay=true; multiNext=now+0.8; return false;
            }
        }
        if(storyClosePending)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Applied,"native story panel close rejected");
            Require(!WildNetworkUI.IsOpen,"original story panel remained open after its close receipt");
            storyClosePending=false; storyContinuationPending=true;
        }
        if(storyContinuationPending)
        {
            Require(now<multiUntil,"story close did not complete its native continuation: "+storyClosingClient);
            if(activeId!=storyClosingClient && !world.HasDialogue && !world.PendingMainDialogue)
            {
                if(storyClosingClient=="wild2")
                    Require(store.isNetworkIntroduced && StorePhoneClient.GetWildPhoneClient().phoneState==StorePhoneClient.PhoneState.Regular &&
                        QaOwnedIncludingParked().Where(item=>item.identifier=="joe_card").Sum(item=>item.unitCount)==storyCardsBefore+1,
                        "native network introduction did not grant its original phone card and contact exactly once");
                storyContinuationPending=false; storyClosed=true;
                Record("PASS mandatory-story-panel","original scripted Wild window/close and continuation completed; client="+storyClosingClient+
                    "; introduction continues original dialogue before dismissal; no QA flag or customer changes");
            }
            // The introduction resumes a real dialogue on close; allow the
            // ordinary dialogue driver below to play it through to dismissal.
        }
        if(WildNetworkUI.IsOpen)
        {
            Require(activeId=="wild_upgrade_reminder" || activeId=="wild2","unexpected native story panel: "+activeId);
            Require(!storyContinuationPending,"native story reopened before completing the previous continuation");
            storyClosingClient=activeId;
            storyCardsBefore=QaOwnedIncludingParked().Where(item=>item.identifier=="joe_card").Sum(item=>item.unitCount);
            NativeOffscreenCapture.Capture("qa-story-upgrade");
            WildNetworkUI.Close(); Require(QaWaiting(session.Native),"story panel close emitted no request");
            storyClosePending=true; multiNext=now+0.8; return false;
        }
        if(rentReplayPending)
        {
            Require(QaResult(session.Native)==(ushort)ShopResult.Stale && store.playerCash==rentCash-rentPrice,"repeated rent choice was accepted or charged twice");
            rentReplayPending=false;
            Record("PASS mandatory-rent-stale-choice","old dialogue proof with new request ID rejected; no second rent charge");
        }
        if(rentPending)
        {
            if(store.playerCash==rentCash) { Require(now<multiUntil,"rent acknowledgement did not update cash"); return false; }
            Require(store.playerCash==rentCash-rentPrice && store.dayUntilRent==7 && store.temporaryRent==0,"rent charged incorrectly or reset missing");
            rentPending=false; rentVerified=true;
            Record("PASS mandatory-rent","native guest choice -> host OnRentPaid exactly once; cash="+rentCash+"->"+store.playerCash+" timer=7");
            Require(session.Native.Submit(rentCommand,now),"could not submit obsolete rent choice");
            rentReplayPending=true; multiNext=now+0.8; return false;
        }
        if(multiStep==0)
        {
            if(completedDays==0) completedDays=1;
            multiDay=StoreStation.Instance.dayCounter; multiTrades=0; multiUntil=now+240;
            StartOfDayUIManager.Instance.OnStartDayButtonClicked(); AdvCalendarUIManager.Instance.CloseUI();
            multiStep=1; multiNext=now+1; return false;
        }
        Require(now<multiUntil,"consecutive day stalled day="+multiDay+" step="+multiStep+" client="+multiClient);
        if(multiMovePending) { multiMovePending=false; Require(QaResult(session.Native)==(ushort)ShopResult.Applied,"consecutive native move rejected: "+QaResult(session.Native)); }
        if(multiTradePending)
        {
            if(QaResult(session.Native)==(ushort)ShopResult.Stale && multiTradeRetries++<3)
            {
                Require(store.playerCash==multiCash,"stale trade changed cash"); multiTradePending=false;
                Record("PASS stale-live-quote","no cash mutation; wait for refreshed native quote before retry"); multiNext=now+1; return false;
            }
            Require(QaResult(session.Native)==(ushort)ShopResult.Applied,"multiday trade rejected: "+QaResult(session.Native));
            bool zeroSale=zeroBudgetItems!=null && zeroBudgetItems.Length>0;
            if(zeroSale)
            {
                Require(store.playerCash==multiCash,"zero-budget native sale unexpectedly changed cash");
                for(int i=0;i<zeroBudgetItems.Length;i++)
                    if(!world.Objects.TryGetValue(zeroBudgetItems[i],out var obj) || obj is not GameItem sold ||
                        sold.parentInventory!=EmporiumEntry.Instance.soldElement || GeneralHelper.IsItemOwned(sold) || sold.unitCount!=zeroBudgetCounts[i])
                    { zeroSale=false; break; }
            }
            bool freeBought=false;
            if(freePurchaseStock!=null)
            {
                Require(store.playerCash==multiCash,"free native purchase changed cash");
                var inventory=OwnedStock();
                freeBought=freePurchaseStock.All(pair=>inventory.TryGetValue(pair.Key,out int actual) && actual==pair.Value) &&
                    freePurchaseIds.All(id=>!world.Objects.TryGetValue(id,out var value) ||
                        value is GameItem item && GeneralHelper.IsItemOwned(item));
            }
            if(store.playerCash==multiCash && !zeroSale && !freeBought)
            {
                Require(now<multiTradeUntil,"native trade acknowledged without cash/item outcome; client="+multiClient+
                    " zeroExpected="+(zeroBudgetItems?.Length ?? 0)+" counter="+string.Join(",",EmporiumEntry.Instance.backInvinvElement.childItems.Select(i=>
                        world.PointerIdentity(i)+":"+i.identifier+":"+i.unitCount+":"+GeneralHelper.IsItemOwned(i)))+
                    " sold="+string.Join(",",EmporiumEntry.Instance.soldElement.childItems.Select(i=>world.PointerIdentity(i)+":"+i.identifier+":"+i.unitCount)));
                multiNext=now+0.5; return false;
            }
            if(zeroSale)
            {
                zeroBudgetVerified=true;
                Record("PASS native-zero-budget-sale","cash unchanged, all "+zeroBudgetItems.Length+" exact entities/quantities moved to native sold inventory and ownership cleared");
            }
            if(freeBought) Record("PASS native-free-purchase","cash unchanged; exact offered identities acquired or merged, owned quantities verified; client="+multiClient);
            freePurchaseStock=null; freePurchaseIds=null;
            zeroBudgetItems=null; zeroBudgetCounts=null;
            multiTradePending=false; multiClientTraded=true; multiTrades++;
            if(multiClient=="courrierRedImp")
            {
                Require(supplierOfferVerified && store.playerCash==multiCash-supplierQuote && supplierGoods.All(id=>
                    world.Objects.TryGetValue(id,out var value) && value is GameItem item && GeneralHelper.IsItemOwned(item)),
                    "courier purchase did not preserve offered identities, transfer ownership and charge its native quote");
                supplierBought=true;
                Record("PASS native-supplier-purchase","original wholesale button; exact offered cases acquired; cash="+multiCash+"->"+store.playerCash);
            }
            Record("PASS consecutive-trade","day="+multiDay+" client="+multiClient+" cash="+multiCash+"->"+store.playerCash);
            if(multiClient=="annoyedEngineer" && !preferenceBuyerVerified)
            {
                preferenceBuyerVerified=true;
                Record("PASS native-preference-buyer","original guest buying UI accepts delegate-selected food and host completed the native sale");
            }
        }
        if(world.HasDialogue)
        {
            var dialog=DialogUIManager.Instance;
            var choice=dialog.dialogChoiceBubbles.FirstOrDefault(b=>b.gameObject.activeSelf && b.IsInteractable());
            if(choice!=null)
            {
                // Disposable acceptance scenario takes the native first enabled option:
                // landlord's pay-rent branch and ordinary acknowledgement choices.
                Record("TRACE consecutive-choice","day="+multiDay+" client="+store.currentClientInstance?.GetClientBlueprint()?.identifier+" text="+choice.TMP.text);
                if(activeId=="landlord" && !rentVerified)
                {
                    Require(store.CanPayRent(),"fixture cannot afford mandatory rent"); rentPending=true; rentCash=store.playerCash; rentPrice=store.rentValue+store.temporaryRent;
                    var projection=(Newtonsoft.Json.Linq.JObject)AccessTools.Field(typeof(NativeWorld),"dialogueState").GetValue(world);
                    rentCommand=System.Text.Encoding.UTF8.GetBytes(new Newtonsoft.Json.Linq.JObject { ["kind"]="dialogue",["choice"]=dialog.dialogChoiceBubbles.IndexOf(choice),["proof"]=projection["proof"] }.ToString(Newtonsoft.Json.Formatting.None));
                }
                choice.OnDialogChoiceClick();
            }
            else dialog.OnAdvancePressed();
            multiNext=now+0.8; return false;
        }
        if(reportSent && !reportVerified) { multiNext=now+0.2; return false; }
        // Arrival and StartMainDialogue are separated by a native scheduled
        // callback. A fast snapshot may legitimately show an arrived, dismissable
        // story customer before its first line. Do not make the test dismiss it.
        if(world.PendingMainDialogue) { multiNext=now+0.2; return false; }
        if(obsoleteBellCommand!=null && !bellReplayVerified && activeId!=null)
        {
            Require(session.Native.Submit(obsoleteBellCommand,now),"old bell replay was not submitted");
            bellReplayPending=true; multiNext=now+0.8; return false;
        }
        if(multiStep==1)
        {
            if(store.storeState==PlayerStore.StoreState.MORNING)
            { if(world.CanOpen && store.lockShutterButtonFlag==0) StoreShutterButton.Instance.OnClick(); multiNext=now+3.5; return false; }
            Require(store.storeState==PlayerStore.StoreState.OPEN,"consecutive day failed to enter open state");
            Record("PASS consecutive-open","day="+multiDay+" native generated queue="+world.QueuedClients);
            multiStep=2;
        }
        if(multiStep==2)
        {
            var client=store.currentClientInstance?.GetClientBlueprint();
            if(client==null)
            {
                multiClient=""; multiClientTraded=false;
                // Host performs the ordinary complete-slot QA rearrangement
                // only while neither actor is holding/committing. Wait for its
                // projected result before asking the next real customer in.
                if(EmporiumEntry.Instance.frontInvinvElement.childItems.Any(GeneralHelper.IsItemOwned))
                { multiNext=now+0.2; return false; }
                if(world.QueuedClients>0)
                {
                    if(obsoleteBellCommand==null)
                    {
                        var projection=(Newtonsoft.Json.Linq.JObject)AccessTools.Field(typeof(NativeWorld),"dialogueState").GetValue(world);
                        obsoleteBellCommand=System.Text.Encoding.UTF8.GetBytes(new Newtonsoft.Json.Linq.JObject {
                            ["kind"]="bell",["proof"]=projection["bellProof"] }.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    UnityEngine.Object.FindObjectOfType<StoreBell>().GetComponent<Interactable>().OnClick();
                    Require(QaWaiting(session.Native),"original bell interaction did not enter the native authoritative route");
                    multiNext=now+3; return false;
                }
                Require(multiTrades>0,"consecutive day did not complete an economic trade");
                Require(world.CanClose,"empty queue cannot close");
                StoreShutterButton.Instance.OnClick(); multiStep=3; multiNext=now+3; return false;
            }
            if(!store.isClientArrived || store.lockNextBellFlag>0 || NegociationUIManager.IsTradeLocked()) { multiNext=now+0.5; return false; }
            if(client.identifier=="wanted8" && !reportVerified)
            {
                reportCash=store.playerCash;
                reportReward=StartingPerkContent.HandleReportRewardModelCitizen(StartingPerkContent.HandleReportRewardConvict(client.reportReward));
                var projection=(Newtonsoft.Json.Linq.JObject)AccessTools.Field(typeof(NativeWorld),"securityScene").GetValue(world);
                oldReportCommand=System.Text.Encoding.UTF8.GetBytes(new Newtonsoft.Json.Linq.JObject { ["kind"]="security",["proof"]=projection["proof"] }.ToString(Newtonsoft.Json.Formatting.None));
                StoreSecButton.Instance.GetComponent<Interactable>().OnClick();
                Require(QaWaiting(session.Native),"original security button emitted no request");
                reportSent=reportAck=true; multiNext=now+0.8; return false;
            }
            if(multiClient!=client.identifier)
            {
                multiClient=client.identifier; multiClientTraded=false; multiTradeRetries=0;
                multiObserved.Add(client.identifier); Record("TRACE consecutive-client","day="+multiDay+" id="+client.identifier+" intent="+client.clientIntent);
            }
            if(!multiClientTraded && !client.isBusinessComplete)
            {
                var e=EmporiumEntry.Instance;
                // The successful-rent scenario must not spend its rent on a
                // randomized supplier. This only changes the QA actor's buying
                // decisions; no cash, quote, native button or rent rule changes.
                int reservedRent=rentVerified ? 0 : Math.Max(0,store.rentValue+store.temporaryRent);
                int spendable=Math.Max(0,store.playerCash-reservedRent);
                GameItem offered=null;
                if(client.clientIntent is StoreClient.ClientIntent.SELL or StoreClient.ClientIntent.SELLNBUY)
                    offered=e.frontInvinvElement.childItems.FirstOrDefault(i=>!GeneralHelper.IsItemOwned(i) && i.GetNegociatedValue()<=spendable);
                else if(client.clientIntent==StoreClient.ClientIntent.BUY)
                    offered=e.invElement.childItems.FirstOrDefault(i=>GeneralHelper.IsItemOwned(i) && i.identifier=="processed_meat" && !i.itemFeatures.Any(f=>f.identifier==ExposureFixtureId));
                bool wholesaleAffordable=client.isOfferWholesale && store.GetWholesalePrice()<=spendable;
                if(offered!=null && !wholesaleAffordable && !client.isRegularSellDisable && !e.backInvinvElement.childItems.Any(i=>GeneralHelper.IsItemOwned(i)==GeneralHelper.IsItemOwned(offered)))
                {
                    var slot=e.backInvinvElement.TryFindOneValidInventorySlot(offered);
                    if(slot?.IsValid()==true) { slot.TryAcceptOnce(); multiMovePending=QaWaiting(session.Native); multiNext=now+0.8; return false; }
                    if(client.identifier=="clinicVolunteer")
                    {
                        // A previous supplier may have left paid stock on the
                        // counter. Make room through ordinary guest moves before
                        // deciding that the volunteer has nothing to give.
                        foreach(var own in e.backInvinvElement.childItems.Where(GeneralHelper.IsItemOwned))
                        {
                            foreach(var destination in new[]{e.invElement,e.showcaseElement})
                            {
                                var clear=destination.TryFindOneValidInventorySlot(own);
                                if(clear?.IsValid()!=true || clear.numTransfer<own.unitCount) continue;
                                clear.TryAcceptOnce();
                                Require(QaWaiting(session.Native),"counter rearrangement did not reach host");
                                multiMovePending=true; multiNext=now+0.8; return false;
                            }
                        }
                        throw new System.InvalidOperationException("No accessible place to move paid counter stock for the clinic offer");
                    }
                }
                ui.OpenUI(); ui.RefreshUI();
                var button=(TabletButton)AccessTools.Field(typeof(NegociationUIManager),"primaryButton").GetValue(ui);
                if(client.identifier=="courrierRedImp" && !supplierOfferVerified)
                {
                    string source=MachineReplica().GetTagReadonly(SupplierOfferTag).GetString();
                    if(string.IsNullOrEmpty(source)) { multiNext=now+0.2; return false; }
                    string projected=SupplierOfferEvidence(world);
                    Require(projected==source,"guest supplier items/contents/identities/quote differ from the native host offer");
                    supplierQuote=store.GetWholesalePrice();
                    supplierGoods=e.GetAllNonOwnedItem().Select(world.PointerIdentity).ToArray();
                    var wholesale=(TabletButton)AccessTools.Field(typeof(NegociationUIManager),"secondaryButton").GetValue(ui);
                    var frame=(NegotiationTabletFrame)AccessTools.Field(typeof(NegociationUIManager),"frame").GetValue(ui);
                    string total=TabletUI.Credits(e.backInvinvElement.childItems.Where(item=>!GeneralHelper.IsItemOwned(item)).Sum(item=>(int)item.GetNegociatedValue()))+
                        " / "+TabletUI.Credits(supplierQuote)+" ";
                    Require(ui.uiObject.activeInHierarchy && AccessTools.Field(typeof(NegociationUIManager),"mode").GetValue(ui).ToString()=="Selling" &&
                        wholesale!=null && wholesale.Interactable==store.CanPay(supplierQuote) && button?.Interactable==false &&
                        frame.footer.GetComponentsInChildren<TMPro.TMP_Text>().Any(text=>text.gameObject.activeInHierarchy && text.text.StartsWith(total,StringComparison.Ordinal)),
                        "guest original supplier UI lacks the native wholesale quote or has incorrect affordability/regular-purchase controls");
                    supplierOfferVerified=true; NativeOffscreenCapture.Capture("qa-supplier-delivery");
                    Record("PASS native-supplier-delivery-offer",projected+"; original Guest Selling tablet and wholesale footer/button verified; cash="+store.playerCash);
                    if(!wholesaleAffordable)
                        Record("TRACE supplier-not-purchased","real offer retained; cash="+store.playerCash+" reservedRent="+reservedRent+" quote="+supplierQuote+"; no receipt/ownership claim");
                }
                if(wholesaleAffordable)
                {
                    button=(TabletButton)AccessTools.Field(typeof(NegociationUIManager),"secondaryButton").GetValue(ui);
                }
                if(!wholesaleAffordable && AccessTools.Field(typeof(NegociationUIManager),"mode").GetValue(ui).ToString()=="Selling" &&
                    e.backInvinvElement.childItems.Where(i=>!GeneralHelper.IsItemOwned(i)).Sum(i=>(int)i.GetNegociatedValue())>spendable)
                {
                    Record("TRACE rent-reserve","skip purchase cash="+store.playerCash+" reserved="+reservedRent+" client="+client.identifier);
                    button=null;
                }
                if(button?.Interactable==true)
                {
                    // The disposable food/inspection fixture is seeded once.
                    // The same blueprint may return naturally on a later day,
                    // after that exact food has already been sold.
                    if(client.identifier=="annoyedEngineer" && !preferenceBuyerVerified)
                    {
                        if(!exposureBuyerVerified)
                        {
                            var inspect=world.Objects.Values.OfType<GameItem>().SingleOrDefault(i=>i.itemFeatures.Any(f=>f.identifier==ExposureFixtureId));
                            Require(inspect!=null && client.IsClientBuyingThisItem(inspect) && client.CanClientExposeAnyFeature(inspect) &&
                                client.CanClientExposeAnyFeatureDeep(inspect) && !store.CanSellThisItem(inspect,true),"guest inspection decision differs from native host sale gate");
                            exposureBuyerVerified=true;
                            Record("PASS native-customer-inspection","real guest owned eligible food remains unsellable because host inspection callback can expose it; normal/deep native queries match");
                        }
                        var food=e.backInvinvElement.childItems.FirstOrDefault(i=>i.identifier=="processed_meat" && GeneralHelper.IsItemOwned(i));
                        Require(food!=null && client.willAcceptItem?.Invoke(food)==true && client.IsClientBuyingThisItem(food) && !client.IsClientRefusingItem(food),"native customer preference projection differs from eligible food");
                        Require(!client.CanClientExposeAnyFeature(food) && !client.CanClientExposeAnyFeatureDeep(food),"ordinary eligible food incorrectly blocked by inspection projection");
                        NativeOffscreenCapture.Capture("qa-preference-buyer");
                    }
                    multiCash=store.playerCash; zeroBudgetItems=null; zeroBudgetCounts=null;
                    ObserveFreePurchase(ui,client,wholesaleAffordable,world);
                    button.button.onClick.Invoke();
                    var frame=(NegotiationTabletFrame)AccessTools.Field(typeof(NegociationUIManager),"frame").GetValue(ui);
                    if(!QaWaiting(session.Native) && frame.confirmRoot.activeSelf)
                    {
                        Require(!ui.IsClientHaveEnoughToBuy(),"unexpected budget confirmation");
                        NativeOffscreenCapture.Capture("qa-budget-confirmation");
                        ui.OnConfirmPanelNo();
                        Require(!QaWaiting(session.Native) && store.playerCash==multiCash && !frame.confirmRoot.activeSelf,"declining budget sale changed shared state");
                        ui.OnSellAllClick();
                        Require(frame.confirmRoot.activeSelf && !QaWaiting(session.Native),"budget confirmation did not remain local");
                        if(client.GetBudget()==0)
                        {
                            var sale=ui.GetSellableItem();
                            Require(sale.Count>0 && sale.All(GeneralHelper.IsItemOwned),"zero-budget confirmation has no owned goods");
                            zeroBudgetItems=sale.Select(world.PointerIdentity).ToArray(); zeroBudgetCounts=sale.Select(i=>i.unitCount).ToArray();
                            Require(zeroBudgetItems.All(id=>id!=0),"zero-budget sale contains unidentified goods");
                        }
                        ui.OnConfirmPanelYes();
                        Require(QaWaiting(session.Native) && !frame.confirmRoot.activeSelf,"confirmed budget sale emitted no authoritative request");
                        budgetVerified=true;
                        Record("PASS budget-sale-confirmation","original guest confirmation: no cancels locally, yes submits versioned sale; host executes native SellMultiple");
                    }
                    if(QaWaiting(session.Native)) { multiTradePending=true; multiTradeUntil=now+12; multiNext=now+1; return false; }
                }
                Record("TRACE consecutive-no-trade","client="+client.identifier+" cash="+store.playerCash+" wholesale="+client.isOfferWholesale+" wholesalePrice="+store.GetWholesalePrice()+" regularDisabled="+client.isRegularSellDisable+" offered="+offered?.identifier+" back="+string.Join(",",e.backInvinvElement.childItems.Select(i=>i.identifier+":"+i.GetNegociatedValue()+":"+GeneralHelper.IsItemOwned(i)))+" button="+button?.Interactable);
            }
            if(store.IsClientDimissable())
            {
                UnityEngine.Object.FindObjectOfType<StoreBell>().GetComponent<Interactable>().OnClick();
                Require(QaWaiting(session.Native),"original dismiss/call interaction bypassed the native context proof");
                multiNext=now+3; return false;
            }
            multiNext=now+0.8; return false;
        }
        if(multiStep==3)
        {
            if(store.storeState!=PlayerStore.StoreState.CLOSED) return false;
            Record("PASS consecutive-business-close","day="+multiDay+" all customers handled; trades="+multiTrades);
            if(dayStep!=0) dayStep=0;
            multiStep=4;
        }
        if(!RunGuestDay(session,now)) return false;
        completedDays++;
        Record("PASS consecutive-day","completed="+completedDays+" savedDay="+StoreStation.Instance.dayCounter);
        NativeOffscreenCapture.Capture("qa-day-"+StoreStation.Instance.dayCounter);
        multiStep=0;
        if(completedDays>=3)
        {
            Require(QaDocument("procurement_slip").GetTagReadonly("PROCUREMENT_DAY").GetInt()==0,
                "native procurement deadline did not advance exactly once per shared night");
            Record("PASS native-paper-day-cycle","same source procurement paper deadline 3->0 after three native nights; local reading did not advance shared lifecycle");
            Require(rentVerified,"multi-day regression never verified the mandatory rent payment");
            Require(supplierOfferVerified,"phone supplier order never produced complete native goods and the matching original guest wholesale quote");
            Require(storyClosed,"native mandatory story UI was never completed by guest");
            Require(reportVerified,"native security arrest/reward sequence was not verified");
            Require(budgetVerified,"original budget-sale confirmation was not exercised");
            Require(zeroBudgetVerified,"native zero-budget transfer and unchanged cash were not verified");
            Require(preferenceBuyerVerified,"delegate-backed customer native sale was not exercised");
            Require(exposureBuyerVerified,"delegate-backed customer inspection decision was not exercised");
            Require(bellReplayVerified,"obsolete native bell context was not exercised");
            Record("PASS consecutive-mandatory-events","paid rent and verified actual telephone supplier goods/original quote; supplierPurchased="+supplierBought+"; clients="+string.Join(",",multiObserved));
        }
        return completedDays>=3;
    }
}
