using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ProbablyCoop.Core;

internal sealed class OpeningRentPlan
{
    internal const int MinimumWeeks = 0;
    internal const int MaximumWeeks = 6;
    internal const int DefaultWeeks = 4;
    internal const int StartingCash = 600;
    internal const int SavingPerkCredit = 150;

    private static readonly IReadOnlyDictionary<string, int> Fees = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["apprentice"] = 50, ["water_merchant"] = 200, ["chemist"] = 300,
        ["fence"] = 150, ["hydroponic_farmer"] = 150, ["gunsmith"] = 450,
        ["rancher"] = 0, ["aquafarmer"] = 450, ["rock_bottom"] = 0
    };

    internal static bool TryFee(string scenario, out int fee)
    {
        fee = 0;
        return scenario != null && Fees.TryGetValue(scenario, out fee);
    }

    internal static bool CanCombine(string host, string partner) =>
        TryFee(host, out _) && TryFee(partner, out _) &&
        (host == partner || host != "rock_bottom" && partner != "rock_bottom");

    internal static int QuotedShortfall(string host, string partner, bool savingPerk)
    {
        if (!CanCombine(host, partner)) throw new ArgumentException("Incompatible starting scenarios.");
        int cost = Fees[host] + (host == partner ? 0 : Fees[partner]);
        return Math.Max(0, cost - StartingCash - (savingPerk ? SavingPerkCredit : 0));
    }

    internal ReadOnlyCollection<int> Amounts { get; }
    internal int Index { get; }
    internal int Active => Index < Amounts.Count ? Amounts[Index] : 0;
    internal bool Complete => Index == Amounts.Count;
    internal int Future => Amounts.Skip(Index + 1).Sum();

    private OpeningRentPlan(int[] amounts, int index)
    {
        Amounts = Array.AsReadOnly(amounts);
        Index = index;
    }

    internal static OpeningRentPlan Create(int missing, int weeks)
    {
        if (missing < 0) throw new ArgumentOutOfRangeException(nameof(missing));
        if (weeks is < MinimumWeeks or > MaximumWeeks) throw new ArgumentOutOfRangeException(nameof(weeks));
        if (weeks == 0 || missing == 0) return new OpeningRentPlan(Array.Empty<int>(), 0);

        int[] amounts = new int[weeks];
        int weights = weeks * (weeks + 1) / 2;
        int remainder = missing;
        for (int i = 0; i < weeks; i++)
        {
            amounts[i] = (int)((long)missing * (i + 1) / weights);
            remainder -= amounts[i];
        }
        for (int i = weeks - 1; remainder > 0; i--, remainder--) amounts[i]++;
        return new OpeningRentPlan(amounts, 0);
    }

    internal static OpeningRentPlan Restore(IEnumerable<int> amounts, int index)
    {
        if (amounts == null) throw new ArgumentNullException(nameof(amounts));
        var frozen = amounts.Take(MaximumWeeks + 1).ToArray();
        if (frozen.Length > MaximumWeeks || index < 0 || index > frozen.Length)
            throw new ArgumentException("Invalid rent installment cursor.");
        int sum = frozen.Aggregate(0, (total, amount) => checked(total + amount));
        if (sum < 0 || !Create(sum, frozen.Length).Amounts.SequenceEqual(frozen))
            throw new ArgumentException("Invalid rent installment amounts.");
        return new OpeningRentPlan(frozen, index);
    }

    internal OpeningRentPlan Advance() => Complete ? this : new OpeningRentPlan(Amounts.ToArray(), Index + 1);
}
