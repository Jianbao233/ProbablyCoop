using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Api;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed class CoopOpening
{
    internal readonly NewGameData Request;
    internal readonly StartingScenario HostScenario;
    internal readonly StartingScenario PartnerScenario;
    private readonly CoopOpeningCareer hostCareer, partnerCareer;
    internal readonly StartingPerk[] HostPerks;
    internal readonly StartingPerk[] PartnerPerks;
    internal readonly string[] ModProviders;
    internal readonly int RentWeeks;

    internal CoopOpening(NewGameData request, string partnerScenario, IEnumerable<string> partnerPerks, int rentWeeks)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        HostScenario = FindScenario(request.startScenarioId, out hostCareer);
        PartnerScenario = FindScenario(partnerScenario, out partnerCareer);
        if (!CanCombine(hostCareer, partnerCareer))
            throw new InvalidDataException(ModHelper.GetLocalized("probably_coop", "opening_career_conflict"));
        if (rentWeeks is < OpeningRentPlan.MinimumWeeks or > OpeningRentPlan.MaximumWeeks)
            throw new ArgumentOutOfRangeException(nameof(rentWeeks));
        RentWeeks = rentWeeks;
        HostPerks = NativeActorPartnerSetup.ValidateSelection(request.startingPerks.Select(perk => perk.id), fullOpening: true);
        PartnerPerks = NativeActorPartnerSetup.ValidateSelection(partnerPerks, fullOpening: true);
        ModProviders = HostPerks.Concat(PartnerPerks).Select(perk => perk.source)
            .Concat(new[] { HostScenario.source, PartnerScenario.source })
            .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var host in HostPerks)
        foreach (var partner in PartnerPerks)
            if (NativeActorPartnerSetup.ConflictsAcrossActors(host, partner))
                throw new InvalidDataException(ModHelper.GetLocalized("probably_coop", "opening_shop_perk_conflict",
                    host.GetLocalizedDisplayName(), partner.GetLocalizedDisplayName()));
    }

    internal int Shortfall => OpeningRentPlan.QuotedShortfall(hostCareer.CashCost,
        HostScenario.id == PartnerScenario.id ? 0 : partnerCareer.CashCost,
        HostPerks.Concat(PartnerPerks).Any(perk => perk.id == "saving"));

    internal int Cost(string id) => id == HostScenario.id ? hostCareer.CashCost :
        id == PartnerScenario.id ? partnerCareer.CashCost :
        throw new InvalidOperationException("Cannot start an unselected career.");

    internal static StartingScenario FindScenario(string id, out CoopOpeningCareer career)
    {
        var scenario = CoopOpeningCareer.ValidId(id) ? StartingScenario.Get(id) : null;
        if (!TryCareer(scenario, out career))
            throw new InvalidDataException(ModHelper.GetLocalized("probably_coop", "opening_career_unavailable", id ?? "—"));
        return scenario;
    }

    internal static bool TryCareer(StartingScenario scenario, out CoopOpeningCareer career)
    {
        career = null;
        if (scenario == null || !CoopOpeningCareer.ValidId(scenario.id) || scenario.hidden || !scenario.CanStart())
            return false;
        if (!scenario.IsMod)
        {
            if (!OpeningRentPlan.TryFee(scenario.id, out int cost)) return false;
            career = new CoopOpeningCareer(scenario.id, cost);
            return true;
        }
        if (string.IsNullOrEmpty(scenario.source)) return false;
        var declarations = ModHelper.Lookup<IReadOnlyCollection<CoopOpeningCareer>>(scenario.source,
            CoopExtensionApi.OpeningCareersService);
        if (declarations == null || declarations.Count > 32) return false;
        foreach (var declaration in declarations)
        {
            if (declaration?.Id != scenario.id) continue;
            if (career != null) return false;
            career = declaration;
        }
        return career != null && (scenario.onStart != null || career.CashCost == 0);
    }

    internal static bool CanCombine(CoopOpeningCareer host, CoopOpeningCareer partner) =>
        OpeningRentPlan.CanCombineIds(host.Id, partner.Id) && !host.ConflictsWith(partner);

    internal void ApplyMenuChoices()
    {
        Request.startingPerks = HostPerks.ToList();
        Request.skipIntro |= HostScenario.skipIntro || PartnerScenario.skipIntro;
    }

    internal void ApplyPartner(NativeActorSession session)
    {
        session.Store.modData[NativeActorPartnerSetup.SaveKey] =
            NativeActorPartnerSetup.PrepareOpening(session.Run, PartnerPerks.Select(perk => perk.id)).Commit();
    }
}
