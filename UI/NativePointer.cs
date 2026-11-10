using System;
using System.Collections.Generic;
using ProbablyCoop.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

namespace ProbablyCoop;

internal sealed class NativePointer : IDisposable
{
    internal static NativePointer Current { get; private set; }
    internal PointerContext QaLastEntityContext { get; private set; }
    internal uint QaLastClickedEntity { get; private set; }
    internal string QaRaycastDetails()
    {
        var rows=new List<string>();
        foreach(var hit in raycasts)
        {
            var render=hit.gameObject.GetComponentInParent<TreeNodeRender>();
            rows.Add(hit.gameObject.name+":"+render?.FindParentElement()?.GetType().Name+":"+render?.window?.GetType().Name);
            if(rows.Count==8) break;
        }
        return string.Join("|",rows);
    }
    private const ushort Viewport = 32;
    private const ushort InventoryBase = 40;
    private const ushort Negotiation = 50;
    private const ushort NightBill = 51;
    private const ushort MorningReport = 52;
    private const ushort Hidden = 0;
    private readonly ICoopRoom room;
    private Canvas overlay;
    private RectTransform marker;
    private TextMeshProUGUI label;
    private Locale labelLocale;
    private FontLoader labelFonts;
    private ushort remoteSurface, remoteLocation;
    private uint remoteEntity;
    private readonly List<RaycastResult> raycasts=new List<RaycastResult>();
    private PointerEventData raycastEvent;
    private EventSystem raycastSystem;
    private Vector2 remotePosition;
    private Vector2? remoteViewport, qaClickedViewport;
    private double remoteAt, clickUntil;
    private bool clicked,snapToSurface;
    private readonly CursorCadence cadence=new CursorCadence();
    private ReplicaLink.Stamp cursorStamp,remoteStamp;

    internal NativePointer(ICoopRoom room)
    {
        this.room = room;
        Current=this;
        room.RemoteCursor += Receive;
    }

    internal void Tick(CoopSession session, bool enabled)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (!enabled || session?.State != LinkState.Connected)
        {
            Hide();
            remoteSurface = Hidden;
            clicked = false;
            cadence.Reset();
            return;
        }

        if(!cursorStamp.IsCurrent(session.Native))
        {
            cursorStamp=session.Native.CaptureStamp();
            cadence.Reset(); clicked=false; snapToSurface=true; clickUntil=0;
            if(!remoteStamp.IsCurrent(session.Native)) remoteSurface=Hidden;
        }
        ushort location = CurrentLocation();
        var game = new Rect(LetterboxManager.GameAreaX, LetterboxManager.GameAreaY,
            LetterboxManager.GameAreaWidth, LetterboxManager.GameAreaHeight);
        var mouse = (Vector2)Input.mousePosition;
        bool visible = Application.isFocused && game.width > 0 && game.height > 0 && game.Contains(mouse);
        clicked |= visible && Input.GetMouseButtonDown(0);
        if (cadence.ShouldSample(now,visible,clicked))
        {
            ushort x=0,y=0;
            uint? viewport = null;
            ulong context=new PointerContext(location,Viewport).Encode();
            if (visible)
            {
                var position = Rect.PointToNormalized(game, mouse);
                ushort surface = FindSurface(mouse, out var relative,out uint entity);
                if (surface != Viewport)
                {
                    viewport = (uint)Axis(position.x) | ((uint)Axis(position.y) << 16);
                    position = relative;
                }
                x=Axis(position.x); y=Axis(position.y);
                context=new PointerContext(location,surface,entity).Encode();
            }
            SendSample(session,now,visible,clicked,context,x,y,viewport);
            clicked = false;
        }

        if (remoteSurface == Hidden || now - remoteAt >= 1)
        {
            Hide();
            return;
        }

