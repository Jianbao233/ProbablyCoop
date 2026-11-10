using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private readonly HashSet<uint> remoteTools=new HashSet<uint>();
    private JObject targetQueries=new JObject();
    private bool useNativeAbilityGuards;
    private int verifiedAbilityGuards;
    private static int ReadNativeAbilities(GameItem item) => ItemAbilityBits.Pack(item.MayActivateSlotItem(),item.CanActivateSlotItem(),
        item.MayToggleSlotItem(),item.CanToggleSlotItem(),item.MaySelectSlotItem(),item.CanSelectSlotItem());
    private int CaptureItemAbilities(GameItem item)
    {
        if(!useNativeAbilityGuards) return ReadNativeAbilities(item);
        var parent=item.parentInventory;
        if(parent==null) return 0;
        // With neither native callback installed, these queries return false.
        // The vanilla implementation still builds a SlotMarker and scans its
        // inventory for the item's index each time. Preserve real callbacks.
        int abilities=ItemAbilityBits.Pack(
            (item.mayActivateSlotItemFunc!=null || parent.mayActivateSlotItemFunc!=null) && item.MayActivateSlotItem(),
            (item.canActivateSlotItemFunc!=null || parent.canActivateSlotItemFunc!=null) && item.CanActivateSlotItem(),
            (item.mayToggleSlotItemFunc!=null || parent.mayToggleSlotItemFunc!=null) && item.MayToggleSlotItem(),
            (item.canToggleSlotItemFunc!=null || parent.canToggleSlotItemFunc!=null) && item.CanToggleSlotItem(),
            (item.maySelectSlotItemFunc!=null || parent.maySelectSlotItemFunc!=null) && item.MaySelectSlotItem(),
            (item.canSelectSlotItemFunc!=null || parent.canSelectSlotItemFunc!=null) && item.CanSelectSlotItem());
        if(NativeBackgroundQa.Enabled && verifiedAbilityGuards<256)
        {
            if(abilities!=ReadNativeAbilities(item)) throw new InvalidOperationException("Native slot ability fast path differs from original");
            if(++verifiedAbilityGuards==256) UnityEngine.Debug.Log("[probably_coop QA] PASS native-ability-guard 256 projections match original queries");
        }
        return abilities;
    }
    private JObject CaptureTargetQueries()
    {
        var queries=new JObject();
        if(UnityEngine.Time.realtimeSinceStartupAsDouble>=viewsExpiry) return queries;
        var targets=model.Objects.Where(p=>p.Value is GameItem i && i.parentInventory!=null).ToArray();
        foreach(uint id in remoteTools.Take(4))
        {
            if(!model.Objects.TryGetValue(id,out var obj) || obj is not GameItem source || source.parentInventory==null) continue;
            var pairs=new JObject();
            foreach(var target in targets)
            {
                var item=(GameItem)target.Value;
                bool may=source.MayTarget(item);
                if(may) pairs[target.Key.ToString()]=source.CanTarget(item) ? 3 : 1;
            }
            queries[id.ToString()]=pairs;
        }
        return queries;
    }
    internal bool ItemQuery(GameItem item,string name) => TryData(item,out var data) &&
        data["abilities"]?.Type==JTokenType.Integer && ItemAbilityBits.Has((int)data["abilities"],name);
    internal bool TargetQuery(GameItem source,GameItem target,bool can)
    {
        if(source==null || target==null) return false;
        int flags=(int?)targetQueries[model.ReplicaId(source).ToString()]?[model.ReplicaId(target).ToString()] ?? 0;
        return (flags & (can ? 2 : 1))!=0;
    }

    private static EditorKind? TargetEditor(GameItem tool) => tool.identifier switch {
        "magnifier" => EditorKind.Inspection,
        "labeler" => EditorKind.Labels,
        "labeler_blue" => EditorKind.Rename,
        _ => null
    };

    private void OpenAcknowledgedDragEditor(Gesture hand)
    {
        var command=hand.Drop;
        bool itemDrop=(string)command["kind"]=="containerDrop";
        if(!itemDrop && ((string)command["kind"]!="item" || (string)command["action"]!="target") ||
            hand.Serial!=nextHandSerial ||
            hand.Context!=observedOperationContext ||
            !model.Objects.TryGetValue((uint)command["id"],out var source) || source is not GameItem tool ||
            TargetEditor(tool) is not EditorKind editor ||
            !model.Objects.TryGetValue((uint)command["target"],out var value) || value is not GameItem target ||
            !Accessible(tool.parentInventory) || !Accessible(target.parentInventory)) return;
        if(!itemDrop || ReplicaMoveToken(tool)==(string)command["token"])
            OpenEditor(editor,tool,target);
    }
    internal void RequestItem(GameItem item,string action,GameItem target=null)
    {
        if(!TryData(item,out var data)) return;
        if(CloseKnownFrontend(item,action)) return;
        if(action=="activate" && OpenLocalDocument(item)) return;
        // These three native tools first open an actor-local editor. Its eventual
        // business actions are separately routed, so the host's panel stays independent.
        if(action=="target" && TargetEditor(item) is EditorKind editor && TargetQuery(item,target,true))
        {
            OpenEditor(editor,item,target);
            return;
        }
        if(target!=null && !TryData(target,out _)) return;
        Submit(new JObject { ["kind"]="item",["action"]=action,["id"]=(uint)data["entity"],["token"]=(string)data["moveToken"],
            ["target"]=model.ReplicaId(target),["targetToken"]=target!=null ? (string)model.Data[model.ReplicaId(target)]["moveToken"] : "" });
    }
    private readonly ContentsProofCache contentsProofs=new ContentsProofCache();
    private readonly List<ContentsProofCache.Row> contentsProofRows=new List<ContentsProofCache.Row>();
    private int contentsProofAudits;
    private string ContentsProof(GameItem item,bool replica=false)
    {
        contentsProofRows.Clear();
        // Preserve the native traversal order and query all three values again,
        // even on a hit. Nothing about descendants or their locks is inferred.
        var descendants=item.children.Count==0 ? null : GraphUtils.FindAllChildrenType<GameItem>(item);
        AppendContentsProofRow(item,replica);
        if(descendants!=null)
            foreach(var child in descendants) AppendContentsProofRow(child,replica);
        uint id=replica ? model.ReplicaId(item) : model.Id(item);
        if(contentsProofs.TryGet(id,contentsProofRows,out string proof))
        {
            if(NativeBackgroundQa.Enabled && contentsProofAudits<64)
            {
                if(proof!=HashContentsProofRows()) throw new InvalidOperationException("Cached contents proof differs from canonical native proof");
                if(++contentsProofAudits==64) UnityEngine.Debug.Log("[probably_coop QA] PASS contents-proof-oracle 64 native cache hits equal original canonical JSON/SHA256");
            }
            return proof;
        }
        proof=HashContentsProofRows();
        contentsProofs.Put(id,contentsProofRows,proof);
        return proof;
    }
    private void AppendContentsProofRow(GameItem item,bool replica)=>contentsProofRows.Add(new ContentsProofCache.Row(
        replica ? ReplicaMoveToken(item) : MoveToken(item),ItemPinHelper.IsPinned(item),GeneralHelper.IsItemLocked(item)));
    private string HashContentsProofRows()=>HashData(new JObject { ["items"]=new JArray(contentsProofRows.Select(row=>new JObject {
        ["move"]=row.Move,["pinned"]=row.Pinned,["locked"]=row.Locked })) });
    internal void RequestInventoryItem(GameItem item,bool unload,bool value)
    {
        if(!TryData(item,out var data)) return;
        Submit(new JObject { ["kind"]="item",["action"]=unload ? "unload" : "pin",["id"]=model.ReplicaId(item),
            ["token"]=data["moveToken"],["contents"]=data["contentsToken"],["value"]=value });
    }
    private ushort ExecuteItem(JObject command)
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        if(!CurrentItem(command,out var item)) return (ushort)ShopResult.Stale;
        if(!Accessible(item.parentInventory)) return (ushort)ShopResult.Unavailable;
        if(HandTouches(item)) return (ushort)ShopResult.Busy;
        switch((string)command["action"])
        {
            case "pin":
                if(!ItemPinHelper.MayPin(item)) return (ushort)ShopResult.Unavailable;
                if((string)command["contents"]!=ContentsProof(item)) return (ushort)ShopResult.Stale;
                ItemPinHelper.SetPinned(item,(bool)command["value"]); break;
            case "unload":
                if(item.contentWindow==null || item.contentWindow.isLocked || !GeneralHelper.IsItemOwned(item) || item.IsTag("IS_UNLOAD_DISABLED_TAG")) return (ushort)ShopResult.Unavailable;
                if((string)command["contents"]!=ContentsProof(item)) return (ushort)ShopResult.Stale;
                item.TryUnloadAll((bool)command["value"]); break;
            case "activate":
                if(!item.MayActivateSlotItem() || !item.CanActivateSlotItem()) return (ushort)ShopResult.Unavailable;
                item.ActivateSlotItem();
                if(NativeBackgroundQa.Enabled) UnityEngine.Debug.Log("[probably_coop QA] native activation id="+model.Id(item)+" type="+item.identifier+" parent="+model.Id(item.parentInventory)+" callbacks="+item.onActivateSlotItemFunc?.GetInvocationList().Length);
                break;
            case "toggle":
                if(!item.MayToggleSlotItem() || !item.CanToggleSlotItem()) return (ushort)ShopResult.Unavailable;
                item.ToggleSlotItem(); break;
            case "target":
                if(!model.Objects.TryGetValue((uint)command["target"],out var obj) || obj is not GameItem target ||
                    target.parentInventory==null || !Accessible(target.parentInventory) || MoveToken(target)!=(string)command["targetToken"])
                    return (ushort)ShopResult.Stale;
                if(!item.MayTarget(target) || !item.CanTarget(target)) return (ushort)ShopResult.Unavailable;
                if(HandTouches(target)) return (ushort)ShopResult.Busy;
                // This request only opens the partner's editor. Subsequent
                // changes carry their own proofs and native commit boundary.
                if(TargetEditor(item)!=null) break;
                ItemBehaviourManager.current.Target(item,target);
                if(item.MayTarget(target) && item.CanTarget(target)) item.Target(target);
                break;
            default: return (ushort)ShopResult.Unavailable;
        }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    private static bool Accessible(GameInventory inventory)
    {
        if(inventory==null) return false;
        var world=NativeGuest.Active ? Guest : Host;
        if(world?.HasActorProjection==true)
        {
            if(!world.ActorCanAccess(inventory,out int actorPlace) || actorPlace is 22 or 23 or 9) return false;
            return !MapUIManager.Instance.IsOutside || actorPlace is 3 or 4 or 5 or 8 ||
                MapUIManager.Instance.Current?.ShowsStoreCounters==true && actorPlace is 2 or 6;
        }
        var e=EmporiumEntry.Instance;
        for(int place=1;place<=23;place++)
        {
            var root=Root(place);
            if(root==null || !ReferenceEquals(root,inventory) && !GraphUtils.FindAllChildrenType<GameInventory>(root).Contains(inventory)) continue;
            if(place is 22 or 23 or 9) return false; // native transfer buffers are never user destinations
            return !MapUIManager.Instance.IsOutside || place is 3 or 4 or 5 or 8 ||
                MapUIManager.Instance.Current?.ShowsStoreCounters==true && place is 2 or 6;
        }
        return false;
    }
}

