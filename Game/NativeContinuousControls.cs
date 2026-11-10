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
    private sealed class ControlDraft
    { internal object Control; internal JObject Request; internal double Due; }
    private sealed class ContinuousPreview
    { internal object Control; internal ActivityRequests.Entry Attempt; }
    private readonly Dictionary<uint,ControlDraft> controlDrafts=new Dictionary<uint,ControlDraft>();
    private readonly Dictionary<uint,ContinuousPreview> continuousPreviews=new Dictionary<uint,ContinuousPreview>();
    private ulong continuousThrough;
    internal bool EditingContinuous(object control) => controlDrafts.ContainsKey(model.ReplicaId(control)) ||
        continuousPreviews.ContainsKey(model.ReplicaId(control));
    internal static bool DraggingContinuous(object control) => control is MachinerySlider slider
        ? (bool)AccessTools.Field(typeof(MachinerySlider),"dragging").GetValue(slider)
        : control is MachineryRangeSlider range && Convert.ToInt32(AccessTools.Field(typeof(MachineryRangeSlider),"dragging").GetValue(range))!=0;
    private void EditContinuous(object control,float low,float high)
    {
        if(!TryData(control,out var data)) return;
        uint id=(uint)data["entity"];
        if(!controlDrafts.TryGetValue(id,out var draft))
        {
            if(controlDrafts.Count>=16) { CommandNotice("待确认的机器设置过多，请稍后再试。"); return; }
            draft=new ControlDraft { Control=control, Request=new JObject { ["kind"]="control",["id"]=id,["owner"]=data["owner"], ["token"]=data["controlToken"] } };
            controlDrafts.Add(id,draft);
        }
        if(control is MachinerySlider) draft.Request["value"]=low;
        else { draft.Request["low"]=low; draft.Request["high"]=high; }
        draft.Due=Time.realtimeSinceStartupAsDouble+0.15;
    }
    private void ReleaseContinuous(object control)
    { if(controlDrafts.TryGetValue(model.ReplicaId(control),out var draft)) draft.Due=0; }
    private void TickContinuousControls()
    {
        if(host || !Ready) return;
        foreach(var pair in controlDrafts.ToArray())
        {
            var draft=pair.Value;
            if(!model.Objects.TryGetValue(pair.Key,out var current) || !ReferenceEquals(current,draft.Control))
            { controlDrafts.Remove(pair.Key); continue; }
            if(Time.realtimeSinceStartupAsDouble<draft.Due || DraggingContinuous(current)) continue;
            if(MachinePending((uint)draft.Request["owner"])) continue;
            // Keep the pre-edit token. Another actor's change must produce Stale,
            // not silently rebase this edit over a different machine configuration.
            if(!Submit(draft.Request)) continue;
            controlDrafts.Remove(pair.Key);
            continuousPreviews[pair.Key]=new ContinuousPreview { Control=current,Attempt=UiRequestState(LastSubmittedUiRequest) };
        }
    }
    private void ReconcileContinuous(ulong through)
    {
        continuousThrough=Math.Max(continuousThrough,through);
        foreach(var pair in continuousPreviews.ToArray())
        {
            var preview=pair.Value; var attempt=preview.Attempt;
            bool present=model.Objects.TryGetValue(pair.Key,out var current) && ReferenceEquals(current,preview.Control);
            if(present && attempt.Active && (!attempt.Result.HasValue ||
                attempt.Result==(ushort)ShopResult.Applied && attempt.Serial>continuousThrough)) continue;
            continuousPreviews.Remove(pair.Key);
            // A late receipt must not erase a newer local draft/drag. It also
            // must not mutate machine tags: only the confirmed model is read.
            if(!present || controlDrafts.ContainsKey(pair.Key) || DraggingContinuous(current) || !TryData(current,out var state)) continue;
            if(current is MachinerySlider slider) slider.SetValue((float)state["value"],false);
            if(current is MachineryRangeSlider range) range.SetValues((float)state["low"],(float)state["high"],false);
        }
    }
}
