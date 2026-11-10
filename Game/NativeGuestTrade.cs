using HarmonyLib;
using ProbablyCoop.Core;
using TMPro;
using UnityEngine;

namespace ProbablyCoop;

internal sealed class NativeGuestTrade
{
    private static NativeGuestTrade current;
    private ShopLink link;
    private static readonly AccessTools.FieldRef<StoreShutterButton,bool> DoorClosed =
        AccessTools.FieldRefAccess<StoreShutterButton,bool>("isClosed");
    private StoreShutterButton shownDoor;
    private bool observeShutterMotion;
    private byte shutter = byte.MaxValue;
    private string spriteKey;
    private uint offer;
    private int funds;
    private bool tabletDrawn;
    private string customer;
    private TabletButton trade;

    internal void Tick(ShopLink connection,ShopView coherentView)
    {
        current = this;
        link = connection;
        var view = coherentView;
        var entry = EmporiumEntry.Instance;
        var door = StoreShutterButton.Instance;
        if (door != null && (shownDoor != door || shutter != view.Shutter))
        {
            bool open = view.Shutter == 1;
            bool initialize = shownDoor != door;
            shownDoor = door;
            shutter = view.Shutter;
            if (initialize || view.GuestOutside)
            {
                DoorClosed(door) = !open;
                door.shutterGameObject.transform.position = open ? door.endPosition : door.startPosition;
                door.blockerGameObject.transform.localPosition = open ? door.blockerEndPosition : door.blockerStartPosition;
                door.blockerMaskObject.gameObject.SetActive(!open);
            }
            else if (DoorClosed(door) == open)
            {
                door.ToggleShutterDoor(); // Native visual and audio only. No open/close gameplay callback.
                observeShutterMotion=NativeBackgroundQa.Enabled;
            }
            if (open) entry.ShowFrontArea();
            else entry.HideFrontArea();
            PlayerStore.Instance.storeState = shutter switch
            {
                1 => PlayerStore.StoreState.OPEN,
                2 => PlayerStore.StoreState.CLOSED,
                _ => PlayerStore.StoreState.MORNING
            };
        }

        if(observeShutterMotion && door!=null)
        {
            var y=door.shutterGameObject.transform.position.y;
            if(y>door.startPosition.y && y<door.endPosition.y)
            {
                Debug.Log("[probably_coop QA] PASS native-shutter-intermediate guest displayed a native in-motion frame");
                observeShutterMotion=false;
            }
        }

        var actor = StoreClientMono.Instance;
        string sprite = view.GuestOutside ? "" : view.CustomerSprite;
        if (NativeWorld.Guest?.Ready != true && actor != null && spriteKey != sprite)
        {
            spriteKey = sprite;
            if (sprite.Length > 0 && SpriteDict.Instance != null &&
                (SpriteDict.Instance.spriteDictionary.TryGetValue(sprite, out var portrait) ||
                 SpriteDict.Instance.spriteDictionary.TryGetValue("default", out portrait)))
            {
                actor.image.sprite = portrait;
                actor.SetImmediatlyArrive();
            }
            else StoreUIManager.Instance.HideClient();
        }

        var tablet = NegociationUIManager.Instance;
        if (NativeWorld.Guest?.Ready == true) return;
        if (tablet?.IsOpen != true) return;
        if (view.GuestOutside || view.Customer.Length == 0)
        {
            tablet.CloseUI();
            return;
        }
        if (!tabletDrawn || customer != view.Customer || offer != view.OfferId || funds != view.Cash)
            DrawTablet();
        if (trade != null && trade.Interactable == link.Waiting)
            trade.SetInteractable(!link.Waiting);
    }

    internal static void Toggle()
    {
        var replica = current;
        var tablet = NegociationUIManager.Instance;
        if (replica?.link.View.Customer.Length > 0 && tablet?.uiObject != null)
        {
            if (tablet.IsOpen) tablet.CloseUI();
            else replica.Open();
        }
    }

    internal static void Select(uint id)
    {
        var replica = current;
        if (replica?.link.View.Customer.Length > 0 && !replica.link.Waiting)
        {
            replica.link.Submit(ShopAction.SelectOffer, id, Time.realtimeSinceStartupAsDouble);
            replica.Open();
        }
    }

    private void Open()
    {
        NegociationUIManager.Instance.OpenUI();
        if (NativeWorld.Guest?.Ready != true) DrawTablet();
    }

    private void DrawTablet()
    {
        var frame = NegociationUIManager.Instance.uiObject.GetComponent<NegotiationTabletFrame>();
        if (frame == null) return;
        var view = link.View;
        customer = view.Customer;
        offer = view.OfferId;
        funds = view.Cash;
        tabletDrawn = true;
        frame.clientName.richText = false;
        frame.clientName.text = customer;
        frame.clientSubtitle.gameObject.SetActive(false);
        frame.bargainButton.gameObject.SetActive(false);
        frame.sectionHeader.text = ModHelper.GetLocalized("probably_coop", "trade_current_offer");
        TabletUI.ClearChildren(frame.content);
        TabletUI.ClearChildren(frame.footer);
        TabletUI.ClearChildren(frame.buttonHost);
        trade = null;
        if (offer != 0)
        {
            var row = TabletUI.RowRoot(frame.content, "CoopOffer", 32f);
            var item = TabletUI.NewText(frame, row, "Item", 12f, 0f, 350f, 28f,
                FontLoader.FontRole.Noto_Sans, 18f, TabletStyle.INK, TextAlignmentOptions.MidlineLeft);
            item.richText = false;
            item.text = view.OfferLabel;
            var price = TabletUI.NewText(frame, row, "Price", 367f, 0f, 128f, 28f,
                FontLoader.FontRole.Noto_Sans, 20f, TabletStyle.INK, TextAlignmentOptions.MidlineRight);
            price.text = TabletUI.Credits(view.Price);
            TabletUI.AutoSize(item);
            TabletUI.FooterLine(frame, frame.footer, 0f, LocHelper.GetLocalizedUI("ui_nego_your_funds"),
                TabletUI.Credits(view.Cash));
            var action = view.OfferAction;
            uint quoteId = view.OfferId;
            trade = TabletUI.BigButton(frame, frame.buttonHost, 153f, 0f,
                ModHelper.GetLocalized("probably_coop", action == ShopAction.Buy ? "buy" : "sell"), () =>
                {
                    if (link.View.OfferId == quoteId)
                        link.Submit(action, quoteId, Time.realtimeSinceStartupAsDouble);
                });
            trade.SetInteractable(!link.Waiting);
        }
        TabletUI.LayoutSection(frame, 0f, withSeparator: true, 500f);
    }

    internal static void Reset() => current = null;

    [HarmonyPatch(typeof(NegociationUIManager), "RefreshNow")]
    private static class KeepAuthoritativeQuote
    {
        private static bool Prefix() => !NativeGuest.Active || NativeWorld.Guest?.Ready == true;
    }
}
