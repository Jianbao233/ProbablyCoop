using System;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

// Carry the user's co-op choice through the original scenario/perk/difficulty
// screens. Arm save creation only when the original flow actually starts.
internal static class CoopNewGameFlow
{
    private static MainMenuUIController menu;
    private static NewGameData request;
    private static Action<MainMenuUIController,NewGameData> pairedStart;
    internal static bool SelectingPerks => menu != null && request == NewGameData.Instance && NewGamePerksUI.IsOpen;

    internal static bool Begin(MainMenuUIController controller, Action<MainMenuUIController,NewGameData> beginPaired = null)
    {
        if (IsSetupOpen(controller)) return false;
        controller.OnNewGameClick();
        if (!NewGameSpecializationUI.IsOpen) return false;
        menu = controller;
        request = NewGameData.Instance;
        pairedStart = beginPaired;
        return true;
    }

    internal static void CancelIfClosed()
    {
        if (menu == null || request != NewGameData.Instance || !IsSetupOpen(menu)) Cancel();
    }

    internal static void Cancel()
    {
        menu = null;
        request = null;
        pairedStart = null;
    }

    private static bool IsSetupOpen(MainMenuUIController controller) =>
        NewGameSpecializationUI.IsOpen || NewGamePerksUI.IsOpen || NewGameDifficultyUI.IsOpen ||
        controller.confirmPanelUIManager?.panel?.activeInHierarchy == true;

    [HarmonyPatch(typeof(MainMenuUIController), nameof(MainMenuUIController.OnNewGameClick))]
    private static class ResetSelection
    {
        private static void Prefix() => Cancel();
    }

    [HarmonyPatch(typeof(MainMenuUIController), nameof(MainMenuUIController.OpenPerkInterface))]
    private static class PrepareHostOpeningPicker
    {
        private static void Postfix(MainMenuUIController __instance)
        {
            if (menu == __instance && pairedStart != null && SelectingPerks)
                NativeActorPartnerSetup.ConfigureHostOpeningPicker();
        }
    }

    [HarmonyPatch(typeof(MainMenuUIController), nameof(MainMenuUIController.OnPerkFinishedClick))]
    private static class StartSelectedCoopGame
    {
        private static bool Prefix(MainMenuUIController __instance)
        {
            var draft = request;
            var paired = pairedStart;
            bool isCoop = menu != null && __instance == menu && draft != null && draft == NewGameData.Instance && draft.saveSlot < 0;
            Cancel();
            if (!isCoop) return true;
            if (paired == null)
            {
                CoopRun.Armed = true;
                return true;
            }
            try { paired(__instance, draft); }
            catch (Exception error)
            {
                Debug.LogWarning("[probably_coop] paired opening rejected: " + error.Message);
                __instance.confirmPanelUIManager.OpenPanel(error.Message, () => { }, () => { });
            }
            return false;
        }
    }
}
