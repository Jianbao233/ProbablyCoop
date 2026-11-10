using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string LogisticsQaTag="PROBABLY_COOP_QA_LOGISTICS_";
    private readonly Dictionary<string,GameItem> logisticsQaItems=new Dictionary<string,GameItem>();
    private JObject logisticsQaHostBefore;
    private bool logisticsQaPersonalReady,logisticsQaHostSeeded;
    private bool LogisticsQaHostDone { get; set; }
    private int logisticsQaStage;
    private double logisticsQaDeadline,logisticsQaNext;
    private static byte[] logisticsQaOldConfig,logisticsQaOldMind;
    private static int logisticsQaMindCommits;
    private static int logisticsQaManualRuns,logisticsQaAutomaticRuns,logisticsQaPresetWrites,logisticsQaPinWrites,logisticsQaChainRuns;
    private static string logisticsQaPerk;
    private static int logisticsQaPerkCost;
    private CustomUIWindow logisticsQaHostWindow;
    private ulong logisticsQaCloseSerial;
    private readonly Dictionary<NativeActorInventory.CarrySlot,GameItem> logisticsQaParkedPartner=new Dictionary<NativeActorInventory.CarrySlot,GameItem>();
    private readonly Dictionary<GameItem,(GameInventory Parent,int Count)> logisticsQaHostCarry=new Dictionary<GameItem,(GameInventory,int)>();
    private int logisticsQaAutomaticCommits;
    private bool logisticsQaHostWindowLostAccess;

    internal static void RememberLogisticsCommand(JObject command)
    { if((int?)command["type"]==(int)NativeWorld.FrontendKind.Hopper) logisticsQaOldConfig??=Encoding.UTF8.GetBytes(command.ToString(Formatting.None)); }
    internal static void RememberMindCommand(JObject command)
    { logisticsQaOldMind??=Encoding.UTF8.GetBytes(command.ToString(Formatting.None)); }
    private GameItem LogisticsQaItem(string role)=>NativeWorld.Guest.Objects.Values.OfType<GameItem>().Single(item=>item.IsTag(LogisticsQaTag+role) && item.parentInventory!=null);
    private GameItem LogisticsQaCreate(string role,string id,GameInventory destination=null)
    {
        var item=DirectoryMaster.Item(id); Require(item!=null,"logistics native factory missing "+id);
        GeneralHelper.SetItemOwned(item,true); item.EnableTag(LogisticsQaTag+role);
        var places=destination==null ? new GameInventory[]{EmporiumEntry.Instance.invElement,EmporiumEntry.Instance.backInvinvElement,EmporiumEntry.Instance.hirelingInv} : new[]{destination};
        bool placed=false;
        foreach(var inventory in places)
        {
            var slot=inventory.TryFindOneValidInventorySlot(item);
            if(slot?.IsValid()!=true || slot.targetItem!=null || slot.numTransfer!=item.unitCount) continue;
            Require(slot.TryAcceptOnce()==item.unitCount,"logistics fixture native placement failed"); placed=true; break;
        }
        Require(placed,"logistics fixture has no native capacity "+role);
        logisticsQaItems[role]=item; return item;
    }
    private void SeedLogisticsQa()
    {
        Require(Enabled && host && IsHostProduct && !logisticsQaHostSeeded,"invalid logistics QA seed");
        logisticsQaOldConfig=null; logisticsQaOldMind=null; logisticsQaMindCommits=0;
        logisticsQaManualRuns=logisticsQaAutomaticRuns=logisticsQaPresetWrites=logisticsQaPinWrites=logisticsQaChainRuns=0;
        var source=LogisticsQaCreate("SOURCE","backpack_medium");
        var destination=LogisticsQaCreate("DESTINATION","backpack_medium");
        var intake=LogisticsQaCreate("INTAKE","intake_hopper",(GameInventory)source.contentWindow.childElement);
        var outlet=LogisticsQaCreate("OUTLET","outlet_hopper",(GameInventory)destination.contentWindow.childElement);
        LogisticsQaCreate("LOOT","toilet_paper",(GameInventory)source.contentWindow.childElement);
        HopperHelper.Link(intake,outlet);
        var automaticLoot=LogisticsQaCreate("AUTO_LOOT","screwdriver");
        var actors=NativeActorSession.Current;
        foreach(var item in actors.Carry.GetAllCarriedItems(NativeActorRun.Slot.Host))
            logisticsQaHostCarry.Add(item,(item.parentInventory,item.unitCount));
        // The isolated checkpoint may already contain ordinary personal gear.
        // Park exact Partner entities in real shared space; restore the same
        // references to the same slots after this bounded logistics scenario.
        using(actors.Enter(NativeActorRun.Slot.Partner))
            foreach(var place in new[]{NativeActorInventory.CarrySlot.Backpack,NativeActorInventory.CarrySlot.LeftPocket})
            {
                var prior=actors.Carry.Get(NativeActorRun.Slot.Partner,place).childItem;
                if(prior==null) continue;
                logisticsQaParkedPartner.Add(place,prior);
                var parking=LogisticsQaSharedSlot(prior);
                Require(parking.TryAcceptOnce()==prior.unitCount,"native logistics could not park original Partner carry");
            }
        ((GameInventory)destination.contentWindow.childElement).onSlotAddItemEndFunc+=(item,_,__)=> {
            if(!ReferenceEquals(item,automaticLoot)) return;
            Require(NativeLogisticsAutomatic.IsRunning && actors.Run.CurrentActor==NativeActorRun.Slot.Partner,
                "automatic hopper lost intake-owner actor across its native yield");
            Require(++logisticsQaAutomaticCommits==1,"automatic hopper moved the same fixture twice");
        };
        LogisticsQaCreate("COORDINATOR","storage_coordinator");
        var mind=LogisticsQaCreate("MIND","mind_reprogrammer");
        mind.ModifyTag("MIND_REPROGRAMMER_CHARGE_INT",tag=>tag.Enable().SetInt(100));
        mind.ModifyTag("MIND_REPROGRAMMER_COOLDOWN_DAYS_INT",tag=>tag.Enable().SetInt(0));
        var toolbox=LogisticsQaCreate("TOOLBOX","toolbox");
        LogisticsQaCreate("TOOL","screwdriver",ToolboxHelper.GetToolboxSlots(toolbox)[0]);
        HopperHelper.OpenWindow(intake); logisticsQaHostWindow=CustomUIManager.Instance.GetWindow("intake_hopper");
        Require(logisticsQaHostWindow?.IsAlive==true,"host original logistics window absent");
        NativeOffscreenCapture.Capture("qa-native-hopper-host-independent");
        logisticsQaHostSeeded=true;
        Record("FIXTURE logistics","original hopper pair, different containing bags, coordinator, toolbox and mind programmer; isolated Partner carry temporarily parked in native shared space, personal profile/files untouched");
    }
    private void CheckHostLogisticsQa()
    {
        if(!logisticsQaHostSeeded || LogisticsQaHostDone) return;
        var actors=NativeActorSession.Current; Require(actors!=null,"logistics actors missing");
        var hostCarry=actors.Carry.GetAllCarriedItems(NativeActorRun.Slot.Host);
        Require(hostCarry.Count==logisticsQaHostCarry.Count && hostCarry.All(item=>logisticsQaHostCarry.TryGetValue(item,out var before) &&
            ReferenceEquals(before.Parent,item.parentInventory) && before.Count==item.unitCount),"logistics touched Host private carry");
        if(!logisticsQaPersonalReady)
        {
            actors.Store.modData.TryGetValue(NativeActorPartnerSetup.SaveKey,out var setup);
            if(!NativeActorPartnerSetup.IsComplete(setup,actors.Run.Run)) return;
            var perk=MindReprogrammerHelper.GetEditablePerks().Single(value=>value.id=="nicotine_addiction");
            if(!actors.Run.Partner.startingPerks.Any(value=>value.id==perk.id))
                actors.Run.Partner.startingPerks.Add(new NativeActorState { startingPerks=new List<StartingPerk>{perk} }.Clone().startingPerks[0]);
            actors.Run.Partner.healthData.daysSinceNicotineConsumption=9;
            logisticsQaPerk=perk.id; logisticsQaPerkCost=MindReprogrammerHelper.GetCost(perk);
            logisticsQaHostBefore=NativeActorState.Capture(actors.Store).Serialize();
            logisticsQaItems["MIND"].EnableTag(LogisticsQaTag+"READY");
            logisticsQaPersonalReady=true;
        }
        Require(JToken.DeepEquals(logisticsQaHostBefore,NativeActorState.Capture(actors.Store).Serialize()),"logistics changed Host personal state");
        if(ReferenceEquals(logisticsQaItems["SOURCE"].parentInventory,actors.Carry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.Backpack)))
            logisticsQaHostWindowLostAccess=true;
        if(!logisticsQaHostWindowLostAccess)
            Require(logisticsQaHostWindow.IsAlive && ReferenceEquals(NativeLogisticsUi.OpenItem(NativeWorld.FrontendKind.Hopper),logisticsQaItems["INTAKE"]),"guest stole or closed Host original hopper window");
        var mind=logisticsQaItems["MIND"];
        if(actors.Run.Partner.startingPerks.Any(perk=>perk.id==logisticsQaPerk)) return;
        Require(logisticsQaMindCommits==1 && MindReprogrammerHelper.GetCharge(mind)==100-logisticsQaPerkCost &&
            MindReprogrammerHelper.GetCooldown(mind)==logisticsQaPerkCost*5,"mind native operation was not exactly once");
        Require(actors.Run.Partner.healthData.daysSinceNicotineConsumption==0,"mind personal native consequence missing");
        Require(ReferenceEquals(logisticsQaItems["LOOT"].parentInventory,logisticsQaItems["DESTINATION"].contentWindow.childElement),"hopper source transfer missing");
        Require(MachineStorageCoordinator.LoadPresets(logisticsQaItems["COORDINATOR"]).Count==1,"coordinator source preset missing");
        Require(ItemPinHelper.IsPinned(logisticsQaItems["COORDINATOR"]),"native pin source commit missing");
        Require(logisticsQaAutomaticCommits==1 && ReferenceEquals(logisticsQaItems["AUTO_LOOT"].parentInventory,logisticsQaItems["DESTINATION"].contentWindow.childElement),
            "Partner private automatic hopper transfer did not commit exactly once");
        Require(logisticsQaManualRuns==1 && logisticsQaAutomaticRuns==1 && logisticsQaPresetWrites==1 && logisticsQaPinWrites==1,
            "native logistics source invocation counts differ from one manual run, one automatic run, one preset write and one pin");
        Require(logisticsQaHostWindowLostAccess && !logisticsQaHostWindow.IsAlive,"Host retained a stale interactive window for Partner private equipment");
        using(actors.Enter(NativeActorRun.Slot.Partner))
            foreach(var pair in logisticsQaParkedPartner)
            {
                var original=actors.Carry.Get(NativeActorRun.Slot.Partner,pair.Key);
                Require(original.childItem==null,"logistics QA left an original Partner carry slot occupied");
                var slot=original.TryFindOneValidInventorySlot(pair.Value);
                Require(slot?.IsValid()==true && slot.TryAcceptOnce()==pair.Value.unitCount && ReferenceEquals(original.childItem,pair.Value),
                    "native logistics could not restore original Partner carry");
            }
        LogisticsQaHostDone=true;
        Record("PASS host-native-logistics","original helper configurations, manual plus Partner-private on-add transfer once and pin committed; Host carry unchanged; Host panel independently retained then closed on loss of ownership; original Partner gear restored");
        Record("TRACE host-native-logistics-counts","manual="+logisticsQaManualRuns+" automatic="+logisticsQaAutomaticRuns+
            " autoDestinationAdds="+logisticsQaAutomaticCommits+" presetWrites="+logisticsQaPresetWrites+" pinWrites="+logisticsQaPinWrites+" mind="+logisticsQaMindCommits);
        Record("PASS host-native-mind-reprogrammer","Partner-only perk removal/withdrawal reset, original charge and cooldown once; Host personal state identical");
    }
    private static void LogisticsQaClick(CustomUIWindow window,string text)
    {
        var matches=window.root.GetComponentsInChildren<Button>(true).Where(button=>button.interactable && button.GetComponentInChildren<TextMeshProUGUI>()?.text==text).ToArray();
        Require(matches.Length==1,"original logistics button ambiguous: "+text); matches[0].onClick.Invoke();
    }
    private static SlotMarker LogisticsQaSharedSlot(GameItem item)
    {
        foreach(var inventory in new GameInventory[]{EmporiumEntry.Instance.invElement,EmporiumEntry.Instance.backInvinvElement,EmporiumEntry.Instance.hirelingInv})
        {
            var slot=inventory.TryFindOneValidInventorySlot(item);
            if(slot?.IsValid()==true && slot.targetItem==null && slot.numTransfer==item.unitCount) return slot;
        }
        throw new InvalidOperationException("Logistics QA requires native shared capacity without merging fixture identity");
    }
    private static void LogisticsQaMove(ReplicaLink link,GameItem item,GameInventory destination)
    {
        var slot=destination==null ? LogisticsQaSharedSlot(item) : destination.TryFindOneValidInventorySlot(item);
        Require(slot?.IsValid()==true && slot.targetItem==null && slot.numTransfer==item.unitCount,"native logistics private fixture destination has no capacity");
        slot.TryAcceptOnce(); Require(QaWaiting(link),"native logistics slot did not emit authenticated move");
    }
    private bool RunGuestLogisticsQa(ReplicaLink link,double now)
    {
        if(logisticsQaStage==14) return true;
        if(logisticsQaDeadline==0) logisticsQaDeadline=now+150;
        Require(now<logisticsQaDeadline,"native logistics bounded regression timed out at "+logisticsQaStage);
        if(now<logisticsQaNext || QaWaiting(link)) return false;
        var world=NativeWorld.Guest;
        if(logisticsQaStage==0)
        {
            var mind=LogisticsQaItem("MIND"); if(!mind.IsTag(LogisticsQaTag+"READY")) return false;
            logisticsQaPerk="nicotine_addiction";
            logisticsQaPerkCost=MindReprogrammerHelper.GetCost(MindReprogrammerHelper.GetEditablePerks().Single(perk=>perk.id==logisticsQaPerk));
            LogisticsQaItem("INTAKE").ActivateSlotItem(); logisticsQaStage=1; return false;
        }
        if(logisticsQaStage==1)
        {
            Require(NativeLogisticsUi.Visible(NativeWorld.FrontendKind.Hopper,LogisticsQaItem("INTAKE")),"guest original Hopper window did not open");
            var window=CustomUIManager.Instance.GetWindow("intake_hopper");
            var sliders=window.root.GetComponentsInChildren<Slider>(true);
            Require(sliders.Length==2,"original Hopper sliders missing");
            sliders[0].value=9; // Actual native slider callback, no direct tag assignment.
            Require(HopperHelper.GetOrder(LogisticsQaItem("INTAKE"))==9,"local Hopper draft was not immediate");
            logisticsQaNext=now+0.6; logisticsQaStage=2; return false;
        }
        if(logisticsQaStage==2)
        {
            Require(logisticsQaOldConfig!=null && HopperHelper.GetOrder(LogisticsQaItem("INTAKE"))==9,"hopper config not confirmed");
            Require(link.Submit(logisticsQaOldConfig,now),"cannot replay original stale logistics proof"); logisticsQaStage=3; return false;
        }
        if(logisticsQaStage==3)
        {
            Require(QaResult(link)==(ushort)ShopResult.Stale,"old helper settings were accepted twice");
            NativeOffscreenCapture.Capture("qa-native-hopper-guest-confirmed");
            LogisticsQaClick(CustomUIManager.Instance.GetWindow("intake_hopper"),LocHelper.GetLocalizedMechanic("mech_hopper_btn_transfer_now"));
            Require(QaWaiting(link),"original hopper transfer button emitted no request"); logisticsQaStage=4; return false;
        }
        if(logisticsQaStage==4)
        {
            Require(ReferenceEquals(LogisticsQaItem("LOOT").parentInventory,LogisticsQaItem("DESTINATION").contentWindow.childElement),"hopper moved item missing on real guest");
            CustomUIManager.Instance.CloseWindow("intake_hopper");
            LogisticsQaMove(link,LogisticsQaItem("SOURCE"),world.ActorCarry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.Backpack));
            logisticsQaStage=20; return false;
        }
        if(logisticsQaStage==20)
        {
            Require(ReferenceEquals(LogisticsQaItem("SOURCE").parentInventory,world.ActorCarry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.Backpack)),"Partner source bag was not carried");
            LogisticsQaMove(link,LogisticsQaItem("DESTINATION"),world.ActorCarry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.LeftPocket));
            logisticsQaStage=21; return false;
        }
        if(logisticsQaStage==21)
        {
            Require(ReferenceEquals(LogisticsQaItem("DESTINATION").parentInventory,world.ActorCarry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.LeftPocket)),"Partner outlet bag was not carried");
            LogisticsQaItem("INTAKE").ActivateSlotItem(); logisticsQaStage=22; return false;
        }
        if(logisticsQaStage==22)
        {
            var window=CustomUIManager.Instance.GetWindow("intake_hopper");
            Require(window?.IsAlive==true,"Partner private hopper original window missing");
            var toggles=window.root.GetComponentsInChildren<Toggle>(true);
            Require(toggles.Length==5,"native hopper toggle layout changed"); toggles[0].isOn=true;
            logisticsQaNext=now+0.6; logisticsQaStage=23; return false;
        }
        if(logisticsQaStage==23)
        {
            Require(HopperHelper.IsOnAdd(LogisticsQaItem("INTAKE")),"Partner private hopper on-add setting not confirmed");
            LogisticsQaMove(link,LogisticsQaItem("AUTO_LOOT"),(GameInventory)LogisticsQaItem("SOURCE").contentWindow.childElement);
            logisticsQaStage=24; return false;
        }
        if(logisticsQaStage==24)
        {
            if(!ReferenceEquals(LogisticsQaItem("AUTO_LOOT").parentInventory,LogisticsQaItem("DESTINATION").contentWindow.childElement)) return false;
            CustomUIManager.Instance.CloseWindow("intake_hopper");
            Record("PASS native-logistics-auto-carry","native on-add queue yielded then transferred between Partner-carried containers; both physical roots retained");
            LogisticsQaMove(link,LogisticsQaItem("SOURCE"),null); logisticsQaStage=25; return false;
        }
        if(logisticsQaStage==25)
        {
            LogisticsQaMove(link,LogisticsQaItem("DESTINATION"),null); logisticsQaStage=26; return false;
        }
        if(logisticsQaStage==26)
        {
            LogisticsQaItem("COORDINATOR").ActivateSlotItem(); logisticsQaStage=5; return false;
        }
        if(logisticsQaStage==5)
        {
            var window=CustomUIManager.Instance.GetWindow("storage_coordinator"); Require(window?.IsAlive==true,"coordinator original window missing");
            window.Get("new_preset").button.onClick.Invoke(); logisticsQaNext=now+0.6; logisticsQaStage=6; return false;
        }
        if(logisticsQaStage==6)
        {
            Require(MachineStorageCoordinator.LoadPresets(LogisticsQaItem("COORDINATOR")).Count==1,"native coordinator preset not confirmed");
            NativeOffscreenCapture.Capture("qa-native-coordinator-guest-confirmed");
            // Exact original helper actions, explicitly not a physical-key test.
            ItemMouseDoubleClickHandler.current.OpenContentAction(LogisticsQaItem("TOOLBOX"),new Vector2(300,300));
            ToolboxHelper.TryToggleEquipSlot(0);
            Require(ReferenceEquals(ItemSelectHandler.current.GetEquippedItem(),LogisticsQaItem("TOOL")),"original toolbox selection did not find shared native tool");
            ToolboxHelper.TryToggleEquipSlot(0);
            Require(ItemSelectHandler.current.GetEquippedItem()==null,"original toolbox selection did not toggle off");
            ItemPinHelper.SetPinned(LogisticsQaItem("COORDINATOR"),true);
            Require(QaWaiting(link),"original pin emitted no request"); logisticsQaStage=7; return false;
        }
        if(logisticsQaStage==7)
        {
            Require(ItemPinHelper.IsPinned(LogisticsQaItem("COORDINATOR")),"native pin not confirmed");
            CustomUIManager.Instance.CloseWindow("storage_coordinator");
            logisticsQaCloseSerial=world.LastSubmittedUiRequest; logisticsQaNext=now+0.8; logisticsQaStage=8; return false;
        }
        if(logisticsQaStage==8)
        {
            Require(CustomUIManager.Instance.GetWindow("storage_coordinator")?.IsAlive!=true && CustomUIManager.Instance.GetWindow("intake_hopper")?.IsAlive!=true &&
                world.LastSubmittedUiRequest==logisticsQaCloseSerial,"snapshot reopened closed logistics UI or close became business operation");
            Record("PASS native-logistics","real native CustomUI sliders/preset button/hopper transfer; stale proof refused; pin replicated; toolbox native selection; local closes stay closed (no physical keyboard claim)");
            LogisticsQaItem("MIND").ActivateSlotItem(); logisticsQaStage=9; return false;
        }
        if(logisticsQaStage==9)
        {
            var window=CustomUIManager.Instance.GetWindow("mind_reprogrammer"); Require(window?.IsAlive==true,"mind original window missing");
            var label=window.root.GetComponentsInChildren<TextMeshProUGUI>(true).Single(text=>text.text.StartsWith(
                MindReprogrammerHelper.GetEditablePerks().Single(perk=>perk.id==logisticsQaPerk).GetLocalizedDisplayName()+" (",StringComparison.Ordinal));
            var row=label.transform.parent;
            var buttons=row.GetComponentsInChildren<Button>(true).Where(button=>button.interactable && button.GetComponentInChildren<TextMeshProUGUI>()?.text==LocHelper.GetLocalizedUI("ui_mind_reprogrammer_remove")).ToArray();
            Require(buttons.Length==1,"native mind personal remove button absent");
            NativeOffscreenCapture.Capture("qa-native-mind-guest-before-remove");
            buttons[0].onClick.Invoke();
            Require(QaWaiting(link),"native mind button emitted no request"); logisticsQaStage=10; return false;
        }
        if(logisticsQaStage==10)
        {
            Require(!PlayerStore.Instance.startingPerks.Any(perk=>perk.id==logisticsQaPerk) &&
                MindReprogrammerHelper.GetCharge(LogisticsQaItem("MIND"))==100-logisticsQaPerkCost,"guest mind personal/charge projection missing");
            Require(logisticsQaOldMind!=null && link.Submit(logisticsQaOldMind,now),"cannot replay original mind proof"); logisticsQaStage=11; return false;
        }
        if(logisticsQaStage==11)
        {
            Require(QaResult(link)==(ushort)ShopResult.Stale,"old mind apply accepted twice");
            MindReprogrammerHelper.CloseWindow(); logisticsQaCloseSerial=world.LastSubmittedUiRequest;
            logisticsQaNext=now+1; logisticsQaStage=12; return false;
        }
        if(logisticsQaStage==12)
        {
            Require(CustomUIManager.Instance.GetWindow("mind_reprogrammer")?.IsAlive!=true && world.LastSubmittedUiRequest==logisticsQaCloseSerial,
                "mind window reopened after local close");
            Require(MindReprogrammerHelper.GetCharge(LogisticsQaItem("MIND"))==100-logisticsQaPerkCost,"mind stale replay consumed charge");
            Record("PASS native-mind-reprogrammer","original perk picker button, Partner native personal consequence and machine charge once, stale replay refused, local close remains closed");
            logisticsQaStage=14; return true;
        }
        return false;
    }
    [HarmonyPatch(typeof(MindReprogrammerHelper),nameof(MindReprogrammerHelper.Apply))]
    private static class LogisticsMindQaCount
    {
        private static void Postfix(GameItem item,bool __runOriginal)
        {
            if(Enabled && IsHostProduct && __runOriginal && item.IsTag(LogisticsQaTag+"MIND"))
                Require(++logisticsQaMindCommits==1,"native Mind Apply invoked more than once, including after the completion marker");
        }
    }
    [HarmonyPatch(typeof(HopperHelper),nameof(HopperHelper.RunAndRecord))]
    private static class LogisticsHopperQaCount
    {
        private static void Postfix(GameItem intake,int __result,bool __runOriginal)
        {
            if(!Enabled || !IsHostProduct || !__runOriginal) return;
            if(intake.IsTag(LogisticsQaTag+"CHAIN_INTAKE"))
            {
                Require(__result==2 && ++logisticsQaChainRuns==1,"second native hopper hop did not transfer both units exactly once");
                return;
            }
            if(!intake.IsTag(LogisticsQaTag+"INTAKE")) return;
            Require(__result==1,"native QA hopper run did not transfer exactly one fixture unit");
            if(NativeLogisticsAutomatic.IsRunning) Require(++logisticsQaAutomaticRuns==1,"native automatic hopper invoked twice");
            else Require(++logisticsQaManualRuns==1,"native manual hopper invoked twice");
        }
    }
    [HarmonyPatch(typeof(MachineStorageCoordinator),nameof(MachineStorageCoordinator.SavePresets))]
    private static class LogisticsCoordinatorQaCount
    {
        private static void Postfix(GameItem item,bool __runOriginal)
        {
            if(Enabled && IsHostProduct && __runOriginal && item.IsTag(LogisticsQaTag+"COORDINATOR"))
                Require(++logisticsQaPresetWrites<=3,"native coordinator repeated a preset write");
        }
    }
    [HarmonyPatch(typeof(ItemPinHelper),nameof(ItemPinHelper.SetPinned))]
    private static class LogisticsPinQaCount
    {
        private static void Postfix(GameItem item,bool pinned,bool __runOriginal)
        {
            if(Enabled && IsHostProduct && __runOriginal && item?.IsTag(LogisticsQaTag+"COORDINATOR")==true)
                Require(pinned && ++logisticsQaPinWrites==1,"native pin changed more than once");
        }
    }
}
