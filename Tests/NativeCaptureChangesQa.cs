using System;
using System.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    internal bool QaUsesCaptureFallback(GameItem item)=>untrackedCaptureRoots.Contains(item);
    internal bool QaCanReuseCaptureItem(GameItem item)=>captureRoots.TryGetValue(item,out var entry) &&
        entry.Confirmed && captureChanges.IsCurrent(item,entry.Version) && !untrackedCaptureRoots.Contains(item);
    internal bool QaCaptureFeatureIdentities()=>model.Objects.Values.OfType<GameItem>().All(item=>
        item.itemFeatures.All(feature=>model.Objects.TryGetValue(model.Id(feature),out var live) && ReferenceEquals(feature,live)));
}

internal sealed partial class NativeBackgroundQa
{
    private readonly bool captureChangesQa=Environment.GetCommandLineArgs().Contains("--probably-coop-qa-change-capture");
    private const string CaptureQaTag="PROBABLY_COOP_CAPTURE_QA";
    private GameItem captureQaItem;
    private GameItem captureQaAuditItem;
    private int captureQaStep;
    private double captureQaAt;
    private void TickCaptureChangesHost(RoomSelector room,double now)
    {
        if(captureQaStep==4 && now>=captureQaAt)
        {
            Finish("PASS change-capture-host"); return;
        }
        if(room.Session?.State!=LinkState.Connected || room.Session.Native.FullRevision==0 || NativeWorld.Host==null || now<captureQaAt) return;
        if(captureQaStep==0)
        {
            captureQaItem=NativeWorld.Host.Objects.Values.OfType<GameItemElement>().FirstOrDefault(i=>
                i.contentWindow==null && i.examineWindow==null && i.parentInventory?.GetParentItem()!=null && GeneralHelper.IsItemOwned(i));
            Require(captureQaItem!=null,"nested native item fixture missing");
        }
        // Mutated records need not match the acknowledged checkpoint yet.
        // Inject a direct dictionary write into a different, reusable record
        // so the audit really has to discover it instead of a normal capture.
        if(captureQaStep==2)
        {
            captureQaAuditItem=NativeWorld.Host.Objects.Values.OfType<GameItemElement>().FirstOrDefault(i=>
                !ReferenceEquals(i,captureQaItem) && i.contentWindow==null && i.examineWindow==null &&
                i.parentInventory?.GetParentItem()!=null && NativeWorld.Host.QaCanReuseCaptureItem(i));
            if(captureQaAuditItem==null) return;
        }
        if(captureQaStep==3)
        {
            Require(NativeWorld.Host.QaUsesCaptureFallback(captureQaAuditItem),"direct native field write did not enable fallback");
            Require(NativeWorld.Host.QaCaptureFeatureIdentities(),"reused native feature identity was swept");
        }
        int value=++captureQaStep;
        if(value==3) captureQaAuditItem.state.dict.Add(CaptureQaTag+"Direct",new TagState(CaptureQaTag+"Direct") {valueInt=3});
        else if(value==4) captureQaAuditItem.state.dict[CaptureQaTag+"Direct"].SetInt(value);
        else captureQaItem.state.GetTag(CaptureQaTag).SetInt(value);
        Record("TRACE change-capture-host",$"step={value} nested={(value>=3 ? captureQaAuditItem : captureQaItem).identifier} direct={value==3}");
        captureQaAt=now+6;
    }
    private void TickCaptureChangesGuest(RoomSelector room,double now)
    {
        if(now<captureQaAt) return;
        captureQaAt=now+0.1;
        string key=captureQaStep>=2 ? CaptureQaTag+"Direct" : CaptureQaTag;
        var item=NativeWorld.Guest.Objects.Values.OfType<GameItemElement>().FirstOrDefault(i=>i.state.dict.ContainsKey(key));
        if(item==null) return;
        int value=item.state.dict[key].valueInt;
        if(value==captureQaStep) return;
        Require(value==captureQaStep+1,"nested replica skipped a mutation stage");
        Require(item.parentInventory?.GetParentItem()!=null,"nested ownership was lost");
        Require(NativeWorld.Guest.AuditProjection(out var detail),detail);
        captureQaStep=value;
        Record("PASS change-capture-guest-stage",$"step={value} nested native values and current projection agree");
        if(value==4) Finish("PASS change-capture-guest");
    }
}
