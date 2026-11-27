using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Api;
using UnityEngine.SceneManagement;

namespace ProbablyCoop;

// Personal character creation reuses the native menu picker. It never enters
// a new-game scenario or calls StartingPerkContent's shared shop initializers.
internal static class NativeActorPartnerSetup
{
    internal const string SaveKey = "probably_coop.partner_setup.v1";
    private const int StartingLuckBonus = 15;
    internal enum PerkScope { Unknown, Personal, Mixed, SharedShop, RetiredDefinition }
    private static readonly Dictionary<string, PerkScope> Scope = BuildScopes();
    private static readonly FieldInfo Sorted = AccessTools.Field(typeof(NewGamePerksUI), "sorted");
    private static readonly FieldInfo Frame = AccessTools.Field(typeof(NewGamePerksUI), "frame");
    private static readonly FieldInfo Window = AccessTools.Field(typeof(NewGamePerksUI), "window");
    private static readonly FieldInfo FirstRun = AccessTools.Field(typeof(NewGamePerksUI), "firstRun");
    private static readonly FieldInfo StartingGame = AccessTools.Field(typeof(NewGamePerksUI), "startingGame");
    private static readonly MethodInfo Rebuild = AccessTools.Method(typeof(NewGamePerksUI), "Rebuild");
    private static Pending pending;
    internal static bool SelectingPartnerPerks => pending != null;

    private static Dictionary<string, PerkScope> BuildScopes()
    {
        var result = new Dictionary<string, PerkScope>(StringComparer.Ordinal);
        void Add(PerkScope scope, string ids) { foreach (string id in ids.Split(' ')) result.Add(id, scope); }
        Add(PerkScope.Personal, "diplomatic intimidating colorful_character blabbermouth proficient_scavenger maimed wild_favorite wild_bad_side lucky unlucky tinkerer careful_scavenger awful_negotiator illiterate agoraphobia");
        Add(PerkScope.Mixed, "convict model_citizen sycophant nicotine_addiction narcotic_addiction alcohol_addiction sentinel gambling_addiction degenerative_osteoarthritis known_snitch banned_commissary short_sleeper");
        Add(PerkScope.SharedShop, "saving bad_lease well_connected public_enemy alert sawyer_crew side_street secret_admirer blackmail minimalist ascetic welfare_program tapehead handy aug_menace hoarder makeshift_reactor working_pipes prime_real_estate vending revolution_sympathizer");
        Add(PerkScope.RetiredDefinition, "loan debt unsubscribed speedy expedition_organizer");
        return result;
    }
    internal static PerkScope Classify(string id)
    {
        if (string.IsNullOrEmpty(id)) return PerkScope.Unknown;
        return Scope.TryGetValue(id, out var scope) ? scope : DeclaredScope(StartingPerkList.GetModPerk(id));
    }

    private static PerkScope DeclaredScope(StartingPerk definition)
    {
        if (definition == null || !CoopOpeningCareer.ValidId(definition.source) || !CoopOpeningCareer.ValidId(definition.id) ||
            definition.cost is < -1000 or > 1000 || definition.maxSlot is < -16 or > 16 ||
            (int)definition.type is < 0 or > 2 ||
            definition.incompatiblePerks?.Any(id => !CoopOpeningCareer.ValidId(id)) == true ||
            definition.incompatiblePerks?.Count > NativeActorState.MaximumPerks)
            return PerkScope.Unknown;

        var declared = ModHelper.Lookup<IReadOnlyDictionary<string, CoopOpeningPerkScope>>(definition.source,
            CoopExtensionApi.OpeningPerksService);
        if (declared == null || declared.Count > 32 || !declared.TryGetValue(definition.id, out var scope))
            return PerkScope.Unknown;
        return scope switch
        {
            CoopOpeningPerkScope.Personal => PerkScope.Personal,
            CoopOpeningPerkScope.SharedShop => PerkScope.SharedShop,
            _ => PerkScope.Unknown
        };
    }

