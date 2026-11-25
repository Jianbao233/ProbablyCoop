using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

// A new game draft belongs to an already authenticated room epoch. The host
// keeps the vanilla difficulty and character draft until both seats commit.
internal sealed class CoopOpeningLobby : IDisposable
{
    internal const string Marker = "openingDraftV1";
    private ReplicaLink link;
    private ReplicaLink.Stamp stamp;
    private ulong seenRevision;
    private Guid id;
    private bool isHost, sending;
    private MainMenuUIController controller;
    private NewGameData request;
    private CustomUIWindow window;
    private CustomUIElement status, launch;
    private NativeActorPartnerSetup.DraftHandle perks;
    private string hostScenario, selectedScenario;
    private string[] selectedPerks;
    private int weeks = OpeningRentPlan.DefaultWeeks;
    private CoopOpening validated;
    private JObject guestDraft;
    internal bool HasGuestDraft => guestDraft != null;
    internal bool CoversRoomPanel => !isHost && (perks != null || window?.IsVisible == true);

    private static string Loc(string key, params object[] args) => ModHelper.GetLocalized("probably_coop", key, args);

    internal bool BeginHost(RoomSelector room, MainMenuUIController menu, NewGameData data)
    {
        if (!NativeOpening.CanHost || !room.IsHost || room.Session?.State != LinkState.Connected || data != NewGameData.Instance) return false;
        _ = CoopOpening.FindScenario(data.startScenarioId);
        _ = NativeActorPartnerSetup.ValidateSelection(data.startingPerks.Select(perk => perk.id), fullOpening: true);
        Dispose();
        Bind(room.Session.Native, authority: true);
        controller = menu;
        request = data;
        hostScenario = data.startScenarioId;
        id = Guid.NewGuid();
        link.Execute = ReceiveSelection;
        PublishHostDraft();

        var builder = CustomUIManager.Window("probably_coop_opening_host", Loc("opening_title"), "menu")
            .SetSize(640, 380).Center().SetCloseOnEscape().OnClose(CancelHost);
        builder.AddLabel(Loc("opening_host_role", Title(hostScenario)));
        builder.AddLabel(Loc("opening_rent_term"));
        int[] terms = { 0, 6, 5, 4, 3, 2, 1 };
        builder.AddDropdown(terms.Select(term => Loc(term switch { 0 => "opening_rent_waive", 1 => "opening_rent_one", _ => "opening_rent_weeks" }, term)).ToArray(),
            Array.IndexOf(terms, OpeningRentPlan.DefaultWeeks), index =>
            {
                weeks = terms[index];
                ValidateHostSelection();
                PublishHostDraft();
            });
        builder.AddLabel(Loc("opening_waiting_partner")); status = builder.Last;
        builder.BeginRow(8);
        builder.AddButton(Loc("opening_launch"), Launch); launch = builder.Last;
        builder.AddButton(Loc("opening_cancel"), CancelHost);
        builder.End();
        window = builder.Show();
        launch.SetInteractable(false);
        return true;
    }

    internal void Tick(RoomSelector room, bool inMenu)
    {
        var session = room.Session;
        if (!inMenu || session?.State != LinkState.Connected)
        {
            if (link != null) Dispose();
            return;
        }
        if (!ReferenceEquals(link, session.Native) || !stamp.IsCurrent(session.Native))
        {
            Dispose();
            Bind(session.Native, room.IsHost);
        }
        if (isHost || session.Shop.View.Active)
        {
            if (!isHost && id != Guid.Empty) ClearDraft();
            return;
        }

        var frame = link.Snapshot;
        if (link.Revision != seenRevision && frame != null)
        {
            seenRevision = link.Revision;
            if (frame.Count == 1 && !frame.Delta && frame.Bytes <= 8192 &&
                NativeSnapshotCodec.Unpack(frame.First)[Marker] is JObject draft)
            {
                try { ShowGuestDraft(draft); }
                catch (Exception error) when (error is InvalidDataException or ArgumentException or JsonException)
                {
                    ClearDraft();
                    Debug.LogWarning("[probably_coop] invalid paired opening draft: " + error.Message);
                }
            }
        }
        if (!sending || link.Waiting) return;
        sending = false;
        if (link.Result != (ushort)ShopResult.Applied)
            status?.SetText(Loc("opening_rejected"));
    }

