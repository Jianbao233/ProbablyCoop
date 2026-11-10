using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private JObject CaptureClientItemQueries(StoreClient client)
    {
        using var timing=NativePerformance.Measure("host.client_item_queries");
        var preference=client.willAcceptItem;
        var rows=new JArray();
        foreach(var pair in model.Objects)
        {
            if(pair.Value is not GameItem item) continue;
            int mask=(client.IsClientBuyingThisItem(item,false) ? 1 : 0) |
                (client.IsClientBuyingThisItem(item,true) ? 2 : 0) |
                (client.IsClientRefusingItem(item,false) ? 4 : 0) |
                (client.IsClientRefusingItem(item,true) ? 8 : 0) |
                (preference?.Invoke(item)==true ? 16 : 0) |
                (client.CanClientExposeAnyFeature(item) ? 32 : 0) |
                (client.CanClientExposeAnyFeatureDeep(item) ? 64 : 0);
            rows.Add(new JArray(pair.Key,mask));
        }
        return new JObject { ["preference"]=preference!=null,["items"]=rows };
    }
    private void BindClientItemQueries(StoreClient client,JObject data)
    {
        if(data?["items"] is not JArray rows || rows.Count>model.Objects.Count || data["preference"]?.Type!=JTokenType.Boolean)
            throw new InvalidDataException("Invalid native customer query projection");
        var values=new Dictionary<GameItem,byte>();
        foreach(var token in rows)
        {
            if(token is not JArray row || row.Count!=2 || row[0].Type!=JTokenType.Integer || row[1].Type!=JTokenType.Integer ||
                !uint.TryParse(row[0].ToString(),out uint id) || !model.Objects.TryGetValue(id,out var obj) || obj is not GameItem item ||
                !byte.TryParse(row[1].ToString(),out byte mask) || mask>127 || values.ContainsKey(item))
                throw new InvalidDataException("Invalid native customer item query");
            values.Add(item,mask);
        }
        NativeClientItemQueries.Bind(client,values,(bool)data["preference"]);
    }
}

// Pure query results owned by the same complete snapshot as the customer/items.
// No factory or business callback is copied/executed on the guest. Missing local
// items fail closed until their authoritative projection has arrived.
internal static class NativeClientItemQueries
{
    private static readonly ConditionalWeakTable<StoreClient,Dictionary<GameItem,byte>> views=new ConditionalWeakTable<StoreClient,Dictionary<GameItem,byte>>();
    internal static void Bind(StoreClient client,Dictionary<GameItem,byte> values,bool preference)
    {
        views.Remove(client); views.Add(client,values);
        client.willAcceptItem=null;
        if(preference) client.willAcceptItem=item=>item!=null && values.TryGetValue(item,out byte mask) && (mask&16)!=0;
    }
    internal static bool Read(StoreClient client,GameItem item,out byte mask)
    {
        mask=12;
        if(!NativeGuest.Active || client==null || !views.TryGetValue(client,out var values)) return false;
        if(item!=null && values.TryGetValue(item,out byte found)) mask=found;
        return true;
    }
    [HarmonyPatch(typeof(StoreClient),nameof(StoreClient.IsClientBuyingThisItem),typeof(GameItem),typeof(bool))]
    private static class Buying
    {
        private static bool Prefix(StoreClient __instance,GameItem __0,bool __1,ref bool __result)
        { if(!Read(__instance,__0,out byte mask)) return true; __result=(mask&(__1 ? 2 : 1))!=0; return false; }
    }
    [HarmonyPatch(typeof(StoreClient),nameof(StoreClient.IsClientRefusingItem),typeof(GameItem),typeof(bool))]
    private static class Refusing
    {
        private static bool Prefix(StoreClient __instance,GameItem __0,bool __1,ref bool __result)
        { if(!Read(__instance,__0,out byte mask)) return true; __result=(mask&(__1 ? 8 : 4))!=0; return false; }
    }
    [HarmonyPatch(typeof(StoreClient),nameof(StoreClient.CanClientExposeAnyFeature))]
    private static class Exposing
    {
        private static bool Prefix(StoreClient __instance,GameItem __0,ref bool __result)
        { if(!Read(__instance,__0,out byte mask)) return true; __result=(mask&32)!=0; return false; }
    }
    [HarmonyPatch(typeof(StoreClient),nameof(StoreClient.CanClientExposeAnyFeatureDeep))]
    private static class ExposingDeep
    {
        private static bool Prefix(StoreClient __instance,GameItem __0,ref bool __result)
        { if(!Read(__instance,__0,out byte mask)) return true; __result=(mask&64)!=0; return false; }
    }
}
