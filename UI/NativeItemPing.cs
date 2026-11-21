using System.Collections.Generic;
using System.Linq;
using ProbablyCoop.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProbablyCoop;

internal sealed class NativeItemPing
{
    private ReplicaLink.Stamp stamp;
    private ulong seen, context;
    private uint entity;
    private double until;
    private readonly List<(PixelElement Target, RectTransform Marker)> markers =
        new List<(PixelElement Target, RectTransform Marker)>();
    private readonly List<RaycastResult> hits=new List<RaycastResult>();

    internal void Tick(CoopSession session,bool canInput,Vector2 mouse,double now)
    {
        if(session==null || !stamp.IsCurrent(session.Native))
        {
            Clear();
            seen=0;
            if(session==null) return;
            stamp=session.Native.CaptureStamp();
        }

        var world=NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host;
        if(world==null || NativeGuest.Active && !world.Ready) { Clear(); return; }

        var incoming=session.RemoteItemPing;
        if(incoming.Serial>seen)
        {
            seen=incoming.Serial;
            if(incoming.Context==world.ItemPingContext) Mark(world,incoming.Entity,incoming.Context,now);
        }
        var key=CoopSettings.ItemPingKey;
        var events=EventSystem.current;
        if(canInput && key!=KeyCode.None && Input.GetKeyDown(key) && events!=null &&
            !HasFocusedInput(events.currentSelectedGameObject))
        {
            hits.Clear();
            events.RaycastAll(new PointerEventData(events) { position=mouse },hits);
            var item=hits.Count==0 ? null : ItemAt(hits[0].gameObject);
            uint id=item==null ? 0 : world.PointerIdentity(item);
            ulong current=world.ItemPingContext;
            if(VisibleItem(world,id)!=null && session.SendItemPing(id,current,now)) Mark(world,id,current,now);
        }

        if(entity==0) return;
        var target=now<until && world.ItemPingContext==context ? VisibleItem(world,entity) : null;
        if(target==null || markers.Count==0) { Clear(); return; }
        foreach(var entry in markers)
        {
            if(!IsRendered(entry.Target))
            {
                Clear();
                return;
            }
            var bounds=NativePointer.ElementBounds(entry.Target);
            var marker=entry.Marker;
            if(marker==null) { Clear(); return; }
            marker.anchoredPosition=new Vector2(bounds.xMin,bounds.yMin)-Vector2.one;
            marker.sizeDelta=bounds.size+Vector2.one*2;
            marker.SetAsLastSibling();
        }
    }

    private static bool HasFocusedInput(GameObject selected)
    {
        // Unity can retain a destroyed object in the selection or raycast list.
        // Use Unity's lifetime-aware equality before calling native APIs.
        if(selected==null || !selected.activeInHierarchy) return false;
        var textInput=selected.GetComponentInParent<TMP_InputField>();
        if(textInput!=null && textInput.isFocused) return true;
        var legacyInput=selected.GetComponentInParent<InputField>();
        return legacyInput!=null && legacyInput.isFocused;
    }

    private static GameItem ItemAt(GameObject hit)
    {
        if(hit==null || !hit.activeInHierarchy) return null;
        var node=hit.GetComponentInParent<TreeNodeRender>();
        return node!=null && node.FindParentElement() is GameItemElement item && IsRendered(item) ? item : null;
    }

    private static bool IsRendered(PixelElement target)
    {
        if(target==null || target is GameItemElement item && item.IsDestroyed()) return false;
        var handler=target.handler;
        return handler!=null && handler.gameObject.activeInHierarchy && target.rectTransform!=null;
    }

    private static GameItemElement VisibleItem(NativeWorld world,uint id)
    {
        if(id==0 || world.PointerElement(id) is not GameItemElement item || item.parentInventory==null ||
            !IsRendered(item) || NativeContainerAccess.BehindLock(item.parentInventory)) return null;
        return item;
    }

    private void Mark(NativeWorld world,uint id,ulong generation,double now)
    {
        var item=VisibleItem(world,id);
        if(item==null)
        {
            if(world.PointerElement(id) is GameItem hidden && NativeWorld.CanSeeItemPing(hidden) &&
                StoreUIManager.Instance is { } ui)
            {
                Clear();
                ui.Notify(ModHelper.GetLocalized("probably_coop","item_ping_notice",hidden.GetDisplayName()));
                EmporiumAudioPlayer.Instance?.Play();
            }
            return;
        }
        Clear();
        entity=id;
        context=generation;
        until=now+3;
        var targets=new List<PixelElement> { item };
        if(item.parentInventory!=null)
        {
            foreach(var parent in GraphUtils.FindParentsType<GameItem>(item.parentInventory,
                passthrough:static _=>true))
            {
                if(parent is PixelElement element && !targets.Contains(element)) targets.Add(element);
                if(parent.contentWindow is PixelWindow window && IsRendered(window) && !targets.Contains(window))
                    targets.Add(window);
            }
        }
        if(item.parentInventory is PixelElement parentInventory && IsRendered(parentInventory))
            targets.Add(parentInventory);
        foreach(var target in targets.Distinct())
        {
            if(!IsRendered(target)) continue;
            if(target is GameItem game && NativeContainerAccess.BehindLock(game.parentInventory)) continue;
            var targetMarker=(RectTransform)new GameObject("ProbablyCoopItemPing",typeof(RectTransform)).transform;
            targetMarker.SetParent(target.rectTransform,false);
            targetMarker.anchorMin=targetMarker.anchorMax=target.rectTransform.pivot;
            targetMarker.pivot=Vector2.zero;
            AddEdge(targetMarker,new Vector2(0,0),new Vector2(1,0),new Vector2(0,2));
            AddEdge(targetMarker,new Vector2(0,1),new Vector2(1,1),new Vector2(0,2));
            AddEdge(targetMarker,new Vector2(0,0),new Vector2(0,1),new Vector2(2,0));
            AddEdge(targetMarker,new Vector2(1,0),new Vector2(1,1),new Vector2(2,0));
            markers.Add((target,targetMarker));
        }
        if(markers.Count==0) { entity=0; return; }
        EmporiumAudioPlayer.Instance?.Play();
    }

    private static void AddEdge(RectTransform parent,Vector2 min,Vector2 max,Vector2 size)
    {
        var edge=new GameObject("Edge",typeof(RectTransform),typeof(Image)).GetComponent<Image>();
        edge.raycastTarget=false;
        edge.color=new Color(1f,0.84f,0.28f);
        var rect=edge.rectTransform;
        rect.SetParent(parent,false);
        rect.anchorMin=min;
        rect.anchorMax=max;
        rect.sizeDelta=size;
        rect.anchoredPosition=Vector2.zero;
    }

    internal void Clear()
    {
        foreach(var entry in markers)
        {
            if(entry.Marker!=null)
            {
                entry.Marker.gameObject.SetActive(false);
                Object.Destroy(entry.Marker.gameObject);
            }
        }
        markers.Clear();
        entity=0;
    }
}
