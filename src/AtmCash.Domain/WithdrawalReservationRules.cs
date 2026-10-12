namespace AtmCash.Domain;

public sealed record CassetteReservation(Guid CassetteId, Currency Currency, int Denomination, int Count);
public sealed record WithdrawalReservationPlan(IReadOnlyList<CassetteReservation> Notes, int LimitAmount);

public static class WithdrawalReservationRules
{
    public static WithdrawalReservationPlan Build(Currency currency, int requestedAmount,
        IReadOnlyList<WithdrawalAlternative> alternatives)
    {
        CashRules.Require(Enum.IsDefined(currency), "INVALID_CURRENCY", "Quote currency is not supported.");
        CashRules.Require(requestedAmount > 0, "INVALID_AMOUNT", "Requested amount must be positive.");
        CashRules.Require(alternatives.Count is >= 1 and <= 2,
            "INVALID_ALTERNATIVES", "A quote requires one exact alternative or one or two neighbouring alternatives.");
        var kinds = alternatives.Select(x => x.Kind).ToArray();
        CashRules.Require(kinds.Distinct().Count() == kinds.Length && kinds.All(Enum.IsDefined),
            "INVALID_ALTERNATIVES", "Alternative kinds must be unique and supported.");
        CashRules.Require(!kinds.Contains(WithdrawalAlternativeKind.Exact) || kinds.Length == 1,
            "INVALID_ALTERNATIVES", "An exact alternative cannot be combined with lower or upper alternatives.");

        var requirements = new Dictionary<(Guid CassetteId, Currency Currency, int Denomination), int>();
        foreach (var alternative in alternatives)
        {
            CashRules.Require(alternative.Amount > 0 && alternative.Allocations.Count > 0,
                "INVALID_ALTERNATIVE", "An alternative needs a positive amount and banknotes.");
            CashRules.Require(alternative.Kind switch
            {
                WithdrawalAlternativeKind.Exact => alternative.Amount == requestedAmount,
                WithdrawalAlternativeKind.Lower => alternative.Amount < requestedAmount,
                WithdrawalAlternativeKind.Upper => alternative.Amount > requestedAmount,
                _ => false
            }, "INVALID_ALTERNATIVE", "Alternative amount does not match its kind.");

            var seen = new HashSet<(Guid CassetteId, Currency Currency, int Denomination)>();
            long total = 0;
            foreach (var allocation in alternative.Allocations)
            {
                CashRules.ValidateNote(allocation.Currency, allocation.Denomination);
                CashRules.Require(allocation.Currency == currency && allocation.CassetteId != Guid.Empty
                    && allocation.Count is > 0 and <= CashRules.Capacity,
                    "INVALID_ALLOCATION", "Allocation must use the quote currency, a cassette and 1 to 100 notes.");
                var key = (allocation.CassetteId, allocation.Currency, allocation.Denomination);
                CashRules.Require(seen.Add(key), "DUPLICATE_ALLOCATION", "A cassette/note pair may appear only once per alternative.");
                total += (long)allocation.Denomination * allocation.Count;
                requirements[key] = Math.Max(requirements.GetValueOrDefault(key), allocation.Count);
            }
            CashRules.Require(total == alternative.Amount, "ALTERNATIVE_AMOUNT_MISMATCH",
                "Alternative amount must equal the value of its banknotes.");
        }

        var notes = requirements.OrderBy(x => x.Key.CassetteId).ThenBy(x => x.Key.Currency)
            .ThenBy(x => x.Key.Denomination)
            .Select(x => new CassetteReservation(x.Key.CassetteId, x.Key.Currency, x.Key.Denomination, x.Value))
            .ToArray();
        return new WithdrawalReservationPlan(notes, alternatives.Max(x => x.Amount));
    }
}
