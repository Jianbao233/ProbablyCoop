using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;
internal sealed partial class NativeWorld
{
    internal bool RequestGroup(IEnumerable<GameItemElement> items,GameInventory destination,GameItem target=null,bool precise=false,int dx=0,int dy=0)
    {
        var sources=items.Where(i=>i!=null && !i.IsDestroyed()).ToArray();
        if(sources.Length==0 || sources.Length>32) { CommandNotice("一次最多移动 32 件物品。"); return false; }
        var rows=new JArray();
        foreach(var item in sources)
        {
            if(!TryData(item,out var data)) return false;
            rows.Add(new JObject { ["id"]=model.ReplicaId(item),["token"]=data["moveToken"] });
        }
        return Submit(new JObject { ["kind"]="group",["items"]=rows,["destination"]=model.ReplicaId(destination),
            ["target"]=model.ReplicaId(target),["targetToken"]=target!=null && TryData(target,out var targetData) ? targetData["moveToken"] : null,
            ["precise"]=precise,["dx"]=dx,["dy"]=dy });
    }
    private ushort ExecuteGroup(JObject command)
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        var rows=command["items"] as JArray;
        if(rows==null || rows.Count<1 || rows.Count>32) return (ushort)ShopResult.Unavailable;
        var items=new List<GameItemElement>();
        foreach(JObject row in rows)
        {
            if(!CurrentItem(row,out var item) || item is not GameItemElement element || items.Contains(element)) return (ushort)ShopResult.Stale;
            if(HandTouches(item)) return (ushort)ShopResult.Busy;
            if(!Accessible(item.parentInventory) || !item.MayRemove() || item.MaxNumRemove()<1) return (ushort)ShopResult.Unavailable;
            items.Add(element);
        }
        model.Objects.TryGetValue((uint)command["destination"],out var dest); var inventory=dest as GameInventory;
        model.Objects.TryGetValue((uint)command["target"],out var obj); var target=obj as GameItem;
        if(target!=null && (!Accessible(target.parentInventory) || MoveToken(target)!=(string)command["targetToken"] || items.Contains(target))) return (ushort)ShopResult.Stale;
        if(inventory!=null && (!Accessible(inventory) || !GeneralHelper.MayPlayerInsertInto(inventory))) return (ushort)ShopResult.Unavailable;
        if(HandTouches(target) || HandDestination(inventory) || !(bool)command["precise"] && HandInside(inventory)) return (ushort)ShopResult.Busy;
        if((bool)command["precise"])
        {
            int dx=(int)command["dx"],dy=(int)command["dy"];
            if(inventory==null || !inventory.SupportsGroupPlacement || Math.Abs((long)dx)>127 || Math.Abs((long)dy)>127) return (ushort)ShopResult.Unavailable;
            var handler=ItemMultiSelectHandler.current;
            var selected=NativeGroupTransfer.Selected(handler); var previous=selected.ToArray();
            try
            {
                selected.Clear(); selected.AddRange(items);
                if(!(bool)AccessTools.Method(typeof(ItemMultiSelectHandler),"IsFormationValid").Invoke(handler,new object[]{inventory,dx,dy})) return (ushort)ShopResult.NoSpace;
                AccessTools.Method(typeof(ItemMultiSelectHandler),"CommitPrecisePlacement").Invoke(handler,new object[]{inventory,dx,dy});
            }
            finally { selected.Clear(); selected.AddRange(previous.Where(i=>!i.IsDestroyed() && i.parentInventory!=null)); }
        }
        else
        {
            if(inventory==null && target==null) return (ushort)ShopResult.Unavailable;
            int moved=0;
            foreach(var item in items)
            {
                if(item.IsDestroyed() || item.parentInventory==null || !item.MayRemove()) continue;
                int amount=Math.Min(item.unitCount,item.MaxNumRemove());
                if(target==null) { if(item.parentInventory!=inventory) moved+=amount-GraphUtils.TryAcceptAll(inventory,item,amount); }
                else if(target is GameItemElement destroyed && destroyed.IsDestroyed()) break;
                else if(GeneralHelper.MayPlayerInsertInto(target) && target.MayHaveValidInventorySlot(item)) moved+=amount-GraphUtils.TryAcceptAll(target,item,amount);
                else if(target.MayStack(item)) moved+=target.GetStackSlot(item)?.TryAcceptOnce(amount) ?? 0;
                else if(item.MayTarget(target) && item.CanTarget(target))
                { ItemBehaviourManager.current.Target(item,target); if(!(target is GameItemElement removed && removed.IsDestroyed()) && !item.IsDestroyed() && item.MayTarget(target) && item.CanTarget(target)) item.Target(target); moved++; }
            }
            if(moved==0) return (ushort)ShopResult.NoSpace;
        }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
}
internal static class NativeGroupTransfer
{
    internal static List<GameItemElement> Selected(ItemMultiSelectHandler handler) => (List<GameItemElement>)AccessTools.Field(typeof(ItemMultiSelectHandler),"selectedItems").GetValue(handler);
    [HarmonyPatch(typeof(ItemMultiSelectHandler),"CommitPrecisePlacement")]
    private static class Precise
    {
        private static bool Prefix(ItemMultiSelectHandler __instance,GameInventory inventory,int deltaX,int deltaY)
        {
            if(!NativeGuest.Active)
            {
                var host=NativeWorld.Host;
                return host?.PartnerHasHand!=true || !host.PartnerBlocksInventory(inventory,false) && !Selected(__instance).Any(host.RejectHeldInput);
            }
            NativeWorld.Guest?.RequestGroup(Selected(__instance),inventory,null,true,deltaX,deltaY); return false;
        }
    }
    [HarmonyPatch(typeof(ItemMultiSelectHandler),"EndGroupDrag")]
    private static class Drop
    {
        private static bool Prefix(ItemMultiSelectHandler __instance)
        {
            if(!NativeGuest.Active) return true;
            var items=Selected(__instance); if(items.Count==0) return false;
            string dragStart=items[0].soundDragStart,dragEnd=items[0].soundDragEnd;
            var point=Input.mousePosition;
            var target=RenderHandler.RaycastElement<GameItem>(point);
            if(target is GameItemElement member && items.Contains(member)) target=null;
            var destination=target==null ? RenderHandler.RaycastFindParentElement<GameInventory>(point) : null;
            bool tab=false;
            if(target==null && destination==null) { destination=MachineryTabs.FindDropInventoryAt(point); tab=destination!=null; }
            bool precise=false; int dx=0,dy=0;
            if(target==null && destination?.SupportsGroupPlacement==true && !tab)
            {
                var home=(GameInventory)AccessTools.Method(typeof(ItemMultiSelectHandler),"GetCommonHomeInventory").Invoke(__instance,null);
                if(home!=null && (home==destination || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                {
                    object[] args={destination,0,0};
                    if((bool)AccessTools.Method(typeof(ItemMultiSelectHandler),"TryGetPreciseDelta").Invoke(__instance,args))
                    { precise=true; dx=(int)args[1]; dy=(int)args[2]; }
                    else destination=null;
                }
            }
            if(destination!=null || target!=null) NativeWorld.Guest?.RequestGroup(items,destination,target,precise,dx,dy);
            if(destination!=null)
                foreach(var window in WindowsHandler.current.visibleWindows.Where(w=>GraphUtils.FindChildEqualsIgnoreType<GameItem>(w,destination)).ToArray()) window.ToFront();
            foreach(string name in new[]{"RestoreGhosts","ClearDropTarget","ClearSelection"}) AccessTools.Method(typeof(ItemMultiSelectHandler),name).Invoke(__instance,null);
            var state=AccessTools.Field(typeof(ItemMultiSelectHandler),"state"); state.SetValue(__instance,Enum.ToObject(state.FieldType,0));
            AudioManager.Instance.TryPlayOne(dragEnd,dragStart);
            return false;
        }
    }
}
