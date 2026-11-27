using System;
using System.Collections.Generic;
using System.IO;
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
    private const int TerminalModRows = 24;
    private const string ModJoinResumeFile = "mod-join-resume.txt";
    private bool modJoinResumeLoaded;
    private RoomJoinResume savedModJoin;
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
    private RoomModManifest lastModReview;
    private bool lastModReviewAccepted;
    private readonly List<(RoomModManifest.Row Row, CustomUIElement Label)> workshopReviewLabels = new();
    private double nextWorkshopReviewRefresh;
    private string previousConnectionState;
    private bool placeWindowsTogether, reviewingOpeningChoices;

    private string JoinWindowLayer => inMenu ? "menu" : "overlay";
    private static string JoinText(string key, params object[] values) => ModHelper.GetLocalized("probably_coop", key, values);

    private void OpenConnectionDetails()
    {
        if (diagnosticsWindow?.IsAlive != true)
        {
            var builder = CustomUIManager.Window("probably_coop_connection", ">_  " + Loc("terminal_details_title"), JoinWindowLayer)
                .SetSize(inShop ? 1120 : 780, inShop ? 610 : 440).Center().SetDraggable(true).SetCloseOnEscape().OnClose(() =>
                {
                    pendingMutationRecovery = null;
                    pendingForcedDay = null;
                    RestoreReviewPosition();
                });
            builder.AddLabel("", "connection_summary");
            diagnosticSummary = builder.Last;
            diagnosticSummary.text.richText = false;
            StyleLabel(diagnosticSummary, TextAlignmentOptions.Left, TerminalInk, inShop ? 25 : 21);
            builder.BeginPanel(padding: 12, spacing: 8);
            TintPanel(builder, TerminalBackground, ensureBackground: true);
            builder.BeginScrollFlexible(125);
            diagnosticScroll = builder.CurrentContainer.GetComponentInParent<ScrollRect>();
            builder.AddLabel("", "connection_log");
            terminalDetails = builder.Last;
            terminalDetails.text.richText = true;
            StyleLabel(terminalDetails, TextAlignmentOptions.TopLeft, TerminalInk, inShop ? 23 : 19);
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
            if (inShop)
            {
                var available = JoinViewport(diagnosticsWindow);
                diagnosticsWindow.SetSize(Mathf.Min(1120, available.x - 56), Mathf.Min(610, available.y - 56));
            }
        }
        else diagnosticsWindow.Show();
        diagnosticsWindow.BringToFront();
        nextTerminalRefresh = 0;
        TickConnectionDetails(opening: true);
        PlaceConnectionBesideReview();
    }

    private void TickConnectionDetails(bool opening = false)
    {
        string stateKey = room.Transport + "/" + room.Status;
        if (previousConnectionState != stateKey)
        {
            previousConnectionState = stateKey;
            if (room.Status is not ("idle" or "local_idle" or "none"))
                AddConnectionEvent("INFO", JoinText("diag_connection_state", room.Transport, room.Status));
        }
        double now = Time.realtimeSinceStartupAsDouble;
        TickWorkshopReview(now);
        if (diagnosticsWindow?.IsVisible != true) return;
        if (now < nextTerminalRefresh) return;
        nextTerminalRefresh = now + 0.5;
        var state = RoomConnectionStatus.Format(room, joinPhase, joinCounters);
        var issue = CurrentIssue();
        diagnosticSummary.SetText(ConnectionHeading(state.Summary, issue.Code, issue.Advice));
        string body = ConnectionText(state.Details, issue.Code == null ? "" : issue.Context, decorated: true);
        bool changed = terminalDetails.text.text != body;
        bool followTail = changed && diagnosticScroll?.verticalNormalizedPosition <= 0.05f;
        if (changed) terminalDetails.SetText(body);
        bool confirming = pendingMutationRecovery != null && ReferenceEquals(pendingMutationRecovery, mutationFault);
        mutationRecoveryConfirmation.gameObject.SetActive(confirming);
        bool canForce = !inMenu && dayShop?.CanOfferForcedDay == true;
        bool canRecall = canForce && room.Session?.State != LinkState.Connected &&
            NativeWorld.Host?.CanReturnOfflinePartner == true;
        bool confirmingDay = canForce && !canRecall && pendingForcedDay is { } pending && ReferenceEquals(pending.Shop, dayShop) &&
            pending.Day == StoreStation.GetDayCounter();
        if (!confirmingDay) pendingForcedDay = null;
        forcedDayConfirmation.gameObject.SetActive(confirmingDay);
        forceDay.SetText(Loc(canRecall ? "trip_offline_return" : "force_day"));
        forceDay.SetVisible(canForce && !confirmingDay);
        bool canResumeStoppedRun = inMenu && recoveryExit?.Target != null && !room.IsGuest && !room.Busy;
        recoverDay.SetVisible(!confirming && (recoveryAttempt != null || mutationFault != null || canResumeStoppedRun));
        recoverDay.SetText(Loc(mutationFault != null || dayShop?.HasVerifiedDayRecovery == true || canResumeStoppedRun
            ? "mutation_recovery_resume" : "day_recovery_exit"));
        copyIssue.SetText(Loc(issueCopied ? "diag_copied" : "diag_copy"));
        FitJoinWindow(diagnosticsWindow, 780, 460);
        if (diagnosticScroll != null && (opening || followTail))
        {
            Canvas.ForceUpdateCanvases();
            diagnosticScroll.verticalNormalizedPosition = opening ? 1 : 0;
        }
    }

    private void SetForcedDayPrompt(bool show)
    {
        if (show && room.Session?.State != LinkState.Connected &&
            NativeWorld.Host is { CanReturnOfflinePartner: true } world)
        {
            ShowOfflinePartnerReturn(world);
            return;
        }
        pendingForcedDay = show && dayShop?.CanOfferForcedDay == true ? (dayShop, StoreStation.GetDayCounter()) : null;
        nextTerminalRefresh = 0;
        TickConnectionDetails();
    }

    private void ShowOfflinePartnerReturn(NativeWorld world)
    {
        var confirm = GenericConfirmationUIManager.Instance;
        if (confirm?.panel?.activeSelf != false || world.PrepareOfflinePartnerReturn() is not { } request) return;
        string prompt = JoinText("trip_offline_return_confirm", request.Location.GetTitle());
        if (request.HasGroundItems)
            prompt += "\n\n" + (request.Location.GetLeaveWarning() ?? LocHelper.GetLocalizedMechanic("afterhour_items_lost_warning"));
        confirm.OpenPanel(prompt, Loc("trip_offline_return"), Loc("join_mod_cancel"), () =>
        {
            var result = room.Session?.State == LinkState.Connected || !ReferenceEquals(world, NativeWorld.Host)
                ? ShopResult.Stale : world.ReturnOfflinePartner(request.Proof, request.PeerGeneration);
            string key = result switch
            {
                ShopResult.Applied => "trip_offline_return_done",
                ShopResult.NoSpace => "trip_merchant_no_space",
                ShopResult.Busy => "trip_block_busy",
                _ => "trip_offline_return_stale"
            };
            AddConnectionEvent(result == ShopResult.Applied ? "INFO" : "WARN", Loc(key));
            TickConnectionDetails();
        });
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
        AppendModDiagnostics(lines, decorated);
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
        if (reviewingOpeningChoices) return Loc("opening_mod_review");
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

    internal void RecordModReview(RoomModManifest review, bool accepted = false)
    {
        lastModReview = review;
        lastModReviewAccepted = accepted;
        nextTerminalRefresh = 0;
        issueCopied = false;
        if (review.Rows.Length == 0) return;
        AddConnectionEvent(review.Blocked ? "ERROR" : "INFO", (review.Blocked ? "SYNC-106  " : "") +
            JoinText("join_mod_count", review.Rows.Length));
        foreach (var row in review.Rows)
        {
            string level = row.Blocker != null ? "ERROR" : row.Kind == RoomModManifest.Difference.LocalOnly ? "INFO" : "WARN";
            Debug.Log($"[probably_coop] join {level}: {row.Diagnostic()}");
        }
    }

    private void AppendModDiagnostics(List<string> lines, bool forTerminal)
    {
        if (lastModReview is not { Rows.Length: > 0 } review) return;
        lines.Add(JoinText("join_mod_count", review.Rows.Length));
        if (!forTerminal)
        {
            lines.Add($"mod_review_consent={(lastModReviewAccepted ? "accepted" : "none")}");
            foreach (var row in review.Rows) lines.Add(row.Diagnostic());
            return;
        }
        foreach (var row in review.Rows.Take(TerminalModRows))
            lines.Add(JoinText("join_diag_mod", row.Id, RoomModManifest.DiagnosticVersion(row.HostVersion),
                RoomModManifest.DiagnosticVersion(row.LocalVersion), FormatReviewStatus(row)));
        if (review.Rows.Length > TerminalModRows)
            lines.Add(JoinText("join_diag_full_mods", review.Rows.Length - TerminalModRows));
    }

    internal void RecordModConsent()
    {
        lastModReviewAccepted = true;
        AddConnectionEvent("WARN", Loc("join_mod_consent"));
    }

    internal void RecordProjectionFailure(Exception error, NativeSyncFailure reason)
    {
        string detail = error switch
        {
            NativeItemProjectionException item => JoinText("join_diag_item", item.Identifier, item.Provider,
                item.InnerException.GetType().Name),
            ModExtensionProjectionException extension => JoinText("join_diag_extension", extension.ModId,
                extension.InnerException.GetType().Name),
            ModExtensionJoinException mismatch => JoinText(mismatch.MessageKey, mismatch.ModId),
            _ => $"{reason}  {error?.GetType().Name}"
        };
        AddConnectionEvent("ERROR", $"{joinFailureCode}  {JoinText("join_diag_load", detail)}");
    }

    internal void ShowModReview(RoomModManifest review, Action accept, Action recheck, Action cancel, bool openingChoices = false)
    {
        if (reviewWindow?.IsAlive == true && ReferenceEquals(shownReview, review)) return;
        CloseModReview();
        shownReview = review;
        reviewingOpeningChoices = openingChoices;
        bool canOpenMods = inMenu && room.IsGuest && room.Code.Length != 0 &&
            review.Rows.Any(row => row.Kind is RoomModManifest.Difference.Disabled or RoomModManifest.Difference.LoadFailed ||
                row.LocalOverridesWorkshop || CanShowWorkshopProgress(row));
        var builder = CustomUIManager.Window("probably_coop_join_review",
            Loc(openingChoices ? "opening_mod_review" : "join_mod_title"), JoinWindowLayer)
            .SetSize(900, 520).Center().SetDraggable(true).SetCloseOnEscape().OnClose(cancel);
        builder.BeginPanel(alignment: TextAnchor.UpperLeft, padding: 16, spacing: 7);
        builder.AddLabel(Loc(openingChoices ? "opening_mod_review_heading" :
            review.Blocked ? "join_mod_blocked" : "join_mod_can_try"), "mod_review_heading");
        StyleLabel(builder.Last, TextAlignmentOptions.Left, ReviewWarning, 27);
        builder.AddLabel(Loc(openingChoices ? "opening_mod_review_hint" :
            review.Blocked ? "join_mod_resolve" : "join_mod_explain"));
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
            if (CanShowWorkshopProgress(row))
                workshopReviewLabels.Add((row, builder.Last));
            if (row.Workshop != 0 && row.Kind != RoomModManifest.Difference.LocalOnly)
                builder.AddButton(Loc("join_mod_workshop"), () => OpenWorkshopFromReview(row.Workshop), "mod_review_workshop_" + row.Id);
            builder.End().End().End();
        }
        builder.End();
        if (canOpenMods)
        {
            builder.BeginPanel(alignment: TextAnchor.UpperLeft, padding: 12, spacing: 8);
            builder.AddLabel(Loc("join_mod_open_mods_hint"));
            StyleLabel(builder.Last, TextAlignmentOptions.Left, TerminalInk, 18);
            builder.AddButton(Loc("join_mod_open_mods"), OpenNativeModsFromReview, "mod_review_open_mods");
            builder.End();
        }
        if (!openingChoices)
        {
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
        }
        builder.BeginRow(10, TextAnchor.MiddleRight);
        builder.AddButton(Loc("terminal_details_title"), OpenConnectionDetails, "mod_review_diagnostics");
        if (room.IsGuest && room.Code.Length != 0)
            builder.AddButton(Loc("join_mod_copy_join"), CopyReviewJoinInfo, "mod_review_copy_join");
        builder.AddFlexibleSpace();
        builder.AddButton(Loc("join_mod_recheck"), () =>
        {
            AddConnectionEvent("INFO", Loc("join_mod_recheck"));
            CloseModReview();
            recheck();
        }, "mod_review_recheck");
        if (openingChoices) builder.AddButton(Loc("opening_mod_back"), accept, "opening_mod_back");
        else builder.AddButton(Loc("join_mod_force"), () => reviewConfirmation.gameObject.SetActive(true), "join_force");
        confirmReview = builder.Last;
        confirmReview.SetInteractable(!review.Blocked);
        if (confirmReview.button.image != null) confirmReview.button.image.color = new Color32(163, 221, 210, 255);
        builder.End();
        reviewWindow = builder.Show();
        nextTerminalRefresh = 0;
        int controlsHeight = canOpenMods ? 350 : 280;
        FitJoinWindow(reviewWindow, 900, Mathf.Clamp(controlsHeight + review.Rows.Length * 76, 375, 620));
        if (diagnosticsWindow?.IsVisible == true) PlaceConnectionBesideReview();
    }

    private void TickWorkshopReview(double now)
    {
        if (reviewWindow?.IsVisible != true || workshopReviewLabels.Count == 0 ||
            now < nextWorkshopReviewRefresh) return;
        nextWorkshopReviewRefresh = now + 1;
        foreach (var (row, label) in workshopReviewLabels)
        {
            string status = RowStatus(row);
            if (label.text.text != status) label.SetText(status);
        }
    }

    private void CopyReviewJoinInfo()
    {
        if (reviewWindow?.IsVisible != true || !room.IsGuest || string.IsNullOrEmpty(room.Code)) return;
        GUIUtility.systemCopyBuffer = room.GuestJoinInfo;
        reviewWindow.Get("mod_review_copy_join")?.SetText(Loc("join_mod_join_copied"));
    }

    private void OpenWorkshopFromReview(ulong item)
    {
        if (inMenu && room.IsGuest && room.Code.Length != 0 && reviewWindow?.IsVisible == true)
            RememberModJoin(room.GuestJoinInfo);
        ModWorkshop.OpenItemPage(item);
    }

    private void OpenNativeModsFromReview()
    {
        if (!inMenu || !room.IsGuest || room.Code.Length == 0 ||
            reviewWindow?.IsVisible != true || MainMenuUIController.Instance is not { } menu) return;
        string joinInfo = room.GuestJoinInfo;
        GUIUtility.systemCopyBuffer = joinInfo;
        RememberModJoin(joinInfo);
        CloseModReview();
        room.Leave();
        restoreAfterPartnerPicker = false;
        if (window?.IsAlive == true) window.Hide();
        AddConnectionEvent("INFO", Loc("join_mod_join_copied"));
        menu.OpenModPanel();
    }

    private void RememberModJoin(string joinInfo)
    {
        var resume = RoomJoinResume.Create(joinInfo, DateTime.UtcNow);
        if (resume == null) return;
        modJoinResumeLoaded = true;
        savedModJoin = resume;
        try { ModHelper.WriteModDataFileText(RoomModManifest.OwnId, ModJoinResumeFile, resume.Write()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Debug.LogWarning("[probably_coop] mod join resume could not be saved: " + error.GetType().Name); }
    }

    private RoomJoinResume GetSavedModJoin()
    {
        DateTime now = DateTime.UtcNow;
        if (!modJoinResumeLoaded)
        {
            modJoinResumeLoaded = true;
            try
            {
                string text = ModHelper.ReadModDataFileText(RoomModManifest.OwnId, ModJoinResumeFile);
                savedModJoin = RoomJoinResume.Read(text, now);
                if (text != null && savedModJoin == null) ClearSavedModJoin();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Debug.LogWarning("[probably_coop] mod join resume could not be read: " + error.GetType().Name); }
        }
        if (savedModJoin != null && !savedModJoin.IsCurrent(now)) ClearSavedModJoin();
        return savedModJoin;
    }

    private void ClearSavedModJoin()
    {
        savedModJoin = null;
        try { ModHelper.DeleteModDataFile(RoomModManifest.OwnId, ModJoinResumeFile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Debug.LogWarning("[probably_coop] mod join resume could not be cleared: " + error.GetType().Name); }
    }

    private void ResumeModJoin()
    {
        if (!inMenu || !CanConnect || GetSavedModJoin() is not { } resume) return;
        code.SetString(resume.Code);
        ipv4.RestoreMapping(resume.Mapping ?? "");
        RequestJoin();
    }

    private static bool CanShowWorkshopProgress(RoomModManifest.Row row) => row.Workshop != 0 && !row.LocalOverridesWorkshop &&
        row.Kind is RoomModManifest.Difference.Missing or RoomModManifest.Difference.Version or RoomModManifest.Difference.Files;

    private static string RowStatus(RoomModManifest.Row row) =>
        FormatReviewStatus(row, CanShowWorkshopProgress(row) && ModWorkshop.IsAvailable ? WorkshopInstallStatus(row) : null);

    private static string FormatReviewStatus(RoomModManifest.Row row, string status = null)
    {
        status ??= Loc("join_mod_" + row.Kind);
        if (row.LocalOverridesWorkshop) status += "  ·  " + Loc("join_mod_local_override");
        if (row.Blocker != null) return status + "  ·  " + JoinText(row.Blocker, row.Id);
        return status;
    }

    private static string WorkshopInstallStatus(RoomModManifest.Row row)
    {
        var id = new PublishedFileId_t(row.Workshop);
        var state = (EItemState)SteamUGC.GetItemState(id);
        var progress = RoomModManifest.ReadWorkshopProgress(row.Kind,
            (state & EItemState.k_EItemStateSubscribed) != 0,
            (state & EItemState.k_EItemStateInstalled) != 0,
            pending: (state & EItemState.k_EItemStateDownloadPending) != 0,
            needsUpdate: (state & EItemState.k_EItemStateNeedsUpdate) != 0,
            downloading: (state & EItemState.k_EItemStateDownloading) != 0);
        if (progress == RoomModManifest.WorkshopProgress.Downloading &&
            SteamUGC.GetItemDownloadInfo(id, out ulong received, out ulong total) &&
            RoomModManifest.WorkshopDownloadPercent(received, total) is int percent)
            return JoinText("join_mod_download_percent", percent);
        return progress switch
        {
            RoomModManifest.WorkshopProgress.NotSubscribed => Loc("join_mod_unsubscribed"),
            RoomModManifest.WorkshopProgress.PendingDownload => Loc("join_mod_download_queued"),
            RoomModManifest.WorkshopProgress.Downloading => Loc("join_mod_downloading"),
            RoomModManifest.WorkshopProgress.WaitingForDownload => Loc("join_mod_download_waiting"),
            RoomModManifest.WorkshopProgress.RestartRequired => Loc("join_mod_restart"),
            _ => null
        };
    }

    internal void CloseOpeningModReview()
    {
        if (reviewingOpeningChoices) CloseModReview();
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
        reviewingOpeningChoices = false;
        workshopReviewLabels.Clear();
        nextWorkshopReviewRefresh = 0;
    }

    private void CloseDiagnosticsWindows()
    {
        CloseModReview();
        if (diagnosticsWindow?.IsAlive == true) diagnosticsWindow.Close();
        diagnosticsWindow = null;
    }

    private static void TintPanel(CustomUIBuilder builder, Color color, bool ensureBackground = false)
    {
        var image = builder.CurrentContainer.GetComponent<Image>();
        if (image == null && ensureBackground) image = builder.CurrentContainer.gameObject.AddComponent<Image>();
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
