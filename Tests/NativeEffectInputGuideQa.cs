#if PROBABLY_COOP_GRID_QA
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProbablyCoop;

// F9 describes hit targets in the isolated client. Input still enters from
// the Windows player window; this guide never activates a game control.
internal static class NativeEffectInputGuideQa
{
    internal const string FileName = "qa-windows-input-guide.json";
    private static readonly bool Requested =
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-grid-native-input-guide") >= 0;
    internal static bool Enabled => Requested && (NativeBackgroundQa.IsHostProduct || NativeBackgroundQa.IsGuestProduct);
    private static int serial, presses, releases;

    internal static void Tick()
    {
        if (!Enabled) return;
        if (Input.GetMouseButtonDown(0)) presses++;
        if (Input.GetMouseButtonUp(0)) releases++;
        if (!Input.GetKeyDown(KeyCode.F9) || EventSystem.current == null) return;
        Canvas.ForceUpdateCanvases();
        var buttons = new JArray();
        foreach (var button in Resources.FindObjectsOfTypeAll<Button>())
        {
            if (!button.gameObject.scene.IsValid() || !button.gameObject.activeInHierarchy || !button.IsInteractable()) continue;
            var point = ClickablePoint((RectTransform)button.transform,
                hit => ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit) == button.gameObject);
            if (point == null) continue;
            point["name"] = button.name;
            point["text"] = string.Join(" ", button.GetComponentsInChildren<TMP_Text>()
                .Where(label => label.isActiveAndEnabled).Select(label => label.text));
            point["action"] = string.Join("|", Enumerable.Range(0, button.onClick.GetPersistentEventCount())
                .Select(index => button.onClick.GetPersistentMethodName(index)));
            point["mod"] = button.GetComponentInParent<ModElement>()?.ModID ?? "";
            buttons.Add(point);
        }
        var mod = ModLoader.Mods.Find(entry => entry.Manifest.ID == "item_details");
        var data = new JObject
        {
            ["serial"] = ++serial, ["width"] = Screen.width, ["height"] = Screen.height,
            ["focus"] = Application.isFocused, ["presses"] = presses, ["releases"] = releases,
            ["mouseX"] = Input.mousePosition.x, ["mouseY"] = Screen.height - Input.mousePosition.y,
            ["buttons"] = buttons, ["itemDetailsEnabled"] = ModLoader.IsModEnabled("item_details"),
            ["itemDetailsRunning"] = mod?.Instance != null && mod.IsEnabled,
            ["shop"] = NativeInventoryPilot.Current?.DescribeWindowsInputQa(),
        };
        ModHelper.WriteModDataFileText("probably_coop", FileName, data.ToString(Newtonsoft.Json.Formatting.None));
        Debug.Log($"[probably_coop WINDOWS QA] guide={serial} presses={presses} releases={releases}");
    }

    internal static JObject ClickablePoint(RectTransform rect, Func<GameObject, bool> accepts) =>
        ClickablePoints(rect, accepts).FirstOrDefault();

    internal static IEnumerable<JObject> ClickablePoints(RectTransform rect, Func<GameObject, bool> accepts)
    {
        if (rect == null || !rect.gameObject.activeInHierarchy) yield break;
        var canvas = rect.GetComponentInParent<Canvas>();
        if (canvas == null) yield break;
        Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var hits = new List<RaycastResult>();
        foreach (float y in new[] { 0.5f, 0.25f, 0.75f })
            foreach (float x in new[] { 0.5f, 0.25f, 0.75f, 0.1f, 0.9f })
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(
                    Rect.NormalizedToPoint(rect.rect, new Vector2(x, y))));
                if (point.x < 0 || point.y < 0 || point.x >= Screen.width || point.y >= Screen.height) continue;
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
                if (hits.Count != 0 && accepts(hits[0].gameObject))
                    yield return new JObject { ["x"] = point.x, ["y"] = Screen.height - point.y };
            }
    }
}

internal sealed partial class NativeInventoryPilot
{
    private GameItemElement inputJunk, inputMagnifier, inputTarget;
    private GameItemElement inputWater, inputBottle;
    private int inputJunkCycles, inputToolStage;
    private int inputWaterVolume;
    private bool inputSawPickup, inputSawTarget;
    private GameInventory inputSourceGrid;
    private Vector2? inputCarryOrigin;
    private bool inputCarryDone;
    private List<TreeNodeRender> inputTargetHighlights;
    private Sprite inputTargetHighlightSprite;
    private bool inputClearedTarget;

