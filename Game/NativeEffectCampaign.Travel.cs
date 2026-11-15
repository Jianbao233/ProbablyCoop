using System;
using System.Collections.Generic;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    internal const string TransitionWindowId = "probably_coop_transition";

    private CustomUIWindow transitionWindow;
    private ulong shownRevision;
    private byte shownReadyMask;
    private int shownBackpack;
    private bool shownGroundWarning;
    private ulong cancelledRevision;
    private TeamTransition invitedTrip;
    private ulong invitedRevision;
    private List<(GameItem, Action)> invitedChoices;

    private byte OtherActor => host ? NetSession.GuestPeer : SyncHost.HostPeer;
    internal int OtherBackpack => host ? Session.GuestBackpack : Session.HostBackpack;

    internal void RequestTransition(TeamTransitionKind kind, string location = "", GameItem backpack = null)
    {
        if (kind == TeamTransitionKind.Sleep) CancelDepartureForNight();
        var action = new TeamTransition(kind, StoreStation.GetDayCounter(), location);
        ulong revision = Session.Proposal.Equals(action) ? Session.ProposalRevision : 0;
        SubmitTransition(action, backpack, revision);
    }

    private void SubmitTransition(TeamTransition action, GameItem backpack, ulong revision)
    {
        var result = backpack != null && !inventory.CanInteract(backpack) ? TeamVoteFailure.BackpackUnavailable :
            Session.TryVote(action, backpack?.uniqueId ?? 0, revision);
        Debug.Log($"[probably_coop TRIP] role={(host ? "host" : "guest")} day={action.Day} " +
            $"action={action.Kind} destination={action.Destination} backpack={backpack?.uniqueId ?? 0} " +
            $"invitation={revision} result={result}");
        ShowVoteFailure(result);
    }

    private static void ShowVoteFailure(TeamVoteFailure failure)
    {
        if (failure == TeamVoteFailure.None) return;
        Debug.Log($"[probably_coop TRIP] vote rejected: {failure}");
        var ui = StoreUIManager.Instance;
        if (ui != null) ui.Notify(TripText("transition_" + failure));
    }

    private void UpdateTransitionUi()
    {
        ShowVoteFailure(Session.TakeVoteFailure());
        var proposal = Session.Proposal;
        ulong revision = Session.ProposalRevision;
        if (Busy || Session.Host?.Closed == true || Session.Guest?.Closed == true || revision == 0)
        {
            CloseTransitionUi();
            return;
        }
        if (invitedRevision != 0)
        {
            if (invitedRevision == revision && InvitedBackpackSelectionOpen) return;
            ClearInvitedBackpackSelection(close: true);
        }
        if (cancelledRevision == revision) return;

        byte mask = Session.ReadyMask;
        bool localReady = (mask & (host ? 1 : 2)) != 0;
        var tablet = EndOfDayUIManager.Instance;
        if (proposal.Kind == TeamTransitionKind.Sleep && tablet != null && tablet.IsOpen)
        {
            if (!localReady)
            {
                transitionWindow?.Hide();
                return;
            }
            tablet.CloseUI();
        }
        bool groundWarning = proposal.Kind == TeamTransitionKind.Return && HasItemsOnGround();
        int backpack = OtherBackpack;
        if (transitionWindow?.IsAlive == true && shownRevision == revision &&
            shownReadyMask == mask && shownBackpack == backpack && shownGroundWarning == groundWarning)
        {
            if (!transitionWindow.IsVisible) transitionWindow.Show();
            return;
        }

        if (proposal.Kind == TeamTransitionKind.Depart) TransitUI.Close();
        CloseTransitionWindow();
        shownRevision = revision;
        shownReadyMask = mask;
        shownBackpack = backpack;
        shownGroundWarning = groundWarning;
        string description = proposal.Kind switch
        {
            TeamTransitionKind.Depart => string.Format(TripText("transition_depart"),
                AfterhourLocation.Get(proposal.Destination)?.GetTitle() ?? proposal.Destination),
            TeamTransitionKind.Return => TripText("transition_return"),
            _ => TripText("transition_sleep"),
        };
        var builder = CustomUIManager.Window(TransitionWindowId, TripText("title"), "meta")
            .SetSize(660, groundWarning ? 350 : 300).Center().SetCloseOnEscape()
            .OnClose(() => CancelTransition(proposal, revision))
            .AddLabel(description, "destination")
            .AddLabel(NativeDayUi.ReadinessText(mask, !host), "readiness");
        if (proposal.Kind == TeamTransitionKind.Depart && (mask & (host ? 2 : 1)) != 0)
        {
            string bagName = backpack == 0 ? TripText("transition_no_bag") :
                inventory.TryFindAccessible(backpack, OtherActor, out var bag) ? bag.GetDisplayName() :
                TripText("transition_bag_selected");
            builder.AddLabel(string.Format(TripText("transition_peer_bag"), bagName), "peer_bag");
        }
        if (groundWarning) builder.AddWarning(LeaveWarning(), "ground_warning");
        if (mask == 3) builder.AddLabel(TripText("transition_both_ready"));
        if (!localReady)
        {
            string caption = proposal.Kind switch
            {
                TeamTransitionKind.Depart => "transition_choose_bag",
                TeamTransitionKind.Return => "transition_confirm_return",
                _ => "transition_open_night",
            };
            builder.AddButton(TripText(caption), () => ConfirmTransition(proposal, revision, groundWarning), "confirm");
        }
        builder.AddButton(TripText("transition_cancel"), () => CancelTransition(proposal, revision), "cancel");
        transitionWindow = builder.Show();
    }

    private void ConfirmTransition(TeamTransition action, ulong revision, bool warned)
    {
        if (Session.ProposalRevision != revision || !Session.Proposal.Equals(action))
        {
            ShowVoteFailure(TeamVoteFailure.Stale);
            return;
        }
        switch (action.Kind)
        {
            case TeamTransitionKind.Depart:
                var shop = EmporiumEntry.Instance;
                StoreResponse.ShowAfterhourResponse();
                invitedTrip = action;
                invitedRevision = revision;
                invitedChoices = shop.choices;
                transitionWindow.Hide();
                break;
            case TeamTransitionKind.Return:
                if (!warned && HasItemsOnGround()) UpdateTransitionUi();
                else SubmitTransition(action, null, revision);
                break;
            case TeamTransitionKind.Sleep:
                EndOfDayUIManager.Instance.OpenUI();
                transitionWindow.Hide();
                break;
        }
    }

    internal bool FinishInvitedBackpackSelection()
    {
        if (invitedRevision == 0) return false;
        var action = invitedTrip;
        ulong revision = invitedRevision;
        var backpack = EmporiumEntry.Instance.selectedBackpack;
        ClearInvitedBackpackSelection(close: false);
        SubmitTransition(action, backpack, revision);
        return true;
    }

    private bool InvitedBackpackSelectionOpen
    {
        get
        {
            var shop = EmporiumEntry.Instance;
            return shop != null && ReferenceEquals(shop.choices, invitedChoices) &&
                shop.responseWindowClosable?.IsVisible() == true;
        }
    }

    private void ClearInvitedBackpackSelection(bool close)
    {
        if (close && invitedRevision != 0 && InvitedBackpackSelectionOpen)
            EmporiumEntry.Instance.HideResponseWindow();
        invitedTrip = default;
        invitedRevision = 0;
        invitedChoices = null;
    }

    private void CancelTransition(TeamTransition action, ulong revision)
    {
        var result = Session.CancelProposal(action, revision);
        ShowVoteFailure(result);
        if (result != TeamVoteFailure.None) return;
        cancelledRevision = revision;
        ClearInvitedBackpackSelection(close: true);
        transitionWindow?.Hide();
    }

    internal void CancelDepartureForNight()
    {
        if (Session.Proposal.Kind == TeamTransitionKind.Depart && cancelledRevision != Session.ProposalRevision)
            CancelTransition(Session.Proposal, Session.ProposalRevision);
        if (!TransitUI.IsOpen) return;
        WithdrawDeparture();
        TransitUI.Close();
    }

    internal void CloseTransitionUi()
    {
        ClearInvitedBackpackSelection(close: true);
        CloseTransitionWindow();
        cancelledRevision = 0;
    }

    private void CloseTransitionWindow()
    {
        if (transitionWindow?.IsAlive == true)
        {
            transitionWindow.onClose = null;
            transitionWindow.Close();
        }
        transitionWindow = null;
    }

    private static bool HasItemsOnGround() => EmporiumEntry.Instance.afterhourInventory.childItems.Count != 0;

    private static string LeaveWarning()
    {
        try
        {
            string warning = MapUIManager.Instance.Current?.GetLeaveWarning();
            if (!string.IsNullOrEmpty(warning)) return warning;
        }
        catch (Exception error)
        {
            Debug.LogWarning($"[probably_coop TRIP] leave warning: {error.Message}");
        }
        return LocHelper.GetLocalizedMechanic("afterhour_items_lost_warning");
    }

    private static string TripText(string key) => ModHelper.GetLocalized("probably_coop", key);
}
