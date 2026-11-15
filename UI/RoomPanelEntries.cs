using System;
#if PROBABLY_COOP_GRID_QA
using System.Linq;
#endif
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace ProbablyCoop;

internal sealed class RoomPanelEntries : IDisposable
{
    private const float LauncherSize = 120f;
    private const string HideLauncherKey = "hide_nutrifruit_launcher";
    private const string HideLauncherPreference = "probably_coop." + HideLauncherKey;
    private bool launcherHidden = PlayerPrefs.GetInt(HideLauncherPreference, 0) != 0;
    private GameObject pauseEntry;
    private TextMeshProUGUI pauseLabel;
    private GameObject launcher;

    internal void Tick(bool inMenu, bool menuEntryAvailable, bool panelOpen, Action open)
    {
        var pause = inMenu ? null : StoreUIManager.Instance?.escapeUIManager;
        bool paused = pause?.menuPanel != null && pause.menuPanel.activeInHierarchy;
        if (paused && pauseEntry == null) AddPauseEntry(pause, open);
        if (paused && pauseLabel != null)
        {
            string title = ModHelper.GetLocalized("probably_coop", "pause_entry");
            if (pauseLabel.text != title) pauseLabel.text = title;
        }

        bool showLauncher = !launcherHidden && !menuEntryAvailable && !paused && !panelOpen;
        if (showLauncher && launcher == null) AddLauncher(open);
        if (launcher != null && launcher.activeSelf != showLauncher) launcher.SetActive(showLauncher);
    }

    internal void AddLauncherOption(CustomUIBuilder builder)
    {
        builder.AddToggle(ModHelper.GetLocalized("probably_coop", HideLauncherKey), launcherHidden, hidden =>
        {
            launcherHidden = hidden;
            PlayerPrefs.SetInt(HideLauncherPreference, hidden ? 1 : 0);
            PlayerPrefs.Save();
            if (hidden && launcher != null) launcher.SetActive(false);
        }, HideLauncherKey);
    }

    private void AddPauseEntry(EscapeUIManager pause, Action open)
    {
        foreach (var source in pause.menuPanel.GetComponentsInChildren<Button>(true))
        {
            for (int index = 0; index < source.onClick.GetPersistentEventCount(); index++)
            {
                if (source.onClick.GetPersistentTarget(index) != pause ||
                    source.onClick.GetPersistentMethodName(index) != nameof(EscapeUIManager.OnOption)) continue;
                pauseEntry = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent, false);
                pauseEntry.name = "ProbablyCoopPauseEntry";
                pauseEntry.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
                var button = pauseEntry.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(() => { pause.CloseEscapeMenu(); open(); });
                foreach (var localization in pauseEntry.GetComponentsInChildren<LocalizeStringEvent>(true))
                    localization.enabled = false;
                pauseLabel = pauseEntry.GetComponentInChildren<TextMeshProUGUI>(true);
                if (source.transform.parent.GetComponent<LayoutGroup>() == null)
                {
                    var rect = (RectTransform)pauseEntry.transform;
                    rect.anchoredPosition += Vector2.right * (rect.rect.width + 12);
                }
                pauseEntry.SetActive(true);
                return;
            }
        }
    }

    private void AddLauncher(Action open)
    {
        var root = CustomUIManager.Instance.GetLayerRoot("overlay");
        var sprite = RenderHandler.TryLoadFromAtlas("Items/items_hydroponic_plant", "nutrifruit");
        if (sprite == null) return;
        launcher = new GameObject("ProbablyCoopNutrifruit", typeof(RectTransform), typeof(Image), typeof(Button),
            typeof(RoomLauncherDrag));
        var rect = (RectTransform)launcher.transform;
        rect.SetParent(root, false);
        rect.sizeDelta = Vector2.one * LauncherSize;
        rect.anchoredPosition = new Vector2(root.rect.xMin + LauncherSize / 2 + 24, 250);
        var image = launcher.GetComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        var button = launcher.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(open.Invoke);
    }

