#if PROBABLY_COOP_GRID_QA
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private const string TestSeller = "coop_qa_seller";
    private const string TestBuyer = "coop_qa_buyer";
    private const string TestGoods = "processed_juice";
    private static readonly AccessTools.FieldRef<NegociationUIManager, TabletButton> TradeButton =
        AccessTools.FieldRefAccess<NegociationUIManager, TabletButton>("primaryButton");

    private int businessDay, businessPhase, businessPurchases, businessSales;
    private int businessItem, price, cash, oldPrice;
    private ulong priorQuote;
    private bool guestSawSellerWalk, guestSawBuyerWalk, guestSawDialogue;
    private double businessNext;

    private void SeedBusinessQa(int day, bool verifyDialogueTheme = false)
    {
        var store = PlayerStore.Instance;
        businessPurchases = businessSales = 0;
        store.isBargainIntroduced = true;
        var seller = new StoreClient
        {
            identifier = TestSeller,
            displayName = "QA Seller",
            spriteName = SpriteDict.GetRandomLowerLevelSpriteName(),
            clientIntent = StoreClient.ClientIntent.SELL,
            isIntroduced = true,
            OnArrival = () => store.AddDirectSellingItemToTable(TestGoods),
            onItemSold = (item, _) => { if (item.identifier == TestGoods) businessPurchases++; },
        };
        var buyer = new StoreClient
        {
            identifier = TestBuyer,
            displayName = "QA Buyer",
            spriteName = SpriteDict.GetRandomScavSpriteName(),
            clientIntent = StoreClient.ClientIntent.BUY,
            isIntroduced = true,
            useClientBudget = true,
            onItemBought = (item, _) => { if (item.identifier == TestGoods) businessSales++; },
        };
        if (verifyDialogueTheme)
        {
            seller.clientFaction = "FACTION_UPPER_LEVEL";
            buyer.bubbleColor = "dark";
        }
        seller.mainDialogue.SetText(seller.displayName, "A drink for sale.");
        buyer.mainDialogue.SetText(buyer.displayName, "I want to buy a drink.");
        buyer.SetBudget(100);
        buyer.clientBuyingIdList.Add(TestGoods);
        store.storeClientManager.AddNextClient(buyer);
        store.storeClientManager.AddNextClient(seller);
        Debug.Log($"[probably_coop BUSINESS] queued two original customers day={day}");
    }

    private bool RunGuestBusinessQa(int day, bool hostCallsFirst = false)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (businessDay != day)
        {
            businessDay = day;
            businessPhase = 0;
            businessNext = now + 1.4;
            guestSawSellerWalk = guestSawBuyerWalk = guestSawDialogue = false;
        }
        if (businessPhase == 8) return true;
        if (now < businessNext) return false;
        var store = PlayerStore.Instance;
        var client = store.currentClientInstance?.GetClientBlueprint();
        var tablet = NegociationUIManager.Instance;
        var shop = EmporiumEntry.Instance;
        if (client?.identifier == TestSeller && StoreClientMono.Instance.IsMoving()) guestSawSellerWalk = true;
        if (client?.identifier == TestBuyer && StoreClientMono.Instance.IsMoving()) guestSawBuyerWalk = true;
        if (client != null && campaign.ClientDialogue && businessPhase is 1 or 3 or 5)
        {
            var ui = DialogUIManager.Instance;
            if (ui.currentDialog?.isMainDialog == true)
            {
                guestSawDialogue = true;
                if (hostCallsFirst && ui.clientBubble.gameObject.activeInHierarchy)
                {
                    string theme = client.identifier == TestSeller ? "upper" : client.identifier == TestBuyer ? "dark" : null;
                    if (theme != null) CheckShopDialoguePalette(ui, theme);
                }
            }
            DialogUIManager.Instance.OnAdvancePressed();
            return false;
        }
        switch (businessPhase)
        {
            case 0:
                if (hostCallsFirst)
                {
                    if (client?.identifier != TestSeller) return false;
                }
                else
                {
                    if (store.lockNextBellFlag > 0) return false;
                    Object.FindFirstObjectByType<StoreBell>().OnClick();
                }
                break;
            case 1:
                if (client?.identifier != TestSeller || !store.isClientArrived || StoreClientMono.Instance.IsMoving()) return false;
                var drink = shop.frontInvinvElement.childItems.FirstOrDefault(item => item.identifier == TestGoods);
                if (drink == null) return false;
                CampaignCheck(guestSawSellerWalk && guestSawDialogue, "guest sees the seller walk and native main dialogue");
                businessItem = drink.uniqueId;
                priorQuote = campaign.QuoteNumber;
                var slot = shop.backInvinvElement.TryFindOneValidInventorySlot(drink);
                CampaignCheck(slot?.TryAcceptOnce() == 1, "original counter accepts the seller's item");
                tablet.OpenUI();
                break;
            case 2:
                if (session.Guest.PendingCount != 0 || campaign.QuoteNumber <= priorQuote) return false;
                CampaignCheck(world.TryFindAccessible(businessItem, ProbablyCoop.Net.NetSession.GuestPeer, out var offer) &&
                    offer.parentInventory == shop.backInvinvElement, "host confirmed the seller's original counter move");
                oldPrice = (int)offer.GetNegociatedValue();
                BargainUIManager.Instance.OpenUI();
                break;
            case 3:
                if (!BargainUIManager.Instance.BuyingMarkupOffered)
                {
                    var markup = BargainUIManager.Instance.uiObject.GetComponentsInChildren<TabletButton>()
                        .FirstOrDefault(button => button.Interactable && button.label.text == "+25%");
                    if (markup == null) return false;
                    markup.button.onClick.Invoke();
                    businessNext = now + 0.4;
                    return false;
                }
                if (TradeButton(tablet)?.Interactable != true) return false;
                var trade = shop.backInvinvElement.childItems.Single(item => item.uniqueId == businessItem);
                price = (int)trade.GetNegociatedValue();
                CampaignCheck(price > oldPrice && price > 0, "the original bargaining panel displays the changed quote");
                cash = store.playerCash;
                BargainUIManager.Instance.CloseUI();
                TradeButton(tablet).button.onClick.Invoke();
                TradeButton(tablet).button.onClick.Invoke();
                break;
            case 4:
                if (store.playerCash == cash) return false;
                CampaignCheck(store.playerCash == cash - price &&
                    world.TryFindAccessible(businessItem, ProbablyCoop.Net.NetSession.GuestPeer, out var purchase) &&
                    GeneralHelper.IsItemOwned(purchase), "two stale native buy clicks pay once and transfer ownership");
                Object.FindFirstObjectByType<StoreBell>().OnClick();
                break;
            case 5:
                if (client?.identifier != TestBuyer || !store.isClientArrived || StoreClientMono.Instance.IsMoving()) return false;
                CampaignCheck(guestSawBuyerWalk, "guest sees the buyer walk in after the seller leaves");
                CampaignCheck(world.TryFindAccessible(businessItem, ProbablyCoop.Net.NetSession.GuestPeer, out var goods),
                    "the purchased item is still available for sale");
                priorQuote = campaign.QuoteNumber;
                if (goods.parentInventory != shop.backInvinvElement)
                    CampaignCheck(shop.backInvinvElement.TryFindOneValidInventorySlot(goods)?.TryAcceptOnce() == 1,
                        "original counter accepts the item for the buyer");
                else priorQuote--;
                tablet.OpenUI();
                break;
            case 6:
                if (session.Guest.PendingCount != 0 || campaign.QuoteNumber <= priorQuote ||
                    TradeButton(tablet)?.Interactable != true) return false;
                var sale = shop.backInvinvElement.childItems.Single(item => item.uniqueId == businessItem);
                price = (int)sale.GetNegociatedValue();
                CampaignCheck(price > 0, "original sale has a positive quote");
                cash = store.playerCash;
                TradeButton(tablet).button.onClick.Invoke();
                TradeButton(tablet).button.onClick.Invoke();
                break;
            case 7:
                if (store.playerCash == cash) return false;
                CampaignCheck(store.playerCash == cash + price &&
                    !world.TryFindAccessible(businessItem, ProbablyCoop.Net.NetSession.GuestPeer, out _),
                    "two stale native sell clicks pay once and retire the item");
                Object.FindFirstObjectByType<StoreDismissButton>().OnClick();
                Debug.Log($"[probably_coop BUSINESS] PASS original guest bell, walks, dialogue, bargain, buy and sell day={day}");
                break;
        }
        businessPhase++;
        return businessPhase == 8;
    }
}
#endif
