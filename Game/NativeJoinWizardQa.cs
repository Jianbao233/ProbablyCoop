#if PROBABLY_COOP_GRID_QA
using System;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class CoopPanel
{
    private int joinReviewQaStep, joinReviewQaFrame;

    private void TickJoinReviewQa()
    {
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
                QaJoinRequire(!triedWithDifferences && !reviewConfirmation.gameObject.activeSelf, "cancelled dialog admitted the guest");
                confirmReview.button.onClick.Invoke();
                reviewWindow.Get("join_accept_differences").button.onClick.Invoke();
                QaJoinRequire(triedWithDifferences, "explicit confirmation did not admit the guest");
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
        NativeOffscreenCapture.Capture("join-review-recovered");
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
}
#endif
