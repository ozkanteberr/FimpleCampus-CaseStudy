using AtmCash.Domain;

namespace AtmCash.Tests;

public sealed class WithdrawalReservationRulesTests
{
    [Fact]
    public void Shared_banknotes_are_reserved_once_per_cassette()
    {
        var fiftyCassette = Guid.NewGuid();
        var twentyCassette = Guid.NewGuid();
        var alternatives = new[]
        {
            Alternative(WithdrawalAlternativeKind.Lower, 70,
                Allocation(fiftyCassette, 50, 1), Allocation(twentyCassette, 20, 1)),
            Alternative(WithdrawalAlternativeKind.Upper, 90,
                Allocation(fiftyCassette, 50, 1), Allocation(twentyCassette, 20, 2))
        };

        var plan = WithdrawalReservationRules.Build(Currency.TRY, 80, alternatives);

        Assert.Equal(90, plan.LimitAmount);
        Assert.Equal(2, plan.Notes.Count);
        Assert.Equal(1, plan.Notes.Single(x => x.CassetteId == fiftyCassette).Count);
        Assert.Equal(2, plan.Notes.Single(x => x.CassetteId == twentyCassette).Count);
    }

    [Fact]
    public void Different_alternatives_reserve_each_required_cassette_but_one_limit_amount()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var plan = WithdrawalReservationRules.Build(Currency.USD, 110,
        [
            Alternative(WithdrawalAlternativeKind.Lower, 100,
                Allocation(first, 50, 2, Currency.USD)),
            Alternative(WithdrawalAlternativeKind.Upper, 120,
                Allocation(second, 100, 1, Currency.USD), Allocation(third, 20, 1, Currency.USD))
        ]);

        Assert.Equal(3, plan.Notes.Count);
        Assert.Equal(120, plan.LimitAmount);
        Assert.Equal(220, plan.Notes.Sum(x => x.Count * x.Denomination));
    }

    [Fact]
    public void Exact_alternative_reserves_its_banknotes_and_requested_amount()
    {
        var cassette = Guid.NewGuid();
        var plan = WithdrawalReservationRules.Build(Currency.EUR, 100,
        [
            Alternative(WithdrawalAlternativeKind.Exact, 100,
                Allocation(cassette, 50, 2, Currency.EUR))
        ]);

        Assert.Equal(100, plan.LimitAmount);
        var note = Assert.Single(plan.Notes);
        Assert.Equal(cassette, note.CassetteId);
        Assert.Equal(Currency.EUR, note.Currency);
        Assert.Equal(2, note.Count);
    }

    [Fact]
    public void Invalid_alternatives_and_allocations_are_rejected()
    {
        var cassette = Guid.NewGuid();
        AssertError("INVALID_ALTERNATIVES", () => WithdrawalReservationRules.Build(Currency.TRY, 80, []));
        AssertError("INVALID_ALTERNATIVES", () => WithdrawalReservationRules.Build(Currency.TRY, 80,
        [
            Alternative(WithdrawalAlternativeKind.Exact, 80, Allocation(cassette, 20, 4)),
            Alternative(WithdrawalAlternativeKind.Upper, 100, Allocation(cassette, 100, 1))
        ]));
        AssertError("ALTERNATIVE_AMOUNT_MISMATCH", () => WithdrawalReservationRules.Build(Currency.TRY, 80,
        [Alternative(WithdrawalAlternativeKind.Lower, 70, Allocation(cassette, 50, 1))]));
        AssertError("DUPLICATE_ALLOCATION", () => WithdrawalReservationRules.Build(Currency.TRY, 100,
        [Alternative(WithdrawalAlternativeKind.Exact, 100,
            Allocation(cassette, 50, 1), Allocation(cassette, 50, 1))]));
        AssertError("INVALID_ALLOCATION", () => WithdrawalReservationRules.Build(Currency.TRY, 100,
        [Alternative(WithdrawalAlternativeKind.Exact, 100, Allocation(cassette, 100, 1, Currency.USD))]));
    }

    private static WithdrawalAlternative Alternative(WithdrawalAlternativeKind kind, int amount,
        params WithdrawalAllocation[] allocations) => new() { Kind = kind, Amount = amount, Allocations = allocations.ToList() };

    private static WithdrawalAllocation Allocation(Guid cassetteId, int denomination, int count,
        Currency currency = Currency.TRY) => new()
        {
            CassetteId = cassetteId, Currency = currency, Denomination = denomination, Count = count
        };

    private static void AssertError(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<BusinessException>(action).Code);
}
