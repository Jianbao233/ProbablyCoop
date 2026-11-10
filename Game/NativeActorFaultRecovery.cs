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
    private static string pendingNotice;
    private static CustomUIWindow notice;

    // Call before room.Tick, native capture and any new gameplay command.
    // true means this frame was consumed by the recovery transition.
    internal static bool TryReturnToMenu(Action closeNetwork)
    {
        ShowPendingNotice();
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
        pendingNotice = "合作局已停止，已返回主菜单。\n\n" + reason + "\n\n" +
            (session.VerifiedCheckpoint == null
                ? "本次合作局尚无已确认的存档；请重新新建合作局。未确认的操作不保留。"
                : "请从“继续合作局”返回最近成功保存的合作检查点，未确认的当日操作不保留。最近已确认：第 " +
                    session.VerifiedCheckpoint.Day + " 天。");
        CoopRun.Armed = false;
        try
        {
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

    private static void ShowPendingNotice()
    {
        if (SceneManager.GetActiveScene().name != "EmporiumMenu") return;
        returning = false;
        if (pendingNotice == null || CustomUIManager.Instance == null) return;
        var builder = CustomUIManager.Window("probably_coop_failed_run", "合作局已安全停止", "menu")
            .SetSize(700, 350).Center().SetDraggable(true).SetCloseOnEscape();
        builder.AddLabel(pendingNotice);
        if (builder.Last.text != null) builder.Last.text.richText = false;
        builder.AddButton("知道了", () => notice?.Close());
        notice = builder.Show();
        notice.BringToFront();
        pendingNotice = null;
    }

    internal static void Reset()
    {
        if (notice?.IsAlive == true) notice.Close();
        notice = null; pendingNotice = null; returning = false;
    }
    private static void Try(Action action, List<Exception> failures)
    { try { action(); } catch (Exception error) { failures.Add(error); } }
}
