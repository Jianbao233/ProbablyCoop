using HarmonyLib;
using ProbablyCoop.Core;
using TMPro;
using UnityEngine;

namespace ProbablyCoop;

internal sealed class NativeGuestTrade
{
    private static NativeGuestTrade current;
    private ShopLink link;
    private StoreShutterButton shownDoor;
    private ulong bellCue, shutterCue;
    private bool cuesInitialized, wasOutside, frontAwaitingClose, closeImpactPending;
    private byte shutter = byte.MaxValue;
    private bool qaShutterMoving;
    private float qaPriorFraction;
    private string spriteKey;
    private uint offer;
    private int funds;
    private bool tabletDrawn;
    private string customer;
    private TabletButton trade;

    internal void Tick(ShopLink connection,ShopView coherentView,NativeShopPresentation presentation)
    {
        current = this;
        link = connection;
        var view = coherentView;
        PlayBell(presentation.Bell,view.GuestOutside);
        PresentShutter(view,presentation);
        cuesInitialized=true;
        wasOutside=view.GuestOutside;

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

    private void PlayBell(ulong sequence, bool outside)
    {
        bool play=cuesInitialized && !outside && !wasOutside && sequence>bellCue;
        bellCue=sequence;
        if(!play) return;
        var source=Object.FindObjectOfType<StoreBell>()?.audioSource;
        if(source==null) return;
        source.Play();
        if(NativeBackgroundQa.Enabled)
            Debug.Log($"[probably_coop QA] guest bell cue={sequence} playing={source.isPlaying} clip={source.clip!=null}");
    }

    private void PresentShutter(ShopView view, NativeShopPresentation presentation)
    {
        var door=StoreShutterButton.Instance;
        if(door==null) return;
        bool changed=shownDoor!=door || shutter!=view.Shutter || wasOutside!=view.GuestOutside;
        bool newCue=shownDoor!=door || shutterCue!=presentation.Shutter || wasOutside!=view.GuestOutside;
        if(newCue)
        {
            bool play=shownDoor==door && !wasOutside && !view.GuestOutside && presentation.Animated;
            if(NativeBackgroundQa.Enabled)
            {
                qaShutterMoving=play;
                qaPriorFraction=NativeShopPresentation.Fraction(door);
            }
            presentation.ApplyShutter(door,play);
            closeImpactPending=play && presentation.ShutterClosed;
            shownDoor=door;
            shutterCue=presentation.Shutter;
        }

        float fraction=NativeShopPresentation.Fraction(door);
        if(qaShutterMoving && fraction>0 && fraction<1 && door.blockerMaskObject.IsActive() &&
            (presentation.ShutterClosed ? fraction<qaPriorFraction : fraction>qaPriorFraction))
        {
            qaShutterMoving=false;
            Debug.Log($"[probably_coop QA] PASS native-shutter-motion direction={(presentation.ShutterClosed ? "closing" : "opening")} visible-mask=True fraction={fraction:0.000}");
        }
        if(!view.GuestOutside)
        {
            var entry=EmporiumEntry.Instance;
            if(view.Shutter==1)
            {
                if(changed) entry.ShowFrontArea();
                frontAwaitingClose=false;
                if(fraction>=1) door.blockerMaskObject.gameObject.SetActive(false);
            }
            else if(changed || frontAwaitingClose)
            {
                frontAwaitingClose=fraction>0;
                if(!frontAwaitingClose) entry.HideFrontArea();
            }
            if(closeImpactPending && fraction<=0.08f)
            {
                door.audioSourceShutterClosed.Play();
                closeImpactPending=false;
            }
        }
        else frontAwaitingClose=false;

        if(changed)
        {
            shutter=view.Shutter;
            PlayerStore.Instance.storeState = shutter switch
            {
                1 => PlayerStore.StoreState.OPEN,
                2 => PlayerStore.StoreState.CLOSED,
                _ => PlayerStore.StoreState.MORNING
            };
        }
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
