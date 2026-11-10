using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// A guest's native radio light can follow the click immediately. The machine
// state, output and transaction proof still come exclusively from the host.
internal sealed partial class NativeWorld
{
    private sealed class ControlPreview
    {
        internal object Widget;
        internal ulong Serial;
        internal uint Owner;
        internal int Choice,SentChoice;
    }
    private readonly Dictionary<uint,ControlPreview> controlPreviews=new Dictionary<uint,ControlPreview>();

    private void PreviewSelection(object widget,JObject command,int choice)
    {
        if(closed || host || !Ready || link==null) return;
        uint id=model.ReplicaId(widget);
        CheckCommandReply();
        if(command!=null && controlPreviews.TryGetValue(id,out var existing) && ReferenceEquals(existing.Widget,widget))
        {
            // Coalesce until the first result is projected. Only then can the
            // next action use the new host control token, including stock changes.
            existing.Choice=choice;
            RefreshSelection(id);
            return;
        }
        if(id!=0 && command!=null)
        {
            if(controlPreviews.Count>=16)
            {
                CommandNotice("待确认的设备操作过多，请稍后再试。");
                return;
            }
            uint owner=(uint)command["owner"];
            var preview=new ControlPreview { Widget=widget,Owner=owner,Choice=choice,SentChoice=choice };
            controlPreviews[id]=preview;
            if(!MachinePending(owner) && !OwnerControlPending(owner) && Submit(command)) preview.Serial=LastSubmittedUiRequest;
        }
        RefreshSelection(id);
    }
    private bool OwnerControlPending(uint owner) => controlPreviews.Values.Any(preview=>preview.Owner==owner && preview.Serial!=0);
    private void RefreshSelection(uint id)
    {
        if(!model.Data.TryGetValue(id,out var data) || !model.Objects.TryGetValue(id,out var widget)) return;
        controlPreviews.TryGetValue(id,out var preview);
        bool pending=preview!=null && ReferenceEquals(preview.Widget,widget);
        if(widget is MachineryRadioList list)
        {
            int selected=pending ? preview.Choice : (int)data["selected"];
            var candidate=list.Get(selected);
            if(pending && (candidate==null || candidate.isDisabled || candidate.isHidden)) selected=(int)data["selected"];
            if(list.group.selected==selected) return;
            if(selected>=0) list.Select(selected,false);
            else foreach(var button in list.group.buttons) button.SetOn(false,false);
        }
        else if(widget is MachineryRadioButton toggle && toggle.group==null)
        {
            bool on=pending && !toggle.isDisabled && !toggle.isHidden ? preview.Choice!=0 : (bool)data["on"];
            toggle.SetOn(on,false);
        }
    }
    private void ReconcileSelections(ulong through,ulong rejected=0)
    {
        if(controlPreviews.Count==0) return;
        foreach(var entry in controlPreviews.ToArray())
        {
            if(rejected!=0 && entry.Value.Serial==rejected ||
                !model.Objects.TryGetValue(entry.Key,out var widget) || !ReferenceEquals(widget,entry.Value.Widget))
                controlPreviews.Remove(entry.Key);
            else if(entry.Value.Serial!=0 && entry.Value.Serial<=through)
            {
                if(entry.Value.SentChoice==entry.Value.Choice) controlPreviews.Remove(entry.Key);
                else entry.Value.Serial=0;
            }
            RefreshSelection(entry.Key);
        }
    }
    private void FlushControlPreview()
    {
        if(controlPreviews.Count==0) return;
        var next=controlPreviews.FirstOrDefault(entry=>entry.Value.Serial==0 && !MachinePending(entry.Value.Owner) && !OwnerControlPending(entry.Value.Owner));
        var preview=next.Value;
        if(preview==null) return;
        if(model.Data.TryGetValue(next.Key,out var data) && model.Objects.TryGetValue(next.Key,out var widget) &&
            ReferenceEquals(widget,preview.Widget))
        {
            var command=widget switch
            {
                MachineryRadioList list when (int)data["selected"]!=preview.Choice => RadioIntent(list,preview.Choice),
                MachineryRadioButton toggle when (bool)data["on"]!=(preview.Choice!=0) => ToggleIntent(toggle,preview.Choice!=0),
                _ => null
            };
            if(command!=null)
            {
                if(Submit(command))
                {
                    preview.Serial=LastSubmittedUiRequest;
                    preview.SentChoice=preview.Choice;
                }
                return;
            }
        }
        controlPreviews.Remove(next.Key);
        RefreshSelection(next.Key);
    }
}