#if PROBABLY_COOP_GRID_QA
    internal void QaVerifyLauncherOption(Action open)
    {
        var manager = CustomUIManager.Instance;
        var pause = StoreUIManager.Instance.escapeUIManager;
        const string panelId = "probably_coop_panel";
        bool previousHidden = launcherHidden;
        bool hadPreference = PlayerPrefs.HasKey(HideLauncherPreference);
        try
        {
            launcherHidden = false;
            Tick(false, false, false, open);
            Check(launcher != null && ((RectTransform)launcher.transform).rect.width == 120f,
                "native Nutrifruit is one and a half times the previous width");
            launcher.GetComponent<Button>().onClick.Invoke();
            var toggle = manager.GetWindow(panelId).Get(HideLauncherKey);
            Check(toggle.toggle.isActiveAndEnabled && !toggle.GetBool(), "panel has an active local hide option");
            toggle.SetBool(true, notify: true);
            manager.CloseWindow(panelId);
            Tick(false, false, false, open);
            Check(!launcher.activeSelf && PlayerPrefs.GetInt(HideLauncherPreference) == 1,
                "closing the panel keeps the Nutrifruit hidden and saves the setting");
            pause.OpenEscapeMenu();
            Tick(false, false, false, open);
            pauseEntry.GetComponent<Button>().onClick.Invoke();
            toggle = manager.GetWindow(panelId).Get(HideLauncherKey);
            Check(toggle.GetBool(), "the native pause menu can reopen the hidden launcher option");
            toggle.SetBool(false, notify: true);
            manager.CloseWindow(panelId);
            Tick(false, false, false, open);
            Check(launcher.activeSelf, "clearing the setting restores the Nutrifruit button");
            Debug.Log("[probably_coop QA] PASS traits native launcher size, hide, persist and restore");
        }
        finally
        {
            launcherHidden = previousHidden;
            if (hadPreference) PlayerPrefs.SetInt(HideLauncherPreference, previousHidden ? 1 : 0);
            else PlayerPrefs.DeleteKey(HideLauncherPreference);
            PlayerPrefs.Save();
        }
    }

    internal void QaVerify(Action open)
    {
        var manager = CustomUIManager.Instance;
        var pause = StoreUIManager.Instance.escapeUIManager;
        Tick(false, false, false, open);
        Check(launcher != null && launcher.GetComponent<Image>().sprite != null, "native Nutrifruit sprite");
        var rect = (RectTransform)launcher.transform;
        Check(rect.rect.width == LauncherSize, "enlarged Nutrifruit sprite");
        Canvas.ForceUpdateCanvases();
        var initial = rect.anchoredPosition;
        var press = RectTransformUtility.WorldToScreenPoint(null, rect.position);
        var pointer = new PointerEventData(EventSystem.current)
        {
            button = PointerEventData.InputButton.Left,
            pressPosition = press,
            position = press + new Vector2(24, 24),
            eligibleForClick = true,
        };
        var drag = launcher.GetComponent<RoomLauncherDrag>();
        drag.OnBeginDrag(pointer);
        drag.OnDrag(pointer);
        Check(rect.anchoredPosition != initial, "the first drag step includes movement from the press point");
        pointer.position = new Vector2(Screen.width * 2, Screen.height * 2);
        drag.OnDrag(pointer);
        Rect bounds = ScreenBounds(rect);
        Check(!pointer.eligibleForClick && initial != rect.anchoredPosition && bounds.xMax <= Screen.width + 1 &&
            bounds.yMax <= Screen.height + 1 && !manager.IsOpen("probably_coop_panel"), "drag clamps at screen edges");
        var corner = rect.anchoredPosition;
        drag.UpdatePosition(Time.unscaledTime + 1);
        Check(rect.anchoredPosition.x < corner.x - 10 && rect.anchoredPosition.y < corner.y - 10,
            "the upper right edge pushes the launcher back inside");
        pointer.position = new Vector2(-Screen.width, -Screen.height);
        drag.OnDrag(pointer);
        bounds = ScreenBounds(rect);
        Check(bounds.xMin >= -1 && bounds.yMin >= -1, "drag clamps at the opposite edges");
        corner = rect.anchoredPosition;
        drag.OnEndDrag(pointer);
        drag.UpdatePosition(Time.unscaledTime + 1);
        Check(rect.anchoredPosition.x > corner.x + 10 && rect.anchoredPosition.y > corner.y + 10,
            "the lower left edge finishes rebounding after release");
        rect.anchoredPosition = initial;
        launcher.GetComponent<Button>().onClick.Invoke();
        Check(manager.IsOpen("probably_coop_panel"), "Nutrifruit button opens the main panel");
        manager.CloseWindow("probably_coop_panel");

        pause.OpenEscapeMenu();
        Tick(false, false, false, open);
        Canvas.ForceUpdateCanvases();
        Check(pauseEntry != null && pauseEntry.activeInHierarchy && !launcher.activeSelf, "native pause entry is visible");
        Check(pauseLabel != null && pauseLabel.text == ModHelper.GetLocalized("probably_coop", "pause_entry"),
            "native pause entry has the localized multiplayer label");
        var pauseButton = pauseEntry.GetComponent<Button>();
        bounds = ScreenBounds((RectTransform)pauseEntry.transform);
        Check(bounds.xMin >= -1 && bounds.yMin >= -1 && bounds.xMax <= Screen.width + 1 &&
            bounds.yMax <= Screen.height + 1, "pause entry fits on-screen");
        Check(!pause.menuPanel.GetComponentsInChildren<Button>().Any(other => other != pauseButton &&
            bounds.Overlaps(ScreenBounds((RectTransform)other.transform))), "pause entry does not cover native buttons");
        pauseButton.onClick.Invoke();
        Check(!pause.menuPanel.activeSelf && manager.IsOpen("probably_coop_panel"), "pause button opens the main panel");
        manager.CloseWindow("probably_coop_panel");
        Debug.Log("[probably_coop ENTRY] PASS native pause entry and Nutrifruit edge rebound");
    }

    private static Rect ScreenBounds(RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        var canvas = rect.GetComponentInParent<Canvas>().rootCanvas;
        var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Campaign QA: " + message);
    }
