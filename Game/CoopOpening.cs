using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed class CoopOpening
{
    internal readonly NewGameData Request;
    internal readonly StartingScenario HostScenario;
    internal readonly StartingScenario PartnerScenario;
    internal readonly StartingPerk[] HostPerks;
    internal readonly StartingPerk[] PartnerPerks;
    internal readonly int RentWeeks;

    internal CoopOpening(NewGameData request, string partnerScenario, IEnumerable<string> partnerPerks, int rentWeeks)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        HostScenario = FindScenario(request.startScenarioId);
        PartnerScenario = FindScenario(partnerScenario);
        if (!OpeningRentPlan.CanCombine(HostScenario.id, PartnerScenario.id))
            throw new InvalidDataException("The two starting careers cannot share a shop.");
        if (rentWeeks is < OpeningRentPlan.MinimumWeeks or > OpeningRentPlan.MaximumWeeks)
            throw new ArgumentOutOfRangeException(nameof(rentWeeks));
        RentWeeks = rentWeeks;
        HostPerks = NativeActorPartnerSetup.ValidateSelection(request.startingPerks.Select(perk => perk.id), fullOpening: true);
        PartnerPerks = NativeActorPartnerSetup.ValidateSelection(partnerPerks, fullOpening: true);
        foreach (var host in HostPerks)
        foreach (var partner in PartnerPerks)
        {
            if (NativeActorPartnerSetup.IsPersonalChoice(host.id) && NativeActorPartnerSetup.IsPersonalChoice(partner.id))
                continue;
            if (host.incompatiblePerks?.Contains(partner.id) == true || partner.incompatiblePerks?.Contains(host.id) == true)
                throw new InvalidDataException($"Shared shop perks conflict: {host.GetLocalizedDisplayName()} / {partner.GetLocalizedDisplayName()}.");
        }
    }

    internal int Shortfall => OpeningRentPlan.QuotedShortfall(HostScenario.id, PartnerScenario.id,
        HostPerks.Concat(PartnerPerks).Any(perk => perk.id == "saving"));

    internal static StartingScenario FindScenario(string id)
    {
        var scenario = string.IsNullOrEmpty(id) ? null : StartingScenario.Get(id);
        if (scenario == null || !OpeningRentPlan.TryFee(id, out _) || scenario.IsMod || scenario.hidden || !scenario.CanStart())
            throw new InvalidDataException("Starting career is not available in the two-player draft.");
        return scenario;
    }

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
