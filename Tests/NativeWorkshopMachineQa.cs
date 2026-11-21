using System;
using System.Linq;
using HarmonyLib;
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
        if (workshopMachine != SelfLearningMod)
            throw new InvalidOperationException("Unknown Workshop pilot: " + workshopMachine);
        if (authority) TickWorkshopSelfLearningHost(room,now);
        else TickWorkshopSelfLearningGuest(room,now);
    }
}
