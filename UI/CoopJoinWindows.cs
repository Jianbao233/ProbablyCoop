using System;
using System.Collections.Generic;
using System.Linq;
using ProbablyCoop.Core;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;

internal sealed partial class CoopPanel
{
    private const int ConnectionLogLimit = 48;
    private static readonly Color TerminalBackground = new Color32(19, 25, 33, 255);
    private static readonly Color TerminalInk = new Color32(197, 222, 219, 255);
    private static readonly Color ReviewWarning = new Color32(244, 205, 146, 255);
    private readonly Queue<string> connectionLog = new Queue<string>();
    private CustomUIWindow diagnosticsWindow, reviewWindow;
    private CustomUIElement diagnosticSummary, confirmReview;
    private RectTransform reviewConfirmation, mutationRecoveryConfirmation, forcedDayConfirmation;
    private NativeShop.MutationFault pendingMutationRecovery;
    private CustomUIElement forceDay;
    private (NativeShop Shop, int Day)? pendingForcedDay;
    private ScrollRect diagnosticScroll;
    private RoomModManifest shownReview;
    private string previousConnectionState;
    private bool triedWithDifferences, placeWindowsTogether, followDiagnosticTail;

    private string JoinWindowLayer => inMenu ? "menu" : "meta";
    private static string JoinText(string key, params object[] values) => ModHelper.GetLocalized("probably_coop", key, values);

    private void OpenConnectionDetails()
    {
        if (diagnosticsWindow?.IsAlive != true)
        {
            var builder = CustomUIManager.Window("probably_coop_connection", ">_  " + Loc("terminal_details_title"), JoinWindowLayer)
                .SetSize(780, 440).Center().SetDraggable(true).SetCloseOnEscape().OnClose(() =>
                {
                    pendingMutationRecovery = null;
                    pendingForcedDay = null;
                    RestoreReviewPosition();
                });
            builder.AddLabel("", "connection_summary");
            diagnosticSummary = builder.Last;
            diagnosticSummary.text.richText = false;
            StyleLabel(diagnosticSummary, TextAlignmentOptions.Left, TerminalInk, 21);
            builder.BeginPanel(padding: 12, spacing: 8);
            TintPanel(builder, TerminalBackground);
            builder.BeginScrollFlexible(125);
            diagnosticScroll = builder.CurrentContainer.GetComponentInParent<ScrollRect>();
            builder.AddLabel("", "connection_log");
            terminalDetails = builder.Last;
            terminalDetails.text.richText = true;
            StyleLabel(terminalDetails, TextAlignmentOptions.TopLeft, TerminalInk, 19);
            builder.End().End();
            builder.BeginPanel(padding: 12, spacing: 8);
            TintPanel(builder, new Color32(58, 44, 49, 255));
            mutationRecoveryConfirmation = builder.CurrentContainer;
            builder.AddLabel(Loc("mutation_recovery_confirm"));
            StyleLabel(builder.Last, TextAlignmentOptions.Left, ReviewWarning, 19);
            builder.BeginRow(12, TextAnchor.MiddleRight);
            builder.AddFlexibleSpace();
            builder.AddButton(Loc("join_mod_cancel"), () => SetMutationRecoveryPrompt(null), "cancel_mutation_recovery");
            builder.AddButton(Loc("mutation_recovery_resume"), ConfirmMutationRecovery, "confirm_mutation_recovery");
            builder.End().End();
            mutationRecoveryConfirmation.gameObject.SetActive(false);
            builder.BeginPanel(padding: 12, spacing: 8);
            TintPanel(builder, new Color32(58, 44, 49, 255));
            forcedDayConfirmation = builder.CurrentContainer;
            builder.AddLabel(Loc("force_day_confirm_text"));
            StyleLabel(builder.Last, TextAlignmentOptions.Left, ReviewWarning, 19);
            builder.BeginRow(12, TextAnchor.MiddleRight);
            builder.AddFlexibleSpace();
            builder.AddButton(Loc("join_mod_cancel"), () => SetForcedDayPrompt(false), "cancel_forced_day");
            builder.AddButton(Loc("force_day_confirm"), ConfirmForcedDay, "confirm_forced_day");
            builder.End().End();
            forcedDayConfirmation.gameObject.SetActive(false);
            builder.BeginRow(12, TextAnchor.MiddleRight);
            builder.AddFlexibleSpace();
            builder.AddButton(Loc("force_day"), () => SetForcedDayPrompt(true), "force_day"); forceDay = builder.Last;
            builder.AddButton(Loc("day_recovery_exit"), RecoverDay, "recover_day"); recoverDay = builder.Last;
            builder.AddButton(Loc("diag_copy"), CopyConnectionDetails, "copy_issue"); copyIssue = builder.Last;
            builder.End();
            diagnosticsWindow = builder.Show();
        }
        else diagnosticsWindow.Show();
        followDiagnosticTail = true;
        nextTerminalRefresh = 0;
        TickConnectionDetails();
        PlaceConnectionBesideReview();
    }