    internal static StartingPerk FindDeclaredSharedPerk(string id)
    {
        if (Scope.ContainsKey(id)) return null;
        var definition = StartingPerkList.GetModPerk(id);
        return DeclaredScope(definition) == PerkScope.SharedShop ? NativeActorState.ClonePerk(definition) : null;
    }
    internal static bool IsPersonalChoice(string id) => Classify(id) is PerkScope.Personal or PerkScope.Mixed;

    internal static bool ConflictsAcrossActors(StartingPerk host, StartingPerk partner)
    {
        if (host.incompatiblePerks?.Contains(partner.id) != true &&
            partner.incompatiblePerks?.Contains(host.id) != true) return false;
        if (!IsPersonalChoice(host.id) || !IsPersonalChoice(partner.id)) return true;

        // These mixed perks apply opposite starting security reputation to
        // the same shop. The scavenging and luck exclusions stay personal.
        return host.id is "convict" or "model_citizen" &&
            partner.id is "convict" or "model_citizen";
    }

    // Enumerate definitions only. Menu validation never calls gameplay effects.
    internal static Dictionary<string, StartingPerk> Catalog(bool fullOpening = false, ISet<string> eligibleMods = null)
    {
        var result = new Dictionary<string, StartingPerk>(StringComparer.Ordinal);
        var definitions = fullOpening ? StartingPerkList.AllPerkDefinitionsWithMods() : StartingPerkList.AllPerkDefinitions();
        foreach (var definition in definitions)
        {
            if (!string.IsNullOrEmpty(definition.source) ||
                ReferenceEquals(StartingPerkList.GetModPerk(definition.id), definition))
            {
                if (fullOpening && !Scope.ContainsKey(definition.id) &&
                    (eligibleMods == null || eligibleMods.Contains(definition.source)) &&
                    DeclaredScope(definition) != PerkScope.Unknown)
                    result.Add(definition.id, NativeActorState.ClonePerk(definition));
                continue;
            }
            if (!Scope.TryGetValue(definition.id, out var scope) || scope == PerkScope.RetiredDefinition)
                throw new InvalidDataException("Unreviewed native perk definition: " + definition.id);
            if ((!fullOpening && scope is not (PerkScope.Personal or PerkScope.Mixed)) ||
                StartingPerkList.GetModPerk(definition.id) != null) continue;
            result.Add(definition.id, definition);
        }
        return result;
    }

