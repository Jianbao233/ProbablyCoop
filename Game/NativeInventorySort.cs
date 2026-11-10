using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private JObject sortState=new JObject();
    private readonly SortProofCache sortProofs=new SortProofCache();
    private readonly byte[] sortShapeBuffer=new byte[128*128];
    private readonly List<SortProofCache.Row> sortProofRows=new List<SortProofCache.Row>();
    private int verifiedSortProofs;
    private string SortProof(GameGridInventory inventory)
    {
        using var timing=NativePerformance.Measure("host.sort_proof");
        uint id=model.Id(inventory); var shape=inventory.inventoryShape;
        if(shape.width<0 || shape.height<0 || shape.width>128 || shape.height>128)
            throw new System.IO.InvalidDataException("Invalid sort shape");
        int length=0;
        for(int y=0;y<shape.height;y++) for(int x=0;x<shape.width;x++) sortShapeBuffer[length++]=shape.GetLocal(x,y);
        var state=new SortProofCache.State(id,shape.width,shape.height,shape.GetOutsideBounds(),
            MapUIManager.Instance.Current?.Id ?? "",InventorySortHelper.CanUndo(inventory));
        sortProofRows.Clear();
        foreach(var item in inventory.childItems)
            sortProofRows.Add(new SortProofCache.Row(MoveToken(item),ItemPinHelper.IsPinned(item),DebrisHelper.IsDebris(item)));
        if(!sortProofs.TryGet(state,sortShapeBuffer.AsSpan(0,length),sortProofRows,out var proof))
        {
            var cells=new JArray(); for(int i=0;i<length;i++) cells.Add(sortShapeBuffer[i]);
            var rows=new JArray(); foreach(var row in sortProofRows)
                rows.Add(new JObject { ["move"]=row.Move,["pinned"]=row.Pinned,["debris"]=row.Debris });
            proof=HashData(new JObject { ["id"]=state.Id,
                ["shape"]=new JObject { ["w"]=state.Width,["h"]=state.Height,["outside"]=state.Outside,["cells"]=cells },
                ["scene"]=state.Scene,["undo"]=state.Undo,["items"]=rows });
            sortProofs.Put(state,sortShapeBuffer.AsSpan(0,length),sortProofRows,proof);
        }
        if(NativeBackgroundQa.Enabled && verifiedSortProofs<64)
        {
            if(proof!=OriginalSortProof(inventory)) throw new InvalidOperationException("Native sort proof differs from original");
            if(++verifiedSortProofs==64) UnityEngine.Debug.Log("[probably_coop QA] PASS exact-sort-proof 64 native proofs match original complete JSON/SHA");
        }
        return proof;
    }
    private string OriginalSortProof(GameGridInventory inventory)=>HashData(new JObject {
        ["id"]=model.Id(inventory),["shape"]=RootShape(inventory.inventoryShape),
        ["scene"]=MapUIManager.Instance.Current?.Id ?? "",
        ["undo"]=InventorySortHelper.CanUndo(inventory),
        ["items"]=new JArray(inventory.childItems.Select(i=>new JObject {
            ["move"]=MoveToken(i),["pinned"]=ItemPinHelper.IsPinned(i),["debris"]=DebrisHelper.IsDebris(i) })) });
    private JObject CaptureSorts()
    {
        var result=new JObject();
        foreach(var pair in model.Objects.Where(p=>p.Value is GameGridInventory).ToArray())
        {
            var inventory=(GameGridInventory)pair.Value;
            result[pair.Key.ToString()]=new JObject { ["proof"]=SortProof(inventory),
                ["mayUndo"]=InventorySortHelper.MayUndo(inventory),["canUndo"]=InventorySortHelper.CanUndo(inventory) };
        }
        return result;
    }
    internal bool SortUndoQuery(GameGridInventory inventory,bool can)=>
        sortState[model.ReplicaId(inventory).ToString()]?[can ? "canUndo" : "mayUndo"]?.Value<bool>()==true;
    internal bool RequestSort(GameGridInventory inventory,bool undo,bool bigFirst=false,bool fromEnd=false)
    {
        var data=sortState[model.ReplicaId(inventory).ToString()];
        return data!=null && Submit(new JObject { ["kind"]="sort",["id"]=model.ReplicaId(inventory),
            ["undo"]=undo,["proof"]=data["proof"],["bigFirst"]=bigFirst,["fromEnd"]=fromEnd,["rotate"]=EmporiumOption.SortAutoRotate });
    }
    private ushort ExecuteSort(JObject command)
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        if(!model.Objects.TryGetValue((uint)command["id"],out var obj) || obj is not GameGridInventory inventory || !Accessible(inventory)) return (ushort)ShopResult.Unavailable;
        if(HandInside(inventory)) return (ushort)ShopResult.Busy;
        if((string)command["proof"]!=SortProof(inventory)) return (ushort)ShopResult.Stale;
        bool previous=EmporiumOption.Instance.sortAutoRotate;
        bool applied;
        try
        {
            EmporiumOption.Instance.sortAutoRotate=(bool)command["rotate"];
            applied=(bool)command["undo"] ? InventorySortHelper.Undo(inventory) : InventorySortHelper.Sort(inventory,(bool)command["bigFirst"],(bool)command["fromEnd"]);
        }
        finally { EmporiumOption.Instance.sortAutoRotate=previous; }
        if(applied && !(bool)command["undo"]) TutorialUIManager.Instance.OnInventorySorted();
        nextCapture=0; return (ushort)(applied ? ShopResult.Applied : ShopResult.NoSpace);
    }
}
[HarmonyPatch(typeof(InventorySortHelper),nameof(InventorySortHelper.Sort))]
internal static class ReplicaInventorySort
{
    private static bool Prefix(GameGridInventory inventory,bool bigFirst,bool fromEnd,ref bool __result)
    {
        if(!NativeGuest.Active && NativeWorld.Host?.PartnerBlocksInventory(inventory,true)!=true) return true;
        __result=NativeGuest.Active && NativeWorld.Guest?.RequestSort(inventory,false,bigFirst,fromEnd)==true; return false;
    }
}
[HarmonyPatch(typeof(InventorySortHelper),nameof(InventorySortHelper.Undo))]
internal static class ReplicaInventoryUndo
{
    private static bool Prefix(GameGridInventory inventory,ref bool __result)
    {
        if(!NativeGuest.Active && NativeWorld.Host?.PartnerBlocksInventory(inventory,true)!=true) return true;
        __result=NativeGuest.Active && NativeWorld.Guest?.RequestSort(inventory,true)==true; return false;
    }
}
[HarmonyPatch(typeof(InventorySortHelper),nameof(InventorySortHelper.MayUndo))]
internal static class ReplicaInventoryMayUndo
{
    private static bool Prefix(GameGridInventory inventory,ref bool __result)
    { if(!NativeGuest.Active) return true; __result=NativeWorld.Guest?.SortUndoQuery(inventory,false)==true; return false; }
}
[HarmonyPatch(typeof(InventorySortHelper),nameof(InventorySortHelper.CanUndo))]
internal static class ReplicaInventoryCanUndo
{
    private static bool Prefix(GameGridInventory inventory,ref bool __result)
    { if(!NativeGuest.Active) return true; __result=NativeWorld.Guest?.SortUndoQuery(inventory,true)==true; return false; }
}