#endif

    public void Dispose()
    {
        if (pauseEntry != null) UnityEngine.Object.Destroy(pauseEntry);
        if (launcher != null) UnityEngine.Object.Destroy(launcher);
        pauseEntry = null;
        pauseLabel = null;
        launcher = null;
    }
}

internal sealed class RoomLauncherDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    private const float ReboundDistance = 26f;
    private const float ReboundDuration = 0.16f;
    private RectTransform rect;
    private Vector2 offset, requested, edge, edgeTouchedAt;
    private bool dragging;

    private void Awake() => rect = (RectTransform)transform;
    private void LateUpdate() => UpdatePosition(Time.unscaledTime);
    private void OnDisable()
    {
        dragging = false;
        edge = Vector2.zero;
    }

    public void OnBeginDrag(PointerEventData pointer)
    {
        if (pointer.button != PointerEventData.InputButton.Left) return;
        if (!PointerLocal(pointer.pressPosition, out var local)) return;
        requested = rect.anchoredPosition;
        offset = requested - local;
        edge = Vector2.zero;
        dragging = true;
        pointer.eligibleForClick = false;
    }

    public void OnDrag(PointerEventData pointer)
    {
        if (!dragging || pointer.button != PointerEventData.InputButton.Left || !PointerLocal(pointer.position, out var local)) return;
        requested = local + offset;
        var limits = CenterLimits();
        var hit = new Vector2(Inward(requested.x, limits.xMin, limits.xMax),
            Inward(requested.y, limits.yMin, limits.yMax));
        float now = Time.unscaledTime;
        if (hit.x != edge.x) edgeTouchedAt.x = now;
        if (hit.y != edge.y) edgeTouchedAt.y = now;
        edge = hit;
        UpdatePosition(now);
    }

    public void OnEndDrag(PointerEventData pointer)
    {
        if (pointer.button == PointerEventData.InputButton.Left) dragging = false;
    }

    internal void UpdatePosition(float now)
    {
        var limits = CenterLimits();
        var position = rect.anchoredPosition;
        var clamped = Clamp(dragging || edge != Vector2.zero ? requested : position, limits);
        if (edge != Vector2.zero)
        {
            float x = Mathf.Clamp01((now - edgeTouchedAt.x) / ReboundDuration);
            float y = Mathf.Clamp01((now - edgeTouchedAt.y) / ReboundDuration);
            clamped = Clamp(clamped + new Vector2(edge.x * Recoil(x), edge.y * Recoil(y)), limits);
            if (!dragging && (edge.x == 0 || x == 1) && (edge.y == 0 || y == 1)) edge = Vector2.zero;
        }
        if (clamped != position) rect.anchoredPosition = clamped;
    }

    private Rect CenterLimits()
    {
        var bounds = ((RectTransform)rect.parent).rect;
        var halfSize = Vector2.Min(rect.rect.size, bounds.size) / 2;
        return Rect.MinMaxRect(bounds.xMin + halfSize.x, bounds.yMin + halfSize.y,
            bounds.xMax - halfSize.x, bounds.yMax - halfSize.y);
    }

    private bool PointerLocal(Vector2 position, out Vector2 local) =>
        RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)rect.parent, position, null, out local);

    private static Vector2 Clamp(Vector2 point, Rect limits) =>
        new(Mathf.Clamp(point.x, limits.xMin, limits.xMax), Mathf.Clamp(point.y, limits.yMin, limits.yMax));

    private static float Inward(float position, float min, float max) =>
        position <= min ? 1f : position >= max ? -1f : 0f;

    private static float Recoil(float progress) => Mathf.SmoothStep(0, ReboundDistance, progress);
}