[HarmonyPatch(typeof(ItemPinHelper),nameof(ItemPinHelper.SetPinned))]
internal static class ReplicaPinItem
{
    private static bool Prefix(GameItem item,bool pinned)
    { if(!NativeGuest.Active) return NativeWorld.Host?.RejectHeldInput(item)!=true; NativeWorld.Guest?.RequestInventoryItem(item,false,pinned); return false; }
}
[HarmonyPatch(typeof(GameItem),nameof(GameItem.TryUnloadAll),new[]{typeof(bool)})]
internal static class ReplicaUnloadItem
{
    private static bool Prefix(GameItem __instance,bool skipPinned,ref GameItem __result)
    {
        if(!NativeGuest.Active && NativeWorld.Host?.RejectHeldInput(__instance)!=true) return true;
        if(NativeGuest.Active) NativeWorld.Guest?.RequestInventoryItem(__instance,true,skipPinned);
        __result=__instance; return false;
    }
}

[HarmonyPatch]
internal static class ReplicaItemAbility
{
    private static readonly MethodBase[] Methods=new[]{"MayActivateSlotItem","CanActivateSlotItem","MayToggleSlotItem","CanToggleSlotItem","MaySelectSlotItem","CanSelectSlotItem"}
        .Select(name=>(MethodBase)AccessTools.Method(typeof(GameItem),name)).ToArray();
    private static IEnumerable<MethodBase> TargetMethods() => Methods;
    internal static bool NativeReturnContractUnmodified() => Methods.All(method=>method!=null &&
        (Harmony.GetPatchInfo(method)?.Owners.All(owner=>owner=="mod.probably_coop") ?? true));
    private static bool Prefix(GameItem __instance,MethodBase __originalMethod,ref bool __result)
    { if(NativeLocalItemChooser.Ability(__instance,__originalMethod.Name,out var local)) { __result=local; return false; }
      if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true) return true; __result=NativeWorld.Guest.ItemQuery(__instance,__originalMethod.Name); return false; }
}
[HarmonyPatch]
internal static class ReplicaItemTargetQuery
{
    private static IEnumerable<MethodBase> TargetMethods()
    { yield return AccessTools.Method(typeof(GameItem),nameof(GameItem.MayTarget)); yield return AccessTools.Method(typeof(GameItem),nameof(GameItem.CanTarget)); }
    private static bool Prefix(GameItem __instance,GameItem targetItem,MethodBase __originalMethod,ref bool __result)
    { if(!NativeGuest.Active) return true; __result=NativeWorld.Guest?.TargetQuery(__instance,targetItem,__originalMethod.Name=="CanTarget")==true; return false; }
}
[HarmonyPatch]
internal static class ReplicaItemUse
{
    private static IEnumerable<MethodBase> TargetMethods()
    { yield return AccessTools.Method(typeof(GameItem),nameof(GameItem.ActivateSlotItem)); yield return AccessTools.Method(typeof(GameItem),nameof(GameItem.ToggleSlotItem)); }
    private static bool Prefix(GameItem __instance,MethodBase __originalMethod)
    { if(NativeLocalItemChooser.Activate(__instance)) return false;
      if(!NativeGuest.Active) return NativeWorld.Host?.RejectHeldInput(__instance)!=true; NativeWorld.Guest?.RequestItem(__instance,__originalMethod.Name=="ActivateSlotItem" ? "activate" : "toggle"); return false; }
}
[HarmonyPatch(typeof(GameItem),nameof(GameItem.Target))]
internal static class ReplicaToolTarget
{ private static bool Prefix(GameItem __instance,GameItem targetItem)
    {
        if(!NativeGuest.Active) return NativeWorld.Host?.RejectHeldInput(__instance)!=true && NativeWorld.Host?.RejectHeldInput(targetItem)!=true;
        NativeWorld.Guest?.RequestItem(__instance,"target",targetItem); return false;
    }
}
[HarmonyPatch(typeof(ItemBehaviourManager),nameof(ItemBehaviourManager.Target))]
internal static class ReplicaToolBehaviours
{
    // A guest declares the complete action from GameItem.Target. The host can
    // still use a different tool while the partner is holding an item.
    private static bool Prefix(GameItem source,GameItem target) => !NativeGuest.Active &&
        NativeWorld.Host?.RejectHeldInput(source)!=true && NativeWorld.Host?.RejectHeldInput(target)!=true;
}
