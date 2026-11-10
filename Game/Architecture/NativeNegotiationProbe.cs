using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;

/// <summary>Native tablet builder parity test. No replacement panels, button actions,
/// transport, or save operations; results are explicitly semantic render evidence only.</summary>
internal static class NativeNegotiationProbe
{
    private static readonly FieldInfo[] ClientFields = typeof(StoreClient).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(f => !f.IsStatic && (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string) || f.FieldType == typeof(List<string>))).ToArray();
    private static StoreClient CopyClient(StoreClient source)
    {
        var target = new StoreClient();
        // Fixed native contract. No CLR type name or delegate originates in the payload.
        foreach (var field in ClientFields)
        {
            var value = field.GetValue(source);
            var data = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            field.SetValue(target, data.Type == JTokenType.Null ? null : data.ToObject(field.FieldType));
        }
        if (source.barterOffer != null)
        {
            target.barterOffer = new BarterOffer { ownerClient = target, barterTextBody = source.barterOffer.barterTextBody,
                isFreeReward = source.barterOffer.isFreeReward, currentSetIndex = source.barterOffer.currentSetIndex,
                acceptedResponse = source.barterOffer.acceptedResponse, deniedResponse = source.barterOffer.deniedResponse };
            foreach (var c in source.barterOffer.conditions)
                target.barterOffer.conditions.Add(new BarterCondition { requiredItemId = c.requiredItemId,
                    requiredItemTag = c.requiredItemTag, requiredDisplay = c.requiredDisplay, offeredDisplay = c.offeredDisplay,
                    blacklistedConditionId = c.blacklistedConditionId?.ToList() });
            NativeQueryProbe.Bind(source.barterOffer, target.barterOffer);
        }
        if (source.gunOrderData != null)
        {
            var g = source.gunOrderData;
            target.gunOrderData = new GunOrderData { tier = g.tier, baseReward = g.baseReward, dialogText = g.dialogText,
                isBestOrder = g.isBestOrder, isServiceOrder = g.isServiceOrder,
                mandatoryRequirements = g.mandatoryRequirements.Select(CopyRequirement).ToList(),
                bonusRequirements = g.bonusRequirements.Select(CopyRequirement).ToList() };
        }
        return target;
    }
    private static GunOrderRequirement CopyRequirement(GunOrderRequirement r) => new GunOrderRequirement {
        id = r.id, value = r.value, reward = r.reward, isMustBeLargerThan = r.isMustBeLargerThan, isCaliber = r.isCaliber
    };
    private sealed class Fixture
    {
        internal string Name;
        internal StoreClient.ClientIntent Mode;
        internal bool Owned = true, Featured;
        internal Action<StoreClient> Configure;
    }
    private static Fixture[] Fixtures()
    {
        var list = Enum.GetValues(typeof(StoreClient.ClientIntent)).Cast<StoreClient.ClientIntent>()
            .Select(mode => new Fixture { Name = mode.ToString(), Mode = mode, Owned = mode != StoreClient.ClientIntent.SELL }).ToList();
        void Add(string name, StoreClient.ClientIntent mode, Action<StoreClient> configure, bool owned = true, bool featured = false)
            => list.Add(new Fixture { Name = name, Mode = mode, Configure = configure, Owned = owned, Featured = featured });
        Add("BUY_LOW_BUDGET", StoreClient.ClientIntent.BUY, c => c.OverrideBudget(1));
        Add("BUY_NO_BUDGET", StoreClient.ClientIntent.BUY, c => c.useClientBudget = false);
        Add("BUY_RESTRICTIONS", StoreClient.ClientIntent.BUY, c => {
            c.isRequireVariety = true; c.noAcceptingContraband = true; c.isSecurity = true;
            c.clientBlackTagList = new List<string> { "FOOD" };
        });
        Add("BUY_FEATURED", StoreClient.ClientIntent.BUY, c => {}, featured: true);
        Add("SELL_WHOLESALE", StoreClient.ClientIntent.SELL, c => { c.isOfferWholesale = true; c.wholesaleDiscount = -15; }, false);
        Add("SELL_DISABLED", StoreClient.ClientIntent.SELL, c => c.isRegularSellDisable = true, false);
        Add("SELLNBUY_SELLING", StoreClient.ClientIntent.SELLNBUY, c => {}, false);
        Add("BUY_COMPLETE", StoreClient.ClientIntent.BUY, c => c.isBusinessComplete = true);
        Add("INFO_UNAFFORDABLE", StoreClient.ClientIntent.INFORMATION_DEALER, c => c.serviceCost = 999999);
        Add("INFO_NOT_INTRODUCED", StoreClient.ClientIntent.INFORMATION_DEALER, c => c.isIntroduced = false);
        Add("BARTER_MULTI", StoreClient.ClientIntent.BARTER, c => {
            c.barterOffer.AddOfferSet(() => {}); c.barterOffer.AddOfferSet(() => {}); c.barterOffer.currentSetIndex = 1;
        });
        Add("BARTER_QUERY_REJECT", StoreClient.ClientIntent.BARTER, c => {
            c.barterOffer.conditions.Clear();
            c.barterOffer.getBarterResult = () => new BarterResult { isAccepted = false, response = "QA authoritative predicate" };
        });
        return list.ToArray();
    }
    internal static int ExpectedCases => Fixtures().Length;

    internal static IEnumerator Run(List<object> results)
    {
        if (!NativeArchitectureQa.Active) throw new InvalidOperationException("QA only");
        var store = PlayerStore.Instance; var entry = EmporiumEntry.Instance; var ui = NegociationUIManager.Instance;
        var originalClient = store.currentClientInstance; var originalItem = store.currentNegociatedItem;
        var originalBack = entry.backInvinvElement; var originalFront = entry.frontInvinvElement;
        var backProperty = AccessTools.Property(typeof(EmporiumEntry), nameof(EmporiumEntry.backInvinvElement));
        var frontProperty = AccessTools.Property(typeof(EmporiumEntry), nameof(EmporiumEntry.frontInvinvElement));
        var previousTimeScale = Time.timeScale;
        Time.timeScale = 0;
        try
        {
            foreach (var fixture in Fixtures())
            {
                var mode = fixture.Mode;
                var source = new StoreClient { identifier = "coop_qa_" + mode, displayName = "QA Native " + mode,
                    clientIntent = mode, isIntroduced = true, useClientBudget = true, isBargainAvailable = true,
                    attractedType = "FOOD", serviceCost = 16, clientBuyingTagList = new List<string> { "FOOD" } };
                source.OverrideBudget(82);
                if (mode == StoreClient.ClientIntent.BARTER)
                {
                    source.barterOffer = new BarterOffer { ownerClient = source, barterTextBody = "QA barter requires meat" };
                    source.barterOffer.conditions.Add(new BarterCondition { requiredItemId = "processed_meat", requiredDisplay = "QA meat", offeredDisplay = "QA juice" });
                }
                if (mode == StoreClient.ClientIntent.GUNSMITH)
                    source.gunOrderData = new GunOrderData { baseReward = 80, mandatoryRequirements = new List<GunOrderRequirement> {
                        new GunOrderRequirement { id = "GUN_CURRENT_FIREPOWER_TAG", value = 20, reward = 5 } } };
                fixture.Configure?.Invoke(source);
                var sourceBack = new GameGridInventory(18, 4); var sourceFront = new GameGridInventory(18, 4);
                backProperty.SetValue(entry, sourceBack); frontProperty.SetValue(entry, sourceFront);
                store.currentClientInstance = StoreClientInstance.CreateClientInstance(source);
                var item = DirectoryMaster.Item("processed_meat");
                GeneralHelper.SetItemOwned(item, fixture.Owned);
                if (fixture.Featured) item.AddItemFeature(ItemFeatureList.Discount(-15));
                if (!sourceBack.UncheckedAccept(item)) throw new InvalidOperationException("QA fixture insertion");
                Select(item);
                ui.OpenUI(); AccessTools.Method(typeof(NegociationUIManager), "RefreshNow").Invoke(ui, null);
                yield return null; yield return null; yield return null;
                var baseline = Capture(ui.uiObject.transform);
                var baselineBounds = NativeOffscreenCapture.Bounds(ui.uiObject.transform as RectTransform);
                NativeArchitectureQa.CaptureFrame(fixture.Name + "-source");
                yield return null; yield return null;
                var data = NativeMirrorProbe.Capture(item).ToString();
                var guestClient = CopyClient(source);
                var guestItem = NativeMirrorProbe.Hydrate(JObject.Parse(data));
                var guestBack = new GameGridInventory(18, 4); var guestFront = new GameGridInventory(18, 4);
                ui.CloseUI();
                backProperty.SetValue(entry, guestBack); frontProperty.SetValue(entry, guestFront);
                store.currentClientInstance = StoreClientInstance.CreateClientInstance(guestClient);
                // Projection attachment must not execute native ownership/price callbacks again.
                ((List<GameItem>)AccessTools.Field(typeof(GameGridInventory), "items").GetValue(guestBack)).Add(guestItem);
                guestItem.parentInventory = guestBack; ((PixelElement)guestItem).SetRenderingParent(guestBack); guestItem.Validate();
                Select(guestItem);
                ui.OpenUI(); AccessTools.Method(typeof(NegociationUIManager), "RefreshNow").Invoke(ui, null);
                yield return null; yield return null; yield return null;
                var replica = Capture(ui.uiObject.transform);
                var replicaBounds = NativeOffscreenCapture.Bounds(ui.uiObject.transform as RectTransform);
                NativeArchitectureQa.CaptureFrame(fixture.Name + "-replica");
                yield return null; yield return null;
                results.Add(new { @case = fixture.Name, mode = mode.ToString(), status = JToken.DeepEquals(baseline, replica) ? "PASS" : "FAIL",
                    evidence = "Original native tablet, active text/images/buttons and rects; data roundtrip in isolated Unity process",
                    baseline, replica, baselineBounds, replicaBounds,
                    limitations = "Fixture for this mode only; no claim of all branches, native click routing or network. GPU comparison is a separate output." });
                ui.CloseUI();
                foreach (var inventory in new[] { sourceBack, sourceFront, guestBack, guestFront })
                {
                    foreach (var child in inventory.childItems) child.Destroy();
                    UnityEngine.Object.Destroy(inventory.handler.gameObject);
                }
            }
        }
        finally
        {
            ui.CloseUI(); backProperty.SetValue(entry, originalBack); frontProperty.SetValue(entry, originalFront);
            store.currentClientInstance = originalClient; store.currentNegociatedItem = originalItem;
            ui.currentNegociatedItem = originalItem; Time.timeScale = previousTimeScale;
        }
    }
    private static void Select(GameItem item)
    {
        var ui = NegociationUIManager.Instance;
        ui.currentNegociatedItem = item; PlayerStore.Instance.currentNegociatedItem = item;
        AccessTools.Field(typeof(NegociationUIManager), "selectedItem").SetValue(ui, item);
        AccessTools.Field(typeof(NegociationUIManager), "builtSignature").SetValue(ui, "");
    }
    internal static JArray Capture(Transform root)
    {
        Canvas.ForceUpdateCanvases();
        var output = new List<JObject>();
        foreach (var node in root.GetComponentsInChildren<RectTransform>(true))
        {
            if (!node.gameObject.activeInHierarchy) continue;
            var text = node.GetComponent<TMP_Text>(); var button = node.GetComponent<Button>(); var image = node.GetComponent<Image>();
            if (text == null && button == null && image == null) continue;
            var row = new JObject { ["name"] = node.name, ["text"] = text?.text, ["font"] = text?.font?.name,
                ["fontSize"] = text == null ? JValue.CreateNull() : new JValue(text.fontSize),
                ["sprite"] = image?.sprite?.name, ["button"] = button != null,
                ["interactable"] = button == null ? JValue.CreateNull() : new JValue(button.IsInteractable()),
                ["w"] = Math.Round(node.rect.width, 3), ["h"] = Math.Round(node.rect.height, 3) };
            var corners = new Vector3[4]; node.GetWorldCorners(corners);
            row["relativeCorners"] = new JArray(corners.Select(v => {
                var p = root.InverseTransformPoint(v);
                return new JArray(Math.Round(p.x, 2), Math.Round(p.y, 2));
            }));
            output.Add(row);
        }
        // Native row caches may reuse sibling order differently after a rebuild; visible
        // semantics and each element's size are compared as a multiset, not instance IDs.
        return new JArray(output.OrderBy(x => x.ToString(), StringComparer.Ordinal));
    }
}