        Vector2 screen;
        string name = room.PartnerName;
        if(NativeWorld.Host?.PartnerHasHand==true || NativeWorld.Guest?.RemoteHostDragging==true) name+=" · "+Loc("cursor_carrying");
        if (remoteLocation != location)
        {
            screen = new Vector2(game.xMin + 24, game.yMax - 42);
            name += " · " + LocalLocation(remoteLocation);
        }
        else if (remoteSurface == Viewport)
            screen = Rect.NormalizedToPoint(game, remotePosition);
        else
        {
            var surface = remoteSurface==PointerContext.NativeElement ? ResolveElement(remoteEntity) : Surface(remoteSurface);
            if (surface == null || !surface.gameObject.activeInHierarchy)
            {
                screen = remoteViewport.HasValue
                    ? Rect.NormalizedToPoint(game, remoteViewport.Value)
                    : new Vector2(game.xMin + 24, game.yMax - 42);
                name += " · " + (remoteSurface==PointerContext.NativeElement ? ElementActivity(remoteEntity) : LocalSurface(remoteSurface));
            }
            else
            {
                var bounds=remoteSurface==PointerContext.NativeElement ? ElementBounds(World.PointerElement(remoteEntity)) : surface.rect;
                var position = Rect.NormalizedToPoint(bounds, remotePosition);
                screen = RectTransformUtility.WorldToScreenPoint(CameraFor(surface), surface.TransformPoint(position));
            }
        }

