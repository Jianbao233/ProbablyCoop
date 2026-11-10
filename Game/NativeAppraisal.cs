using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// The original certificate window reads the local actor's confirmed appraisal
// data. Its toggles submit a typed, versioned edit; no host UI is borrowed and
// no replica writes PlayerStore.appraisalBlacklist.
internal sealed partial class NativeWorld
{
    private const string AppraisalWindowId="appraisal_items";
    private static readonly MethodInfo AppraisalSetAllowed=AccessTools.Method(typeof(AppraisalItemsWindow),"SetAllowed");
    private GameItem appraisalFrontendTarget;
    private string appraisalRenderedProof;
    private bool appraisalRefresh;
    private ActivityRequests.Entry appraisalAttempt;

    private void CaptureAppraisalNames()
    {
        // A registry row can be visible without any matching physical item in
        // the shop. Populate the existing name projection by registered type,
        // using the native authority cache; never discover names via factories
        // on a replica. Canonical category names win over renamed stock items.
        foreach(var type in AppraisalItemRegistry.GetAll())
            if(!string.IsNullOrEmpty(type.itemId)) itemNames[type.itemId]=type.GetDisplayName();
    }

    private static string AppraisalProof()=>HashData(new JObject {
        ["rank"]=AppraisalRankHelper.GetRank(),
        ["declined"]=NativeActorFieldAccess.Get_appraisalPromotionDeclined(PlayerStore.Instance),
        ["blacklist"]=new JArray((NativeActorFieldAccess.Get_appraisalBlacklist(PlayerStore.Instance) ??
            new System.Collections.Generic.List<string>()).OrderBy(id=>id,StringComparer.Ordinal)) });
    private static string AppraisalRenderProof()=>HashData(new JObject { ["state"]=AppraisalProof(),
        ["names"]=new JArray(AppraisalItemRegistry.GetUnlocked(AppraisalRankHelper.GetRank()).Select(type=>type.GetDisplayName())) });

    internal bool AppraisalFrontendVisible(GameItem target)=>target!=null && ReferenceEquals(appraisalFrontendTarget,target) &&
        CustomUIManager.Instance?.GetWindow(AppraisalWindowId)?.IsAlive==true;

    internal void OpenAppraisalFrontend(GameItem target)
    {
        if(host || target==null) return;
        appraisalFrontendTarget=target;
        AppraisalItemsWindow.Open();
        appraisalRenderedProof=AppraisalRenderProof();
        appraisalRefresh=false;
    }

    internal void CloseAppraisalFrontend(GameItem target)
    {
        if(!ReferenceEquals(appraisalFrontendTarget,target)) return;
        if(AppraisalFrontendVisible(target)) CustomUIManager.Instance.CloseWindow(AppraisalWindowId);
        appraisalFrontendTarget=null;
        appraisalRenderedProof=null;
        appraisalRefresh=false;
    }

    internal bool RequestAppraisalAllowed(AppraisalItemType type,bool allowed)
    {
        if(host || !Ready || type==null || !localFrontends.TryGetValue(FrontendKind.Appraisal,out var view) ||
            !AppraisalFrontendVisible(view.Target) || !TryData(view.Target,out var data)) return false;
        // A second toggle cannot turn an unconfirmed draft into a new proof.
        // Its visual state is reconciled after the outstanding source result.
        if(appraisalAttempt!=null) { appraisalRefresh=true; return false; }
        if(!AppraisalItemRegistry.CanBlock(type,AppraisalRankHelper.GetRank())) { appraisalRefresh=true; return false; }
        var command=new JObject { ["kind"]="frontend",["type"]=(int)FrontendKind.Appraisal,
            ["view"]=view.Serial,["id"]=(uint)data["entity"],["token"]=data["moveToken"],
            ["actor"]=(int)NativeActorRun.Slot.Partner,["expected"]=AppraisalProof(),
            ["itemType"]=type.id,["allowed"]=allowed };
        if(!Submit(command)) { appraisalRefresh=true; return false; }
        appraisalAttempt=UiRequestState(LastSubmittedUiRequest);
        if(NativeBackgroundQa.Enabled) NativeBackgroundQa.RememberAppraisalCommand(command);
        return true;
    }

