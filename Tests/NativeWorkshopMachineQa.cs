using System;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly string workshopMachine = ReadWorkshopMachineArgument();
    private readonly bool workshopMachineRejoin = Environment.GetCommandLineArgs().Contains("--probably-coop-qa-workshop-rejoin");
    private readonly bool workshopApproval = Environment.GetCommandLineArgs().Contains("--probably-coop-qa-workshop-approval");
    private double workshopApprovalNext;
    private string workshopApprovalObserved;
    private bool workshopCheckedMod;
    private double workshopStarted;
    private const string TowerMod = "hydroponic_tower";
    private const string TowerBottleTag = "QA_WORKSHOP_TOWER_BOTTLE";
    private GameItem workshopTower, workshopTowerBottle;
    private GameSlotInventory workshopTowerWaterSlot;
    private bool workshopTowerMoved, workshopTowerHostVerified;

    private static string ReadWorkshopMachineArgument()
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args,"--probably-coop-qa-workshop-machine");
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // Open the mod browser and select the pilot for the test. The harness
    // supplies input to the game's original warning and approval buttons.
    private void TickWorkshopApproval(bool inMenu,double now)
    {
        if (!inMenu || now < workshopApprovalNext || workshopMachine == null) return;
        workshopApprovalNext = now + 0.6;
        var mod = ModLoader.Mods.Find(candidate => candidate.Manifest?.ID == workshopMachine);
        if (mod == null) return;
        if (ModLoader.IsModEnabled(workshopMachine))
        {
            Debug.Log("[probably_coop QA] PASS workshop-approved " + workshopMachine);
            done = true;
            return;
        }
        var menu = MainMenuUIController.Instance;
        var modUi = ModUIManager.Instance;
        if (modUi?.panel?.activeInHierarchy != true)
        {
            if (modUi != null && menu?.moddingButton?.activeInHierarchy == true)
                menu.OpenModPanel();
            return;
        }
        if (ModWarningUI.IsOpen)
        {
            ReportWorkshopButton("warning",FindWorkshopButton(GameObject.Find("mod_warning_backdrop"),"button_confirm"));
            return;
        }
        if (ModEnablePromptUI.IsOpen)
        {
            var prompt = GameObject.Find("mod_enable_prompt_backdrop");
            ReportWorkshopButton("confirm",FindWorkshopButton(prompt,"button_activate"));
            return;
        }
        var selected = AccessTools.Field(typeof(ModUIManager),"selectedModID")?.GetValue(modUi) as string;
        var diagnostic = $"mod panel visible, selected={selected ?? "(none)"}";
        if (selected != workshopMachine)
        {
            var row = modUi.panel.GetComponentsInChildren<ModElement>().FirstOrDefault(element => element.ModID == workshopMachine);
            diagnostic += $", matching row={row != null}, warning={ModWarningUI.IsOpen}";
            if (workshopApprovalObserved != diagnostic)
            {
                workshopApprovalObserved = diagnostic;
                Debug.Log("[probably_coop QA] TRACE workshop-approval " + diagnostic);
            }
            if (row != null && !ModWarningUI.IsOpen) modUi.OnModElementClicked(workshopMachine,row);
            return;
        }
        ReportWorkshopButton("review",FindWorkshopButton(modUi.panel,"button_review") ??
            FindWorkshopButton(modUi.panel,"button_activate"));
    }

    private static Button FindWorkshopButton(GameObject root,string name)
    {
        if (root == null) return null;
        return root.GetComponentsInChildren<Button>().FirstOrDefault(button => button.gameObject.name == name ||
            button.transform.parent?.name == name);
    }

    private static void ReportWorkshopButton(string key,Button button)
    {
        if (button == null || !button.isActiveAndEnabled || !button.IsInteractable()) return;
        ReportWorkshopRect(key,button.transform as RectTransform);
    }

    private static void ReportWorkshopRect(string key,RectTransform rect)
    {
        if (rect == null || rect.rect.width <= 1 || rect.rect.height <= 1) return;
        var canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
        var camera = canvas?.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas?.worldCamera;
        Vector2 point = RectTransformUtility.WorldToScreenPoint(camera,rect.TransformPoint(rect.rect.center));
        if (point.x < 0 || point.x >= Screen.width || point.y < 0 || point.y >= Screen.height) return;
        Debug.Log($"[probably_coop QA] TRACE workshop-approval-button {key} {Mathf.RoundToInt(point.x)} {Screen.height-1-Mathf.RoundToInt(point.y)} {Screen.width} {Screen.height}");
    }

    private void TickWorkshopMachine(RoomSelector room, double now, bool authority)
    {
        if (!workshopCheckedMod)
        {
            var mod = ModLoader.Mods.SingleOrDefault(candidate => candidate.Manifest?.ID == workshopMachine);
            Require(mod?.IsEnabled == true && mod.Assembly != null, "Workshop pilot is missing or inactive: " + workshopMachine);
            Require(ModHelper.IsModRunning(workshopMachine), "Workshop pilot was not activated: " + workshopMachine);
            Record("PASS workshop-active",workshopMachine + " " + mod.Manifest.ModVersion);
            workshopCheckedMod = true;
            workshopStarted = now;
        }
        Require(now-workshopStarted < 135, "Workshop pilot timed out: " + workshopMachine);
        switch (workshopMachine)
        {
            case SelfLearningMod:
                if (authority) TickWorkshopSelfLearningHost(room,now);
                else TickWorkshopSelfLearningGuest(room,now);
                break;
            case TowerMod:
                if (authority) TickWorkshopTowerHost();
                else TickWorkshopTowerGuest(room);
                break;
            default:
                throw new InvalidOperationException("Unknown Workshop pilot: " + workshopMachine);
        }
    }

    private void TickWorkshopTowerHost()
    {
        if (workshopTower == null)
        {
            workshopTower = DirectoryMaster.Item(TowerMod);
            Require(workshopTower?.contentWindow != null, "Workshop tower factory did not build its original window");
            var shop = EmporiumEntry.Instance;
            Require(GraphUtils.TryAcceptAll(shop.invElement, workshopTower) == 0 ||
                GraphUtils.TryAcceptAll(shop.backInvinvElement, workshopTower) == 0, "tower does not fit in the shop");
            var root = (GridPixelElement)workshopTower.contentWindow.childElement;
            var switches = (GridPixelElement)root.GetElement(0, 3);
            Require(switches.GetElement(0, 0).GetType().Assembly != typeof(SelectableElement).Assembly,
                "fixture did not exercise a third-party control");
            var plant = DirectoryMaster.Item("hydroponic");
            Require(GraphUtils.TryAcceptAll((GameGridInventory)root.GetElement(0, 1), plant) == 0,
                "tower work grid rejected the original hydroponic machine");
            workshopTowerWaterSlot = MachineHydroponic.GetWaterSlot(plant);
            workshopTowerBottle = SeedFrontendItem("bottled_water", TowerBottleTag);
            Record("TRACE workshop-fixture", TowerMod + " custom switch row, nested native water slot and bottle");
            return;
        }

        if (workshopTowerHostVerified || !ReferenceEquals(workshopTowerWaterSlot.childItem, workshopTowerBottle)) return;
        workshopTowerHostVerified = true;
        Record("PASS workshop-host", TowerMod + " guest moved the bottle into the original nested water slot");
    }

    private void TickWorkshopTowerGuest(RoomSelector room)
    {
        var world = NativeWorld.Guest;
        if (world?.Ready != true || world.HasPendingWork || QaWaiting(room.Session.Native)) return;
        var tower = world.Objects.Values.OfType<GameItemElement>()
            .SingleOrDefault(item => item.parentInventory != null && item.identifier == TowerMod);
        if (tower?.contentWindow == null) return;

        var root = (GridPixelElement)tower.contentWindow.childElement;
        var row = (GridPixelElement)root.GetElement(0, 3);
        for (int i = 0; i < 4; i++)
        {
            var control = row.GetElement(i, 0) as MachineryButton;
            Require(control?.isDisabled == true && world.TryData(control, out var data) && data["controlToken"] == null,
                $"unsupported switch {i} must be disabled and have no authority token");
            // Select invokes the native callback even for disabled controls when
            // called directly. The placeholder must never acquire that callback.
            control.Select();
            Require(!QaWaiting(room.Session.Native), $"unsupported switch {i} submitted an action");
        }
        var workGrid = (GameGridInventory)root.GetElement(0, 1);
        var plant = workGrid.childItems.Single(item => item.IsTag("HYDROPONIC_MACHINE_TAG"));
        var slot = MachineHydroponic.GetWaterSlot(plant);
        var bottle = FindSelfLearningItem(world, TowerBottleTag);
        Require(slot != null && bottle != null, "native nested item or slot was lost during projection");

        if (workshopMachineRejoin || workshopTowerMoved)
        {
            if (!workshopMachineRejoin)
                Require(QaResult(room.Session.Native) == (ushort)ShopResult.Applied, "native nested slot request failed");
            if (!ReferenceEquals(slot.childItem, bottle)) return;
            Require(world.AuditProjection(out var detail), detail);
            Finish((workshopMachineRejoin ? "PASS workshop-rejoin " : "PASS workshop-guest ") + TowerMod);
            return;
        }

        tower.contentWindow.Show();
        Require(tower.contentWindow.IsVisible(), "guest could not open the mixed machine window");
        var target = slot.TryFindOneValidInventorySlot(bottle);
        Require(target?.IsValid() == true, "native nested slot did not accept the bottle");
        target.TryAcceptOnce(1);
        Require(QaWaiting(room.Session.Native), "native nested slot did not submit to the room");
        workshopTowerMoved = true;
    }
}