    private void Bind(ReplicaLink value, bool authority)
    {
        link = value;
        stamp = link.CaptureStamp();
        isHost = authority;
    }

    private void PublishHostDraft()
    {
        var data = new JObject { ["id"] = id.ToString("D"), ["career"] = hostScenario, ["weeks"] = weeks };
        if (selectedScenario != null)
        {
            data["partner"] = selectedScenario;
            if (validated != null) data["shortfall"] = validated.Shortfall;
        }
        link.Publish(NativeSnapshotCodec.Pack(new JObject { [Marker] = data }));
    }

    private ushort ReceiveSelection(byte[] bytes)
    {
        if (id == Guid.Empty || request != NewGameData.Instance || !NativeCommandEnvelope.TryRead(bytes, out var command) ||
            command.Count != 4 || (string)command["kind"] != Marker || command["draft"]?.Type != JTokenType.String ||
            !Guid.TryParse((string)command["draft"], out var token) || token != id || command["career"]?.Type != JTokenType.String ||
            command["perks"] is not JArray rows || rows.Count > 16 || rows.Any(row => row.Type != JTokenType.String))
            return (ushort)ShopResult.Unavailable;
        try
        {
            string scenario = (string)command["career"];
            string[] chosen = rows.Select(row => (string)row).ToArray();
            validated = new CoopOpening(request, scenario, chosen, weeks);
            selectedScenario = scenario;
            selectedPerks = chosen;
            RefreshHostStatus();
            PublishHostDraft();
            return (ushort)ShopResult.Applied;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            validated = null;
            selectedScenario = null;
            selectedPerks = null;
            status?.SetText(error.Message);
            launch?.SetInteractable(false);
            PublishHostDraft();
            return (ushort)ShopResult.Unavailable;
        }
    }

    private void ValidateHostSelection()
    {
        validated = selectedScenario == null ? null : new CoopOpening(request, selectedScenario, selectedPerks, weeks);
        RefreshHostStatus();
    }

    private void RefreshHostStatus()
    {
        status?.SetText(validated == null ? Loc("opening_waiting_partner") :
            Terms(selectedScenario, validated.Shortfall, weeks));
        launch?.SetInteractable(validated != null);
    }

    private void Launch()
    {
        if (validated == null || !stamp.IsCurrent(link) || request != NewGameData.Instance) return;
        var menu = controller;
        var opening = validated;
        Dispose();
        NativeOpening.Arm(opening);
        menu.OnPerkFinishedClick();
    }

    private void CancelHost()
    {
        if (id == Guid.Empty) return;
        ClearDraft();
        if (link != null && stamp.IsCurrent(link))
            link.Publish(NativeSnapshotCodec.Pack(new JObject { [Marker] = new JObject() }));
    }

    internal void Reopen()
    {
        if (!HasGuestDraft || perks != null) return;
        ShowGuestDraft(guestDraft);
        window?.Show();
        window?.BringToFront();
    }

