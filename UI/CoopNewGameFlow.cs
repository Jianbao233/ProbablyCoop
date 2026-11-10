using HarmonyLib;

namespace ProbablyCoop;

// Carry the user's co-op choice through the original scenario/perk/difficulty
// screens. Arm save creation only when the original flow actually starts.
internal static class CoopNewGameFlow
{
    private static MainMenuUIController menu;
    private static NewGameData request;

    internal static bool Begin(MainMenuUIController controller)
    {
        if (IsSetupOpen(controller)) return false;
        controller.OnNewGameClick();
        if (!NewGameSpecializationUI.IsOpen) return false;
        menu = controller;
        request = NewGameData.Instance;
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
    }

    private static bool IsSetupOpen(MainMenuUIController controller) =>
        NewGameSpecializationUI.IsOpen || NewGamePerksUI.IsOpen || NewGameDifficultyUI.IsOpen ||
        controller.confirmPanelUIManager?.panel?.activeInHierarchy == true;

    [HarmonyPatch(typeof(MainMenuUIController), nameof(MainMenuUIController.OnNewGameClick))]
    private static class ResetSelection
    {
        private static void Prefix() => Cancel();
    }

    [HarmonyPatch(typeof(MainMenuUIController), nameof(MainMenuUIController.OnPerkFinishedClick))]
    private static class StartSelectedCoopGame
    {
        private static void Prefix(MainMenuUIController __instance)
        {
            if (menu != null && __instance == menu && request != null && request == NewGameData.Instance && request.saveSlot < 0)
                CoopRun.Armed = true;
            Cancel();
        }
    }
}
