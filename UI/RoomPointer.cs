using System;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;

// Unreliable presence in the original game viewport. The UI owns its own
// marker and never forwards or consumes a mouse event.
internal sealed class RoomPointer : IDisposable
{
    private const double Interval = 1d / 15d;
    private readonly NetRoom room;
    private Canvas canvas;
    private RectTransform marker;
    private TextMeshProUGUI label;
    private CursorPresence remote;
    private bool active, sent, lastShown, clicked;
    private uint epoch;
    private double nextSend, receivedAt, clickUntil;

    internal RoomPointer(NetRoom room)
    {
        this.room = room;
        room.Datagram += Receive;
        room.Ended += Reset;
    }

    internal void Tick(bool enabled, uint barrier)
    {
        if (!enabled || epoch != barrier) Clear();
        active = enabled;
        epoch = barrier;
        if (!active) return;

        double now = Time.realtimeSinceStartupAsDouble;
        var viewport = new Rect(LetterboxManager.GameAreaX, LetterboxManager.GameAreaY,
            LetterboxManager.GameAreaWidth, LetterboxManager.GameAreaHeight);
        var mouse = (Vector2)Input.mousePosition;
        bool hasViewport = viewport.width > 0 && viewport.height > 0;
        bool shown = hasViewport && Application.isFocused && viewport.Contains(mouse);
        clicked |= shown && Input.GetMouseButtonDown(0);
        if (!sent || shown != lastShown || shown && now >= nextSend)
        {
            Vector2 point = shown ? Rect.PointToNormalized(viewport, mouse) : Vector2.zero;
            room.SendLoose(CursorPresence.Channel, new CursorPresence(epoch, Axis(point.x), Axis(point.y), shown, clicked).Encode());
            nextSend = now + Interval;
            sent = true;
            lastShown = shown;
            clicked = false;
        }
        if (!hasViewport || !remote.Shown || now - receivedAt >= 1)
        {
            Hide();
            return;
        }

        EnsureMarker();
        var screen = Rect.NormalizedToPoint(viewport, new Vector2(remote.X / 65535f, remote.Y / 65535f));
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)canvas.transform, screen, null, out var pointOnCanvas))
        {
            Hide();
            return;
        }
        marker.localPosition = marker.gameObject.activeSelf
            ? Vector3.Lerp(marker.localPosition, pointOnCanvas, 1 - Mathf.Exp(-20 * Time.unscaledDeltaTime))
            : pointOnCanvas;
        marker.sizeDelta = Vector2.one * (now < clickUntil ? 15 : 9);
        string name = room.PartnerName;
        if (label.text != name)
        {
            var font = ScriptFontSwap.FontForText(name);
            if (font != null) label.font = font;
            label.text = name;
        }
        marker.gameObject.SetActive(true);
    }

    private void Receive(byte channel, byte[] data)
    {
        if (channel != CursorPresence.Channel || !active || !CursorPresence.TryRead(data, out var pointer) ||
            pointer.Epoch != epoch) return;
        remote = pointer;
        receivedAt = Time.realtimeSinceStartupAsDouble;
        if (pointer.Clicked) clickUntil = receivedAt + 0.22;
    }

    private void EnsureMarker()
    {
        if (canvas != null) return;
        var layer = new GameObject("ProbablyCoopRoomPointerLayer", typeof(RectTransform), typeof(Canvas));
        canvas = layer.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;
        var body = new GameObject("PartnerPointer", typeof(RectTransform));
        marker = (RectTransform)body.transform;
        marker.SetParent(layer.transform, false);
        var image = body.AddComponent<Image>();
        image.color = new Color(0.15f, 1f, 0.82f);
        image.raycastTarget = false;
        var name = new GameObject("PlayerName", typeof(RectTransform));
        label = name.AddComponent<TextMeshProUGUI>();
        label.rectTransform.SetParent(marker, false);
        label.rectTransform.pivot = new Vector2(0, 0.5f);
        label.rectTransform.anchoredPosition = new Vector2(12, 0);
        label.rectTransform.sizeDelta = new Vector2(190, 24);
        label.fontSize = 16;
        label.color = image.color;
        label.raycastTarget = false;
        label.richText = false;
        Hide();
    }

    private void Reset(NetClose _) => Clear();

    private void Clear()
    {
        active = sent = clicked = false;
        remote = default;
        clickUntil = 0;
        Hide();
    }

    private void Hide()
    {
        if (marker != null && marker.gameObject.activeSelf) marker.gameObject.SetActive(false);
    }

    private static ushort Axis(float value) => (ushort)Mathf.RoundToInt(Mathf.Clamp01(value) * ushort.MaxValue);

    public void Dispose()
    {
        room.Datagram -= Receive;
        room.Ended -= Reset;
        if (canvas != null) UnityEngine.Object.Destroy(canvas.gameObject);
    }
}
