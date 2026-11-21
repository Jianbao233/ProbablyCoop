using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProbablyCoop;

// The next runtime tick leaves a faulted cooperative native scene. It does not
// call EscapeUI/PlayerStore.Reset, SaveGame, or mutate a save path. This avoids
// keeping a partly executed world open behind hundreds of input-specific guards.
internal static class NativeActorFaultRecovery
{
    private static bool returning;

    // Call before room.Tick, native capture and any new gameplay command.
    // true means this frame was consumed by the recovery transition.
    internal static bool TryReturnToMenu(Action closeNetwork, Action<int?> reportStop)
    {
        if (returning && SceneManager.GetActiveScene().name == "EmporiumMenu") returning = false;
        var session = NativeActorSession.Current;
        if (session == null || !session.Faulted || session.Terminal || returning) return false;
        var scene = SceneManager.GetActiveScene();
        if (scene.name != "InventoryScene" || NativeGuest.Active || !PlayerStore.HasInstance ||
            !ReferenceEquals(PlayerStore.Instance, session.Store) || !ReferenceEquals(EmporiumEntry.Instance, session.Carry.Source) ||
            !Guid.TryParse(session.Store.runID, out var run) || run != session.Run.Run || session.Store.saveSlotId < 0 ||
            session.Store.saveSlotId != session.NativeSaveSlot ||
            (!session.CreatedFromArmedRun && !CoopRun.IsRegistered(session.Store))) return false;
        if (closeNetwork == null) throw new ArgumentNullException(nameof(closeNetwork));
        // Update is the synchronous boundary: do not invalidate an executing
        // callback or night from within its own stack.
        if (session.Activities.HasExecuting || session.NightActive) return true;

        returning = true;
        var failures = new List<Exception>();
        string reason = session.Failure ?? "合作状态未能完成一致性校验。";
        if (reason.Length > 1200) reason = reason.Substring(0, 1200);
        reportStop(session.VerifiedCheckpoint?.Day);
        CoopRun.Armed = false;
        try
        {
            // The nightly report can still be on a separate UI canvas when the
            // store scene is frozen. Hide it before loading the native menu.
            Try(() =>
            {
                var calendar = AdvCalendarUIManager.Instance;
                if (calendar != null && calendar.UIPanel != null) calendar.UIPanel.SetActive(false);
            }, failures);
            // Freeze only the verified failing InventoryScene. DontDestroyOnLoad
            // runtime and unrelated scenes are not touched. No close-button or
            // dialogue cancellation action is invoked while discarding this run.
            foreach (var root in scene.GetRootGameObjects())
                if (root != null) Try(() => root.SetActive(false), failures);
            Try(closeNetwork, failures);
            Try(session.Dispose, failures);
            // Same no-save singleton reset ordering as NativeGuest.ReturnToMenu.
            Try(EmporiumEntry.ResetData, failures);
            Try(PlayerStore.ResetData, failures);
            Try(StoreStation.ResetData, failures);
            if (NewGameData.Instance != null) Try(NewGameData.HardReset, failures);
            SceneManager.LoadScene("EmporiumMenu", LoadSceneMode.Single);
            Debug.LogError("[probably_coop] cooperative run stopped without saving: " + reason);
            foreach (var failure in failures) Debug.LogError("[probably_coop] recovery cleanup: " + failure);
            return true;
        }
        catch
        {
            // A missing native menu scene is a runtime/platform failure. Leave
            // the current scene frozen; never resume a partially mutated run.
            returning = false;
            throw;
        }
    }

    internal static void Reset() => returning = false;
    private static void Try(Action action, List<Exception> failures)
    { try { action(); } catch (Exception error) { failures.Add(error); } }
}