    internal static StartingPerk[] ValidateSelection(IEnumerable<string> ids, bool fullOpening = false,
        ISet<string> eligibleMods = null)
    {
        if (ids == null) throw new InvalidDataException("Missing partner perk draft.");
        var selected = ids.Take(17).ToArray();
        if (selected.Length > 16 || selected.Any(id => string.IsNullOrEmpty(id) || id.Length > 256) || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new InvalidDataException("Invalid or duplicate partner perks.");
        var catalog = Catalog(fullOpening, eligibleMods);
        var perks = selected.Select(id => catalog.TryGetValue(id, out var perk) ? perk : throw new InvalidDataException("Partner perk is not a supported personal definition: " + id)).ToArray();
        int points = 0, slots = NewGamePerksUI.BASE_PERK_COUNT;
        foreach (var perk in perks) { points = checked(points + perk.cost); slots = checked(slots + perk.maxSlot); }
        if (points > NewGamePerksUI.MAX_PERK_POINT || perks.Length > slots) throw new InvalidDataException("Partner perk budget exceeded.");
        foreach (var a in perks) foreach (var b in perks)
            if (!ReferenceEquals(a, b) && a.incompatiblePerks?.Contains(b.id) == true) throw new InvalidDataException("Incompatible partner perks.");
        return perks;
    }

    internal static JObject CreateDraft(Guid run, IEnumerable<string> ids)
    {
        if (run == Guid.Empty) throw new InvalidDataException("Missing partner setup run.");
        var perks = ValidateSelection(ids);
        return new JObject { ["schema"] = 1, ["run"] = run.ToString("D"), ["slot"] = 1, ["ids"] = new JArray(perks.Select(perk => perk.id).OrderBy(id => id, StringComparer.Ordinal)) };
    }
    private static string[] ReadIds(JToken token)
    {
        if (token is not JArray rows || rows.Count > 16 || rows.Any(row => row.Type != JTokenType.String)) throw new InvalidDataException("Invalid partner perk IDs.");
        return rows.Select(row => (string)row).ToArray();
    }
    private static void CheckEnvelope(JObject value, Guid run, int count)
    {
        if (value == null || run == Guid.Empty || value.Count != count || value["schema"]?.Type != JTokenType.Integer || (string)value["schema"] != "1" ||
            value["slot"]?.Type != JTokenType.Integer || (string)value["slot"] != "1" || value["run"]?.Type != JTokenType.String ||
            !Guid.TryParse((string)value["run"], out var found) || found != run) throw new InvalidDataException("Invalid partner setup identity.");
    }
    private static string SelectionProof(IEnumerable<string> ids, JObject owners = null)
    {
        JToken selection = new JArray(ids.OrderBy(id => id, StringComparer.Ordinal));
        if (owners != null)
        {
            var orderedOwners = new JObject();
            foreach (var property in owners.Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
                orderedOwners.Add(property.Name, property.Value.DeepClone());
            selection = new JObject { ["ids"] = selection, ["externalPerks"] = orderedOwners };
        }
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(selection.ToString(Formatting.None)))).Replace("-", "");
    }
    internal static bool IsComplete(string receipt, Guid run)
    {
        if (string.IsNullOrEmpty(receipt)) return false;
        ReadReceipt(receipt, run); return true;
    }
    private static JObject ReadReceipt(string receipt, Guid run)
    {
        if (receipt == null || receipt.Length > 8192) throw new InvalidDataException("Invalid partner setup receipt.");
        var data = JObject.Parse(receipt);
        var external = data["externalPerks"];
        CheckEnvelope(data, run, external == null ? 6 : 7);
        var ids = ReadIds(data["ids"]);
        // Checkpoint validation can run off the game thread. Read saved
        // provenance without consulting the enabled Mod registry.
        var modIds = ReadPersonalModIds(external);
        if (ids.Any(id => string.IsNullOrEmpty(id) || id.Length > 256 ||
                (Scope.TryGetValue(id, out var scope) ? scope is not (PerkScope.Personal or PerkScope.Mixed) :
                    !modIds.Remove(id))) ||
            modIds.Count != 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            data["completed"]?.Type != JTokenType.Boolean || !(bool)data["completed"] || data["selection"]?.Type != JTokenType.String ||
            (string)data["selection"] != SelectionProof(ids, (JObject)external)) throw new InvalidDataException("Invalid partner setup completion.");
        return data;
    }
    private static HashSet<string> ReadPersonalModIds(JToken value)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (value == null) return ids;
        if (value is not JObject owners || owners.Count is < 1 or > 16)
            throw new InvalidDataException("Invalid modded perks in partner receipt.");
        foreach (var owner in owners.Properties())
            if (!CoopOpeningCareer.ValidId(owner.Name) || Scope.ContainsKey(owner.Name) ||
                owner.Value.Type != JTokenType.String || !CoopOpeningCareer.ValidId((string)owner.Value) ||
                !ids.Add(owner.Name))
                throw new InvalidDataException("Invalid modded perk ownership in partner receipt.");
        return ids;
    }

    internal static Prepared Prepare(NativeActorRun run, JToken draft, string persistedReceipt)
    {
        if (run == null) throw new ArgumentNullException(nameof(run));
        var data = draft as JObject; CheckEnvelope(data, run.Run, 4);
        var ids = ReadIds(data["ids"]);
        string proof = SelectionProof(ids);
        if (!string.IsNullOrEmpty(persistedReceipt))
        {
            var old = ReadReceipt(persistedReceipt, run.Run);
            if (SelectionProof(ReadIds(old["ids"])) != proof)
                throw new InvalidDataException("Partner setup was already completed with another selection.");
            return new Prepared(run.Partner, null, persistedReceipt);
        }
        var perks = ValidateSelection(ids);
        return PreparePersonal(run, perks);
    }

    internal static Prepared PrepareOpening(NativeActorRun run, IEnumerable<string> fullSelection)
    {
        // Points and slots are spent across the complete native draft. The
        // personal subset stored per actor does not buy those perks a second time.
        var personal = ValidateSelection(fullSelection, fullOpening: true)
            .Where(perk => IsPersonalChoice(perk.id)).ToArray();
        return PreparePersonal(run, personal);
    }

    private static Prepared PreparePersonal(NativeActorRun run, StartingPerk[] perks)
    {
        if (run.Partner.startingPerks.Count != 0) throw new InvalidDataException("Unmarked partner already has perks; explicit migration required.");
        var ids = perks.Select(perk => perk.id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var owners = new JObject();
        foreach (var perk in perks.Where(perk => !string.IsNullOrEmpty(perk.source)))
            owners.Add(perk.id, perk.source);
        var receipt = new JObject { ["schema"] = 1, ["run"] = run.Run.ToString("D"), ["slot"] = 1,
            ["ids"] = new JArray(ids), ["selection"] = SelectionProof(ids, owners.Count > 0 ? owners : null), ["completed"] = true };
        if (owners.Count > 0) receipt["externalPerks"] = owners;
        return new Prepared(run.Partner, perks, receipt.ToString(Formatting.None));
    }
    internal sealed class Prepared
    {
        private readonly NativeActorState target;
        private readonly StartingPerk[] perks;
        private readonly string before;
        private readonly HealthData health;
        private readonly List<StartingPerk> perkList;
        private readonly int startingLuck;
        private readonly int thread = Environment.CurrentManagedThreadId;
        private bool committed;
        internal readonly string Receipt;
        internal bool AlreadyComplete => perks == null;
        internal Prepared(NativeActorState target, StartingPerk[] perks, string receipt)
        {
            this.target = target; this.perks = perks; Receipt = receipt;
            before = target.Serialize().ToString(Formatting.None); health = target.healthData; perkList = target.startingPerks;
            // Match the original StartingPerkContent.HandleNewGamePerk bonus.
            startingLuck = checked(health.luck +
                (perks?.Any(perk => perk.id == "lucky") == true ? StartingLuckBonus : 0) -
                (perks?.Any(perk => perk.id == "unlucky") == true ? StartingLuckBonus : 0));
        }
        // Caller persists Receipt in the SAME verified native checkpoint as the
        // mutated partner state. Returning this value is not a save-success claim.
        internal string Commit()
        {
            if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Partner setup belongs to the game thread.");
            if (committed || AlreadyComplete) return Receipt;
            if (!ReferenceEquals(health, target.healthData) || !ReferenceEquals(perkList, target.startingPerks) || target.Serialize().ToString(Formatting.None) != before)
                throw new InvalidDataException("Partner changed while its setup was pending.");
            if (perkList.Capacity < perks.Length) perkList.Capacity = perks.Length;
            foreach (var perk in perks)
            {
                perkList.Add(perk);
                // Exact personal initializers in HealthData and
                // StartingPerkContent. Shared money/items/services never run.
                switch (perk.id)
                {
                    case "nicotine_addiction": health.daysSinceNicotineConsumption = 3; break;
                    case "alcohol_addiction": health.daysSinceAlcoholConsumption = 3; break;
                    case "narcotic_addiction": health.daysSinceNarcoticConsumption = 3; break;
                }
            }
            health.luck = startingLuck;
            committed = true;
            return Receipt;
        }
    }

    internal sealed class DraftHandle
    {
        internal readonly NewGameData Data;
        internal readonly List<StartingPerk> Original;
        internal readonly List<StartingPerk> Draft = new List<StartingPerk>();
        internal CustomUIWindow Window;
        internal DraftHandle(NewGameData data)
        { Data = data ?? throw new ArgumentNullException(nameof(data)); Original = data.startingPerks; }
        internal bool RestoreTo(NewGameData current)
        {
            // A different native menu flow may already have replaced either
            // the descriptor or its list. Never overwrite that newer choice.
            if (!ReferenceEquals(current, Data) || !ReferenceEquals(current.startingPerks, Draft)) return false;
            current.startingPerks = Original;
            return true;
        }
    }
    private sealed class Pending
    {
        internal DraftHandle Handle;
        internal List<StartingPerk> Draft => Handle.Draft;
        internal Action<string[]> Accept;
        internal Action Cancel;
        internal bool Ready, Submitted, FullOpening;
        internal ISet<string> EligibleMods;
    }
    internal static bool TryOpenDraft(Action<string[]> accept, Action cancel, out string reason)
        => TryOpenDraft(accept, cancel, out reason, out _);
    internal static bool TryOpenDraft(Action<string[]> accept, Action cancel, out string reason, out DraftHandle owner,
        bool fullOpening = false, ISet<string> eligibleMods = null)
    {
        reason = null; owner = null;
        if (accept == null) throw new ArgumentNullException(nameof(accept));
        if (pending != null || NewGamePerksUI.IsOpen) { reason = "另一个天赋窗口正在使用。"; return false; }
        var data = NewGameData.Instance;
        var ui = CustomUIManager.Instance;
        if (SceneManager.GetActiveScene().name != "EmporiumMenu" || MainMenuUIController.Instance == null)
        { reason = "原版主菜单尚未就绪。"; return false; }
        if (data == null || ui == null || ui.newGamePerksPrefab == null)
        { reason = $"原版天赋窗口尚未就绪（开局数据={data != null}，界面管理器={ui != null}，窗口资源={ui != null && ui.newGamePerksPrefab != null}）。"; return false; }
        var setup = new Pending { Handle = new DraftHandle(data), Accept = accept, Cancel = cancel,
            FullOpening = fullOpening, EligibleMods = eligibleMods };
        owner = setup.Handle;
        pending = setup; data.startingPerks = setup.Draft;
        var previousWindow = Window.GetValue(null);
        try
        {
            NewGamePerksUI.Open();
            setup.Handle.Window = Window.GetValue(null) as CustomUIWindow;
            if (!NewGamePerksUI.IsOpen || Frame.GetValue(null) is not NewGamePerksFrame frame) { reason = "原版天赋窗口未能打开。"; Restore(setup); return false; }
            ConfigurePicker(frame, partner: true, fullOpening, eligibleMods);
            setup.Ready = true;
            return true;
        }
        catch
        {
            var opened = Window.GetValue(null) as CustomUIWindow;
            if (opened != null && !ReferenceEquals(opened, previousWindow)) setup.Handle.Window = opened;
            CancelDraft(setup.Handle); throw;
        }
    }
    internal static void ConfigureHostOpeningPicker()
    {
        if (NewGamePerksUI.IsOpen && Frame.GetValue(null) is NewGamePerksFrame frame)
            ConfigurePicker(frame, partner: false, fullOpening: true);
    }

    private static void ConfigurePicker(NewGamePerksFrame frame, bool partner, bool fullOpening, ISet<string> eligibleMods = null)
    {
        var sorted = (List<StartingPerk>)Sorted.GetValue(null);
        sorted.Clear();
        sorted.AddRange(Catalog(fullOpening, eligibleMods).Values.OrderByDescending(perk => perk.cost).ThenBy(perk => perk.id, StringComparer.Ordinal));
        // The native first-run gate reads the solo save index. A paired
        // opening can allow selection on both seats without writing that index.
        FirstRun.SetValue(null, false);
        if (!partner) frame.instruction.text = LocHelper.GetLocalizedMainMenu("new_perk_instruction");
        else if (fullOpening)
        {
            frame.headerTitle.text = ModHelper.GetLocalized("probably_coop", "opening_partner_perks");
            frame.instruction.text = ModHelper.GetLocalized("probably_coop", "opening_partner_perks_hint");
        }
        else
        {
            frame.headerTitle.text = "合伙人个人天赋";
            frame.instruction.text = "分别选择个人天赋；全店开局配置由房主确定。混合天赋不重复发放店铺赠品。";
        }
        frame.instruction.color = NewGameStyle.DESCRIPTION;
        Rebuild.Invoke(null, null);
    }
    private static void Restore(Pending setup)
    {
        if (!ReferenceEquals(pending, setup)) return;
        pending = null;
        setup.Handle.RestoreTo(NewGameData.Instance);
    }
    internal static bool CancelDraft(DraftHandle owner)
    {
        var setup = pending;
        if (owner == null || setup == null || !ReferenceEquals(setup.Handle, owner)) return false;
        setup.Submitted = true; // owner-driven disposal is not a new cancel input
        try
        {
            if (owner.Window != null && ReferenceEquals(Window.GetValue(null), owner.Window) && owner.Window.IsAlive)
                NewGamePerksUI.CloseWithoutReturning();
        }
        finally { Restore(setup); }
        return true;
    }
    internal static bool ContinueDraft()
    {
        var setup = pending; if (setup == null) return true;
        if (setup.Handle.Window == null || !ReferenceEquals(Window.GetValue(null), setup.Handle.Window)) return true;
        if (!ReferenceEquals(NewGameData.Instance, setup.Handle.Data) || !ReferenceEquals(NewGameData.Instance.startingPerks, setup.Draft))
        { CancelDraft(setup.Handle); setup.Cancel?.Invoke(); return false; }
        string[] ids;
        try { ids = ValidateSelection(setup.Draft.Select(perk => perk.id), setup.FullOpening, setup.EligibleMods).Select(perk => perk.id).ToArray(); }
        catch (InvalidDataException error)
        {
            if (Frame.GetValue(null) is NewGamePerksFrame frame) frame.instruction.text = "天赋组合无效：" + error.Message;
            return false;
        }
        setup.Submitted = true;
        NewGamePerksUI.CloseWithoutReturning(); Restore(setup);
        setup.Accept(ids);
        return false;
    }
    internal static DraftHandle BeforeClosed()
    {
        if (pending?.Handle.Window == null || !ReferenceEquals(Window.GetValue(null), pending.Handle.Window)) return null;
        StartingGame.SetValue(null, true);
        return pending.Handle;
    }
    internal static void AfterClosed(DraftHandle owner)
    {
        var setup = pending; if (setup == null || owner == null || !ReferenceEquals(setup.Handle, owner)) return;
        Restore(setup);
        if (setup.Ready && !setup.Submitted) setup.Cancel?.Invoke();
    }
}

[HarmonyPatch(typeof(NewGamePerksUI), "OnContinueClicked")]
internal static class NativeActorPartnerPerksContinue
{ private static bool Prefix() => NativeActorPartnerSetup.ContinueDraft(); }
[HarmonyPatch(typeof(NewGamePerksUI), "OnWindowClosed")]
internal static class NativeActorPartnerPerksClosed
{
    private static void Prefix(out NativeActorPartnerSetup.DraftHandle __state) => __state = NativeActorPartnerSetup.BeforeClosed();
    private static void Postfix(NativeActorPartnerSetup.DraftHandle __state) => NativeActorPartnerSetup.AfterClosed(__state);
}