    private ushort ExecuteAppraisal(JObject command)
    {
        if((int?)command["actor"]!=(int)OperationActor ||
            !nativeFrontends.TryGetValue(FrontendKind.Appraisal,out var view) ||
            view.Serial!=(ulong)command["view"] || !ValidFrontend(view) || !CurrentItem(command,out var item) ||
            !ReferenceEquals(item,view.Target)) return (ushort)ShopResult.Stale;
        if(AppraisalRankHelper.GetCertificateRank(item)<0 || !GeneralHelper.IsItemEffectivelyOwned(item) ||
            !Accessible(item.parentInventory)) return (ushort)ShopResult.Unavailable;
        if(HandTouches(item)) return (ushort)ShopResult.Busy;
        if(command["expected"]?.Type!=JTokenType.String || (string)command["expected"]!=AppraisalProof())
            return (ushort)ShopResult.Stale;
        if(command["itemType"]?.Type!=JTokenType.String || ((string)command["itemType"]).Length>128 ||
            command["allowed"]?.Type!=JTokenType.Boolean) return (ushort)ShopResult.Unavailable;
        var type=AppraisalItemRegistry.Get((string)command["itemType"]);
        if(type==null || !AppraisalItemRegistry.CanBlock(type,AppraisalRankHelper.GetRank()))
            return (ushort)ShopResult.Unavailable;
        // This original method changes only the scoped actor's category list.
        // The delegate's native rank check remains in force at execution time.
        AppraisalSetAllowed.Invoke(null,new object[]{type,(bool)command["allowed"]});
        nextCapture=0;
        return (ushort)ShopResult.Applied;
    }

    private void ReconcileAppraisal()
    {
        if(host) return;
        if(appraisalAttempt!=null)
        {
            if(!appraisalAttempt.Result.HasValue || appraisalAttempt.Result==(ushort)ShopResult.Applied &&
                appraisalAttempt.Serial>frontendThrough) return;
            appraisalAttempt=null;
            appraisalRefresh=true;
        }
        if(!AppraisalFrontendVisible(appraisalFrontendTarget)) return;
        string proof=AppraisalRenderProof();
        if(!appraisalRefresh && proof==appraisalRenderedProof) return;
        // Native Open preserves the existing window's local position. A local
        // close is respected even while a confirmed snapshot is arriving.
        AppraisalItemsWindow.Open();
        appraisalRenderedProof=proof;
        appraisalRefresh=false;
    }

    private void ClearAppraisal()
    {
        CloseAppraisalFrontend(appraisalFrontendTarget);
        appraisalAttempt=null;
    }

    [HarmonyPatch(typeof(AppraisalItemsWindow),nameof(AppraisalItemsWindow.Open),new Type[0])]
    private static class RouteAppraisalWindow
    {
        private static bool Prefix()
        {
            var world=RemoteUiExecution;
            if(world==null) return true;
            var certificate=world.remoteChoiceOrigins.FirstOrDefault(item=>AppraisalRankHelper.GetCertificateRank(item)>=0);
            if(certificate==null) throw new InvalidOperationException("Appraisal opening has no validated certificate origin.");
            world.CaptureFrontend(FrontendKind.Appraisal,certificate);
            return false;
        }
    }

    [HarmonyPatch(typeof(AppraisalItemsWindow),nameof(AppraisalItemsWindow.Open),new[]{typeof(bool)})]
    private static class KeepHostAppraisalWindow
    {
        // Rev12 SetAllowed refreshes the native window via Open(false).
        // The partner receives the updated category through ReconcileAppraisal.
        private static bool Prefix()=>RemoteUiExecution==null;
    }
}

[HarmonyPatch(typeof(AppraisalRankHelper),nameof(AppraisalRankHelper.InitCertificate))]
internal static class NativeAppraisalCapability
{
    private static void Postfix(GameItem item)=>NativeFrontendCapabilities.Bind(item,NativeWorld.FrontendKind.Appraisal);
}

[HarmonyPatch(typeof(AppraisalItemsWindow),"SetAllowed")]
internal static class NativeAppraisalAllowed
{
    private static bool Prefix(AppraisalItemType type,bool allowed)
    {
        if(!NativeGuest.Active) return true;
        NativeWorld.Guest?.RequestAppraisalAllowed(type,allowed);
        return false;
    }
}