    private static void SeedWindowsInputStock(EmporiumEntry shop)
    {
        foreach (string catalog in new[] { "junk", "magnifier", "food_stamp", "small_bottled_water", "bottled_water" })
        {
            var item = DirectoryMaster.Item(catalog);
            item.EnableTag("QA_INPUT_STOCK");
            GeneralHelper.SetItemOwned(item, true);
            if (item.IsID("small_bottled_water")) WaterHelper.FillWithBaseWater(item);
            CampaignCheck(GraphUtils.TryAcceptAll(shop.invElement, item) == 0, "place a visible QA " + catalog);
        }
    }

    private void PrepareInputFixtures(EmporiumEntry shop)
    {
        if (inputJunk != null) return;
        var stock = shop.invElement.childItems.OfType<GameItemElement>().Where(item => item.IsTag("QA_INPUT_STOCK")).ToArray();
        inputJunk = stock.First(item => item.IsID("junk"));
        inputMagnifier = stock.First(item => item.IsID("magnifier"));
        inputTarget = stock.First(item => item.IsID("food_stamp"));
        inputWater = stock.First(item => item.IsID("small_bottled_water"));
        inputBottle = stock.First(item => item.IsID("bottled_water"));
        inputWaterVolume = WaterHelper.GetTotalCapacity(inputWater);
        CampaignCheck(inputMagnifier.CanTarget(inputTarget), "the original magnifier can inspect the test stamp");
    }

    private bool VerifyWindowsItemInput(EmporiumEntry shop)
    {
        PrepareInputFixtures(shop);
        var drag = ItemMouseDragHandler.current;
        var source = inputJunkCycles < 20 ? inputJunk : inputToolStage < 2 ? inputMagnifier : inputWater;
        if (drag.IsDraggingItem && drag.GetCurrentItem() == source)
        {
            if (!inputSawPickup)
            {
                inputSawPickup = true;
                inputSourceGrid = source.parentInventory;
            }
            if (source == inputMagnifier && inputToolStage == 0)
            {
                var manager = HarmonyLib.AccessTools.Field(typeof(ItemMouseDragHandler), "highlightManager").GetValue(drag);
                var nodes = (List<TreeNodeRender>)HarmonyLib.AccessTools.Field(typeof(HighlightManager), "highlightedThisNodes").GetValue(manager);
                if (HarmonyLib.AccessTools.Field(typeof(ItemMouseDragHandler), "lastItem").GetValue(drag) == inputTarget &&
                    nodes.Count > 0 && nodes.Any(node => node.image.enabled))
                {
                    inputSawTarget = true;
                    inputTargetHighlights = nodes.ToList();
                    inputTargetHighlightSprite = nodes[0].image.sprite;
                }
                else if (inputSawTarget)
                {
                    inputClearedTarget = inputTargetHighlights.All(node =>
                        !node.image.enabled || node.image.sprite != inputTargetHighlightSprite);
                }
            }
            return false;
        }
        if (!inputSawPickup) return false;
        if (source == inputWater && !WindowsWaterPourCompleted(shop)) return false;
        inputSawPickup = false;
        CampaignCheck(!source.fakeBackground.image.enabled && !HoldsLocalItem(source) &&
            source.parentInventory == inputSourceGrid, "window mouse release kept the original item and retired the preview");
        if (source == inputJunk)
        {
            inputJunkCycles++;
            return false;
        }
        if (inputToolStage == 0)
        {
            CampaignCheck(inputSawTarget && inputClearedTarget && inputTargetHighlights.All(node => !node.image.enabled),
                "dragging off an actual tool target clears its previous highlighted cells");
            inputToolStage = 1;
            return false;
        }
        if (inputToolStage == 1)
        {
            var inspect = InspectionUIManager.Instance;
            CampaignCheck(inspect.currentInspectingItem == inputTarget,
                "the rapid mouse move and release used the magnifier on the actual target");
            inspect.CloseUI();
            inputToolStage = 2;
            ShopUiResult("Windows mouse completed 20 junk lifts and releases, removed the old target highlight, and used the magnifier");
            return false;
        }
        inputToolStage = 3;
        ShopUiResult("Windows mouse quickly poured into the other bottle while both remained in stock");
        return true;
    }

    private bool WindowsWaterPourCompleted(EmporiumEntry shop)
    {
        PrepareInputFixtures(shop);
        return WaterHelper.GetTotalVolume(inputWater) == 0 && WaterHelper.GetTotalVolume(inputBottle) == inputWaterVolume;
    }