    private void ShowGuestDraft(JObject draft)
    {
        if (draft["id"]?.Type != JTokenType.String || !Guid.TryParse((string)draft["id"], out var token) || token == Guid.Empty)
        {
            ClearDraft();
            return;
        }
        string career = draft["career"]?.Type == JTokenType.String ? (string)draft["career"] : null;
        _ = CoopOpening.FindScenario(career);
        bool changed = id != token;
        if (changed) ClearDraft();
        id = token;
        guestDraft = draft;
        if (changed || window?.IsAlive != true)
        {
            hostScenario = career;
            var scenarios = StartingScenario.GetVisible()
                .Where(scenario => scenario.CanStart() && OpeningRentPlan.CanCombine(career, scenario.id)).ToArray();
            if (scenarios.Length == 0) return;
            selectedScenario = scenarios[0].id;
            var builder = CustomUIManager.Window("probably_coop_opening_guest", Loc("opening_title"), "menu")
                .SetSize(640, 350).Center().SetCloseOnEscape();
            builder.AddLabel(Loc("opening_host_role", Title(hostScenario)));
            builder.AddDropdown(scenarios.Select(scenario => scenario.GetTitle()).ToArray(),
                0, i => selectedScenario = scenarios[i].id);
            builder.AddLabel(Loc("opening_choose_perks")); status = builder.Last;
            builder.AddButton(Loc("opening_choose_perks"), ChoosePerks);
            window = builder.Show();
            window.BringToFront();
        }
        if (draft["shortfall"]?.Type == JTokenType.Integer && draft["weeks"]?.Type == JTokenType.Integer &&
            draft["partner"]?.Type == JTokenType.String)
        {
            int shortfall = (int)draft["shortfall"], term = (int)draft["weeks"];
            string partner = (string)draft["partner"];
            if (shortfall < 0 || term is < OpeningRentPlan.MinimumWeeks or > OpeningRentPlan.MaximumWeeks ||
                !OpeningRentPlan.CanCombine(career, partner))
                throw new InvalidDataException("Invalid paired opening summary.");
            status?.SetText(Terms(partner, shortfall, term));
        }
    }

    private void ChoosePerks()
    {
        if (perks != null || sending || !stamp.IsCurrent(link)) return;
        var token = id;
        string scenario = selectedScenario;
        if (!NativeActorPartnerSetup.TryOpenDraft(ids =>
        {
            perks = null;
            if (id != token || !stamp.IsCurrent(link)) return;
            var action = new JObject { ["kind"] = Marker, ["draft"] = id.ToString("D"),
                ["career"] = scenario, ["perks"] = new JArray(ids) };
            sending = link.Submit(Encoding.UTF8.GetBytes(action.ToString(Formatting.None)), Time.realtimeSinceStartupAsDouble);
            window?.Show();
            status?.SetText(Loc(sending ? "opening_sending" : "opening_rejected"));
        }, () => { perks = null; window?.Show(); }, out string error, out perks, fullOpening: true))
        {
            perks = null;
            status?.SetText(error);
            return;
        }
        window?.Hide();
    }

    private static string Title(string career) => StartingScenario.Get(career)?.GetTitle() ?? career;

    private static string Terms(string partner, int shortfall, int term)
    {
        var plan = OpeningRentPlan.Create(shortfall, term);
        string debt = shortfall == 0 ? Loc("opening_no_debt") :
            term == 0 ? Loc("opening_debt_waived", shortfall) :
            Loc("opening_debt_schedule", shortfall, string.Join(" / ", plan.Amounts));
        return Loc("opening_partner_role", Title(partner)) + "\n" + debt;
    }

    private void ClearDraft()
    {
        id = Guid.Empty;
        guestDraft = null;
        sending = false;
        validated = null;
        request = null;
        controller = null;
        selectedScenario = hostScenario = null;
        selectedPerks = null;
        weeks = OpeningRentPlan.DefaultWeeks;
        if (link != null && link.Execute == ReceiveSelection) link.Execute = null;
        var oldWindow = window;
        window = null;
        if (perks != null)
        {
            NativeActorPartnerSetup.CancelDraft(perks);
            perks = null;
        }
        if (oldWindow?.IsAlive == true) oldWindow.Close();
        status = launch = null;
    }

    public void Dispose()
    {
        ClearDraft();
        link = null;
        seenRevision = 0;
    }

#if PROBABLY_COOP_GRID_QA
    internal bool QaValidated => validated != null;
    internal void QaLaunch() => launch.button.onClick.Invoke();
    internal void QaChoosePerks(string career)
    {
        selectedScenario = career;
        ChoosePerks();
    }
#endif
}
