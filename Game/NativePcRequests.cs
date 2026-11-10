using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private readonly List<StoreRequestElement> pcRows = new List<StoreRequestElement>();
    private HashSet<string> pcKnownRequests = new HashSet<string>();

    // Freeze the exact eligible set observed when the actor clicked. Original
    // FillRequest chooses from this ordered list, including nested inventories.
    private string RequestProof(StoreRequest request)
    {
        var tokens = new JArray();
        foreach (var item in EmporiumEntry.Instance.GetAllOwnedItems())
        {
            if (!request.CanThisItemFillRequest(item)) continue;
            if (host) tokens.Add(MoveToken(item));
            else if (TryData(item, out var data) && data["moveToken"]?.Type == JTokenType.String)
                tokens.Add((string)data["moveToken"]);
            else return null;
        }
        return HashData(new JObject { ["request"] = BusinessFields(request, RequestFields), ["items"] = tokens });
    }

    internal JObject PcIntent(StoreRequest request)
    {
        if (request == null || !PlayerStore.Instance.storeRequests.Contains(request)) return null;
        string proof = RequestProof(request);
        return proof == null ? null : new JObject { ["kind"] = "pc", ["id"] = request.id, ["proof"] = proof };
    }

    internal void RequestFulfil(StoreRequest request)
    {
        var intent = PcIntent(request);
        if (intent != null) Submit(intent);
    }
    private ushort ExecutePc(JObject command)
    {
        if(shop.Settling || MapUIManager.Instance.IsOutside) return (ushort)ShopResult.Busy;
        var matches=PlayerStore.Instance.storeRequests.Where(r=>r.id==(string)command["id"]).ToArray();
        if(matches.Length!=1 || RequestProof(matches[0])!=(string)command["proof"]) return (ushort)ShopResult.Stale;
        var request=matches[0];
        if(!request.CanFillRequest()) return (ushort)ShopResult.Unavailable;
        if(DeliveryTouches(request,HandTouches)) return (ushort)ShopResult.Busy;
        request.FillRequest();
        PCUIManager.Instance.OnChange(); PCUIManager.Instance.storePC.RefreshState();
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    internal static bool DeliveryTouches(StoreRequest request,Func<GameItem,bool> occupied)
    {
        var eligible=EmporiumEntry.Instance.GetAllOwnedItems();
        int remaining=request.requiredItemCount;
        for(int i=eligible.Count-1;i>=0 && remaining>0;i--)
        {
            if(!request.CanThisItemFillRequest(eligible[i])) continue;
            if(occupied(eligible[i])) return true;
            remaining--;
        }
        return false;
    }
    private void RefreshPc(bool requestsChanged)
    {
        var ui = PCUIManager.Instance;
        if (ui?.storePC == null) return;
        var requests = PlayerStore.Instance.storeRequests;
        if (requestsChanged)
        {
            var ids = new HashSet<string>(requests.Select(r => r.id));
            if (ids.Count == 0) ui.storePC.SetState(0);
            else if (ids.Any(id => !pcKnownRequests.Contains(id))) ui.storePC.SetState(2);
            else ui.storePC.RefreshState();
            pcKnownRequests = ids;
        }

        if (ui.panel?.activeSelf != true) return;
        if (requestsChanged)
        {
            ui.OnChange();
            return;
        }
        if (requests.Count == 0) return;
        // Eligibility can change independently of order text. Keep the actual
        // buttons alive while the other player moves or consumes stock.
        using var timing = NativePerformance.Measure("guest.open_pc_eligibility");
        var owned = EmporiumEntry.Instance.GetAllOwnedItems();
        ui.gridLayout.GetComponentsInChildren(false, pcRows);
        foreach (var row in pcRows)
        {
            var request = row.storeRequest;
            if (request != null) row.acceptButton.interactable = owned.Count(request.CanThisItemFillRequest) >= request.requiredItemCount;
        }
        pcRows.Clear();
    }
}
[HarmonyPatch(typeof(StoreRequest),nameof(StoreRequest.FillRequest))]
internal static class ReplicaFillRequest
{
    private static bool Prefix(StoreRequest __instance)
    {
        if(NativeGuest.Active) { NativeWorld.Guest?.RequestFulfil(__instance); return false; }
        var host=NativeWorld.Host;
        return host?.PartnerHasHand!=true || !NativeWorld.DeliveryTouches(__instance,host.RejectHeldInput);
    }
}