    private bool VerifyWindowsCarryInput(EmporiumEntry shop)
    {
        var window = shop.afterhourWindow;
        var position = window.rectTransform.anchoredPosition;
        inputCarryOrigin ??= position;
        if ((position - inputCarryOrigin.Value).sqrMagnitude <= 4) return false;
        if (Input.GetMouseButton(0)) return false;
        shop.Validate();
        CampaignCheck((position - window.rectTransform.anchoredPosition).sqrMagnitude < 2,
            "native layout preserves the Windows mouse placement of the personal carry window");
        inputCarryDone = true;
        ShopUiResult("Windows mouse moved the original carry header and its position survived native layout");
        return true;
    }

    internal JObject DescribeWindowsInputQa()
    {
        if (!ShopUiQa) return null;
        var data = new JObject
        {
            ["shopStep"] = shopUiStep, ["travelStep"] = shopUiTravelStep, ["traded"] = shopUiTradingDone,
            ["junkCycles"] = inputJunkCycles, ["toolStage"] = inputToolStage, ["carryDone"] = inputCarryDone,
        };
        if (!Ready) return data;
        data["mouseHeld"] = Input.GetMouseButton(0);
        data["hoveringUi"] = StoreUIManager.Instance.hoveringStoreUI;
        data["nativeHeld"] = ItemMouseDragHandler.current.IsDraggingItem;
        data["nativeSelected"] = ItemMouseDragHandler.current.GetCurrentItem()?.identifier ?? "";
        data["mouseItem"] = RenderHandler.RaycastElement<GameItem>(Input.mousePosition)?.identifier ?? "";
        if (windowsTravelButton != null)
            data["travel"] = NativeEffectInputGuideQa.ClickablePoint((RectTransform)windowsTravelButton.transform,
                hit => ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit) == windowsTravelButton.gameObject);
        if (inputJunk != null && shopUiStep == 0 && !localHost && inputToolStage < 3)
        {
            var source = inputJunkCycles < 20 ? inputJunk : inputToolStage < 2 ? inputMagnifier : inputWater;
            data["source"] = ItemPoint(source);
            data["target"] = ItemPoint(inputToolStage < 2 ? inputTarget : inputBottle);
            data["empty"] = new JObject { ["x"] = Screen.width * 0.48f, ["y"] = Screen.height * 0.40f };
        }
        var shop = EmporiumEntry.Instance;
        if (inputCarryOrigin.HasValue && !inputCarryDone && shop?.afterhourWindow.IsVisible() == true)
        {
            var header = (TreeNodeRender)HarmonyLib.AccessTools.Field(typeof(PixelWindow), "machineryHeader").GetValue(shop.afterhourWindow);
            bool HitsHeader(GameObject hit) => hit != null &&
                ExecuteEvents.GetEventHandler<IBeginDragHandler>(hit) == header.gameObject;
            data["carry"] = new JArray(NativeEffectInputGuideQa.ClickablePoints(header.rectTransform, HitsHeader));
            var input = EventSystem.current.currentInputModule;
            var getPointer = HarmonyLib.AccessTools.Method(typeof(PointerInputModule), "GetLastPointerEventData");
            if (input is PointerInputModule && getPointer?.Invoke(input, new object[] { -1 }) is PointerEventData pointer)
            {
                data["headerHit"] = HitsHeader(pointer.pointerCurrentRaycast.gameObject);
                data["eventX"] = pointer.position.x;
                data["eventY"] = Screen.height - pointer.position.y;
                data["over"] = DescribeInputNode(pointer.pointerCurrentRaycast.gameObject);
                var hits = new List<RaycastResult>();
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = Input.mousePosition }, hits);
                data["currentHit"] = DescribeInputNode(hits.FirstOrDefault().gameObject);
                data["pressRay"] = DescribeInputNode(pointer.pointerPressRaycast.gameObject);
                data["pressed"] = DescribeInputNode(pointer.pointerPress);
                data["dragTarget"] = DescribeInputNode(pointer.pointerDrag);
                data["headerPress"] = pointer.pointerPress == header.gameObject;
                data["headerDrag"] = pointer.pointerDrag == header.gameObject;
                data["uiDragging"] = pointer.dragging;
                data["headerSelected"] = EventSystem.current.currentSelectedGameObject == header.gameObject;
                data["carryPosition"] = shop.afterhourWindow.rectTransform.anchoredPosition.ToString();
            }
        }
        return data;
    }

    private static JObject ItemPoint(GameItemElement item) =>
        NativeEffectInputGuideQa.ClickablePoint(item.background.rectTransform, hit =>
            hit.GetComponent<TreeNodeRender>()?.FindParentElement() == item);

    private static string DescribeInputNode(GameObject node)
    {
        var path = new List<string>();
        for (var part = node?.transform; part != null && path.Count < 7; part = part.parent) path.Add(part.name);
        path.Reverse();
        return string.Join("/", path);
    }
}
#endif
