using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    private static readonly AccessTools.FieldRef<NegociationUIManager, GameItem> SelectedOffer =
        AccessTools.FieldRefAccess<NegociationUIManager, GameItem>("selectedItem");

    private bool executingGuestBusiness;
    private int closeQueueDraft = -1;

    internal bool ExecutingGuestBusiness => executingGuestBusiness;
    internal bool ReplicaTradeLocked => tradeLocked;

    internal void RequestBusiness(BusinessAction action, int value = 0, int item = 0)
    {
        if (commerceView == null) return;
        var tablet = NegociationUIManager.Instance;
        ulong revision = action switch
        {
            BusinessAction.Bell or BusinessAction.Dismiss or BusinessAction.OpenShop or BusinessAction.CloseShop => 0,
            BusinessAction.Advance or BusinessAction.Choice => currentLine,
            BusinessAction.ConfirmBudget => budgetDraft,
            BusinessAction.Aim or BusinessAction.Fire or BusinessAction.Holster => shotRevision,
            _ => appliedQuote,
        };
        if (action == BusinessAction.Buy || action == BusinessAction.Accept &&
            PlayerStore.Instance.currentClientInstance?.GetClientBlueprint().clientIntent is
                StoreClient.ClientIntent.SELL or StoreClient.ClientIntent.BUY or StoreClient.ClientIntent.SELLNBUY)
            item = (SelectedOffer(tablet) ?? tablet.currentNegociatedItem)?.uniqueId ?? 0;
        if (action == BusinessAction.Sell && !tablet.IsClientHaveEnoughToBuy())
        {
            budgetDraft = appliedQuote;
            tablet.OpenConfirmPanel();
            return;
        }
        if (action == BusinessAction.ConfirmBudget)
        {
            budgetDraft = 0;
            tablet.OnConfirmPanelNo();
        }
        if (action == BusinessAction.Advance && !(bool)commerceView["canAdvance"]) return;
        if (action == BusinessAction.Choice && (value < 0 || value >= 16 || (readyChoices & (1 << value)) == 0)) return;
        var request = new BusinessIntent(action, currentCustomer, revision, item, value);
        if (request.Valid)
            Session.Request(AuthorityAction.Business, StoreStation.GetDayCounter(), business: request);
    }

    internal void PromptCloseShutter()
    {
        if (!ShutterAvailable || PlayerStore.Instance.storeState != PlayerStore.StoreState.OPEN) return;
        if (shutterQueue == 0) RequestBusiness(BusinessAction.CloseShop);
        else
        {
            closeQueueDraft = shutterQueue;
            StoreShutterButton.Instance.ShowConfirmCloseUI();
        }
    }

    internal void ConfirmCloseShutter()
    {
        var panel = StoreUIManager.Instance.closeShutterConfirmationPanel;
        int expected = panel.activeSelf ? closeQueueDraft : 0;
        panel.SetActive(false);
        closeQueueDraft = -1;
        if (ShutterAvailable && expected >= 0) RequestBusiness(BusinessAction.CloseShop, expected);
    }

    internal bool ShouldSkipReplicaLine()
    {
        var ui = DialogUIManager.Instance;
        return replicaDialogue != null && ui.IsLineStillDisplaying(replicaDialogue);
    }

    internal bool HoldsReplicaLine(DialogUIManager ui) =>
        !applyingCommerce && replicaDialogue != null && ui.currentDialog == replicaDialogue;

    private void ExecuteBusiness(BusinessIntent request)
    {
        // A second request in this frame sees the prior execution's new quote.
        PublishCommerce();
        bool atEngineer = MapUIManager.Instance.Current is DrJacksonLocation;
        bool away = MapUIManager.Instance.IsOutside;
        if (request.Customer != currentCustomer || away && (!atEngineer ||
            request.Action is not (BusinessAction.Buy or BusinessAction.Sell or BusinessAction.Accept or
                BusinessAction.ConfirmBudget or BusinessAction.Wholesale))) return;
        if (request.Action is BusinessAction.Aim or BusinessAction.Fire or BusinessAction.Holster)
        {
            if (request.Revision == shotRevision) RunShot(request.Action, NativeActorRun.Slot.Partner);
            return;
        }
        bool shutter = request.Action is BusinessAction.OpenShop or BusinessAction.CloseShop;
        if (!shutter && request.Action >= BusinessAction.Buy && request.Revision != quoteSequence) return;
        var actors = NativeActorSession.Current;
        if (actors == null || actors.Terminal || actors.Faulted || !BeginAuthorityAction()) return;
        bool completed = false;
        executingGuestBusiness = true;
        try
        {
            using (actors.Enter(shutter ? NativeActorRun.Slot.Host : NativeActorRun.Slot.Partner))
                RunBusiness(request, actors.Store);
            completed = true;
        }
        finally
        {
            executingGuestBusiness = false;
            EndAuthorityAction(completed);
        }
    }

    private void RunBusiness(BusinessIntent request, PlayerStore store)
    {
        var dialogue = DialogUIManager.Instance;
        switch (request.Action)
        {
            case BusinessAction.OpenShop:
                if (ShutterAvailable && store.storeState == PlayerStore.StoreState.MORNING)
                    StoreShutterButton.Instance.OnClick();
                return;
            case BusinessAction.CloseShop:
                if (ShutterAvailable && store.storeState == PlayerStore.StoreState.OPEN && request.Value == shutterQueue &&
                    !TouchesHand(EmporiumEntry.Instance.GetAllNonOwnedItem()))
                    StoreShutterButton.Instance.EndAndClose();
                return;
            case BusinessAction.Bell:
                if (!ShotActive && store.storeState == PlayerStore.StoreState.OPEN && store.lockNextBellFlag == 0)
                    UnityEngine.Object.FindFirstObjectByType<StoreBell>()?.RingBell();
                return;
            case BusinessAction.Dismiss:
                if (!ShotActive && store.lockNextBellFlag == 0 && store.IsClientDimissable())
                    UnityEngine.Object.FindFirstObjectByType<StoreDismissButton>()?.OnClick();
                return;
            case BusinessAction.Advance:
                if (request.Revision == currentLine && dialogueCanAdvance && !dialogue.IsChoice())
                    dialogue.OnAdvancePressed();
                return;
            case BusinessAction.Choice:
                if (request.Revision == currentLine && dialogue.IsChoice() &&
                    request.Value < dialogue.currentDialog.choices.Count &&
                    request.Value < dialogue.dialogChoiceBubbles.Count &&
                    dialogue.dialogChoiceBubbles[request.Value].gameObject.activeSelf &&
                    dialogue.currentDialog.IsChoiceEnabledAt(request.Value))
                    dialogue.SelectChoice(request.Value);
                return;
        }
        var client = store.currentClientInstance?.GetClientBlueprint();
        if (request.Action == BusinessAction.Report)
        {
            if (CanReport())
            {
                RecordReportingActor();
                StoreSecButton.Instance.OnClick();
            }
            return;
        }
        if (request.Revision != quoteSequence || client == null || !store.isClientArrived ||
            store.storeState != PlayerStore.StoreState.OPEN && MapUIManager.Instance.Current is not DrJacksonLocation ||
            client.isBusinessComplete ||
            pendingStart || dialogue.currentDialog?.isMainDialog == true || NegociationUIManager.IsTradeLocked()) return;

        if (request.Action == BusinessAction.Estimate)
        {
            if (client.clientIntent == StoreClient.ClientIntent.APPRAISAL_SERVICE &&
                inventory.TryFindAccessible(request.Item, NetSession.GuestPeer, out var offer) &&
                offer.parentInventory == EmporiumEntry.Instance.backInvinvElement &&
                offer.IsTag("APPRAISAL_ITEM") && !GeneralHelper.IsItemOwned(offer) && !TouchesHand(new[] { offer }))
                AppraisalServiceHelper.OnEstimate(offer, request.Value);
            return;
        }
        if (request.Action == BusinessAction.BarterCycle || request.Action == BusinessAction.Accept &&
            client.clientIntent == StoreClient.ClientIntent.BARTER)
        {
            if (CanBarter(client, request.Action == BusinessAction.BarterCycle))
            {
                var tablet = NegociationUIManager.Instance;
                if (request.Action == BusinessAction.Accept) tablet.OnOfferAcceptClick();
                else if (request.Value < 0) tablet.OnLeftArrowButtonClick();
                else tablet.OnRightArrowButtonClick();
            }
            return;
        }
        if (request.Action == BusinessAction.Accept && client.clientIntent is
            StoreClient.ClientIntent.INFORMATION_DEALER or StoreClient.ClientIntent.GUNSMITH)
        {
            if (!TouchesHand(EmporiumEntry.Instance.backInvinvElement.childItems))
                NegociationUIManager.Instance.OnOfferAcceptClick();
            return;
        }
        if (request.Action >= BusinessAction.Discount)
        {
            if (store.isBargainIntroduced && client.isBargainAvailable && !StartingPerk.IsAwfulNegotiator())
                ExecuteBargain(request.Action, request.Value);
            return;
        }

        ExecuteTrade(request, store, client);
    }

    private static void ExecuteBargain(BusinessAction action, int percent)
    {
        var ui = BargainUIManager.Instance;
        switch (action)
        {
            case BusinessAction.Discount when ui.CanOfferDiscount(): ui.OfferDiscount(percent); break;
            case BusinessAction.Markup when ui.CanOfferMarkup(): ui.OfferMarkup(percent); break;
            case BusinessAction.Blackmail when ui.CanBlackmail(): ui.Blackmail(); break;
            case BusinessAction.BuyingMarkup when ui.CanOfferBuyingMarkup(): ui.OfferBuyingMarkup(percent); break;
            case BusinessAction.BuyingDiscount when ui.CanDemandBuyingDiscount(): ui.DemandBuyingDiscount(percent); break;
            case BusinessAction.Threaten when ui.CanThreatenAug(): ui.ThreatenAug(); break;
        }
    }

    private void ExecuteTrade(BusinessIntent request, PlayerStore store, StoreClient client)
    {
        var counter = EmporiumEntry.Instance.backInvinvElement;
        var tablet = NegociationUIManager.Instance;
        GameItem item = null;
        if (request.Item != 0 && (!inventory.TryFindAccessible(request.Item, NetSession.GuestPeer, out item) ||
            item.parentInventory != counter)) return;
        var oldStoreItem = store.currentNegociatedItem;
        var oldTabletItem = tablet.currentNegociatedItem;
        var oldSelection = SelectedOffer(tablet);
        try
        {
            store.currentNegociatedItem = item ?? counter.childItems.LastOrDefault();
            tablet.currentNegociatedItem = item;
            SelectedOffer(tablet) = item;
            bool buying = client.clientIntent is StoreClient.ClientIntent.SELL or StoreClient.ClientIntent.SELLNBUY;
            bool selling = client.clientIntent is StoreClient.ClientIntent.BUY or StoreClient.ClientIntent.SELLNBUY;
            switch (request.Action)
            {
                case BusinessAction.Buy when buying && !client.isRegularSellDisable:
                    var goods = counter.childItems.Where(entry => !GeneralHelper.IsItemOwned(entry)).ToList();
                    if (client.isMultiBuyDisabled && goods.Count > 1)
                        goods = new List<GameItem> { item != null && goods.Contains(item) ? item : goods[0] };
                    if (goods.Count > 0 && goods.Sum(entry => entry.GetNegociatedValue()) <= store.playerCash &&
                        !TouchesHand(goods)) tablet.OnBuyAllClick();
                    break;
                case BusinessAction.Sell when selling && tablet.IsClientHaveEnoughToBuy():
                case BusinessAction.ConfirmBudget when selling && client.useClientBudget && !tablet.IsClientHaveEnoughToBuy():
                    var sale = tablet.GetSellableItem();
                    if (sale.Count == 0 || TouchesHand(sale)) break;
                    if (request.Action == BusinessAction.ConfirmBudget) tablet.SellMultiple(sale, ignoreBudget: true);
                    else tablet.OnSellAllClick();
                    break;
                case BusinessAction.Accept when (selling || buying) && item != null && !TouchesHand(new[] { item }):
                    if (GeneralHelper.IsItemOwned(item) ? selling && store.CanSellItemFromTable() :
                        buying && store.CanBuyItemFromTable()) tablet.OnOfferAcceptClick();
                    break;
                case BusinessAction.Wholesale when client.isOfferWholesale:
                    var wholesale = EmporiumEntry.Instance.GetAllNonOwnedItem();
                    if (wholesale.Count > 0 && store.CanPay(store.GetWholesalePrice()) && !TouchesHand(wholesale))
                        tablet.OnWholesaleClick();
                    break;
            }
        }
        finally
        {
            store.currentNegociatedItem = oldStoreItem?.parentInventory == counter ? oldStoreItem : counter.childItems.LastOrDefault();
            tablet.currentNegociatedItem = oldTabletItem?.parentInventory == counter ? oldTabletItem : null;
            SelectedOffer(tablet) = oldSelection?.parentInventory == counter ? oldSelection : null;
            tablet.RefreshUI();
        }
    }

    private bool TouchesHand(IEnumerable<GameItem> items, GameItem draggedSource = null, byte actor = 0) =>
        Session.Host.HasReservations && items.Any(item =>
            (ReferenceEquals(item, draggedSource) ? Session.Host.IsHeldByOther(actor, EntityKey.Item(item.uniqueId)) :
                Session.Host.IsHeld(EntityKey.Item(item.uniqueId))) ||
            GraphUtils.FindAllChildrenType<GameItem>(item).Any(child => Session.Host.IsHeld(EntityKey.Item(child.uniqueId))));

    internal bool HostCanTrade(string control)
    {
        var client = PlayerStore.Instance.currentClientInstance?.GetClientBlueprint();
        if (client?.clientIntent == StoreClient.ClientIntent.BARTER &&
            control is nameof(NegociationUIManager.OnOfferAcceptClick) or "CycleBarterOfferSet")
            return CanBarter(client, control == "CycleBarterOfferSet");
        if (!Session.Host.HasReservations) return true;
        var back = EmporiumEntry.Instance.backInvinvElement;
        return !TouchesHand(control == nameof(NegociationUIManager.OnWholesaleClick)
            ? EmporiumEntry.Instance.GetAllNonOwnedItem() : back.childItems);
    }

    private bool CanBarter(StoreClient client, bool cycle)
    {
        var store = PlayerStore.Instance;
        var shop = EmporiumEntry.Instance;
        return client.clientIntent == StoreClient.ClientIntent.BARTER && client.isIntroduced &&
            !client.isBusinessComplete && client.barterOffer != null && store.isClientArrived &&
            store.storeState == PlayerStore.StoreState.OPEN && !MapUIManager.Instance.IsOutside &&
            !NegociationUIManager.IsTradeLocked() && DialogUIManager.Instance.currentDialog?.isMainDialog != true &&
            (cycle ? client.barterOffer.OfferSetCount() >= 2 : client.barterOffer.IsBarterAcceptable()) &&
            !TouchesHand(shop.frontInvinvElement.childItems.Concat(shop.backInvinvElement.childItems));
    }

    internal void RefreshCounterForLocalGuest(GameItem item, bool added)
    {
        var store = PlayerStore.Instance;
        if (added)
        {
            store.currentNegociatedItem = item;
            store.HandleAutoNegoUIDisplay(item);
        }
        else if (store.currentNegociatedItem == item)
            store.currentNegociatedItem = EmporiumEntry.Instance.backInvinvElement.childItems.LastOrDefault();
        NegociationUIManager.Instance.UpdateUI(added ? item : null);
    }
}
