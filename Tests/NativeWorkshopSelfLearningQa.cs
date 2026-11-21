#if PROBABLY_COOP_GRID_QA
using System;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string SelfLearningMod = "Pen_mod_selflearning";
    private const string SelfLearningId = SelfLearningMod + ".machine";
    private const string SelfLearningSeller = "QA_WORKSHOP_SELF_LEARNING_SELLER";
    private const string SelfLearningMachineTag = "QA_WORKSHOP_SELF_LEARNING_MACHINE";
    private const string SelfLearningModuleTag = "QA_WORKSHOP_SELF_LEARNING_MODULE";
    private const string SelfLearningBatteryTag = "QA_WORKSHOP_SELF_LEARNING_BATTERY";
    private const string SelfLearningResultTag = "QA_WORKSHOP_SELF_LEARNING_RESULT";
    private const string SelfLearningPerformance = "BONUS_PERCENTAGE_PERFORMANCE_INT";

    private GameItem selfLearningMachine, selfLearningModule, selfLearningBattery;
    private GameGridInventory selfLearningModuleOrigin;
    private GridShape selfLearningModulePose;
    private bool selfLearningSellerQueued, selfLearningSawOff, selfLearningSettled, selfLearningHostDone;
    private int selfLearningGuestStage, selfLearningCash, selfLearningPrice;
    private int selfLearningInitialEnergy, selfLearningInitialPerformance, selfLearningFinalEnergy, selfLearningFinalPerformance;
    private double selfLearningDeadline, selfLearningNext;

    private void TickWorkshopSelfLearningHost(RoomSelector room, double now)
    {
        var store = PlayerStore.Instance;
        if (selfLearningMachine == null)
        {
            Require(DirectoryMaster.Has<GameItem>(SelfLearningId), "self-learning Workshop item was not registered");
            NotebookUIManager.Instance.CloseUI();
            selfLearningMachine = DirectoryMaster.Item(SelfLearningId);
            Require(selfLearningMachine?.onCycleEndSlotItemFunc?.Method.DeclaringType?.FullName == "PenSelfLearning.NightService",
                "Workshop factory did not attach the original NightService callback");
            Require(MachineHelper.GetModuleInv(selfLearningMachine) != null && MachineHelper.GetBatterySlot(selfLearningMachine) != null,
                "self-learning machine did not create its native module and battery inventories");
            selfLearningMachine.EnableTag(SelfLearningMachineTag);
            selfLearningModule = SeedFrontendItem("system_module_performance", SelfLearningModuleTag);
            ModuleEffectHelper.InitLearningAlgo(selfLearningModule);
            Require(selfLearningModule.GetTagReadonly("LEARNING_ALGO_MAX_PERFORMANCE_INT").GetInt() > 0,
                "module needs a valid original learning algorithm cap");
            selfLearningBattery = SeedFrontendItem("energy_credit_ext", SelfLearningBatteryTag);
            selfLearningModuleOrigin = (GameGridInventory)selfLearningModule.parentInventory;
            selfLearningInitialEnergy = SelfLearningEnergy(selfLearningBattery);
            selfLearningInitialPerformance = SelfLearningProgress(selfLearningModule);
            Record("TRACE workshop-fixture", SelfLearningMod + " factory, original module and battery ready for the guest; stock offer queued after connection");
        }

        if (room.Session?.State != LinkState.Connected) return;
        if (!selfLearningSellerQueued)
        {
            if (store.storeState == PlayerStore.StoreState.MORNING)
            {
                if (store.CanStartDay()) StoreShutterButton.Instance.OnClick();
                return;
            }
            Require(store.storeState == PlayerStore.StoreState.OPEN, "self-learning purchase fixture requires the open shop");
            var seller = new StoreClient
            {
                identifier = SelfLearningSeller,
                displayName = "QA self-learning machine seller",
                spriteName = SpriteDict.GetRandomScavSpriteName(),
                clientFaction = StoreClient.FACTION_LOWER,
                dismissable = true,
                clientIntent = StoreClient.ClientIntent.SELL,
                isMultiBuyDisabled = true,
                mainDialogue = null,
                isIntroduced = true
            };
            // Deterministic stock offer, with the regular game purchase flow.
            // The Workshop mod's randomized engineer stock is outside this probe.
            seller.OnArrival = () =>
            {
                store.AddDirectToWeightedTable(selfLearningMachine, false);
                Require(selfLearningMachine.parentInventory == EmporiumEntry.Instance.backInvinvElement,
                    "self-learning offer does not fit the original trading counter");
                selfLearningCash = store.playerCash;
                selfLearningPrice = (int)selfLearningMachine.GetNegociatedValue();
                Require(selfLearningPrice > 0 && selfLearningPrice <= selfLearningCash, "self-learning fixture cannot afford the offer");
            };
            store.storeClientManager.clientStack.Clear();
            store.storeClientManager.AddNextClient(seller);
            selfLearningSellerQueued = true;
            return;
        }

        if (selfLearningPrice == 0 || !GeneralHelper.IsItemOwned(selfLearningMachine)) return;
        Require(store.playerCash == selfLearningCash - selfLearningPrice, "purchase must debit the original machine price exactly once");
        if (selfLearningHostDone)
        {
            Require(SelfLearningEnergy(selfLearningBattery) == selfLearningFinalEnergy &&
                SelfLearningProgress(selfLearningModule) == selfLearningFinalPerformance,
                "reconnecting replayed the Workshop settlement");
            return;
        }

        var batterySlot = MachineHelper.GetBatterySlot(selfLearningMachine);
        var moduleBay = MachineHelper.GetModuleInv(selfLearningMachine);
        if (!selfLearningSettled)
        {
            if (!ReferenceEquals(batterySlot.childItem, selfLearningBattery) ||
                !ReferenceEquals(selfLearningModule.parentInventory, moduleBay)) return;
            if (!MachineryHelper.IsMachineryToggleOn(selfLearningMachine))
            {
                selfLearningSawOff = true;
                return;
            }
            if (!selfLearningSawOff) return;

            int expectedDraw = Math.Max(1, selfLearningMachine.GetTagReadonly(MachineryHelper.MACHINERY_DRAWN_POWER).GetInt());
            bool previousNight = CurrentConverterHelper.isNightCycle;
            try
            {
                // Run the same original callback that EndNight runs for this item.
                // Isolate the machine tick so unrelated fixture stock and stores stay put.
                CurrentConverterHelper.isNightCycle = true;
                selfLearningMachine.onCycleEndSlotItemFunc(selfLearningMachine, selfLearningMachine.parentInventory,
                    selfLearningMachine.GetIncompleteSlotInInventory());
            }
            finally { CurrentConverterHelper.isNightCycle = previousNight; }

            selfLearningFinalEnergy = SelfLearningEnergy(selfLearningBattery);
            selfLearningFinalPerformance = SelfLearningProgress(selfLearningModule);
            Require(selfLearningFinalEnergy == selfLearningInitialEnergy - expectedDraw &&
                selfLearningFinalPerformance > selfLearningInitialPerformance,
                $"original NightService did not advance the module and deduct one charge {expectedDraw}: " +
                $"energy {selfLearningInitialEnergy}->{selfLearningFinalEnergy}, performance {selfLearningInitialPerformance}->{selfLearningFinalPerformance}");
            selfLearningSettled = true;
            selfLearningMachine.ModifyTag(SelfLearningResultTag,
                tag => tag.Enable().SetInt(selfLearningFinalPerformance).SetLong(selfLearningFinalEnergy));
            return;
        }

        if (!ReferenceEquals(selfLearningModule.parentInventory, selfLearningModuleOrigin)) return;
        Require(ReferenceEquals(batterySlot.childItem, selfLearningBattery) && moduleBay.childItems.Count == 0 &&
            SelfLearningEnergy(selfLearningBattery) == selfLearningFinalEnergy &&
            SelfLearningProgress(selfLearningModule) == selfLearningFinalPerformance,
            "removing the module shifted the battery or replayed/mutated the original effect");
        selfLearningHostDone = true;
        Record("PASS workshop-host", $"{SelfLearningMod} original sale debit={selfLearningPrice} once; original NightService " +
            $"performance={selfLearningInitialPerformance}->{selfLearningFinalPerformance} " +
            $"energy={selfLearningInitialEnergy}->{selfLearningFinalEnergy}; original module returned to its stock grid");
    }

    private void TickWorkshopSelfLearningGuest(RoomSelector room, double now)
    {
        var link = room.Session.Native;
        var world = NativeWorld.Guest;
        if (selfLearningDeadline == 0) selfLearningDeadline = now + 105;
        Require(now < selfLearningDeadline, "self-learning guest timed out at stage " + selfLearningGuestStage + " result=" + QaResult(link));
        if (now < selfLearningNext || world?.Ready != true || world.HasPendingWork || QaWaiting(link)) return;
        if (workshopMachineRejoin)
        {
            var machine = FindSelfLearningItem(world, SelfLearningMachineTag);
            if (machine == null || !machine.IsTag(SelfLearningResultTag)) return;
            RequireSelfLearningSprite(machine);
            var module = FindSelfLearningItem(world, SelfLearningModuleTag);
            var battery = FindSelfLearningItem(world, SelfLearningBatteryTag);
            Require(module != null && battery != null && GeneralHelper.IsItemOwned(machine) &&
                ReferenceEquals(module.parentInventory, EmporiumEntry.Instance.invElement) &&
                ReferenceEquals(MachineHelper.GetBatterySlot(machine)?.childItem, battery) &&
                MachineHelper.GetModuleInv(machine)?.childItems.Count == 0 && MachineryHelper.IsMachineryToggleOn(machine),
                "rejoin did not restore the bought machine, original nested battery and returned module");
            RequireSelfLearningResult(machine, module, battery);
            Require(world.AuditProjection(out var rejoinDetail), rejoinDetail);
            Finish("PASS workshop-rejoin " + SelfLearningMod);
            return;
        }

        switch (selfLearningGuestStage)
        {
            case 0:
            {
                var store = PlayerStore.Instance;
                if (store.currentClientInstance?.GetClientBlueprint().identifier != SelfLearningSeller)
                {
                    if (world.HasDialogue) DialogUIManager.Instance.OnAdvancePressed();
                    else if (store.storeState == PlayerStore.StoreState.OPEN && world.QueuedClients > 0 &&
                        store.lockNextBellFlag == 0 && !StoreClientMono.Instance.IsMoving())
                        UnityEngine.Object.FindObjectOfType<StoreBell>().OnClick();
                    selfLearningNext = now + 0.4;
                    return;
                }
                if (!store.isClientArrived || world.PendingMainDialogue) return;
                selfLearningMachine = FindSelfLearningItem(world, SelfLearningMachineTag);
                if (selfLearningMachine == null) return;
                RequireSelfLearningSprite(selfLearningMachine);
                selfLearningNext = now + 0.35;
                break;
            }
            case 1:
            {
                Require(selfLearningMachine.parentInventory == EmporiumEntry.Instance.backInvinvElement &&
                    !GeneralHelper.IsItemOwned(selfLearningMachine), "guest must start with an unowned original counter offer");
                var tablet = NegociationUIManager.Instance;
                tablet.UpdateUI(selfLearningMachine);
                tablet.OpenUI();
                tablet.RefreshUI();
                var buy = (TabletButton)AccessTools.Field(typeof(NegociationUIManager), "primaryButton").GetValue(tablet);
                Require(buy?.Interactable == true, "original self-learning purchase button unavailable");
                selfLearningCash = PlayerStore.Instance.playerCash;
                buy.button.onClick.Invoke();
                Require(QaWaiting(link), "original purchase did not submit to the room");
                break;
            }
            case 2:
                Require(QaResult(link) == (ushort)ShopResult.Applied, "original self-learning purchase was rejected");
                if (!GeneralHelper.IsItemOwned(selfLearningMachine) || PlayerStore.Instance.playerCash >= selfLearningCash) return;
                NegociationUIManager.Instance.CloseUI();
                selfLearningModule = FrontendItem(SelfLearningModuleTag);
                selfLearningBattery = FrontendItem(SelfLearningBatteryTag);
                selfLearningModuleOrigin = (GameGridInventory)selfLearningModule.parentInventory;
                selfLearningModulePose = selfLearningModule.modifiedShape.Clone();
                selfLearningInitialEnergy = SelfLearningEnergy(selfLearningBattery);
                selfLearningInitialPerformance = SelfLearningProgress(selfLearningModule);
                ItemMouseDoubleClickHandler.current.OpenContentAction(selfLearningMachine, new Vector2(450, 250));
                Require(selfLearningMachine.contentWindow.IsVisible(), "guest failed to open the original Workshop machine window");
                MoveSelfLearningItem(link, MachineHelper.GetBatterySlot(selfLearningMachine)?.TryFindOneValidInventorySlot(selfLearningBattery),
                    "battery insertion");
                break;
            case 3:
                Require(QaResult(link) == (ushort)ShopResult.Applied, "original battery insertion was rejected");
                if (!ReferenceEquals(MachineHelper.GetBatterySlot(selfLearningMachine)?.childItem, selfLearningBattery)) return;
                MoveSelfLearningItem(link, MachineHelper.GetModuleInv(selfLearningMachine)?.TryFindOneValidInventorySlot(selfLearningModule),
                    "module insertion");
                break;
            case 4:
                Require(QaResult(link) == (ushort)ShopResult.Applied, "original module insertion was rejected");
                if (!ReferenceEquals(selfLearningModule.parentInventory, MachineHelper.GetModuleInv(selfLearningMachine))) return;
                Require(MachineryHelper.IsMachineryToggleOn(selfLearningMachine), "self-learning machine must begin powered on");
                selfLearningMachine.ToggleSlotItem();
                Require(QaWaiting(link), "original machine toggle did not submit to the room");
                break;
            case 5:
                Require(QaResult(link) == (ushort)ShopResult.Applied, "original power-off was rejected");
                if (MachineryHelper.IsMachineryToggleOn(selfLearningMachine)) return;
                selfLearningMachine.ToggleSlotItem();
                Require(QaWaiting(link), "original power-on did not submit to the room");
                break;
            case 6:
                Require(QaResult(link) == (ushort)ShopResult.Applied, "original power-on was rejected");
                if (!selfLearningMachine.IsTag(SelfLearningResultTag)) return;
                Require(MachineryHelper.IsMachineryToggleOn(selfLearningMachine) &&
                    SelfLearningEnergy(selfLearningBattery) < selfLearningInitialEnergy &&
                    SelfLearningProgress(selfLearningModule) > selfLearningInitialPerformance &&
                    selfLearningMachine.contentWindow.IsVisible(), "guest did not receive growth, energy and the original machine UI");
                RequireSelfLearningResult(selfLearningMachine, selfLearningModule, selfLearningBattery);
                MoveSelfLearningItem(link, selfLearningModuleOrigin.TryInventorySlot(selfLearningModule, 1,
                    selfLearningModulePose, selfLearningModule.modifiedState), "module removal");
                break;
            case 7:
                Require(QaResult(link) == (ushort)ShopResult.Applied, "original module removal was rejected");
                if (!ReferenceEquals(selfLearningModule.parentInventory, selfLearningModuleOrigin)) return;
                Require(ReferenceEquals(MachineHelper.GetBatterySlot(selfLearningMachine)?.childItem, selfLearningBattery) &&
                    MachineHelper.GetModuleInv(selfLearningMachine)?.childItems.Count == 0,
                    "guest nesting changed or original module bay did not empty");
                RequireSelfLearningResult(selfLearningMachine, selfLearningModule, selfLearningBattery);
                Require(world.AuditProjection(out var detail), detail);
                Record("PASS workshop-self-learning-ui", "native negotiation button, native content window, SlotMarker insertion/removal, and native power toggle via the live replicated room");
                Finish("PASS workshop-guest " + SelfLearningMod);
                return;
        }
        selfLearningGuestStage++;
    }

    private static GameItem FindSelfLearningItem(NativeWorld world, string tag) =>
        world.Objects.Values.OfType<GameItem>().SingleOrDefault(item => item.parentInventory != null && item.IsTag(tag));

    private static void MoveSelfLearningItem(ReplicaLink link, SlotMarker position, string action)
    {
        Require(position?.IsValid() == true, "original self-learning inventory has no valid slot for " + action);
        position.TryAcceptOnce(1);
        Require(QaWaiting(link), "original SlotMarker did not submit " + action + " to the room");
    }

    private static int SelfLearningEnergy(GameItem battery) => battery.GetTagReadonly(PowerHelper.POWER_SOURCE_ITEM_ENERGY).GetInt();
    private static int SelfLearningProgress(GameItem module) => module.GetTagReadonly(SelfLearningPerformance).GetInt();

    private static void RequireSelfLearningSprite(GameItem item)
    {
        var mod = ModLoader.Mods.Find(candidate => candidate.Manifest?.ID == SelfLearningMod);
        Require(mod != null && mod.Sprites.TryGetValue("self_learning",out var sprite) &&
            item is GameItemElement element && ReferenceEquals(sprite,element.background.image.sprite),
            "guest did not render the Workshop self-learning machine image");
    }

    private static void RequireSelfLearningResult(GameItem machine, GameItem module, GameItem battery)
    {
        var result = machine.GetTagReadonly(SelfLearningResultTag);
        Require(result.GetInt() == SelfLearningProgress(module) && result.GetLong() == SelfLearningEnergy(battery),
            "guest learned stats or battery charge differ from the original host settlement");
    }
}
#endif
