using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Api;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

// A new game draft belongs to an already authenticated room epoch. The host
// keeps the vanilla difficulty and character draft until both seats commit.
internal sealed class CoopOpeningLobby : IDisposable
{
    internal const string Marker = "openingDraftV1";
    private ReplicaLink link;
    private RoomSelector openingRoom;
    private string hostedRoomCode;
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
    private string[] selectedPerks, hostModPerks;
    private int weeks = OpeningRentPlan.DefaultWeeks;
    private CoopOpening validated;
    private JObject guestDraft;
    private Dictionary<string, RoomModManifest.Entry> openingProviders;
    private const int MaximumOpeningProviders = RoomModManifest.OpeningLimit;
    internal bool HasGuestDraft => guestDraft != null;
    internal bool CoversRoomPanel => !isHost && (perks != null || window?.IsVisible == true);

    private static string Loc(string key, params object[] args) => ModHelper.GetLocalized("probably_coop", key, args);

    internal bool BeginHost(RoomSelector room, MainMenuUIController menu, NewGameData data)
    {
        if (!NativeOpening.CanHost || !room.IsHost || room.Session?.State != LinkState.Connected || data != NewGameData.Instance) return false;
        var career = CoopOpening.FindScenario(data.startScenarioId, out _);
        var picked = NativeActorPartnerSetup.ValidateSelection(data.startingPerks.Select(perk => perk.id), fullOpening: true);
        var providers = FindOpeningProviders(career, picked.Where(perk => !string.IsNullOrEmpty(perk.source)).ToArray());
#if PROBABLY_COOP_GRID_QA
        if (NativeBackgroundQa.Enabled && NativeJoinWizardQa.OpeningHandoff)
        {
            var provider = NativeJoinWizardQa.OpeningProvider;
            providers[provider.Id] = provider;
        }
#endif
        Dispose();
        Bind(room.Session.Native, authority: true);
        openingRoom = room;
        hostedRoomCode = room.Code;
        controller = menu;
        request = data;
        hostScenario = data.startScenarioId;
        openingProviders = providers;
        hostModPerks = picked.Where(perk => !string.IsNullOrEmpty(perk.source)).Select(perk => perk.id).ToArray();
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

    internal void Tick(RoomSelector room, CoopPanel panel, bool inMenu)
    {
        var session = room.Session;
        if (inMenu && id != Guid.Empty && isHost && ReferenceEquals(room, openingRoom) &&
            room.IsHost && room.HasLobby && room.Code == hostedRoomCode && request == NewGameData.Instance)
        {
            // A guest can deliberately leave to enable a Mod. Hold the host's
            // opening in the same room, and ask the next occupant to choose anew.
            if (session?.State != LinkState.Connected)
            {
                if (link != null) SuspendHostDraft();
            }
            else if (!ReferenceEquals(link, session.Native) || !stamp.IsCurrent(session.Native))
            {
                SuspendHostDraft();
                Bind(session.Native, authority: true);
                link.Execute = ReceiveSelection;
                PublishHostDraft();
            }
            return;
        }
        if (!inMenu || session?.State != LinkState.Connected)
        {
            if (link != null || id != Guid.Empty) Dispose();
            panel.CloseOpeningModReview();
            return;
        }
        if (isHost && id != Guid.Empty) Dispose();
        if (!ReferenceEquals(link, session.Native) || !stamp.IsCurrent(session.Native))
        {
            Dispose();
            Bind(session.Native, room.IsHost);
        }
        if (isHost || session.Shop.View.Active)
        {
            if (!isHost)
            {
                if (id != Guid.Empty) ClearDraft();
                panel.CloseOpeningModReview();
            }
            return;
        }

        var frame = link.Snapshot;
        if (link.Revision != seenRevision && frame != null)
        {
            seenRevision = link.Revision;
            if (frame.Count == 1 && !frame.Delta && frame.Bytes <= 8192)
            {
                try
                {
                    var content = NativeSnapshotCodec.Unpack(frame.First);
                    if (content[Marker] is JObject draft)
                        ShowGuestDraft(draft, RoomModManifest.Read(content[RoomModManifest.Field]), panel);
                }
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

    private void SuspendHostDraft()
    {
        if (link?.Execute == ReceiveSelection) link.Execute = null;
        link = null;
        id = Guid.NewGuid();
        selectedScenario = null;
        selectedPerks = null;
        validated = null;
        RefreshHostStatus();
    }

    private void PublishHostDraft()
    {
        var data = new JObject { ["id"] = id.ToString("D"), ["career"] = hostScenario, ["weeks"] = weeks };
        if (selectedScenario != null)
        {
            data["partner"] = selectedScenario;
            if (validated != null) data["shortfall"] = validated.Shortfall;
        }
        if (hostModPerks.Length != 0) data["perks"] = new JArray(hostModPerks);
        var frame = new JObject { [Marker] = data };
        if (openingProviders.Count != 0)
            frame[RoomModManifest.Field] = RoomModManifest.Write(openingProviders.Values);
        link.Publish(NativeSnapshotCodec.Pack(frame));
    }

    private static Dictionary<string, RoomModManifest.Entry> FindOpeningProviders(StartingScenario hostCareer,
        StartingPerk[] hostModPerks)
    {
        var required = hostModPerks.Select(perk => perk.source).ToHashSet(StringComparer.Ordinal);
        if (hostCareer.IsMod) required.Add(hostCareer.source);
        var local = NativeRoomModManifest.Local();
        var offered = StartingScenario.GetVisible().Where(scenario => scenario.IsMod && CoopOpening.TryCareer(scenario, out _))
            .Select(scenario => scenario.source)
            .Concat(NativeActorPartnerSetup.Catalog(fullOpening: true).Values.Select(perk => perk.source))
            .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal)
            .Where(id => local.TryGetValue(id, out var mod) && RoomModManifest.ExactPackage(mod, mod))
            .OrderByDescending(required.Contains)
            .ThenBy(id => id, StringComparer.Ordinal).Take(MaximumOpeningProviders)
            .ToDictionary(id => id, id => local[id], StringComparer.Ordinal);
        if (required.Any(id => !offered.ContainsKey(id)))
            throw new InvalidDataException(Loc("opening_mod_missing_match"));
        return offered;
    }

    private static HashSet<string> MatchingProviders(Dictionary<string, RoomModManifest.Entry> offered)
    {
        var local = NativeRoomModManifest.Local();
        return offered.Values.Where(mod => local.TryGetValue(mod.Id, out var own) && RoomModManifest.ExactPackage(mod, own))
            .Select(mod => mod.Id).ToHashSet(StringComparer.Ordinal);
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
            validated = CheckDraft(scenario, chosen);
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

    private CoopOpening CheckDraft(string career, string[] chosen)
    {
        var opening = new CoopOpening(request, career, chosen, weeks);
        if (opening.ModProviders.Length != 0)
        {
            var eligible = MatchingProviders(openingProviders);
            if (opening.ModProviders.Any(id => !eligible.Contains(id)))
                throw new InvalidDataException(Loc("opening_mod_missing_match"));
        }
        return opening;
    }

    private void ValidateHostSelection()
    {
        try
        {
            validated = selectedScenario == null ? null : CheckDraft(selectedScenario, selectedPerks);
            RefreshHostStatus();
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            validated = null;
            status?.SetText(error.Message);
            launch?.SetInteractable(false);
        }
    }

    private void RefreshHostStatus()
    {
        status?.SetText(validated == null ? Loc("opening_waiting_partner") :
            Terms(selectedScenario, validated.Shortfall, weeks));
        launch?.SetInteractable(validated != null);
    }

    private void Launch()
    {
        if (validated == null || openingRoom?.Session is not { State: LinkState.Connected } session ||
            !ReferenceEquals(session.Native, link) || !stamp.IsCurrent(link) || request != NewGameData.Instance) return;
        var menu = controller;
        var room = openingRoom;
        CoopOpening opening;
        try { opening = CheckDraft(selectedScenario, selectedPerks); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            validated = null;
            status?.SetText(error.Message);
            launch?.SetInteractable(false);
            return;
        }
        Dispose();
        NativeOpening.Arm(opening);
        room.AnnounceSceneLoad();
        menu.OnPerkFinishedClick();
    }

    private void CancelHost()
    {
        if (id == Guid.Empty) return;
        ClearDraft();
        if (link != null && stamp.IsCurrent(link))
            link.Publish(NativeSnapshotCodec.Pack(new JObject { [Marker] = new JObject() }));
    }

    internal void Reopen(CoopPanel panel)
    {
        if (!HasGuestDraft || perks != null) return;
        ShowGuestDraft(guestDraft, openingProviders, panel);
        window?.Show();
        window?.BringToFront();
    }

    private void ShowGuestDraft(JObject draft, Dictionary<string, RoomModManifest.Entry> providers, CoopPanel panel)
    {
        if (draft["id"]?.Type != JTokenType.String || !Guid.TryParse((string)draft["id"], out var token) || token == Guid.Empty)
        {
            ClearDraft();
            return;
        }
        string career = draft["career"]?.Type == JTokenType.String ? (string)draft["career"] : null;
        if (!CoopOpeningCareer.ValidId(career)) throw new InvalidDataException("Invalid host career ID.");
        bool changed = id != token;
        if (changed)
        {
            panel.CloseOpeningModReview();
            ClearDraft();
        }
        id = token;
        guestDraft = draft;
        openingProviders = providers;
        if (changed || window?.IsAlive != true)
        {
            hostScenario = career;
            var eligible = MatchingProviders(providers);
            bool MatchingSource(StartingScenario scenario) => !scenario.IsMod || eligible.Contains(scenario.source);
            var hostChoice = StartingScenario.Get(career);
            if (!CoopOpening.TryCareer(hostChoice, out var hostPlan) || !MatchingSource(hostChoice))
            {
                ShowUnavailableOpening(Loc("opening_install_host_career", Title(career)), panel, providers);
                return;
            }
            if (!HasHostModPerks(draft["perks"], eligible))
            {
                ShowUnavailableOpening(Loc("opening_mod_missing_match"), panel, providers);
                return;
            }
            var scenarios = StartingScenario.GetVisible()
                .Where(scenario => MatchingSource(scenario) && CoopOpening.TryCareer(scenario, out var choice) &&
                    CoopOpening.CanCombine(hostPlan, choice)).ToArray();
            if (scenarios.Length == 0) return;
            selectedScenario = scenarios[0].id;
            var builder = CustomUIManager.Window("probably_coop_opening_guest", Loc("opening_title"), "menu")
                .SetSize(640, 350).Center().SetCloseOnEscape();
            builder.AddLabel(Loc("opening_host_role", Title(hostScenario)));
            builder.AddDropdown(scenarios.Select(scenario => scenario.GetTitle()).ToArray(),
                0, i => selectedScenario = scenarios[i].id);
            builder.AddLabel(Loc("opening_choose_perks")); status = builder.Last;
            builder.AddButton(Loc("opening_choose_perks"), ChoosePerks);
            AddOpeningModReview(builder, panel, providers);
            window = builder.Show();
            window.BringToFront();
        }
        if (draft["shortfall"]?.Type == JTokenType.Integer && draft["weeks"]?.Type == JTokenType.Integer &&
            draft["partner"]?.Type == JTokenType.String)
        {
            int shortfall = (int)draft["shortfall"], term = (int)draft["weeks"];
            string partner = (string)draft["partner"];
            _ = CoopOpening.FindScenario(career, out var hostPlan);
            _ = CoopOpening.FindScenario(partner, out var partnerPlan);
            if (shortfall < 0 || term is < OpeningRentPlan.MinimumWeeks or > OpeningRentPlan.MaximumWeeks ||
                !CoopOpening.CanCombine(hostPlan, partnerPlan))
                throw new InvalidDataException("Invalid paired opening summary.");
            status?.SetText(Terms(partner, shortfall, term));
        }
    }

    private static bool HasHostModPerks(JToken token, ISet<string> eligible)
    {
        if (token == null) return true;
        if (token is not JArray ids || ids.Count is < 1 or > 16 ||
            ids.Any(row => row.Type != JTokenType.String || !CoopOpeningCareer.ValidId((string)row)))
            throw new InvalidDataException("Invalid host modded perks.");
        var catalog = NativeActorPartnerSetup.Catalog(fullOpening: true, eligibleMods: eligible);
        return ids.All(row => catalog.TryGetValue((string)row, out var perk) &&
            !string.IsNullOrEmpty(perk.source));
    }

    private void ShowUnavailableOpening(string message, CoopPanel panel, Dictionary<string, RoomModManifest.Entry> providers)
    {
        var builder = CustomUIManager.Window("probably_coop_opening_guest", Loc("opening_title"), "menu")
            .SetSize(640, 245).Center().SetCloseOnEscape()
            .AddLabel(message, "opening_missing_mod");
        AddOpeningModReview(builder, panel, providers);
        window = builder.Show();
        window.Get("opening_missing_mod").text.richText = false;
        window.BringToFront();
    }

    private void AddOpeningModReview(CustomUIBuilder builder, CoopPanel panel, Dictionary<string, RoomModManifest.Entry> providers)
    {
        if (OpeningModReview(providers).Rows.Length != 0)
            builder.AddButton(Loc("opening_mod_review"), () => ShowOpeningModReview(panel), "opening_mod_review");
    }

    private static RoomModManifest OpeningModReview(Dictionary<string, RoomModManifest.Entry> providers) =>
        RoomModManifest.Compare(providers, NativeRoomModManifest.Local(),
            new Dictionary<string, string>(StringComparer.Ordinal), openingChoices: true);

    private void ShowOpeningModReview(CoopPanel panel)
    {
        if (guestDraft == null || !stamp.IsCurrent(link)) return;
        var review = OpeningModReview(openingProviders);
        if (review.Rows.Length == 0)
        {
            var oldWindow = window;
            window = null;
            oldWindow?.Close();
            ShowGuestDraft(guestDraft, openingProviders, panel);
            return;
        }
        panel.RecordModReview(review);
        panel.ShowModReview(review, panel.CloseOpeningModReview, () => ShowOpeningModReview(panel),
            null, openingChoices: true);
    }

    private void ChoosePerks()
    {
        if (perks != null || sending || !stamp.IsCurrent(link)) return;
        var token = id;
        string scenario = selectedScenario;
        if (string.IsNullOrEmpty(scenario)) return;
        if (!NativeActorPartnerSetup.TryOpenDraft(ids =>
        {
            perks = null;
            if (id != token || !stamp.IsCurrent(link)) return;
            var action = new JObject { ["kind"] = Marker, ["draft"] = id.ToString("D"),
                ["career"] = scenario, ["perks"] = new JArray(ids) };
            sending = link.Submit(Encoding.UTF8.GetBytes(action.ToString(Formatting.None)), Time.realtimeSinceStartupAsDouble);
            window?.Show();
            status?.SetText(Loc(sending ? "opening_sending" : "opening_rejected"));
        }, () => { perks = null; window?.Show(); }, out string error, out perks, fullOpening: true,
            eligibleMods: MatchingProviders(openingProviders)))
        {
            perks = null;
            status?.SetText(error);
            return;
        }
        window?.Hide();
    }

    private static string Title(string career) => string.IsNullOrEmpty(career) ? "—" :
        StartingScenario.Get(career)?.GetTitle() ?? career;

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
        openingProviders = null;
        sending = false;
        validated = null;
        request = null;
        controller = null;
        openingRoom = null;
        hostedRoomCode = null;
        selectedScenario = hostScenario = null;
        selectedPerks = hostModPerks = null;
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
    internal Guid QaDraftId => id;
    internal void QaOpenModReview() => window.Get("opening_mod_review").button.onClick.Invoke();
    internal void QaLaunch() => launch.button.onClick.Invoke();
    internal void QaChoosePerks(string career)
    {
        selectedScenario = career;
        ChoosePerks();
    }
#endif
}
