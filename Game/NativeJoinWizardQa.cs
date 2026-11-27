#if PROBABLY_COOP_GRID_QA
using System;
using System.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class CoopPanel
{
    private int joinReviewQaStep, joinReviewQaFrame;

    private void TickJoinReviewQa()
    {
        if (NativeBackgroundQa.Enabled && NativeBackgroundQa.IsGuestProduct && NativeJoinWizardQa.TabletSmoke &&
            reviewWindow?.IsAlive == true && shownReview != null && joinReviewQaStep == 0)
        {
            var differences = shownReview.Rows.Where(row => row.Kind != RoomModManifest.Difference.LocalOnly).ToArray();
            QaJoinRequire(!shownReview.Blocked && differences.Length == 1 &&
                differences[0].Id == "probably_coop_tablet_qa_relay" &&
                differences[0].Kind == RoomModManifest.Difference.Missing,
                "Tablet QA encountered an unapproved mod difference.");
            confirmReview.button.onClick.Invoke();
            reviewWindow.Get("join_accept_differences").button.onClick.Invoke();
            QaJoinRequire(lastModReviewAccepted, "Tablet QA confirmation did not admit the guest.");
            joinReviewQaStep = 3;
            return;
        }
        if (!NativeBackgroundQa.Enabled || !NativeBackgroundQa.IsGuestProduct || !NativeJoinWizardQa.Enabled ||
            reviewWindow?.IsAlive != true || joinReviewQaStep >= 3) return;
        if (joinReviewQaFrame == 0) { joinReviewQaFrame = Time.frameCount; return; }
        if (Time.frameCount - joinReviewQaFrame < 3) return;
        switch (joinReviewQaStep)
        {
            case 0:
                QaJoinRequire(connectionLog.Count > 0 && shownReview.Rows.Length > 0, "manifest did not reach the native review");
                QaJoinRequire(confirmReview.button.interactable != NativeJoinWizardQa.Blocked, "force affordance did not match preflight");
                QaJoinRequire(reviewWindow.Get("mod_review_workshop_qa_missing_expansion")?.button != null,
                    "published item did not get the game's Workshop entry point");
                NativeOffscreenCapture.Capture(NativeJoinWizardQa.Blocked ? "join-review-blocked" : "join-review");
                reviewWindow.Get("mod_review_diagnostics").button.onClick.Invoke();
                joinReviewQaStep++;
                joinReviewQaFrame = Time.frameCount;
                break;
            case 1:
                QaJoinRequire(diagnosticsWindow?.IsVisible == true && diagnosticsWindow.root != reviewWindow.root,
                    "connection details did not open as a separate native window");
                QaJoinRequire(terminalDetails.text.text.Contains("qa_missing_expansion"), "terminal did not show the compared mod");
                QaJoinRequire(!ConnectionText("", "").Contains("PSL-"), "diagnostic exposed the room code");
                NativeOffscreenCapture.Capture("join-review-terminal");
                diagnosticsWindow.Close();
                if (NativeJoinWizardQa.Blocked)
                {
                    joinReviewQaStep = 3;
                    reviewWindow.Close();
                    Debug.Log("[probably_coop QA] PASS join-review blocked extension prevented force entry");
                    return;
                }
                reviewWindow.Get("mod_review_recheck").button.onClick.Invoke();
                joinReviewQaStep++;
                joinReviewQaFrame = Time.frameCount;
                break;
            case 2:
                QaJoinRequire(confirmReview.button.interactable && connectionLog.Count > 0, "recheck lost consent affordance or diagnostics");
                confirmReview.button.onClick.Invoke();
                QaJoinRequire(reviewConfirmation.gameObject.activeSelf, "warning did not open before confirmation");
                reviewWindow.Get("join_cancel_differences").button.onClick.Invoke();
                QaJoinRequire(!lastModReviewAccepted && !reviewConfirmation.gameObject.activeSelf, "cancelled dialog admitted the guest");
                confirmReview.button.onClick.Invoke();
                reviewWindow.Get("join_accept_differences").button.onClick.Invoke();
                QaJoinRequire(lastModReviewAccepted, "explicit confirmation did not admit the guest");
                string copied = ConnectionText("", "");
                QaJoinRequire(copied.Contains("id=qa_missing_expansion host=1.4") && copied.Contains("mod_review_consent=accepted"),
                    "copy-ready diagnostics did not include the review and recorded consent");
                Debug.Log("[probably_coop QA] PASS join-review native recheck, cancel and explicit force consent");
                joinReviewQaStep++;
                break;
        }
    }

    internal void VerifyJoinFailureQa()
    {
        OpenConnectionDetails();
        QaJoinRequire(terminalDetails.text.text.Contains("water_jug") && terminalDetails.text.text.Contains("SYNC-105"),
            "failed apply did not retain the safe item identifier and error code");
        QaJoinRequire(ConnectionText("", "").Contains("id=qa_missing_expansion host=1.4"),
            "failed apply lost the reviewed Mod differences after returning to the menu");
        NativeOffscreenCapture.Capture("join-review-recovered");
    }

    internal string QaOpenNativeModificationsFromOpening()
    {
        opening.QaOpenModReview();
        QaJoinRequire(reviewingOpeningChoices && reviewWindow?.IsVisible == true &&
            reviewWindow.Get("opening_mod_back")?.button != null && reviewWindow.Get("join_force") == null,
            "the opening Mod review did not offer a separate way back to the opening choices");
        NativeOffscreenCapture.Capture("qa-opening-mod-review");
        string originalClipboard = GUIUtility.systemCopyBuffer;
        try
        {
            string roomCode = room.Code;
            reviewWindow.Get("mod_review_open_mods").button.onClick.Invoke();
            string copied = GUIUtility.systemCopyBuffer;
            QaJoinRequire(ModUIManager.Instance.panel.activeInHierarchy && !room.IsGuest &&
                copied == roomCode, "native Mod setup did not open or copy the current room");
            return copied;
        }
        finally { GUIUtility.systemCopyBuffer = originalClipboard; }
    }

    internal void QaRejoinOpening(string copied)
    {
        MainMenuUIController.Instance.CloseModPanel();
        savedModJoin = null;
        modJoinResumeLoaded = false;
        Open();
        QaJoinRequire(savedModJoin?.Code == copied && resumeModJoin.root.activeInHierarchy,
            "reopening the lobby did not restore the previous room without clipboard access");
        resumeModJoin.button.onClick.Invoke();
        QaJoinRequire(room.Busy || room.IsGuest || joinRequestedFrame.HasValue,
            "the saved room control did not reconnect");
    }

    internal void QaConfirmOfflinePartnerReturn()
    {
        OpenConnectionDetails();
        var action = diagnosticsWindow.Get("force_day");
        var confirm = GenericConfirmationUIManager.Instance;
        QaJoinRequire(action?.button?.interactable == true && NativeWorld.Host?.CanReturnOfflinePartner == true,
            "the offline partner action is not present in connection details");
        action.button.onClick.Invoke();
        QaJoinRequire(confirm.panel.activeSelf, "the offline partner action did not open the original confirmation");
        NativeOffscreenCapture.Capture("qa-independent-offline-recall");
        confirm.CancelButtonPressed();
        QaJoinRequire(NativeWorld.Host?.CanReturnOfflinePartner == true, "cancelling offline recall changed the partner's visit");
        action.button.onClick.Invoke();
        QaJoinRequire(confirm.panel.activeSelf, "the cancelled offline recall did not reopen");
        confirm.ConfirmButtonPressed();
    }

    private static void QaJoinRequire(bool result, string reason)
    {
        if (!result) throw new InvalidOperationException(reason);
    }
}

internal static class NativeJoinWizardQa
{
    private static readonly string[] Arguments = Environment.GetCommandLineArgs();
    internal static bool Enabled => Array.IndexOf(Arguments, "--probably-coop-qa-join-review") >= 0;
    internal static bool Blocked => Array.IndexOf(Arguments, "--probably-coop-qa-join-blocked") >= 0;
    internal static bool LateFailure => Array.IndexOf(Arguments, "--probably-coop-qa-join-failure") >= 0;
    internal static bool TabletSmoke => Array.IndexOf(Arguments, "--probably-coop-qa-tablet-smoke") >= 0;
    internal static bool OpeningHandoff => Array.IndexOf(Arguments, "--probably-coop-qa-opening-mod-handoff") >= 0;
    internal static RoomModManifest.Entry OpeningProvider => new RoomModManifest.Entry("qa_opening_optional", "Opening Mod QA", "1", 0);
}
#endif
