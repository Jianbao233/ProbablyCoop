using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

internal static partial class NativeMirrorProbe
{
    // Native Unity class-contract probe, not a claim that all machine recipes
    // have been played. Uses the same capture/read path as network projections.
    internal static void VerifyMutableLayoutIdentity()
    {
        var source=new NativeModelContext(); var replica=new NativeModelContext();
        try
        {
            var label=new MachineryLabel().SetText("before");
            var tag=new TagElement().SetText("before");
            var indicator=new IndicatorElement();
            var push=new PushButtonElement().SetText("button");
            var slider=new MachinerySlider(10,64).SetRange(0,100,1);
            var first=new GameSlotInventory(2,2); var second=new GameSlotInventory(2,2);
            int contentCallbacks=0;
            var advanced=new AdvButtonElement(text:"mod control",width:90,height:18,callback:()=>contentCallbacks++);
            var grid=new GridPixelElement(2,4);
            grid.AttachPos(advanced,0,3);
            grid.AttachPos(label,0,0); grid.AttachPos(indicator,1,0);
            grid.AttachPos(push,0,1); grid.AttachPos(slider,1,1);
            grid.AttachPos(first,0,2); grid.AttachPos(second,1,2);
            var tabs=new MachineryTabs(); tabs.AddTab("machine",grid); tabs.AddTab("details",tag);
            MachineryTabs Project()
            {
                source.Seen.Clear(); JObject data;
                using(source.Enter()) data=CaptureElement(tabs);
                replica.Seen.Clear(); MachineryTabs view;
                using(replica.Enter()) view=(MachineryTabs)ReadElement(data);
                replica.Sweep(true); return view;
            }
            var view=Project(); var viewGrid=(GridPixelElement)view.GetPage(0);
            var stable=replica.Objects.ToDictionary(pair=>pair.Key,pair=>pair.Value);
            var viewPush=(PushButtonElement)viewGrid.GetElement(0,1);
            var viewAdvanced=(AdvButtonElement)viewGrid.GetElement(0,3);
            if(!viewAdvanced.isDisabled || viewAdvanced.widthPixels!=90 || viewAdvanced.text!="mod control")
                throw new InvalidOperationException("An advanced content button blocked projection or had an active callback");
            int callbacks=0; viewPush.onDown=()=>callbacks++; viewPush.onUp=()=>callbacks++;
            view.Select(1,false);
            AccessTools.Field(typeof(MachineryTabs),"hovered").SetValue(view,1);
            for(int pass=0;pass<2;pass++)
            {
                label.SetText(pass==0 ? "updated machine readout" : "after");
                tag.SetText(pass==0 ? "updated details" : "after");
                if(pass==0) { indicator.Light(); push.Light().Lock(); }
                else { indicator.Dark(); push.Dark().Unlock(); }
                AccessTools.Field(typeof(PushButtonElement),"isDown").SetValue(push,pass==0);
                tabs.SetCaption(0,"changed "+pass);
                advanced.Set("mod state "+pass).SetDescription("tooltip "+pass);
                if(pass==0) advanced.Hide(); else advanced.Show();
                var updated=Project();
                if(viewAdvanced.isHidden!=(pass==0) || viewAdvanced.text!="mod state "+pass ||
                    viewAdvanced.description!="tooltip "+pass || !viewAdvanced.isDisabled || contentCallbacks!=0)
                    throw new InvalidOperationException("Mutable advanced button was not safely projected");
                if(!ReferenceEquals(updated,view) || stable.Any(pair=>!ReferenceEquals(replica.Objects[pair.Key],pair.Value)) ||
                    updated.selected!=1 || (int)AccessTools.Field(typeof(MachineryTabs),"hovered").GetValue(updated)!=1 ||
                    viewPush.IsLight()!=(pass==0) || viewPush.IsLocked()!=(pass==0) || viewPush.IsDown()!=(pass==0) || callbacks!=0)
                    throw new InvalidOperationException("Mutable native layout replaced controls/local tab state or executed a business callback");
            }
            grid.Detach(first); grid.Detach(second);
            grid.AttachPos(second,0,2); grid.AttachPos(first,1,2);
            Project();
            if(!ReferenceEquals(viewGrid.GetElement(0,2),replica.Objects[source.Id(second)]) ||
                !ReferenceEquals(viewGrid.GetElement(1,2),replica.Objects[source.Id(first)]))
                throw new InvalidOperationException("Native layout grid storage swap lost ownership");
            tabs.AddTab("new page",new MachineryLabel().SetText("new"));
            var expanded=Project();
            if(expanded.count!=3 || expanded.selected!=1 || !ReferenceEquals(expanded.GetPage(0),viewGrid) ||
                !ReferenceEquals(expanded.GetPage(1),view.GetPage(1)))
                throw new InvalidOperationException("Structural native tab replacement lost its retained children or local page");
        }
        finally
        {
            replica.Seen.Clear(); replica.Sweep(true);
            source.Seen.Clear(); source.Sweep(true);
        }
    }
}