        EnsureMarker();
        var root = (RectTransform)overlay.transform;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen,
                CameraFor(root), out var local))
        {
            Hide();
            return;
        }
        marker.localPosition = marker.gameObject.activeSelf && !snapToSurface
            ? Vector3.Lerp(marker.localPosition, local, 1 - Mathf.Exp(-20 * Time.unscaledDeltaTime))
            : local;
        snapToSurface=false;
        marker.sizeDelta = Vector2.one * (now < clickUntil ? 16 : 10);
        UpdateLabel(name);
        marker.gameObject.SetActive(true);
    }

    private void UpdateLabel(string name)
    {
        var locale=LocalizationSettings.SelectedLocale;
        var fonts=FontLoader.Instance;
        if(label.text==name && labelLocale==locale && labelFonts==fonts) return;
        // Partner names may use a different script from the local game UI.
        // Use the same script-aware font selector as native player-facing text.
        var font=ScriptFontSwap.FontForText(name);
        if(font==null && fonts!=null && locale!=null)
            font=fonts.GetFontByRole(FontLoader.FontRole.Noto_Sans,locale);
        if(font!=null && label.font!=font) label.font=font;
        label.text=name;
        labelLocale=locale;
        labelFonts=fonts;
    }

    internal bool QaHasViewportFallback(ushort x,ushort y) => qaClickedViewport.HasValue &&
        Axis(qaClickedViewport.Value.x)==x && Axis(qaClickedViewport.Value.y)==y;

    internal bool QaRenderedLocation(ushort location)
    {
        if(label==null || !label.isActiveAndEnabled) return false;
        string suffix=LocalLocation(location);
        if(!label.text.EndsWith(" · "+suffix,StringComparison.Ordinal)) return false;
        label.ForceMeshUpdate();
        int start=label.text.Length-suffix.Length;
        for(int i=0;i<suffix.Length;i++)
        {
            if(char.IsWhiteSpace(suffix[i])) continue;
            if(start+i>=label.textInfo.characterCount) return false;
            var glyph=label.textInfo.characterInfo[start+i];
            if(glyph.character!=suffix[i] || !glyph.isVisible) return false;
        }
        return true;
    }

    internal bool SendSample(CoopSession session,double now,bool visible,bool click,ulong context,ushort x,ushort y,uint? viewport=null)
    {
        if(!cadence.Observe(now,visible,click,context,x,y,viewport)) return false;
        session.MoveCursor(x,y,visible,click,context,viewport);
        return true;
    }

    internal static GameGridInventory InventoryUnderMouse()
    {
        if (RenderHandler.current == null) return null;
        return RenderHandler.RaycastElement<GameItem>(Input.mousePosition)?.parentInventory as GameGridInventory ??
            RenderHandler.RaycastElement<GameGridInventory>(Input.mousePosition);
    }

    internal static byte ContainerId(GameGridInventory grid)
    {
        var entry = EmporiumEntry.Instance;
        if (grid == null || entry == null) return 0;
        if (ReferenceEquals(grid, entry.invElement)) return 1;
        if (ReferenceEquals(grid, entry.backInvinvElement)) return 2;
        if (ReferenceEquals(grid, entry.afterhourInventory)) return 3;
        if (ReferenceEquals(grid, entry.frontInvinvElement)) return 6;
        return ReferenceEquals(grid, entry.showcaseElement) ? (byte)7 : (byte)0;
    }

    internal ushort FindSurface(Vector2 mouse, out Vector2 position,out uint entity)
    {
        entity=0;
        position=Vector2.zero;
        if(EmporiumEntry.Instance==null) return Viewport;
        // Use the actual frontmost raycast result, not a rectangle underneath
        // an independently positioned machine window. No OS mouse is changed.
        var events=EventSystem.current;
        if(events!=null)
        {
            if(raycastSystem!=events) { raycastSystem=events; raycastEvent=new PointerEventData(events); }
            raycastEvent.position=mouse; raycasts.Clear(); events.RaycastAll(raycastEvent,raycasts);
            var hitObject=raycasts.Count>0 ? raycasts[0].gameObject : null;
            var hit=hitObject!=null ? hitObject.GetComponentInParent<TreeNodeRender>() : null;
            for(var node=hit;node!=null;node=node.parent)
            {
                var element=node.element;
                uint id=World?.PointerIdentity(element) ?? 0;
                if(id!=0 && TryElement(element,mouse,out position))
                { entity=id; return PointerContext.NativeElement; }
                var window=node.window;
                id=World?.PointerIdentity(window) ?? 0;
                if(id!=0 && TryElement(window,mouse,out position))
                { entity=id; return PointerContext.NativeElement; }
            }
        }
        // A visible tablet takes precedence over the store and the grids underneath it.
        if (TrySurface(Negotiation, mouse, out position)) return Negotiation;
        if (TrySurface(NightBill, mouse, out position)) return NightBill;
        if (TrySurface(MorningReport, mouse, out position)) return MorningReport;
        byte container = ContainerId(InventoryUnderMouse());
        ushort surface = (ushort)(InventoryBase + container);
        return container != 0 && TrySurface(surface, mouse, out position) ? surface : Viewport;
    }

    private static RectTransform Surface(ushort surface)
    {
        var entry = EmporiumEntry.Instance;
        if(entry==null) return null;
        switch(surface)
        {
            case Negotiation:
                var negotiation=NegociationUIManager.Instance;
                var tablet=negotiation!=null && negotiation.uiObject!=null
                    ? negotiation.uiObject.GetComponent<NegotiationTabletFrame>() : null;
                return tablet!=null ? tablet.panel : null;
            case NightBill:
                var end=EndOfDayUIManager.Instance;
                var bill=end!=null && end.UI!=null ? end.UI.GetComponent<EndOfDayTabletFrame>() : null;
                return bill!=null ? bill.panel : null;
            case MorningReport:
                var start=StartOfDayUIManager.Instance;
                return start!=null && start.UIPanel!=null ? start.UIPanel.transform as RectTransform : null;
        }
        var grid=surface switch
        {
            InventoryBase + 1 => entry.invElement,
            InventoryBase + 2 => entry.backInvinvElement,
            InventoryBase + 3 => entry.afterhourInventory,
            InventoryBase + 6 => entry.frontInvinvElement,
            InventoryBase + 7 => entry.showcaseElement,
            _ => null
        };
        return grid?.inner!=null ? grid.inner.rectTransform : null;
    }

    private static bool TrySurface(ushort id, Vector2 mouse, out Vector2 position)
    {
        return TryRect(Surface(id),mouse,out position);
    }
    private static bool TryRect(RectTransform surface,Vector2 mouse,out Vector2 position)
        =>TryRect(surface,surface!=null ? surface.rect : default,mouse,out position);
    private static bool TryElement(PixelElement element,Vector2 mouse,out Vector2 position)
        =>TryRect(element.rectTransform,ElementBounds(element),mouse,out position);
    internal static Rect ElementBounds(PixelElement element)=>new Rect(0,-element.heightPixels,element.widthPixels,element.heightPixels);
    private static bool TryRect(RectTransform surface,Rect bounds,Vector2 mouse,out Vector2 position)
    {
        position = Vector2.zero;
        if (surface == null || !surface.gameObject.activeInHierarchy ||
            !RectTransformUtility.ScreenPointToLocalPointInRectangle(surface, mouse,
                CameraFor(surface), out var local) || bounds.width<=0 || bounds.height<=0 || !bounds.Contains(local)) return false;
        position = Rect.PointToNormalized(bounds, local);
        return true;
    }

    private static NativeWorld World=>NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host;
    internal static RectTransform ResolveElement(uint id)
    {
        var element=World?.PointerElement(id);
        var rect=element?.handler!=null ? element.rectTransform : null;
        return rect!=null && rect.gameObject.activeInHierarchy ? rect : null;
    }
    private static string ElementActivity(uint id)
    {
        var element=World?.PointerElement(id);
        if(element is GameItem item) return item.name;
        if(element is PixelWindow window && window.parentItems.Count>0) return window.parentItems[0].name;
        return Loc("cursor_surface_53");
    }

    private static Camera CameraFor(RectTransform surface)
    {
        var canvas = surface.GetComponentInParent<Canvas>()?.rootCanvas;
        return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
    }

    internal static ushort CurrentLocation()
    {
        if (EmporiumEntry.Instance == null) return 0;
        // Both native frontends now enter the authoritative shared destination.
        // The old GuestOutside bit only distinguished shop/junkyard and would
        // mislabel the guest's cursor in commissary and engineer scenes.
        return MapUIManager.Instance?.Current?.Id switch
        {
            null => 1,
            "dumping_ground" => 2,
            "commissary" => 3,
            "dr_jackson" => 4,
            _ => 5
        };
    }

    private static string Loc(string key) => ModHelper.GetLocalized("probably_coop", key);
    private static string LocalLocation(ushort location) => Loc("cursor_location_" + location);
    private static string LocalSurface(ushort surface) => Loc("cursor_surface_" + surface);

    private void EnsureMarker()
    {
        if (overlay != null) return;
        var layer = new GameObject("ProbablyCoopPointerLayer", typeof(RectTransform), typeof(Canvas));
        overlay = layer.GetComponent<Canvas>();
        overlay.renderMode = RenderMode.ScreenSpaceOverlay;
        overlay.sortingOrder = 32000;
        var root = new GameObject("ProbablyCoopNativePointer", typeof(RectTransform));
        marker = (RectTransform)root.transform;
        marker.SetParent(layer.transform, false);
        var image = root.AddComponent<Image>();
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

    private void Receive(Packet packet)
    {
        if(!PointerContext.TryDecode(packet.Revision,out var context)) return;
        var current=room.Session?.Native;
        if(current!=null) remoteStamp=current.CaptureStamp();
        snapToSurface |= remoteLocation!=context.Location || remoteSurface!=context.Surface || remoteEntity!=context.Entity;
        remoteSurface = (packet.Flags & 1) != 0 ? context.Surface : Hidden;
        remoteLocation = context.Location;
        remoteEntity=context.Entity;
        if(NativeBackgroundQa.Enabled && context.Surface==PointerContext.NativeElement && (packet.Flags&1)!=0) QaLastEntityContext=context;
        remotePosition = new Vector2(packet.X / 65535f, packet.Y / 65535f);
        remoteViewport = packet.Payload?.Length == 4
            ? new Vector2(
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.Payload) / 65535f,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.Payload.AsSpan(2)) / 65535f)
            : (Vector2?)null;
        if(NativeBackgroundQa.Enabled && context.Surface==PointerContext.NativeElement && (packet.Flags&3)==3)
        {
            // Observe the same click packet even if the unfocused QA
            // sender's next hidden heartbeat was drained in this frame.
            QaLastClickedEntity=context.Entity;
            qaClickedViewport=remoteViewport;
        }
        remoteAt = Time.realtimeSinceStartupAsDouble;
        if ((packet.Flags & 2) != 0) clickUntil = remoteAt + 0.22;
    }

    private void Hide()
    {
        if (marker != null) marker.gameObject.SetActive(false);
    }

    private static ushort Axis(float value) => (ushort)Mathf.RoundToInt(Mathf.Clamp01(value) * 65535);

    public void Dispose()
    {
        if(ReferenceEquals(Current,this)) Current=null;
        room.RemoteCursor -= Receive;
        if (overlay != null) UnityEngine.Object.Destroy(overlay.gameObject);
    }
}
