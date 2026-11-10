using System;
using System.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private StoreClient coupleCustomer;
    private GameItemElement[] coupleItems;
    private bool couplePanelOpened, coupleMovesSeen;

    private void SeedCoupleInputScene()
    {
        var store = PlayerStore.Instance;
        Require(store.currentClientInstance == null, "input fixture starts with a customer already present");
        coupleCustomer = StoreClientList.CreateSpacerClientFood();
        // This test isolates business from introductory conversation. The
        // actual queue, door, bell, approach, quote UI and inventory paths run normally.
        coupleCustomer.mainDialogue = null;
        store.storeClientManager.clientStack.Insert(0, coupleCustomer);
        store.isBargainIntroduced = true;
        coupleItems = EmporiumEntry.Instance.invElement.childItems.OfType<GameItemElement>()
            .Where(item => GeneralHelper.IsItemOwned(item) && item.unitCount == 1 &&
                item.modifiedShape.width <= 2 && item.modifiedShape.height <= 2).Take(3).ToArray();
        Require(coupleItems.Length == 3, "couple fixture lacks three whole items");
        StoreShutterButton.Instance.OpenShutter();
        Record("FIXTURE couple-input-scene", "registered food buyer queued in disposable memory; vanilla door and bell; no save");
    }

    private void TickCoupleInputScene(ReplicaLink link)
    {
        var store = PlayerStore.Instance;
        if (!store.isClientArrived) return;
        Require(ReferenceEquals(store.currentClientInstance?.GetClientBlueprint(), coupleCustomer),
            "guest inventory input changed the host customer");
        var trade = NegociationUIManager.Instance;
        var bargain = BargainUIManager.Instance;
        if (!couplePanelOpened)
        {
            NotebookUIManager.Instance.CloseUI();
            AdvCalendarUIManager.Instance.CloseUI();
            trade.OpenUI();
            bargain.OpenUI();
            couplePanelOpened = true;
            Record("TRACE couple-native-panels", "host original negotiation and bargain UI open while guest works");
        }
        Require(trade.IsOpen && bargain.IsOpen, "guest inventory input closed a host native panel");
        if (!coupleMovesSeen && coupleItems.All(item => item.parentInventory == EmporiumEntry.Instance.backInvinvElement))
        {
            var discount = bargain.uiObject.GetComponentsInChildren<UnityEngine.UI.Button>()
                .FirstOrDefault(button => button.interactable && button.GetComponentInChildren<TMPro.TMP_Text>()?.text == "-10%");
            Require(discount != null, "original host discount button missing");
            int before = coupleCustomer.GetBudget();
            discount.onClick.Invoke();
            Require(coupleCustomer.GetBudget() > before && coupleItems.All(item => item.FindItemFeatureByID("bargainDiscount") != null),
                "host original discount did not affect the current customer and moved items");
            coupleMovesSeen = true;
            Record("PASS couple-concurrent-panels", "all three exact guest entities reached host back counter; host clicked original -10% bargain button, budget and moved-item discounts updated");
            NativeOffscreenCapture.Capture("qa-couple-negotiation");
        }
        TickHostInputConflict(link);
    }
}
