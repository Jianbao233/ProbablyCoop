#if PROBABLY_COOP_GRID_QA
using System;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Api;
using ProbablyCoop.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool roomReloadQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-room-reload") >= 0;
    private readonly bool reloadAfterDisconnect = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-reload-disconnected") >= 0;
    private readonly bool reloadRejoinedGuest = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-reload-rejoined") >= 0;
    private readonly bool salvageAudioQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-salvage-audio") >= 0;
    private CoopRun.Checkpoint reloadQaCheckpoint;
    private CoopSession reloadQaConnection;
    private string reloadQaRoom;
    private int reloadQaScene, reloadQaCash, reloadQaDay;
    private bool reloadQaSawUnsaved;
    private int reloadQaWaterStage;
    private bool reloadQaBlenderClicked, reloadQaBlenderDone, reloadQaCosmeticDone;
    private GameItem reloadQaHostLabelTarget;
    private bool reloadQaLabelSelected;
    private bool reloadQaLabelOpen;
    private string reloadQaLabelCondition;
    private bool reloadQaLabelsDone;
    private int reloadQaFallbacks;
    private const string WaterSource = "QA liquid source";
    private const string WaterTarget = "QA liquid target";
    private const string AudioBlender = "QA audio blender";
    private const string AudioLabeler = "QA audio labeler";
    private const string AudioLabelTarget = "QA audio label target";
    private const string WaterLabelCategory = "CATEGORY_WATER_PURITY";
    internal const string QaCosmeticTag = "QA-local-sprite-choice";
    private GameItem reloadQaBlender;
    private const string MissingPortrait = "probably_coop.qa_missing_portrait";
    private const int FixtureDay = 9, FixtureCash = 180;
    private const string SalvageFixture = "QA broken machine";
    private static readonly string[] SalvageTools = { "screwdriver", "wire_cutter", "welder" };
    private GameItem salvageHostMachine;
    private NativeWorld salvageGuestWorld;
    private int salvageStage;
    private bool salvageToolSelected;
    private double salvageRestoreAt;

    private void TickRoomReloadHost(RoomSelector room, CoopPanel panel, double now)
    {
        Require(now - started < 95, "room reload did not finish within the isolated test window");
        if (stage == 0)
        {
            reloadQaCheckpoint = CoopRun.ListCheckpoints().Single();
            reloadQaCash = PlayerStore.Instance.playerCash;
            reloadQaScene = SceneManager.GetActiveScene().handle;
            reloadQaRoom = room.Code;
            Record("TRACE reload-source", $"day={reloadQaCheckpoint.Day} cash={reloadQaCash}");
            ModHelper.WriteModDataFileText("probably_coop", "qa-room.txt", room.Code);
            stage = 1;
        }
        if (StoryQa != StoryFixture.None) { TickStoryHost(room, now); return; }
        if (CustomerArrivalQa) { TickCustomerArrivalHost(room, now); return; }
        if (salvageAudioQa) { TickSalvageAudioHost(room, panel, now); return; }
        if (keycardAudioQa) { TickKeycardAudioHost(room, now); return; }
        if (reloadAfterDisconnect) { TickReloadAfterDisconnect(room, panel); return; }
        if (room.Session?.State != LinkState.Connected) return;
        if (stage == 1)
        {
            if (room.Session.Native.FullRevision == 0 || NativeWorld.Host?.PartnerSetupRequiredForQa != false) return;
            reloadQaConnection = room.Session;
            AdvCalendarUIManager.Instance?.CloseUI();
            panel.QaOpenInShopPanel();
            next = now + 3;
            stage = 2;
        }
        if (stage == 3)
        {
            if (reloadQaHostLabelTarget != null)
            {
                var ui = LabelerUIManager.Instance;
                ChangeReloadLabel(ui);
                ui.CloseUI();
                reloadQaHostLabelTarget.FindItemFeatureByCategory(WaterLabelCategory).ExposeFeature(playBad: true);
                InspectionUIManager.Instance.OnConfrontFailed();
                reloadQaHostLabelTarget = null;
            }
            if (!reloadQaBlender.state.dict.ContainsKey(QaCosmeticTag) && BlenderInventory(reloadQaBlender, "input").childItems.Count == 0)
                reloadQaBlender.state.GetTag(QaCosmeticTag).SetEnabled(true).SetString("blender");
        }
        if (now < next) return;
        if (stage == 2)
        {
            NativeOffscreenCapture.Capture("qa-room-reload-panel");
            panel.QaOpenConnectionDetails();
            NativeOffscreenCapture.Capture("qa-room-reload-connection");
            CustomUIManager.Instance.CloseWindow("probably_coop_connection");
            PlayerStore.Instance.ModCash(17, playSound: false);
            if (!EmporiumEntry.IsWaterTapUnlocked()) NetworkUpgrade.GetUpgradeById("RUSTWATER_TAP").Unlock();
            SeedReloadWater("small_bottled_water", WaterSource, fill: true);
            SeedReloadWater("water_jug", WaterTarget, fill: false);
            reloadQaBlender = SeedReloadBlender();
            SeedReloadEditorAudio();
            NativeWorld.Host.QaPublishPortrait(MissingPortrait);
            next = now + 12;
            stage = 3;
            return;
        }
        if (stage == 3)
        {
            if (!panel.QaChooseWorldReload(reloadQaCheckpoint)) return;
            stage = 4;
            return;
        }
        VerifyReloadRoom(room);
        if (room.Session.WorldGeneration == 0 || room.Session.Native.FullRevision == 0) return;
        VerifyRestoredCheckpoint();
        if (stage == 4)
        {
            Record("PASS host in-room checkpoint reload", "original checkpoint UI confirmation, unchanged room and disk image");
            next = now + 3;
            stage = 5;
        }
        else Finish("PASS host runtime");
    }

    private void TickReloadAfterDisconnect(RoomSelector room, CoopPanel panel)
    {
        if (stage == 1)
        {
            if (room.Session?.State != LinkState.Connected || room.Session.Native.FullRevision == 0 ||
                NativeWorld.Host?.PartnerSetupRequiredForQa != false) return;
            Require(reloadQaCheckpoint.Day == FixtureDay && reloadQaCash == FixtureCash, "unexpected disconnect fixture");
            reloadQaConnection = room.Session;
            AdvCalendarUIManager.Instance?.CloseUI();
            PlayerStore.Instance.ModCash(17, playSound: false);
            stage = 2;
            return;
        }
        VerifyReloadRoom(room);
        if (stage == 2)
        {
            if (room.Session.State != LinkState.Waiting || !panel.QaChooseWorldReload(reloadQaCheckpoint)) return;
            stage = 3;
            return;
        }
        if (stage == 3)
        {
            Require(room.Session.State == LinkState.Waiting && room.Session.WorldGeneration == 0,
                "unattended reload unexpectedly admitted a guest or reused the departed guest's generation");
            VerifyRestoredCheckpoint();
            Record("PASS host waiting-room checkpoint reload", "lost peer, same room, original confirmation and unchanged checkpoint file");
            stage = 4;
        }
        if (stage == 4 && room.Session.State == LinkState.Connected && room.Session.Native.FullRevision != 0)
        {
            Record("PASS host room after restored rejoin", "same room published the recovered shop to a fresh connection");
            done = true;
        }
    }

    private void VerifyReloadRoom(RoomSelector room) =>
        Require(ReferenceEquals(reloadQaConnection, room.Session) && room.Code == reloadQaRoom,
            "the host recreated the room during reload");

    private void VerifyRestoredCheckpoint()
    {
        Require(SceneManager.GetActiveScene().handle != reloadQaScene &&
            PlayerStore.Instance.playerCash == reloadQaCash && StoreStation.Instance.dayCounter == reloadQaCheckpoint.Day,
            "native scene did not rehydrate the durable checkpoint");
        byte[] image = NativeActorPersistence.ReadFileImage(new ES3Settings(reloadQaCheckpoint.File).FullPath);
        Require(CoopRun.Digest(image).AsSpan().SequenceEqual(reloadQaCheckpoint.ImageDigest), "the chosen checkpoint was rewritten");
    }

    private static void SeedReloadWater(string id, string name, bool fill)
    {
        var item = DirectoryMaster.Item(id);
        if (fill) WaterHelper.FillWithPureWater(item);
        else WaterHelper.EmptyContainer(item);
        PlaceReloadFixture(item, name);
    }

    private static GameInventory BlenderInventory(GameItem machine, string role) =>
        (machine.contentWindow.childElement as MachineryPanelRow)?.GetPanel(role)?.content is MachineryIntake intake ?
            intake.content as GameInventory : null;

    private static GameItem SeedReloadBlender()
    {
        var machine = DirectoryMaster.Item("blender");
        var bottle = DirectoryMaster.Item("bottled_water");
        var food = DirectoryMaster.Item("dream_cap");
        WaterHelper.EmptyContainer(bottle);
        GeneralHelper.SetItemOwned(bottle, true);
        GeneralHelper.SetItemOwned(food, true);
        Require(GraphUtils.TryAcceptAll(BlenderInventory(machine, "container"), bottle) == 0 &&
            GraphUtils.TryAcceptAll(BlenderInventory(machine, "input"), food) == 0, "could not place blender ingredients");
        PlaceReloadFixture(machine, AudioBlender);
        return machine;
    }

    private static void PlaceReloadFixture(GameItem item, string name)
    {
        GeneralHelper.SetItemOwned(item, true);
        GeneralHelper.SetCustomName(item, name);
        var entry = EmporiumEntry.Instance;
        foreach (var grid in new[] { entry.backInvinvElement, entry.invElement, entry.showcaseElement })
        {
            var slot = grid.TryFindOneValidInventorySlot(item);
            if (slot?.IsValid() != true || slot.targetItem != null) continue;
            Require(slot.TryAcceptOnce() == item.unitCount, "could not place reload fixture " + name);
            return;
        }
        throw new InvalidOperationException("no free slot for reload fixture " + name);
    }

    private void TickRoomReloadGuest(RoomSelector room, double now)
    {
        if (StoryQa != StoryFixture.None) { TickStoryGuest(room, now); return; }
        if (CustomerArrivalQa) { TickCustomerArrivalGuest(now); return; }
        if (salvageAudioQa) { TickSalvageAudioGuest(now); return; }
        if (keycardAudioQa) { TickKeycardAudioGuest(now); return; }
        Require(now - started < 95, "guest did not rehydrate the replacement world");
        if (NativeWorld.Guest?.Ready != true || room.Session?.State != LinkState.Connected) return;
        var store = PlayerStore.Instance;
        if (reloadAfterDisconnect)
        {
            if (reloadRejoinedGuest)
            {
                Require(StoreStation.Instance.dayCounter == FixtureDay && store.playerCash == FixtureCash &&
                    store.saveSlotId < 0 && room.Session.WorldGeneration == 0, "the fresh join restored the wrong checkpoint");
                Require(NativeWorld.Guest.AuditProjection(out var projection), projection);
                Record("PASS guest rejoined restored room", "fresh process loaded the recovered state with no original save slot");
                done = true;
            }
            else if (!reloadQaSawUnsaved && store.playerCash == FixtureCash + 17)
            {
                reloadQaSawUnsaved = true;
                Record("PASS guest saw unsaved cash before disconnect", "the fixture supervisor can now terminate this peer");
            }
            return;
        }
        if (reloadQaConnection == null)
        {
            reloadQaConnection = room.Session;
            reloadQaRoom = room.Code;
            reloadQaDay = StoreStation.Instance.dayCounter;
            reloadQaCash = store.playerCash;
            reloadQaScene = SceneManager.GetActiveScene().handle;
            ModHelper.OnMessage("probably_coop", CoopExtensionApi.CustomerAppearanceChannel, ObserveQaAppearance);
        }
        Require(ReferenceEquals(reloadQaConnection, room.Session) && room.Code == reloadQaRoom,
            "guest performed another room join during restore");
        if (room.Session.WorldGeneration == 0)
        {
            reloadQaSawUnsaved |= store.playerCash == reloadQaCash + 17;
            TickReloadWater();
            if (reloadQaWaterStage == 4) TickReloadBlender();
            if (reloadQaBlenderDone) TickReloadEditorAudio();
            return;
        }
        Require(reloadQaSawUnsaved && reloadQaWaterStage == 4 && reloadQaBlenderDone && reloadQaCosmeticDone && reloadQaLabelsDone &&
            NativeWorld.Guest.QaLiquidCues == 0 && NativeWorld.Guest.QaMachineCues == 0 &&
            NativeWorld.Guest.QaLabelCues == 0 && NativeWorld.Guest.QaInspectionVictoryCues == 0 &&
            NativeWorld.Guest.QaInspectionScamCues == 0 && NativeWorld.Guest.QaInspectionFailedCues == 0,
            "guest did not complete the original actions before reload, or replayed a historical cue");
        ModHelper.OffMessage("probably_coop", CoopExtensionApi.CustomerAppearanceChannel, ObserveQaAppearance);
        Require(reloadQaFallbacks == 1, "missing cosmetic portrait did not fall back or the local appearance event was duplicated");
        Record("PASS guest portrait fallback", "unknown portrait key used the original resolver and emitted one local appearance event");
        Require(SceneManager.GetActiveScene().handle != reloadQaScene && StoreStation.Instance.dayCounter == reloadQaDay &&
            store.playerCash == reloadQaCash && store.saveSlotId < 0, "guest did not resume the restored day and cash safely");
        Require(NativeWorld.Guest.AuditProjection(out var detail), detail);
        Record("PASS guest in-room checkpoint reload", "automatic native scene restoration in the same room with no replay of prior audio");
        done = true;
    }

    private void TickSalvageAudioHost(RoomSelector room, CoopPanel panel, double now)
    {
        if (room.Session?.State != LinkState.Connected || room.Session.Native.FullRevision == 0 ||
            NativeWorld.Host?.PartnerSetupRequiredForQa != false) return;
        if (stage == 1)
        {
            reloadQaConnection = room.Session;
            AdvCalendarUIManager.Instance?.CloseUI();
            salvageHostMachine = PreBuiltItemHelper.CreateBrokenAlarm();
            PlaceReloadFixture(salvageHostMachine, SalvageFixture);
            foreach (var id in SalvageTools) PlaceReloadFixture(DirectoryMaster.Item(id), "QA salvage " + id);
            stage = 2;
            return;
        }
        if (stage == 2 && salvageHostMachine.parentInventory == null)
        {
            Require(salvageHostMachine.GetTagReadonly(MachineBrokenHelper.BROKEN_MACHINE_SCREW_COUNT).GetInt() == 0 &&
                salvageHostMachine.GetTagReadonly(MachineBrokenHelper.BROKEN_MACHINE_WIRE_COUNT).GetInt() == 0 &&
                salvageHostMachine.GetTagReadonly(MachineBrokenHelper.BROKEN_MACHINE_WELD_COUNT).GetInt() == 0,
                "host did not run all three native salvage operations");
            Record("PASS host machine salvage", "all three original tools changed the real machine; waiting before restoring");
            next = now + 4;
            stage = 3;
        }
        if (stage == 3 && now >= next)
        {
            panel.QaOpenInShopPanel();
            if (panel.QaChooseWorldReload(reloadQaCheckpoint)) stage = 4;
        }
        if (stage == 4 && room.Session.WorldGeneration != 0)
        {
            VerifyReloadRoom(room);
            VerifyRestoredCheckpoint();
            Record("PASS host reloaded after machine salvage", "the original room and checkpoint remained active");
            done = true;
        }
    }

    private void TickSalvageAudioGuest(double now)
    {
        Require(now - started < 80, "guest did not complete the machine salvage audio check");
        var world = NativeWorld.Guest;
        if (world?.Ready != true) return;
        if (salvageGuestWorld != null)
        {
            if (ReferenceEquals(world, salvageGuestWorld)) return;
            Require(world.QaMachineSalvageCues == 0, "salvage sounds replayed on checkpoint restore");
            if (salvageRestoreAt == 0) salvageRestoreAt = now;
            if (now - salvageRestoreAt >= 2 && StoreStation.Instance.dayCounter == FixtureDay &&
                PlayerStore.Instance.playerCash == FixtureCash)
                Finish("PASS guest machine salvage audio and reload");
            return;
        }
        var stock = world.Objects.Values.OfType<GameItem>().ToArray();
        var machine = stock.FirstOrDefault(item => GeneralHelper.GetCustomName(item) == SalvageFixture);
        if (salvageStage == SalvageTools.Length)
        {
            if (machine != null || world.QaMachineSalvageCues < salvageStage) return;
            Require(world.QaMachineSalvageCues == salvageStage, "salvage cues replayed");
            Record("PASS guest original salvage audio", "each tool played exactly once and the machine was removed");
            salvageGuestWorld = world;
            return;
        }
        if (machine == null || world.QaMachineSalvageCues < salvageStage) return;
        if (salvageStage >= 1 && machine.GetTagReadonly(MachineBrokenHelper.BROKEN_MACHINE_SCREW_COUNT).GetInt() != 0) return;
        if (salvageStage >= 2 && machine.GetTagReadonly(MachineBrokenHelper.BROKEN_MACHINE_WIRE_COUNT).GetInt() != 0) return;
        Require(world.QaMachineSalvageCues == salvageStage, "unexpected number of salvage audio cues");
        var toolName = "QA salvage " + SalvageTools[salvageStage];
        var tool = stock.FirstOrDefault(item => GeneralHelper.GetCustomName(item) == toolName);
        if (tool == null) return;
        if (!salvageToolSelected)
        {
            ItemSelectHandler.current.TrySelectItem(tool);
            salvageToolSelected = true;
            return;
        }
        if (!tool.MayTarget(machine) || !tool.CanTarget(machine)) return;
        tool.Target(machine);
        ItemSelectHandler.current.EndSelect();
        salvageToolSelected = false;
        salvageStage++;
    }

    private void ObserveQaAppearance(string sender, object payload)
    {
        if (sender == "probably_coop" && payload is CoopCustomerAppearance { PortraitKey: MissingPortrait, Restoring: false } &&
            StoreClientMono.Instance?.image?.sprite != null)
            reloadQaFallbacks++;
    }

    private void SeedReloadEditorAudio()
    {
        PlaceReloadFixture(DirectoryMaster.Item("labeler"), AudioLabeler);
        var bottle = DirectoryMaster.Item("small_bottled_water");
        WaterHelper.FillWithPureWater(bottle);
        PlaceReloadFixture(bottle, AudioLabelTarget);
        LabelerUIManager.Instance.OpenUI(bottle);
        reloadQaHostLabelTarget = bottle;
    }

    private static string ChangeReloadLabel(LabelerUIManager ui)
    {
        var row = ui.verticalLayoutGroup.GetComponentsInChildren<LabelerElement>().Single(item => item.GetCategory() == WaterLabelCategory);
        row.dropdown.SetValueWithoutNotify((row.dropdown.value + 1) % row.dropdown.options.Count);
        row.OnDropdownValueChanged();
        return row.GetSelectedConditionIdentifier();
    }

    private void TickReloadEditorAudio()
    {
        var world = NativeWorld.Guest;
        Require(world.QaLabelCues <= 2 && world.QaInspectionVictoryCues <= 1 &&
            world.QaInspectionScamCues <= 1 && world.QaInspectionFailedCues <= 1, "an editor or inspection cue repeated");
        if (reloadQaLabelsDone) return;
        if (world.QaLabelCues < 1 || world.QaInspectionVictoryCues == 0 ||
            world.QaInspectionScamCues == 0 || world.QaInspectionFailedCues == 0) return;
        var stock = world.Objects.Values.OfType<GameItem>();
        var bottle = stock.SingleOrDefault(item => GeneralHelper.GetCustomName(item) == AudioLabelTarget);
        var labeler = stock.SingleOrDefault(item => GeneralHelper.GetCustomName(item) == AudioLabeler);
        if (bottle == null || labeler == null) return;
        var ui = LabelerUIManager.Instance;
        if (reloadQaLabelCondition == null)
        {
            Require(world.QaLabelCues == 1, "opening the host editor emitted an extra label cue");
            if (!reloadQaLabelOpen)
            {
                if (!reloadQaLabelSelected)
                {
                    ItemSelectHandler.current.TrySelectItem(labeler);
                    reloadQaLabelSelected = true;
                    return;
                }
                if (!labeler.MayTarget(bottle) || !labeler.CanTarget(bottle)) return;
                labeler.Target(bottle);
                ItemSelectHandler.current.EndSelect();
                Require(ui.panel.activeSelf, "native guest labeler did not open");
                reloadQaLabelOpen = true;
                return;
            }
            reloadQaLabelCondition = ChangeReloadLabel(ui);
            return;
        }
        if (world.QaLabelCues != 2 || bottle.FindItemFeatureByCategory(WaterLabelCategory).fakeCondition?.identifier != reloadQaLabelCondition) return;
        ui.CloseUI();
        reloadQaLabelsDone = true;
        Record("PASS guest label and inspection audio", "host and guest original label selections each played once; victory, expose and negative confrontation native callbacks each played once");
    }

    private void TickReloadWater()
    {
        if (reloadQaWaterStage == 4) return;
        var world = NativeWorld.Guest;
        var stock = world.Objects.Values.OfType<GameItem>();
        var source = stock.FirstOrDefault(item => GeneralHelper.GetCustomName(item) == WaterSource);
        var target = stock.FirstOrDefault(item => GeneralHelper.GetCustomName(item) == WaterTarget);
        if (source == null || target == null) return;
        int cues = world.QaLiquidCues;
        switch (reloadQaWaterStage)
        {
            case 0: source.Target(target); break;
            case 1:
                if (cues < 1 || WaterHelper.GetTotalVolume(target) == 0 || !WaterHelper.IsEmpty(source)) return;
                var faucet = EmporiumEntry.Instance.faucet;
                if (faucet == null || !EmporiumEntry.IsWaterTapUnlocked()) return;
                source.Target(faucet);
                break;
            case 2:
                if (cues < 2 || WaterHelper.IsEmpty(source)) return;
                var drain = EmporiumEntry.Instance.drain;
                if (drain == null) return;
                source.Target(drain);
                break;
            case 3:
                if (cues < 3 || !WaterHelper.IsEmpty(source)) return;
                Require(cues == 3,"liquid actions played duplicate audio");
                Record("PASS guest native water cues", "pour, faucet and drain via native item entry and authenticated room; one original audio cue each");
                break;
        }
        reloadQaWaterStage++;
    }

    private void TickReloadBlender()
    {
        var world = NativeWorld.Guest;
        if (reloadQaBlenderDone)
        {
            Require(world.QaMachineCues == 1, "the blender start repeated without another click");
            var painted = world.Objects.Values.OfType<GameItemElement>().FirstOrDefault(item =>
                item.identifier == "blender" && GeneralHelper.GetCustomName(item) == AudioBlender &&
                item.state.dict.ContainsKey(QaCosmeticTag));
            if (!reloadQaCosmeticDone && painted != null)
            {
                Require(painted.spritePath == "blender_b" &&
                    painted.background.image.sprite == RenderHandler.LoadFromAtlas("Items/items_tool2", "blender"),
                    "tag-only delta did not refresh the original local sprite resolver");
                Record("PASS guest tag-only sprite refresh", "replicated tag changed local art through ResolveItemSprite; the underlying atlas key remained unchanged");
                reloadQaCosmeticDone = true;
            }
            return;
        }
        var machine = world.Objects.Values.OfType<GameItem>().FirstOrDefault(item =>
            item.identifier == "blender" && GeneralHelper.GetCustomName(item) == AudioBlender);
        if (machine == null) return;
        var container = BlenderInventory(machine, "container")?.childItems.SingleOrDefault();
        var input = BlenderInventory(machine, "input");
        if (container == null || input == null) return;
        if (!reloadQaBlenderClicked)
        {
            if (input.childItems.Count != 1) return;
            var button = NativeMachineControls.Read(machine).OfType<MachineryButton>().Single();
            AccessTools.Method(typeof(MachineryButton), "OnSelect").Invoke(button, null);
            reloadQaBlenderClicked = true;
        }
        else if (input.childItems.Count == 0 && WaterHelper.GetTotalVolume(container) > 0 && world.QaMachineCues > 0)
        {
            Require(world.QaMachineCues == 1, "the original blender start played more than once");
            Record("PASS guest native blender audio", "original guest button, host production, replica liquid and one local blender cue");
            reloadQaBlenderDone = true;
        }
    }
}

[HarmonyPatch(typeof(GameItemElement), "ResolveItemSprite")]
internal static class NativeRoomReloadSpriteQa
{
    private static bool Prepare() =>
        Environment.GetCommandLineArgs().Contains("--probably-coop-qa-room-reload");

    private static void Postfix(GameItemElement __instance, ref Sprite __result)
    {
        if (NativeBackgroundQa.Enabled && NativeGuest.Active && __instance.identifier == "blender" &&
            __instance.state.dict.ContainsKey(NativeBackgroundQa.QaCosmeticTag))
            __result = RenderHandler.LoadFromAtlas("Items/items_tool2", "blender");
    }
}
#endif