    private void TickConnectionDetails()
    {
        string stateKey = room.Transport + "/" + room.Status;
        if (previousConnectionState != stateKey)
        {
            previousConnectionState = stateKey;
            if (room.Status is not ("idle" or "local_idle" or "none"))
                AddConnectionEvent("INFO", JoinText("diag_connection_state", room.Transport, room.Status));
        }
        if (diagnosticsWindow?.IsVisible != true) return;
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < nextTerminalRefresh) return;
        nextTerminalRefresh = now + 0.5;
        var state = RoomConnectionStatus.Format(room, joinPhase, joinCounters);
        var issue = CurrentIssue();
        diagnosticSummary.SetText(ConnectionHeading(state.Summary, issue.Code, issue.Advice));
        string body = ConnectionText(state.Details, issue.Code == null ? "" : issue.Context, decorated: true);
        if (terminalDetails.text.text != body)
        {
            bool follow = followDiagnosticTail || diagnosticScroll?.verticalNormalizedPosition <= 0.05f;
            terminalDetails.SetText(body);
            if (follow && diagnosticScroll != null)
            {
                Canvas.ForceUpdateCanvases();
                diagnosticScroll.verticalNormalizedPosition = 0;
            }
            followDiagnosticTail = false;
        }
        bool confirming = pendingMutationRecovery != null && ReferenceEquals(pendingMutationRecovery, mutationFault);
        mutationRecoveryConfirmation.gameObject.SetActive(confirming);
        bool canForce = !inMenu && dayShop?.CanOfferForcedDay == true;
        bool confirmingDay = canForce && pendingForcedDay is { } pending && ReferenceEquals(pending.Shop, dayShop) &&
            pending.Day == StoreStation.GetDayCounter();
        if (!confirmingDay) pendingForcedDay = null;
        forcedDayConfirmation.gameObject.SetActive(confirmingDay);
        forceDay.SetVisible(canForce && !confirmingDay);
        recoverDay.SetVisible(!confirming && (recoveryAttempt != null || mutationFault != null));
        recoverDay.SetText(Loc(mutationFault != null || dayShop?.HasVerifiedDayRecovery == true
            ? "mutation_recovery_resume" : "day_recovery_exit"));
        copyIssue.SetText(Loc(issueCopied ? "diag_copied" : "diag_copy"));
        FitJoinWindow(diagnosticsWindow, 780, 460);
    }

    private void SetForcedDayPrompt(bool show)
    {
        pendingForcedDay = show && dayShop?.CanOfferForcedDay == true ? (dayShop, StoreStation.GetDayCounter()) : null;
        nextTerminalRefresh = 0;
        TickConnectionDetails();
    }

    private void ConfirmForcedDay()
    {
        var request = pendingForcedDay;
        SetForcedDayPrompt(false);
        if (request is not { } pending || !ReferenceEquals(pending.Shop, dayShop)) return;
        var result = pending.Shop.ForceDay(pending.Day);
        if (result == ShopResult.Applied)
        {
            AddConnectionEvent("INFO", Loc("force_day_started"));
            diagnosticsWindow.Hide();
            return;
        }
        var issue = CurrentIssue();
        AddConnectionEvent("WARN", issue.Code == null ? Loc("force_day_stale") : $"[{issue.Code}] {issue.Advice}");
        nextTerminalRefresh = 0;
        TickConnectionDetails();
    }

    private string ConnectionText(string counts, string issue, bool decorated=false)
    {
        var lines = new List<string> { JoinText("diag_build", PlayerStore.VERSION_NUMBER, typeof(CoopPanel).Assembly.GetName().Version, Wire.Version) };
        if (!string.IsNullOrEmpty(counts)) lines.Add(counts);
        if (!string.IsNullOrEmpty(issue)) lines.Add(issue);
        if (connectionLog.Count == 0) lines.Add(Loc("terminal_no_session"));
        else
        {
            lines.Add("");
            foreach (string entry in connectionLog)
            {
                if (!decorated) { lines.Add(entry); continue; }
                string color = entry.Substring(10).StartsWith("ERROR", StringComparison.Ordinal) ? "#FFB5AD" :
                    entry.Substring(10).StartsWith("WARN", StringComparison.Ordinal) ? "#F4CD92" : "#A9D5CB";
                lines.Add($"<color={color}><mspace=0.54em>{entry.Substring(0, 8)}</mspace></color>" +
                    entry.Substring(8).Replace("<", "‹").Replace(">", "›"));
            }
        }
        if (decorated)
            for (int i=0; i<lines.Count && lines[i].Length!=0; i++)
                lines[i] = lines[i].Replace("<", "‹").Replace(">", "›");
        return string.Join("\n", lines);
    }

    private void CopyConnectionDetails()
    {
        var state = RoomConnectionStatus.Format(room, joinPhase, joinCounters);
        var issue = CurrentIssue();
        GUIUtility.systemCopyBuffer = ConnectionHeading(state.Summary, issue.Code, issue.Advice) +
            "\n" + ConnectionText(state.Details, issue.Context);
        issueCopied = true;
        nextTerminalRefresh = 0;
        TickConnectionDetails();
    }

    private string ConnectionHeading(string summary, string code, string advice)
    {
        if (code != null) return $"[{code}] {advice}";
        if (reviewWindow?.IsVisible != true || shownReview == null) return summary;
        return shownReview.Blocked ? $"[SYNC-106] {Loc("join_mod_blocked")}" : Loc("join_mod_can_try");
    }

    private void AddConnectionEvent(string level, string message)
    {
        if (connectionLog.Count == ConnectionLogLimit) connectionLog.Dequeue();
        connectionLog.Enqueue($"{DateTime.Now:HH:mm:ss}  {level}  {message}");
        issueCopied = false;
        nextTerminalRefresh = 0;
        Debug.Log($"[probably_coop] join {level}: {message}");
    }

    internal void RecordModReview(RoomModManifest review)
    {
        if (review.Rows.Length == 0) return;
        AddConnectionEvent(review.Blocked ? "ERROR" : "INFO", (review.Blocked ? "SYNC-106  " : "") +
            JoinText("join_mod_count", review.Rows.Length));
        foreach (var row in review.Rows.Take(24))
        {
            string status = row.Blocker != null ? JoinText(row.Blocker, row.Id) : Loc("join_mod_" + row.Kind);
            string level = row.Blocker != null ? "ERROR" : row.Kind == RoomModManifest.Difference.LocalOnly ? "INFO" : "WARN";
            AddConnectionEvent(level, JoinText("join_diag_mod", row.Id, DiagnosticVersion(row.HostVersion),
                DiagnosticVersion(row.LocalVersion), status));
        }
    }

    private static string DiagnosticVersion(string text)
    {
        if (string.IsNullOrEmpty(text)) return "-";
        return text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '.' or '-' or '_' or '+')
            ? text : "(hidden)";
    }

    internal void RecordModConsent()
    {
        triedWithDifferences = true;
        AddConnectionEvent("WARN", Loc("join_mod_consent"));
    }

    internal void RecordProjectionFailure(Exception error, NativeSyncFailure reason)
    {
        string detail = error is NativeItemProjectionException item
            ? JoinText("join_diag_item", item.Identifier, item.Provider, item.InnerException.GetType().Name)
            : $"{reason}  {error?.GetType().Name}";
        AddConnectionEvent("ERROR", $"{joinFailureCode}  {JoinText("join_diag_load", detail)}");
    }

    internal void ShowModReview(RoomModManifest review, Action accept, Action recheck, Action cancel)
    {
        if (reviewWindow?.IsAlive == true && ReferenceEquals(shownReview, review)) return;
        CloseModReview();
        shownReview = review;
        var builder = CustomUIManager.Window("probably_coop_join_review", Loc("join_mod_title"), JoinWindowLayer)
            .SetSize(900, 520).Center().SetDraggable(true).SetCloseOnEscape().OnClose(cancel);
        builder.BeginPanel(alignment: TextAnchor.UpperLeft, padding: 16, spacing: 7);
        builder.AddLabel(Loc(review.Blocked ? "join_mod_blocked" : "join_mod_can_try"), "mod_review_heading");
        StyleLabel(builder.Last, TextAlignmentOptions.Left, ReviewWarning, 27);
        builder.AddLabel(Loc(review.Blocked ? "join_mod_resolve" : "join_mod_explain"));
        StyleLabel(builder.Last, TextAlignmentOptions.Left, new Color32(208, 195, 177, 255), 19);
        builder.End();
        builder.BeginScrollFlexible(125);
        foreach (var row in review.Rows)
        {
            builder.BeginPanel(padding: 12, spacing: 3);
            TintPanel(builder, new Color32(44, 37, 52, 255));
            builder.BeginRow(10, TextAnchor.MiddleLeft);
            builder.BeginColumn(2);
            var column = builder.CurrentContainer.GetComponent<LayoutElement>() ?? builder.CurrentContainer.gameObject.AddComponent<LayoutElement>();
            column.flexibleWidth = 1;
            builder.AddLabel(row.Name);
            builder.Last.text.richText = false;
            StyleLabel(builder.Last, TextAlignmentOptions.Left, new Color32(236, 222, 198, 255), 22);
            builder.AddLabel(JoinText("join_mod_versions", row.HostVersion ?? "—", row.LocalVersion ?? "—"));
            builder.Last.text.richText = false;
            StyleLabel(builder.Last, TextAlignmentOptions.Left, new Color32(196, 181, 176, 255), 18);
            builder.End();
            builder.BeginColumn(4, TextAnchor.MiddleRight);
            builder.AddLabel(RowStatus(row));
            builder.Last.text.richText = false;
            StyleLabel(builder.Last, TextAlignmentOptions.Right, row.Kind == RoomModManifest.Difference.LocalOnly ?
                new Color32(184, 216, 184, 255) : ReviewWarning, 18);
            if (row.Workshop != 0 && row.Kind != RoomModManifest.Difference.LocalOnly)
                builder.AddButton(Loc("join_mod_workshop"), () => ModWorkshop.OpenItemPage(row.Workshop), "mod_review_workshop_" + row.Id);
            builder.End().End().End();
        }
        builder.End();
        builder.BeginPanel(alignment: TextAnchor.UpperLeft, padding: 12, spacing: 8);
        TintPanel(builder, new Color32(58, 44, 49, 255));
        reviewConfirmation = builder.CurrentContainer;
        builder.AddLabel(Loc("join_mod_warning"));
        StyleLabel(builder.Last, TextAlignmentOptions.Left, ReviewWarning, 19);
        builder.BeginRow(10, TextAnchor.MiddleRight);
        builder.AddFlexibleSpace();
        builder.AddButton(Loc("join_mod_cancel"), () => reviewConfirmation.gameObject.SetActive(false), "join_cancel_differences");
        builder.AddButton(Loc("join_mod_confirm"), accept, "join_accept_differences");
        builder.End().End();
        reviewConfirmation.gameObject.SetActive(false);
        builder.BeginRow(10, TextAnchor.MiddleRight);
        builder.AddButton(Loc("terminal_details_title"), OpenConnectionDetails, "mod_review_diagnostics");
        builder.AddFlexibleSpace();
        builder.AddButton(Loc("join_mod_recheck"), () =>
        {
            AddConnectionEvent("INFO", Loc("join_mod_recheck"));
            recheck();
            CloseModReview();
        }, "mod_review_recheck");
        builder.AddButton(Loc("join_mod_force"), () => reviewConfirmation.gameObject.SetActive(true), "join_force");
        confirmReview = builder.Last;
        confirmReview.SetInteractable(!review.Blocked);
        if (confirmReview.button.image != null) confirmReview.button.image.color = new Color32(163, 221, 210, 255);
        builder.End();
        reviewWindow = builder.Show();
        nextTerminalRefresh = 0;
        FitJoinWindow(reviewWindow, 900, Mathf.Clamp(280 + review.Rows.Length * 76, 375, 560));
        if (diagnosticsWindow?.IsVisible == true) PlaceConnectionBesideReview();
    }

    private static string RowStatus(RoomModManifest.Row row)
    {
        string status = row.Kind == RoomModManifest.Difference.Missing && row.Workshop != 0 && ModWorkshop.IsAvailable
            ? WorkshopInstallStatus(row.Workshop) : Loc("join_mod_" + row.Kind);
        if (row.Blocker != null) return status + "  ·  " + JoinText(row.Blocker, row.Id);
        return status;
    }

    private static string WorkshopInstallStatus(ulong id)
    {
        uint state = SteamUGC.GetItemState(new PublishedFileId_t(id));
        if ((state & 1) == 0) return Loc("join_mod_unsubscribed");
        if ((state & 0x30) != 0 || (state & 4) == 0) return Loc("join_mod_downloading");
        return Loc("join_mod_restart");
    }

    internal void CloseModReview()
    {
        if (reviewWindow?.IsAlive == true)
        {
            if (placeWindowsTogether && diagnosticsWindow?.IsVisible == true) diagnosticsWindow.Center();
            placeWindowsTogether = false;
            reviewWindow.onClose = null;
            reviewWindow.Close();
            nextTerminalRefresh = 0;
        }
        reviewWindow = null;
        shownReview = null;
    }

    private void CloseDiagnosticsWindows()
    {
        CloseModReview();
        if (diagnosticsWindow?.IsAlive == true) diagnosticsWindow.Close();
        diagnosticsWindow = null;
    }

    private static void TintPanel(CustomUIBuilder builder, Color color)
    {
        var image = builder.CurrentContainer.GetComponent<Image>();
        if (image == null) return;
        image.sprite = null;
        image.type = Image.Type.Simple;
        image.color = color;
        image.raycastTarget = false;
    }

    private static void StyleLabel(CustomUIElement element, TextAlignmentOptions align, Color color, float size)
    {
        element.text.alignment = align;
        element.text.color = color;
        element.text.fontSize = size;
    }

    private void PlaceConnectionBesideReview()
    {
        if (reviewWindow?.IsVisible != true || diagnosticsWindow?.IsVisible != true || placeWindowsTogether) return;
        var available = JoinViewport(reviewWindow);
        float wizard = reviewWindow.Rect.rect.width, terminal = diagnosticsWindow.Rect.rect.width;
        const float gap = 24;
        if (available.x < wizard + terminal + gap + 40) return;
        reviewWindow.SetPosition(new Vector2(-(terminal + gap) / 2, reviewWindow.Rect.anchoredPosition.y));
        diagnosticsWindow.SetPosition(new Vector2((wizard + gap) / 2, reviewWindow.Rect.anchoredPosition.y));
        placeWindowsTogether = true;
    }

    private void RestoreReviewPosition()
    {
        if (placeWindowsTogether && reviewWindow?.IsVisible == true) reviewWindow.Center();
        if (placeWindowsTogether && diagnosticsWindow?.IsAlive == true) diagnosticsWindow.Center();
        placeWindowsTogether = false;
    }

    private static void FitJoinWindow(CustomUIWindow window, float width, float height)
    {
        var size = JoinViewport(window);
        var fittedWidth = Mathf.Clamp(size.x - 42, 320, width);
        var fittedHeight = Mathf.Clamp(size.y - 42, 310, height);
        if (window.Rect.sizeDelta != new Vector2(fittedWidth, fittedHeight)) window.SetSize(fittedWidth, fittedHeight);
    }

    private static Vector2 JoinViewport(CustomUIWindow window)
    {
        if (window.Rect.parent is not RectTransform parent) return new Vector2(1920, 1080);
        var canvas = parent.GetComponentInParent<Canvas>()?.rootCanvas;
        if (canvas == null) return parent.rect.size;
        var pixels = canvas.pixelRect;
        var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, pixels.min, camera, out var min) &&
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, pixels.max, camera, out var max))
            return max - min;
        return parent.rect.size;
    }
}
